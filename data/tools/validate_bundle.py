"""Offline shape/reference checks only; this is not a historical query engine."""
import argparse
import json
from datetime import datetime
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker

DATA = Path(__file__).resolve().parents[1]
SCHEMA_PATH = DATA / 'schemas' / 'review-bundle.schema.json'


def read_json(path):
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f'Duplicate JSON key: {key}')
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8-sig'),
                      object_pairs_hook=unique_keys,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(f'Non-JSON number: {value}')))


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def load_schema(path=SCHEMA_PATH):
    """Expand our acyclic, local-only schema modules without network retrieval.

    This intentionally supports only plain $ref nodes and JSON Pointer fragments.
    Reject unsupported scope/recursive features instead of silently misreading them.
    """
    root = Path(path).resolve().parent
    documents = {}

    def document(file):
        if not file.is_relative_to(root):
            raise ValueError('Schema reference escapes schema directory')
        if file not in documents:
            documents[file] = read_json(file)
            Draft202012Validator.check_schema(documents[file])
        return documents[file]

    def expand(node, file, stack):
        if isinstance(node, list):
            return [expand(child, file, stack) for child in node]
        if not isinstance(node, dict):
            return node
        if any(key in node for key in ('$id', '$dynamicRef', '$dynamicAnchor', '$anchor')):
            raise ValueError('Unsupported schema scope or dynamic reference')
        if '$ref' in node:
            if len(node) != 1:
                raise ValueError('Schema $ref siblings are not supported by this loader')
            location, _, fragment = node['$ref'].partition('#')
            if location and (':' in location or '\\' in location or location.startswith('/')):
                raise ValueError('Only relative local schema references are supported')
            target_file = (file.parent / location).resolve() if location else file
            key = (target_file, fragment)
            if key in stack:
                raise ValueError('Cyclic schema reference')
            target = document(target_file)
            if fragment:
                if not fragment.startswith('/'):
                    raise ValueError('Schema fragment must be a JSON Pointer')
                try:
                    for part in fragment[1:].split('/'):
                        part = part.replace('~1', '/').replace('~0', '~')
                        target = target[int(part)] if isinstance(target, list) else target[part]
                except (KeyError, IndexError, ValueError, TypeError) as error:
                    raise ValueError(f'Unresolved schema reference: {node["$ref"]}') from error
            return expand(target, target_file, stack + (key,))
        return {key: expand(child, file, stack) for key, child in node.items()}

    file = Path(path).resolve()
    return expand(document(file), file, ())


def validate(bundle, schema=None):
    # Callers supplying a schema must supply the expanded form from load_schema.
    schema = schema if schema is not None else load_schema()
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    errors = []
    for error in validator.iter_errors(bundle):
        path = '/'.join(map(str, error.absolute_path)) or '<root>'
        # oneOf errors contain the entire record in their default text.
        errors.append(f'{path}: fails {error.validator} constraint')
    if errors:
        return errors

    records = bundle['records']
    entities, revisions, sources = {}, {}, {}
    for source in bundle['sources']:
        sid = source['source_id']
        if sid in sources:
            errors.append(f'duplicate source_id: {sid}')
        sources[sid] = source
    for record in records:
        rid, rev = record['record_id'], record['revision_id']
        if rev in revisions:
            errors.append(f'duplicate revision_id: {rev}')
        revisions[rev] = record
        if rid in entities and entities[rid] != record['kind']:
            errors.append(f'{rid}: identity changes kind')
        entities[rid] = record['kind']
    if set(entities) & set(sources):
        errors.append('source IDs and entity IDs must not collide')

    # Traverse only schema branches which match. The annotations are local,
    # supplementary reference rules; ordinary JSON Schema consumers ignore them.
    def references(value, spec, path):
        for branch in spec.get('allOf', []):
            references(value, branch, path)
        for choice in ('oneOf', 'anyOf'):
            if choice in spec:
                for branch in spec[choice]:
                    if validator.evolve(schema=branch).is_valid(value):
                        references(value, branch, path)
                return
        target = spec.get('x-reference')
        if target:
            actual = 'source' if value in sources else entities.get(value)
            if actual is None or (target != '*' and actual != target):
                errors.append(f'{path}: unknown or wrong-type reference {value} (expected {target})')
        if spec.get('x-revision-reference') and value not in revisions:
            errors.append(f'{path}: missing revision {value}')
        if isinstance(value, dict):
            for key, child in value.items():
                references(child, spec.get('properties', {}).get(key, {}), f'{path}/{key}')
        if isinstance(value, list):
            for index, child in enumerate(value):
                references(child, spec.get('items', {}), f'{path}/{index}')

    references(bundle, schema, '<root>')
    by_entity = {}
    for record in records:
        by_entity.setdefault(record['record_id'], []).append(record)
    successors = {}
    for record in records:
        rev = record['revision_id']
        if record['valid_to'] is not None and instant(record['valid_to']) <= instant(record['valid_from']):
            errors.append(f'{rev}: valid_to must follow valid_from')
        prior = record['supersedes_revision_id']
        source = sources.get(record['source_id'])
        if source:
            if record['provenance'] == 'synthetic' and source['origin'] != 'synthetic_authoring':
                errors.append(f'{rev}: synthetic record needs an authoring source')
            if record['provenance'] == 'sourced' and source['origin'] == 'synthetic_authoring':
                errors.append(f'{rev}: authored source cannot establish sourced real-world data')
        if prior:
            successors.setdefault(prior, []).append(rev)
        if prior in revisions:
            previous = revisions[prior]
            if prior == rev or previous['record_id'] != record['record_id'] or previous['kind'] != record['kind']:
                errors.append(f'{rev}: invalid supersession identity')
            if instant(record['recorded_at']) < instant(previous['recorded_at']):
                errors.append(f'{rev}: recorded_at precedes prior revision')
            if record['provenance'] == previous['provenance'] == 'synthetic':
                if instant(record['synthetic_released_at']) <= instant(previous['synthetic_released_at']):
                    errors.append(f'{rev}: synthetic revision release must advance')
            immutable = {
                'affiliation': ['person_id', 'club_id', 'role'],
                'participant': ['fixture_id', 'person_id'],
                'period': ['fixture_id', 'period_type'],
                'player_profile': ['person_id'],
                'transfer': ['person_id'],
                'match_event': ['fixture_id', 'event_type'],
                'season_membership': ['competition_season_id', 'club_id'],
                'competition_season': ['competition_id', 'season'],
                'club_stadium': ['club_id', 'stadium_id', 'use'],
                'pitch': ['stadium_id'],
                'public_contact_channel': ['owner', 'channel_type', 'purpose'],
                'supporter_group_recognition': ['supporter_group_id', 'recogniser'],
                'academy': ['club_id'],
                'team_programme': ['club_id', 'academy_id', 'category'],
                'facility_use': ['scope', 'facility_id', 'use_type'],
                'support_department': ['scope', 'function'],
                'staff_appointment': ['person_id', 'club_id', 'scope'],
                'coaching_staff_relationship': [
                    'staff_appointment_id', 'lead_staff_appointment_id', 'scope', 'relationship_type'
                ],
                'match_staff_assignment': ['fixture_id', 'subject', 'staff_appointment_id', 'club_id'],
                'attendance_observation': ['fixture_id', 'population_scope', 'measure'],
                'match_capacity': ['fixture_id'],
            }.get(record['kind'], [])
            if any(record['payload'].get(key) != previous['payload'].get(key) for key in immutable):
                errors.append(f'{rev}: revision changes subject identity')
        # Detect cycles even if chronology happens to be malformed too.
        seen, cursor = {rev}, prior
        while cursor in revisions:
            if cursor in seen:
                errors.append(f'{rev}: supersession cycle')
                break
            seen.add(cursor)
            cursor = revisions[cursor]['supersedes_revision_id']

        p, kind = record['payload'], record['kind']
        if kind == 'fixture' and p['home_club_id'] == p['away_club_id']:
            errors.append(f'{rev}: fixture cannot play itself')
        if kind == 'period' and p['ended_at'] and instant(p['ended_at']) <= instant(p['started_at']):
            errors.append(f'{rev}: period end must follow start')
        if 'fixture_id' in p and 'clock' in p:
            periods = by_entity.get(p['clock']['period_id'], [])
            if periods and any(period['payload'].get('fixture_id') != p['fixture_id'] for period in periods):
                errors.append(f'{rev}: clock period belongs to another fixture')
        if kind == 'tracking_sample':
            configs = by_entity.get(p['pitch_configuration_id'], [])
            # Coordinates outside the pitch are legitimate (bench/touchline).
            # No bounds test here: surface position and on-pitch status differ.
            for config in configs:
                if config['kind'] != 'pitch_configuration':
                    continue
                fixtures = by_entity.get(p['fixture_id'], [])
                if any(f['payload'].get('pitch_id') != config['payload']['pitch_id'] for f in fixtures):
                    errors.append(f'{rev}: tracking configuration belongs to another pitch')
        if kind == 'match_event':
            related_types = {
                'pass_resolved': ('pass_event_id', 'pass_released'),
                'interception': ('pass_event_id', 'pass_released'),
                'play_resumed': ('interruption_event_id', 'interruption_started'),
                'penalty_attempt': ('award_event_id', 'penalty_decision'),
            }
            link = related_types.get(p['event_type'])
            if link:
                field, expected_type = link
                for related in by_entity.get(p['details'][field], []):
                    target = related['payload']
                    if target.get('event_type') != expected_type or target.get('fixture_id') != p['fixture_id']:
                        errors.append(f'{rev}: incompatible related event')
                    elif target['clock']['period_id'] == p['clock']['period_id']:
                        before = (target['clock']['elapsed_ms'], target['clock']['sequence'])
                        after = (p['clock']['elapsed_ms'], p['clock']['sequence'])
                        if before > after:
                            errors.append(f'{rev}: resolution precedes related event')

    for rid, versions in by_entity.items():
        if sum(v['supersedes_revision_id'] is None for v in versions) != 1:
            errors.append(f'{rid}: bundle requires one root revision')
    for prior, next_revs in successors.items():
        if len(next_revs) > 1:
            errors.append(f'{prior}: branched revisions require reconciliation')
    if not errors:
        from ecosystem_relations import check_ecosystem_relations
        errors.extend(check_ecosystem_relations(records))
    if not errors:
        from season_views import check_season_relations
        errors.extend(check_season_relations(records))
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', nargs='?', type=Path, default=DATA / 'examples/review-bundle.json')
    args = parser.parse_args()
    try:
        bundle = read_json(args.bundle)
        errors = validate(bundle)
    except (ValueError, OSError) as error:
        parser.exit(1, f'Invalid input: {error}\n')
    if errors:
        parser.exit(1, '\n'.join(errors) + '\n')
    print(f"PASS: {len(bundle['records'])} revisions; schema, formats, references and basic revision/clock checks.")
    print('Not verified: historical queries, football completeness, metric calculations or model answers.')


if __name__ == '__main__':
    main()
