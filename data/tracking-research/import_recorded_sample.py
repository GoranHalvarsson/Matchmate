"""Import only the approved pinned SkillCorner 01:00-04:00 passage.

No full tracking file, event CSV, video or raw prefix is retained. Default/check
mode is offline; --fetch is an explicit capped public-source read.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import urllib.request

HERE = Path(__file__).parent
OUT = HERE.parents[1] / 'prototypes/first-experience/assets/tracking'
PIN = '4340d274572876239c154c90bc507a9b3250a656'
MATCH = 1874553
CAP = 20_000_000
ROOT = f'https://raw.githubusercontent.com/SkillCorner/opendata/{PIN}/'
BASE = f'data/matches/{MATCH}/{MATCH}'
SAMPLE_FILE = '1874553-60-240.json'
LICENSE_FILE = 'SKILLCORNER-LICENSE.txt'
EXPECTED = {
    'metadata': (33730, 'e6974e402779d8be44e75bf88ef2a3acfd4d025b87b3fc57f518fa826a645ad8'),
    'pointer': (133, 'deb9af1b3d17b7eb59df6296cee4c42532342d87c59d33e381aeb3fd44a5f15b'),
    'tracking-prefix': (4058011, '172e0c1ab68fb515b93fabbdbc99c4d9cb8d7991c71550a82d5d6fd3d9659ff8'),
    'license': (1097, '3384cc66ffd008209759af5e6328677eabded72756fb1f56f4f82e68e9b3c6eb'),
}
URLS = {'metadata': ROOT+BASE+'_match.json', 'pointer': ROOT+BASE+'_tracking_extrapolated.jsonl',
        'tracking-prefix': ROOT.replace('raw.githubusercontent.com/', 'media.githubusercontent.com/media/')+BASE+'_tracking_extrapolated.jsonl',
        'license': ROOT+'LICENSE'}


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(body):
    return hashlib.sha256(body).hexdigest()


def clock_seconds(text):
    hours, minutes, seconds = text.split(':')
    return int(hours)*3600 + int(minutes)*60 + float(seconds)


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def position(row):
    """Retain the source quality flag; invalid coordinates never become zero."""
    return {'x': row.get('x') if finite(row.get('x')) else None,
            'y': row.get('y') if finite(row.get('y')) else None,
            'isDetected': row.get('is_detected'),
            'missingReason': None if finite(row.get('x')) and finite(row.get('y')) else 'missing-or-nonfinite-coordinate'}


def normalize(meta, rows, start=60, end=240):
    frames = []
    seen = set()
    roster = {p['id']: p for p in meta['players']}
    require(len(roster) == len(meta['players']), 'duplicate metadata player ID')
    for row in rows:
        if row.get('period') != 1 or row.get('timestamp') is None:
            continue
        seconds = clock_seconds(row['timestamp'])
        if not start <= seconds <= end:
            continue
        players = []
        ids = set()
        for p in row['player_data']:
            ident = p['player_id']
            require(ident in roster and ident not in ids, 'unknown or duplicate player ID')
            ids.add(ident)
            seen.add(ident)
            players.append({'playerId': ident, **position(p)})
        frames.append({'frameId': row['frame'], 'period': row['period'],
                       'sourceTimestamp': row['timestamp'], 'clock': seconds,
                       'players': players, 'ball': position(row.get('ball_data') or {}),
                       'frameState': 'empty-positions' if not players else 'positions-present'})
    people = []
    for ident in sorted(seen):
        p = roster[ident]
        people.append({'id': ident, 'teamId': p['team_id'], 'number': p.get('number'),
                       'name': (p['first_name'].strip()+' '+p['last_name'].strip()).strip(),
                       'crosswalk': 'source-roster-id', 'sourceLocator': f'players[id={ident}]'})
    return {'schemaVersion': 1, 'source': 'SkillCorner', 'pin': PIN, 'matchId': MATCH,
            'label': 'Recorded tracking replay', 'perspective': 'Retrospective, source-derived',
            'competition': meta['competition_edition']['name'], 'dateTime': meta['date_time'],
            'home': {'id': meta['home_team']['id'], 'name': meta['home_team']['name']},
            'away': {'id': meta['away_team']['id'], 'name': meta['away_team']['name']},
            'pitch': {'length': meta['pitch_length'], 'width': meta['pitch_width'],
                      'units': 'metres', 'origin': 'centre', 'homeTeamSide': meta['home_team_side']},
            'period': 1, 'start': start, 'end': end, 'cadence': 0.1,
            'players': people, 'frames': frames,
            'eventPolicy': 'Dynamic events withheld: timing and per-field known-at-time eligibility are unresolved.',
            'limits': 'A-League recorded technical sample, not live or Premier League. Broadcast-derived positions and source estimates are not ground truth. No score, goals, tactics, speed, distance or completed-match statistics supplied.'}


def validate(sample):
    require(sample['pin'] == PIN and sample['matchId'] == MATCH, 'sample source identity')
    require(sample['start'] == 60 and sample['end'] == 240 and sample['cadence'] == 0.1, 'approved clock bounds')
    frames = sample['frames']
    require(len(frames) == 1801 and len(sample['players']) == 22, 'approved frame/roster counts')
    require(len({p['id'] for p in sample['players']}) == 22, 'unique roster')
    roster = {p['id'] for p in sample['players']}
    empty = []
    detected = estimated = 0
    for index, frame in enumerate(frames):
        require(frame['frameId'] == 610+index and frame['period'] == 1, 'raw frame identity/order')
        require(abs(frame['clock']-(60+index/10)) < 1e-8, 'source clock cadence')
        require(clock_seconds(frame['sourceTimestamp']) == frame['clock'], 'preserved source clock')
        ids = [p['playerId'] for p in frame['players']]
        require(len(ids) in (0, 22) and len(set(ids)) == len(ids), 'sample coverage counts')
        if not ids:
            empty.append(frame['frameId'])
        else:
            require(set(ids) == roster, 'sample roster continuity')
        for p in frame['players']:
            require(finite(p['x']) and finite(p['y']), 'present sample coordinate')
            detected += p['isDetected'] is True
            estimated += p['isDetected'] is False
    require(empty == list(range(1769,1909))+list(range(1998,2206)), 'approved empty-position spans')
    require(detected == 15974 and estimated == 15992, 'source quality counts')
    require(sample['pitch']['length'] == 105 and sample['pitch']['width'] == 68 and sample['pitch']['origin'] == 'centre', 'source geometry')


def check(output=OUT):
    manifest = json.loads((output/'manifest.json').read_text(encoding='utf-8'))
    require(manifest['pin'] == PIN and manifest['matchId'] == MATCH, 'manifest identity')
    for key, (size, sha) in EXPECTED.items():
        receipt = next((r for r in manifest['receipts'] if r['kind'] == key), {})
        require(receipt.get('bytes') == size and receipt.get('sha256') == sha, 'pinned receipt '+key)
        require(receipt.get('url') == URLS[key] and receipt.get('resolvedUrl') == URLS[key] and receipt.get('status') == 200, 'pinned receipt URL/status '+key)
    require(len(manifest['receipts']) == 4 and sum(r['bytes'] for r in manifest['receipts']) == manifest['parserBytes'] <= CAP, 'parser byte accounting/cap')
    require({r['file'] for r in manifest['artifacts']} == {SAMPLE_FILE, LICENSE_FILE}, 'bounded artifact allowlist')
    for record in manifest['artifacts']:
        body = (output/record['file']).read_bytes()
        require(len(body) == record['bytes'] and digest(body) == record['sha256'], 'artifact hash '+record['file'])
    require(digest((output/LICENSE_FILE).read_bytes()) == EXPECTED['license'][1], 'full pinned MIT notice')
    sample = json.loads((output/SAMPLE_FILE).read_text(encoding='utf-8'))
    validate(sample)
    return sample, manifest


def fetch(output=OUT):
    total = 0
    receipts = []
    def read(kind, url, lines=None):
        nonlocal total
        with urllib.request.urlopen(url, timeout=40) as response:
            require(response.status == 200, 'source status')
            if lines:
                parts = []
                size = 0
                for _ in range(lines):
                    line = response.readline(CAP-total-size+1)
                    require(line.endswith(b'\n') and total+size+len(line) <= CAP, 'complete capped prefix line')
                    parts.append(line)
                    size += len(line)
                body = b''.join(parts)
            else:
                body = response.read(CAP-total+1)
            require(total+len(body) <= CAP, 'parser byte cap')
            require((len(body), digest(body)) == EXPECTED[kind], 'source receipt mismatch: '+kind)
            total += len(body)
            receipts.append({'kind': kind, 'url': url, 'resolvedUrl': response.url, 'status': response.status,
                             'scope': f'first {lines} complete lines only' if lines else 'whole small response',
                             'bytes': len(body), 'sha256': digest(body)})
            return body
    meta = json.loads(read('metadata', ROOT+BASE+'_match.json'))
    pointer = read('pointer', ROOT+BASE+'_tracking_extrapolated.jsonl')
    require(pointer.startswith(b'version https://git-lfs.github.com/spec/v1'), 'genuine LFS pointer')
    prefix = read('tracking-prefix', ROOT.replace('raw.githubusercontent.com/', 'media.githubusercontent.com/media/')+BASE+'_tracking_extrapolated.jsonl', 2411)
    notice = read('license', ROOT+'LICENSE')
    sample = normalize(meta, [json.loads(line) for line in prefix.splitlines()])
    validate(sample)
    body = (json.dumps(sample, ensure_ascii=False, separators=(',', ':'), allow_nan=False)+'\n').encode('utf-8')
    manifest = {'source': 'SkillCorner/opendata', 'pin': PIN, 'matchId': MATCH,
                'retrievedAt': datetime.now(timezone.utc).isoformat(), 'parserBytes': total, 'parserByteCap': CAP,
                'selection': 'First half 01:00-04:00 inclusive; 1801 samples, 22 source-roster identities.',
                'rawPrefixRetained': False, 'fullTrackingObjectDownloaded': False, 'eventsImported': False,
                'hashSemantics': 'Receipts hash exact parser-returned bytes. Tracking receipt is a prefix, not the complete LFS object.',
                'license': {'name': 'MIT', 'copyright': 'Copyright (c) 2020 SkillCorner. https://skillcorner.com', 'file': LICENSE_FILE},
                'receipts': receipts, 'artifacts': [{'file': SAMPLE_FILE, 'bytes': len(body), 'sha256': digest(body)},
                                                 {'file': LICENSE_FILE, 'bytes': len(notice), 'sha256': digest(notice)}],
                'scope': 'Authorized local unpublished recorded technical sample only. Premier League/live sourcing and hackathon submission clearance remain separate.'}
    output.mkdir(parents=True, exist_ok=True)
    (output/SAMPLE_FILE).write_bytes(body)
    (output/LICENSE_FILE).write_bytes(notice)
    (output/'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return check(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--fetch', action='store_true')
    parser.add_argument('--check', action='store_true', help='offline check (the default)')
    args = parser.parse_args()
    sample, manifest = fetch() if args.fetch else check()
    print(json.dumps({'frames': len(sample['frames']), 'players': len(sample['players']),
                      'parserBytes': manifest['parserBytes'], 'sampleSha256': manifest['artifacts'][0]['sha256']}))
