"""Connected scope checks for club-ecosystem records; not a query engine."""

from datetime import datetime
from itertools import product


def _instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def _overlaps(left, right):
    left_start, right_start = _instant(left['valid_from']), _instant(right['valid_from'])
    left_end = _instant(left['valid_to']) if left['valid_to'] else None
    right_end = _instant(right['valid_to']) if right['valid_to'] else None
    return (left_end is None or right_start < left_end) and (right_end is None or left_start < right_end)


def _coexist(records):
    start = max(_instant(record['valid_from']) for record in records)
    ends = [_instant(record['valid_to']) for record in records if record['valid_to']]
    return not ends or start < min(ends)


def _knowledge_start(record):
    return _instant(record['synthetic_released_at'] if record['provenance'] == 'synthetic'
                    else record['recorded_at'])


def _knowledge_windows(records):
    """Return half-open visibility intervals for each validated revision chain.

    Bundle validation checks complete, unbranched chains before calling here.
    A successor replaces its predecessor at its release/import instant, even
    when both revisions describe overlapping effective dates.
    """
    successors = {}
    for record in records:
        prior = record['supersedes_revision_id']
        if prior is not None:
            successors.setdefault(prior, []).append(record)
    windows = {}
    for record in records:
        successor_starts = [_knowledge_start(successor)
                            for successor in successors.get(record['revision_id'], [])]
        windows[record['revision_id']] = (
            _knowledge_start(record),
            min(successor_starts) if successor_starts else None
        )
    return windows


def _coexist_with_knowledge(records, windows):
    if not _coexist(records):
        return False
    starts = [windows[record['revision_id']][0] for record in records]
    ends = [windows[record['revision_id']][1] for record in records
            if windows[record['revision_id']][1] is not None]
    return not ends or max(starts) < min(ends)


def _has_active_cycle(edges, knowledge_windows):
    """Reject only hierarchy loops sharing effective and revision-visibility time."""
    graph = {}
    for source, target, record in edges:
        graph.setdefault(source, []).append((target, record))
    for origin in graph:
        pending = [(origin, {origin}, [])]
        while pending:
            node, visited, path = pending.pop()
            for target, record in graph.get(node, []):
                extended = path + [record]
                if not _coexist_with_knowledge(extended, knowledge_windows):
                    continue
                if target == origin:
                    return True
                if target not in visited:
                    pending.append((target, visited | {target}, extended))
    return False


def check_ecosystem_relations(records):
    """Validate dated ecosystem scope without selecting a current revision.

    A reference must resolve to a record that can apply during the related
    record's own interval. This deliberately avoids a current-directory rule:
    a later correction or departure cannot invalidate a historic relationship.
    """
    errors = []
    by_id = {}
    knowledge_windows = _knowledge_windows(records)
    for record in records:
        by_id.setdefault(record['record_id'], []).append(record)

    def versions(record_id, kind, during):
        return [
            candidate for candidate in by_id.get(record_id, [])
            if candidate['kind'] == kind and _overlaps(candidate, during)
        ]

    def scope_clubs(scope, during):
        kind = scope['scope_kind']
        if kind == 'club':
            return {scope['club_id']}
        if kind == 'academy':
            return {r['payload']['club_id'] for r in versions(scope['academy_id'], 'academy', during)}
        if kind == 'team_programme':
            return {r['payload']['club_id'] for r in versions(scope['team_programme_id'], 'team_programme', during)}
        if kind == 'support_department':
            clubs = set()
            for department in versions(scope['support_department_id'], 'support_department', during):
                clubs.update(scope_clubs(department['payload']['scope'], during))
            return clubs
        return set()

    def appointments(record_id, during):
        return versions(record_id, 'staff_appointment', during)

    def scope_paths(scope, during):
        kind = scope['scope_kind']
        if kind == 'club':
            return [[r] for r in versions(scope['club_id'], 'club', during)]
        key = kind + '_id'
        paths = []
        for candidate in versions(scope[key], kind, during):
            if kind == 'support_department':
                paths.extend([candidate] + path for path in scope_paths(candidate['payload']['scope'], during))
            else:
                paths.append([candidate])
        return paths

    def appointment_scope_supports(appointment_scope, relationship_scope, during):
        """A club-wide role can support a team; specific team/academy roles cannot be conflated."""
        relationship_kind = relationship_scope['scope_kind']
        if relationship_kind == 'club':
            return scope_clubs(appointment_scope, during) == {relationship_scope['club_id']}
        if relationship_kind != 'team_programme':
            return False
        team_id = relationship_scope['team_programme_id']
        relationship_clubs = scope_clubs(relationship_scope, during)
        if appointment_scope['scope_kind'] == 'club':
            return scope_clubs(appointment_scope, during) == relationship_clubs
        if appointment_scope['scope_kind'] == 'team_programme':
            return appointment_scope['team_programme_id'] == team_id
        if appointment_scope['scope_kind'] == 'support_department':
            return any(
                appointment_scope_supports(department['payload']['scope'], relationship_scope, during)
                for department in versions(appointment_scope['support_department_id'], 'support_department', during)
            )
        return False

    for record in records:
        payload, kind, revision = record['payload'], record['kind'], record['revision_id']

        if kind == 'supporter_group_recognition' and payload['recogniser']['recogniser_kind'] == 'club':
            group_versions = versions(payload['supporter_group_id'], 'supporter_group', record)
            club_id = payload['recogniser']['club_id']
            if not group_versions or not any(club_id in group['payload']['club_ids'] for group in group_versions):
                errors.append(f'{revision}: recognition club is outside the supporter-group scope')

        if kind == 'team_programme' and payload['academy_id'] is not None:
            academy_versions = versions(payload['academy_id'], 'academy', record)
            if not academy_versions or any(academy['payload']['club_id'] != payload['club_id'] for academy in academy_versions):
                errors.append(f'{revision}: team programme academy belongs to another club or time scope')

        if kind == 'facility':
            if payload['parent_facility_id'] == record['record_id']:
                errors.append(f'{revision}: facility cannot parent itself')

        if kind == 'facility_use':
            clubs = scope_clubs(payload['scope'], record)
            facilities = versions(payload['facility_id'], 'facility', record)
            if not clubs or not facilities:
                errors.append(f'{revision}: facility use scope is outside its dated relationship')

        if kind == 'support_department':
            clubs = scope_clubs(payload['scope'], record)
            parent_id = payload['reports_to_department_id']
            if parent_id == record['record_id']:
                errors.append(f'{revision}: department cannot report to itself')
            elif parent_id is not None:
                parent_versions = versions(parent_id, 'support_department', record)
                parent_clubs = set()
                for parent in parent_versions:
                    parent_clubs.update(scope_clubs(parent['payload']['scope'], record))
                if not clubs or not parent_clubs or clubs != parent_clubs:
                    errors.append(f'{revision}: reporting department crosses club scope')

        if kind == 'staff_appointment':
            clubs = scope_clubs(payload['scope'], record)
            if payload['club_id'] not in clubs:
                errors.append(f'{revision}: staff appointment crosses club scope or time')

        if kind == 'coaching_staff_relationship':
            relationship_clubs = scope_clubs(payload['scope'], record)
            staff = appointments(payload['staff_appointment_id'], record)
            leads = appointments(payload['lead_staff_appointment_id'], record)
            staff_clubs = {appointment['payload']['club_id'] for appointment in staff}
            lead_clubs = {appointment['payload']['club_id'] for appointment in leads}
            if not relationship_clubs or not staff_clubs or not lead_clubs or staff_clubs != relationship_clubs or lead_clubs != relationship_clubs:
                errors.append(f'{revision}: coaching relationship crosses club scope or time')
            if any(not appointment_scope_supports(appointment['payload']['scope'], payload['scope'], record) for appointment in staff + leads):
                errors.append(f'{revision}: coaching relationship has incompatible team scope')
            common = any(
                _coexist([record, staff_record, lead_record] + relationship_path + staff_path + lead_path)
                for staff_record, lead_record in product(staff, leads)
                for relationship_path, staff_path, lead_path in product(
                    scope_paths(payload['scope'], record),
                    scope_paths(staff_record['payload']['scope'], record),
                    scope_paths(lead_record['payload']['scope'], record))
            )
            if not common:
                errors.append(f'{revision}: coaching appointments and scope have no common active interval')
            if payload['relationship_type'] == 'reports_to':
                same_appointment = payload['staff_appointment_id'] == payload['lead_staff_appointment_id']
                staff_people = {appointment['payload']['person_id'] for appointment in staff}
                lead_people = {appointment['payload']['person_id'] for appointment in leads}
                if same_appointment or staff_people & lead_people:
                    errors.append(f'{revision}: staff appointment cannot report to itself')

        if kind == 'match_staff_assignment':
            fixtures = versions(payload['fixture_id'], 'fixture', record)
            if payload['club_id'] is not None:
                if not fixtures or any(payload['club_id'] not in (fixture['payload']['home_club_id'], fixture['payload']['away_club_id']) for fixture in fixtures):
                    errors.append(f'{revision}: match staff club is not a fixture participant')
            appointment_id = payload['staff_appointment_id']
            if appointment_id is not None:
                appointment_versions = appointments(appointment_id, record)
                if payload['subject']['subject_type'] != 'person' or not appointment_versions:
                    errors.append(f'{revision}: match staff appointment requires its person subject')
                else:
                    if any(appointment['payload']['person_id'] != payload['subject']['person_id'] for appointment in appointment_versions):
                        errors.append(f'{revision}: match staff appointment belongs to another person')
                    if payload['club_id'] is None or any(appointment['payload']['club_id'] != payload['club_id'] for appointment in appointment_versions):
                        errors.append(f'{revision}: match staff appointment crosses club scope')

    for kind, parent_field in (('facility', 'parent_facility_id'),
                               ('support_department', 'reports_to_department_id')):
        edges = [(r['record_id'], r['payload'][parent_field], r) for r in records
                 if r['kind'] == kind and r['payload'][parent_field] is not None]
        if _has_active_cycle(edges, knowledge_windows):
            errors.append(f'{kind}: active hierarchy cycle')
    reports = [(r['payload']['staff_appointment_id'], r['payload']['lead_staff_appointment_id'], r)
               for r in records if r['kind'] == 'coaching_staff_relationship'
               and r['payload']['relationship_type'] == 'reports_to']
    if _has_active_cycle(reports, knowledge_windows):
        errors.append('coaching_staff_relationship: active reporting cycle')
    return errors
