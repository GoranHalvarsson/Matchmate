"""Capped, pinned SkillCorner passage research; raw inputs remain transient.
Default validates the saved aggregate; --fetch explicitly recaptures public inputs.
"""
import argparse
from collections import Counter
import csv
from datetime import datetime, timezone
import hashlib
import io
import json
import math
from pathlib import Path
import urllib.request

HERE = Path(__file__).parent
PIN = '4340d274572876239c154c90bc507a9b3250a656'
BASE = f'SkillCorner/opendata/{PIN}/data/matches/1874553/1874553'
CAP = 20_000_000
START, END = 60.0, 240.0


def require(value, message):
    if not value:
        raise ValueError(message)


def seconds(text):
    parts = [float(x) for x in text.split(':')]
    return sum(x * 60 ** i for i, x in enumerate(reversed(parts)))


def finite_xy(obj):
    return all(isinstance(obj.get(k), (int, float)) and math.isfinite(obj[k]) for k in ('x', 'y'))


def runs(values):
    """Inclusive frame-ID ranges; no claim of missing-clock duration."""
    result = []
    for value in sorted(set(values)):
        if result and value == result[-1][1] + 1:
            result[-1][1] = value
        else:
            result.append([value, value])
    return result


class Reads:
    def __init__(self):
        self.total = 0
        self.receipts = []

    def get(self, suffix, lines=None, media=False):
        prefix = 'https://media.githubusercontent.com/media/' if media else 'https://raw.githubusercontent.com/'
        url = prefix + BASE + suffix
        remaining = CAP - self.total
        require(remaining > 0, 'parser byte budget exhausted')
        with urllib.request.urlopen(url, timeout=40) as response:
            if lines is None:
                body = response.read(remaining + 1)
                require(len(body) <= remaining, 'parser byte budget exceeded')
                scope = 'whole small response'
            else:
                chunks = []
                size = 0
                for _ in range(lines):
                    line = response.readline(remaining - size + 1)
                    require(size + len(line) <= remaining, 'parser byte budget exceeded')
                    require(line.endswith(b'\n'), 'missing/incomplete requested line')
                    chunks.append(line)
                    size += len(line)
                body = b''.join(chunks)
                scope = f'first {lines} complete lines only'
            self.receipts.append({'url': url, 'resolved_url': response.url, 'status': response.status,
                                  'scope': scope, 'bytes_hashed': len(body),
                                  'sha256': hashlib.sha256(body).hexdigest(),
                                  'hash_semantics': 'exact parser-returned bytes; prefix is not whole LFS object'})
        self.total += len(body)
        return body


def tracking_audit(frames, roster, start=START, end=END):
    selected = [f for f in frames if f['period'] == 1 and f['timestamp'] is not None
                and start <= seconds(f['timestamp']) <= end]
    require(selected, 'no selected passage')
    ids = [f['frame'] for f in selected]
    times = [seconds(f['timestamp']) for f in selected]
    counts = Counter(ids)
    missing = sorted(set(range(min(ids), max(ids) + 1)) - set(ids))
    offsets = [round(f['frame'] / 10 - seconds(f['timestamp']), 6) for f in selected]
    detection = Counter()
    ball_detection = Counter()
    player_counts = Counter()
    finite_counts = Counter()
    teams = Counter()
    unknown = set()
    duplicate_players = []
    bad_xy = []
    ball_missing = []
    empty_player_frames = []
    outside = 0
    positions = []
    presence = Counter()
    per_frame_team_counts = Counter()
    for f in selected:
        players = f['player_data']
        if not players:
            empty_player_frames.append(f['frame'])
        player_counts[len(players)] += 1
        finite_counts[sum(finite_xy(p) for p in players)] += 1
        pids = [p['player_id'] for p in players]
        if len(pids) != len(set(pids)):
            duplicate_players.append(f['frame'])
        unknown.update(set(pids) - set(roster))
        team_counts = Counter()
        for p in players:
            presence[p['player_id']] += 1
            detection[str(p['is_detected'])] += 1
            if p['player_id'] in roster:
                team_counts[str(roster[p['player_id']]['team_id'])] += 1
                teams[str(roster[p['player_id']]['team_id'])] += 1
            if finite_xy(p):
                positions.append((p['x'], p['y']))
                outside += abs(p['x']) > 52.5 or abs(p['y']) > 34
            else:
                bad_xy.append({'frame': f['frame'], 'player_id': p['player_id']})
        per_frame_team_counts[json.dumps(dict(sorted(team_counts.items())), sort_keys=True)] += 1
        if not finite_xy(f['ball_data']):
            ball_missing.append(f['frame'])
        else:
            ball_detection[str(f['ball_data']['is_detected'])] += 1
    result = {
        'selected_period': 1, 'requested_clock_seconds_inclusive': [start, end],
        'frames': len(selected), 'first_last_frame': [ids[0], ids[-1]],
        'first_last_clock_seconds': [times[0], times[-1]],
        'missing_expected_frame_ids': missing, 'duplicate_frame_ids': [k for k, n in counts.items() if n > 1],
        'nonmonotonic_frame_transitions': sum(b <= a for a, b in zip(ids, ids[1:])),
        'nonmonotonic_clock_transitions': sum(b <= a for a, b in zip(times, times[1:])),
        'clock_step_seconds_distribution': dict(Counter(str(round(b-a, 6)) for a, b in zip(times, times[1:]))),
        'frame_div_10_minus_clock_seconds_distribution': dict(Counter(str(x) for x in offsets)),
        'player_count_distribution': dict(player_counts), 'finite_player_xy_count_distribution': dict(finite_counts),
        'missing_nonfinite_player_xy': bad_xy, 'unknown_player_ids': sorted(unknown),
        'zero_player_frame_ranges_inclusive': runs(empty_player_frames),
        'zero_player_clock_ranges_seconds_inclusive': [[seconds(next(f['timestamp'] for f in selected if f['frame'] == a)),
                                                       seconds(next(f['timestamp'] for f in selected if f['frame'] == b))]
                                                      for a,b in runs(empty_player_frames)],
        'gap_duration_convention': 'Inclusive first/last empty sample clocks; endpoints span excludes the final 0.1-second slot. Slot count/10 is sampled coverage, not an exact real-world missing-duration claim.',
        'zero_player_sampled_slots': len(empty_player_frames),
        'duplicate_player_id_frames': duplicate_players, 'unique_player_ids': len(presence),
        'player_frames_present': {str(k): v for k, v in sorted(presence.items())},
        'player_observations_by_team': dict(teams),
        'per_frame_roster_team_counts_distribution': dict(per_frame_team_counts),
        'source_identity_continuity': 'source IDs and roster team membership only; not independent identity/accuracy proof',
        'player_detection_flags': dict(detection), 'ball_detection_flags_when_xy_present': dict(ball_detection),
        'ball_missing_xy_frame_ranges_inclusive': runs(ball_missing),
        'outside_nominal_pitch_player_observations': outside,
        'coordinate_range': {'x': [min(p[0] for p in positions), max(p[0] for p in positions)],
                             'y': [min(p[1] for p in positions), max(p[1] for p in positions)]},
    }
    return result, {f['frame']: f for f in selected}


def endpoint(event, edge, frames, roster, frame_field=None):
    frame_id = int(event[frame_field or 'frame_' + edge])
    f = frames.get(frame_id)
    actor_fields = ['player_id', 'player_in_possession_id', 'player_targeted_id']
    joins = {}
    for field in actor_fields:
        if event[field]:
            pid = int(event[field])
            joins[field] = {'id': pid, 'roster_join': pid in roster,
                            'in_endpoint_frame': None if f is None else any(p['player_id'] == pid for p in f['player_data'])}
    return {'frame': frame_id, 'event_clock_seconds': seconds(event['time_' + edge]),
            'tracking_frame_in_passage': f is not None,
            'clock_residual_seconds': None if f is None else round(seconds(event['time_' + edge])-seconds(f['timestamp']), 6),
            'identity_joins': joins}


def event_audit(events, frames, roster):
    lo, hi = min(frames), max(frames)
    relevant = [e for e in events if e['period'] == '1' and int(e['frame_start']) <= hi and int(e['frame_end']) >= lo]
    require(len({e['event_id'] for e in events}) == len(events), 'duplicate source event ID within inspected match prefix')
    audits = []
    for e in relevant:
        audits.append({'event_id': e['event_id'], 'event_type_id': e['event_type_id'], 'event_type': e['event_type'],
                       'event_subtype_id': e['event_subtype_id'], 'event_subtype': e['event_subtype'],
                       'partial_boundary': int(e['frame_start']) < lo or int(e['frame_end']) > hi,
                       'start': endpoint(e, 'start', frames, roster), 'end': endpoint(e, 'end', frames, roster),
                       'physical_start': endpoint(e, 'start', frames, roster, 'frame_physical_start')
                       if e.get('frame_physical_start') else None})
    endpoints = [a[edge] for a in audits for edge in ('start', 'end')]
    joins = [j for x in endpoints for j in x['identity_joins'].values()]
    examples = []
    # First complete event of each category, then first additional targeted pass-like possession.
    for category in sorted({e['event_type'] for e in audits}):
        choices = [e for e in audits if e['event_type'] == category and not e['partial_boundary']]
        if choices:
            examples.append(choices[0])
    for e, a in zip(relevant, audits):
        if len(examples) >= 6:
            break
        if a not in examples and not a['partial_boundary'] and e['event_type'] == 'player_possession' and e['player_targeted_id']:
            examples.append(a)
    for a in examples:
        a['name_evidence'] = {str(j['id']): {'source_printed_public_name': (roster[j['id']]['first_name']+' '+roster[j['id']]['last_name']).strip(),
                                           'team_id': roster[j['id']]['team_id'], 'shirt': roster[j['id']]['number'],
                                           'locator': f"match metadata players[id={j['id']}]"}
                              for j in a['start']['identity_joins'].values() if j['id'] in roster}
    return {
        'prefix_rows': len(events), 'prefix_first_last_start_frame': [int(events[0]['frame_start']), int(events[-1]['frame_start'])],
        'prefix_nonmonotonic_start_transitions': sum(int(b['frame_start']) < int(a['frame_start']) for a,b in zip(events,events[1:])),
        'overlapping_rows': len(audits), 'fully_contained_rows': sum(not a['partial_boundary'] for a in audits),
        'partial_boundary_event_ids': [a['event_id'] for a in audits if a['partial_boundary']],
        'category_counts': dict(Counter(e['event_type'] for e in relevant)),
        'same_start_frame_groups': sum(n > 1 for n in Counter(e['frame_start'] for e in relevant).values()),
        'endpoint_clock_residual_seconds_distribution': dict(Counter(str(x['clock_residual_seconds']) for x in endpoints if x['tracking_frame_in_passage'])),
        'endpoint_residuals_by_edge_and_category': {
            edge: {category: dict(Counter(str(a[edge]['clock_residual_seconds']) for a in audits
                                         if a['event_type'] == category and a[edge]['tracking_frame_in_passage']))
                   for category in sorted({a['event_type'] for a in audits})}
            for edge in ('start','end')},
        'physical_start_comparison': {
            'rows_with_field': sum(a['physical_start'] is not None for a in audits),
            'outside_passage': sum(a['physical_start'] is not None and not a['physical_start']['tracking_frame_in_passage'] for a in audits),
            'clock_residual_seconds_distribution': dict(Counter(str(a['physical_start']['clock_residual_seconds']) for a in audits
                                                               if a['physical_start'] is not None and a['physical_start']['tracking_frame_in_passage'])),
            'original_nonzero_start_residuals_zero_at_physical_start': sum(
                a['start']['clock_residual_seconds'] not in (None,0) and a['physical_start'] is not None
                and a['physical_start']['clock_residual_seconds'] == 0 for a in audits),
            'meaning': 'Measured candidate field basis only, not a universal category rule or correction of source fields. Specification PDF was inaccessible via web tool; semantic intent not independently established.'},
        'largest_nonzero_endpoint_time_residual_examples': sorted([
            {'event_id': a['event_id'], 'event_type': a['event_type'], 'edge': edge,
             'frame': a[edge]['frame'], 'event_clock_seconds': a[edge]['event_clock_seconds'],
             'tracking_clock_seconds': round(a[edge]['event_clock_seconds']-a[edge]['clock_residual_seconds'],6),
             'residual_seconds_event_minus_tracking': a[edge]['clock_residual_seconds'],
             'physical_start': a['physical_start'] if edge == 'start' else None}
            for a in audits for edge in ('start','end')
            if a[edge]['clock_residual_seconds'] not in (None,0)],
            key=lambda x: (-abs(x['residual_seconds_event_minus_tracking']), x['event_id'], x['edge']))[:4],
        'endpoints_outside_selected_passage': sum(not x['tracking_frame_in_passage'] for x in endpoints),
        'unknown_endpoint_roster_ids': sorted({j['id'] for j in joins if not j['roster_join']}),
        'identity_endpoint_checks_where_frame_present': sum(j['in_endpoint_frame'] is not None for j in joins),
        'identity_not_in_present_endpoint_frame': sum(j['in_endpoint_frame'] is False for j in joins),
        'examples_selection': 'first complete event per alphabetical category, then earliest additional targeted player possessions, up to six',
        'examples': examples,
        'limits': 'Only first 1000 CSV rows inspected. Overlap counts are not a certified complete interval/event feed. Same-frame events retained independently by match-local event_id. Targeted player is not proof of receipt or official pass completion. No event/tracking spatial comparison: CSV x/y requires source-specific scaling.'}


def fetch(write=True):
    reads = Reads()
    meta = json.loads(reads.get('_match.json'))
    pointer = reads.get('_tracking_extrapolated.jsonl').decode()
    require(pointer.startswith('version https://git-lfs.github.com/spec/v1'), 'expected LFS pointer')
    events = list(csv.DictReader(io.StringIO(reads.get('_dynamic_events.csv', lines=1001).decode())))
    frames = [json.loads(line) for line in reads.get('_tracking_extrapolated.jsonl', lines=2411, media=True).splitlines()]
    roster = {p['id']: p for p in meta['players']}
    require(len(roster) == len(meta['players']), 'duplicate roster ID')
    tracking, selected = tracking_audit(frames, roster)
    report = {'observed_on': datetime.now(timezone.utc).date().isoformat(), 'pin': PIN, 'match_id': meta['id'],
              'match_date_time': meta['date_time'], 'competition': meta['competition_edition'],
              'selection': 'First-half 01:00–04:00 inclusive, chosen after actual event prefix inspection to cover multiple dynamic categories within the byte cap; not chosen for official goals/incidents.',
              'coordinate_semantics': 'tracking metres, pitch-centre origin; 105x68 metadata. Bounds flags retained, not clipped or automatically classified as errors.',
              'home_team_side': meta['home_team_side'], 'tracking': tracking,
              'events': event_audit(events, selected, roster), 'receipts': reads.receipts,
              'parser_bytes_total': reads.total, 'parser_byte_cap': CAP,
              'raw_retained': False, 'application_imported': False,
              'limits': 'Source-generated broadcast estimates, not tracking ground truth or independent public identity validation. No live/PL feed, whole-match coverage or product dataset selection. Initial null frames lie before this selected interval.'}
    check(report)
    if write:
        (HERE/'passage-summary.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return report


def check(r):
    require(r['pin'] == PIN and r['match_id'] == 1874553, 'source pin/match')
    require(not r['raw_retained'] and not r['application_imported'], 'research boundary')
    require(r['parser_bytes_total'] == sum(x['bytes_hashed'] for x in r['receipts']) <= CAP, 'receipt budget')
    require(all(PIN in x['url'] and len(x['sha256']) == 64 and x['status'] == 200 for x in r['receipts']), 'source receipts')
    t = r['tracking']
    require(t['first_last_clock_seconds'] == [START, END], 'passage boundaries')
    require(not t['duplicate_frame_ids'] and not t['unknown_player_ids'] and not t['duplicate_player_id_frames'], 'frame/identity integrity')
    require(sum(t['player_count_distribution'].values()) == t['frames'] == sum(t['finite_player_xy_count_distribution'].values()), 'per-frame count accounting')
    require(sum(int(k)*v for k,v in t['player_count_distribution'].items()) == sum(t['player_detection_flags'].values()), 'player observation accounting')
    require(t['first_last_frame'][1]-t['first_last_frame'][0]+1 == t['frames']+len(t['missing_expected_frame_ids']), 'expected frame accounting')
    e = r['events']
    require(e['overlapping_rows'] == e['fully_contained_rows'] + len(e['partial_boundary_event_ids']), 'boundary classification')
    require(sum(e['category_counts'].values()) == e['overlapping_rows'], 'event category accounting')
    require(sum(e['endpoint_clock_residual_seconds_distribution'].values()) + e['endpoints_outside_selected_passage'] == 2*e['overlapping_rows'], 'endpoint accounting')
    require(not e['unknown_endpoint_roster_ids'], 'event roster identity')
    require(4 <= len(e['examples']) <= 6, 'bounded event examples')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--fetch', action='store_true')
    parser.add_argument('--no-write', action='store_true', help='recapture without changing saved aggregate')
    args = parser.parse_args()
    report = fetch(write=not args.no_write) if args.fetch else json.loads((HERE/'passage-summary.json').read_text(encoding='utf-8'))
    check(report)
    print(json.dumps({k: report[k] for k in ('tracking', 'events', 'parser_bytes_total')}, ensure_ascii=True, indent=2))
