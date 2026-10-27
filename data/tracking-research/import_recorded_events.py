"""Separate bounded PP import. Default/--check is offline; --fetch reads a prefix.

This is a retrospective replay spoiler cutoff, never a live-arrival guarantee.
The raw CSV and all target, outcome, metric and spatial columns are discarded.
"""
import argparse
from collections import Counter
import csv
from datetime import datetime, timezone
import io
import json
import re
import urllib.request

from import_recorded_sample import OUT, PIN, MATCH, SAMPLE_FILE, check as tracking_check, digest, finite, require

FILE = '1874553-completed-events.json'
MANIFEST = 'completed-events-manifest.json'
URL = f'https://raw.githubusercontent.com/SkillCorner/opendata/{PIN}/data/matches/{MATCH}/{MATCH}_dynamic_events.csv'
PREFIX_BYTES = 966956
PREFIX_SHA = '77e658496ccf7cf1e08c930fc45e56ac9e8545d558bb7ea326c85a062b1021f8'
POLICY = 'retrospective-minimal-pp-interval-completion-v1'
SELECTION = 'At most six eligible player_possession rows: first three per source team sorted by raw frame_start, frame_end, event_id; then globally sorted by the same keys. Wholly first-half 60-240 seconds, matched endpoints, clean source-clock synchronization and full interval player coverage.'
FIELDS = {'sourceId', 'sourceLine', 'type', 'actorId', 'teamId', 'period', 'frameStart', 'frameEnd', 'timeStart', 'timeEnd', 'start', 'end', 'startMatched', 'endMatched', 'dependency'}
DEPENDENCY_FIELDS = {'kind', 'verified', 'frame', 'clock'}
BUNDLE_FIELDS = {'schemaVersion', 'source', 'pin', 'matchId', 'period', 'start', 'end', 'policy', 'annotations'}


def seconds(text):
    require(isinstance(text, str) and re.fullmatch(r'\d{2}:\d{2}\.\d{1,3}', text), 'printed event clock')
    minutes, rest = text.split(':')
    require(float(rest) < 60, 'printed event seconds')
    return int(minutes)*60+float(rest)


def validate_entry(event, sample):
    require(set(event) == FIELDS, 'event field allowlist')
    require(event['type'] == 'player_possession' and event['period'] == sample['period'] == 1, 'event category/period')
    require(isinstance(event['sourceId'], str) and re.fullmatch(r'8_\d+', event['sourceId']), 'source event key')
    require(type(event['sourceLine']) is int and 2 <= event['sourceLine'] <= 1001, 'source line')
    require(event['startMatched'] is True and event['endMatched'] is True, 'explicit matched endpoints')
    roster = {p['id']: p for p in sample['players']}
    require(type(event['actorId']) is int and event['actorId'] in roster and roster[event['actorId']]['teamId'] == event['teamId'], 'actor roster/team')
    require(all(type(event[k]) is int for k in ('frameStart', 'frameEnd')), 'integer frames')
    require(all(finite(event[k]) for k in ('start', 'end')) and sample['start'] <= event['start'] <= event['end'] <= sample['end'], 'interval bounds')
    require(event['frameStart'] <= event['frameEnd'], 'frame order')
    frames = {f['frameId']: f for f in sample['frames']}
    require(len(frames) == len(sample['frames']), 'duplicate tracking frame IDs')
    for side in ('Start', 'End'):
        frame = frames.get(event['frame'+side])
        require(frame is not None and frame['period'] == event['period'] and frame['clock'] == event[side.lower()] == seconds(event['time'+side]), 'endpoint clock synchronization')
    for ident in range(event['frameStart'], event['frameEnd']+1):
        frame = frames.get(ident)
        require(frame is not None and frame['period'] == 1 and frame['clock'] == round(event['start']+(ident-event['frameStart'])*sample['cadence'], 8), 'full interval frames/clock')
        people = frame['players']
        require(len(people) == len(roster) and {p['playerId'] for p in people} == set(roster) and all(finite(p.get('x')) and finite(p.get('y')) for p in people), 'full interval position coverage')
    dependency = event['dependency']
    require(isinstance(dependency, dict) and set(dependency) == DEPENDENCY_FIELDS, 'dependency allowlist')
    require(dependency['kind'] == POLICY and dependency['verified'] is True and dependency['frame'] == event['frameEnd'] and dependency['clock'] == event['end'], 'verified interval completion dependency')


def key(event):
    return event['frameStart'], event['frameEnd'], event['sourceId']


def normalize(rows, sample):
    candidates = []
    ids = [row.get('event_id') for row in rows]
    require(len(ids) == len(set(ids)), 'duplicate source event IDs')
    for line, row in enumerate(rows, 2):
        if row.get('event_type') != 'player_possession':
            continue
        # No category refinements or physical-start interpretation in this policy.
        if row.get('event_subtype', '') or row.get('frame_physical_start', ''):
            continue
        try:
            require(int(row['match_id']) == MATCH and int(row['period']) == 1, 'source match/period')
            event = {'sourceId': row['event_id'], 'sourceLine': line, 'type': 'player_possession',
                     'actorId': int(row['player_id']), 'teamId': int(row['team_id']), 'period': int(row['period']),
                     'frameStart': int(row['frame_start']), 'frameEnd': int(row['frame_end']),
                     'timeStart': row['time_start'], 'timeEnd': row['time_end'],
                     'start': seconds(row['time_start']), 'end': seconds(row['time_end']),
                     'startMatched': row.get('is_player_possession_start_matched') == 'True',
                     'endMatched': row.get('is_player_possession_end_matched') == 'True'}
            event['dependency'] = {'kind': POLICY, 'verified': True, 'frame': event['frameEnd'], 'clock': event['end']}
            validate_entry(event, sample)
        except (ValueError, KeyError, TypeError):
            continue
        candidates.append(event)
    counts = Counter()
    selected = []
    for event in sorted(candidates, key=key):
        if counts[event['teamId']] < 3:
            selected.append(event)
            counts[event['teamId']] += 1
    return {'schemaVersion': 1, 'source': 'SkillCorner', 'pin': PIN, 'matchId': MATCH,
            'period': 1, 'start': 60, 'end': 240, 'policy': POLICY, 'annotations': selected}


def validate(bundle, sample):
    require(set(bundle) == BUNDLE_FIELDS and bundle['schemaVersion'] == 1 and bundle['source'] == 'SkillCorner', 'event bundle allowlist/version')
    require(bundle['pin'] == sample['pin'] == PIN and bundle['matchId'] == sample['matchId'] == MATCH and bundle['period'] == sample['period'] == 1, 'bundle identity/period')
    require(bundle['start'] == sample['start'] == 60 and bundle['end'] == sample['end'] == 240 and bundle['policy'] == POLICY, 'bundle bounds/policy')
    events = bundle['annotations']
    require(isinstance(events, list) and len(events) <= 6, 'bounded selection')
    require(len({e['sourceId'] for e in events}) == len(events), 'unique event IDs')
    require(events == sorted(events, key=key) and all(n <= 3 for n in Counter(e['teamId'] for e in events).values()), 'deterministic per-team selection')
    for event in events:
        validate_entry(event, sample)


def check(output=OUT):
    sample, tracking_manifest = tracking_check()
    manifest = json.loads((output/MANIFEST).read_text(encoding='utf-8'))
    require(manifest['pin'] == PIN and manifest['matchId'] == MATCH and manifest['policy'] == POLICY, 'event manifest identity/policy')
    receipt = manifest['receipt']
    require(receipt == {'url': URL, 'resolvedUrl': URL, 'status': 200, 'scope': 'first 1001 complete lines only (header + 1000 rows)', 'bytes': PREFIX_BYTES, 'sha256': PREFIX_SHA}, 'event prefix receipt')
    require(manifest['rawPrefixRetained'] is False and manifest['fullEventFileDownloaded'] is False and manifest['selection'] == SELECTION, 'bounded import policy')
    require(manifest['trackingArtifact'] == next(r for r in tracking_manifest['artifacts'] if r['file'] == SAMPLE_FILE), 'immutable tracking dependency')
    body = (output/FILE).read_bytes()
    require(manifest['artifact'] == {'file': FILE, 'bytes': len(body), 'sha256': digest(body)}, 'event artifact hash')
    bundle = json.loads(body)
    validate(bundle, sample)
    return bundle, manifest


def fetch(output=OUT):
    sample, tracking_manifest = tracking_check()
    with urllib.request.urlopen(URL, timeout=40) as response:
        require(response.status == 200 and response.url == URL, 'pinned response URL/status')
        chunks, size = [], 0
        for _ in range(1001):
            line = response.readline(PREFIX_BYTES-size+1)
            require(line.endswith(b'\n') and size+len(line) <= PREFIX_BYTES, 'complete capped CSV prefix')
            chunks.append(line)
            size += len(line)
        prefix = b''.join(chunks)
    require(len(prefix) == PREFIX_BYTES and digest(prefix) == PREFIX_SHA, 'pinned CSV prefix hash')
    bundle = normalize(list(csv.DictReader(io.StringIO(prefix.decode('utf-8')))), sample)
    validate(bundle, sample)
    body = (json.dumps(bundle, ensure_ascii=False, separators=(',', ':'), allow_nan=False)+'\n').encode('utf-8')
    manifest = {'source': 'SkillCorner/opendata', 'pin': PIN, 'matchId': MATCH, 'policy': POLICY,
                'retrievedAt': datetime.now(timezone.utc).isoformat(), 'selection': SELECTION,
                'receipt': {'url': URL, 'resolvedUrl': URL, 'status': 200, 'scope': 'first 1001 complete lines only (header + 1000 rows)', 'bytes': len(prefix), 'sha256': digest(prefix)},
                'hashSemantics': 'Exact parser-returned prefix bytes; not the full CSV or transport byte count.',
                'rawPrefixRetained': False, 'fullEventFileDownloaded': False,
                'trackingArtifact': next(r for r in tracking_manifest['artifacts'] if r['file'] == SAMPLE_FILE),
                'artifact': {'file': FILE, 'bytes': len(body), 'sha256': digest(body)},
                'limits': 'Small curated source-classified PP actor/interval selection. Interval end is an explicitly retrospective replay spoiler cutoff, not verified live arrival or known-then availability. No targets, pass outcomes, metrics, scores, tactics or event coordinates. Other categories and ambiguous timing remain deferred. Local unpublished technical sample; not a complete feed or Premier League replacement.'}
    output.mkdir(parents=True, exist_ok=True)
    (output/FILE).write_bytes(body)
    (output/MANIFEST).write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return check(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--fetch', action='store_true')
    modes.add_argument('--check', action='store_true', help='offline validation (default)')
    args = parser.parse_args()
    bundle, manifest = fetch() if args.fetch else check()
    print(json.dumps({'selected': [{'id': e['sourceId'], 'actorId': e['actorId'], 'cutoff': e['end']} for e in bundle['annotations']], 'prefixBytes': manifest['receipt']['bytes'], 'artifactSha256': manifest['artifact']['sha256']}))
