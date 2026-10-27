"""Turn transient rendered-header observations into a research verification ledger.

No network access. Source scores, venue labels and page text are not retained.
"""
import calendar
import base64
import datetime as dt
import gzip
import hashlib
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parent
SEASON = ROOT.parents[1] / 'prototypes/first-experience/assets/seasons/2016-17.json'
LEDGER = ROOT / 'primary-fixture-audit-2016-17.json'
DATE_BASIS = 'displayed weekday/day/month; year derived from explicit 2016/2017 season'
LOCATOR = 'h1; .match-status__score--home/away; .scoreboard-bottom__match-info-item'
REVERSE_NAMES = {'AFC Bournemouth': 'Bournemouth', 'Arsenal FC': 'Arsenal',
                 'Burnley FC': 'Burnley', 'Chelsea FC': 'Chelsea', 'Everton FC': 'Everton',
                 'Liverpool FC': 'Liverpool', 'Middlesbrough FC': 'Middlesbrough',
                 'Southampton FC': 'Southampton', 'Sunderland AFC': 'Sunderland', 'Watford FC': 'Watford'}


def key(match):
    return '|'.join((match['date'], match['team1'], match['team2']))


def official_slug(match):
    return '-vs-'.join(REVERSE_NAMES.get(n, n).lower().replace(' ', '-') for n in
                      (match['team1'], match['team2']))


def check_url(url, slug):
    if not re.fullmatch(r'https://www\.premierleague\.com/en/match/\d+/' + re.escape(slug) + '/overview', url):
        raise ValueError('unrecognized official URL or source fixture mismatch')


def review(observation, matches, ground_by_club, aliases):
    heading = observation['heading']
    parsed = re.fullmatch(r'(.+) vs (.+) \| Season 2016/2017 \| Premier League', heading)
    if not parsed:
        raise ValueError('wrong or missing explicit season/fixture heading')
    expected_slug = '-vs-'.join(n.lower().replace(' ', '-') for n in parsed.groups())
    if not observation['url'].endswith('/' + expected_slug + '/overview'):
        raise ValueError('source URL and rendered fixture heading disagree')
    names = {'Bournemouth': 'AFC Bournemouth', 'Arsenal': 'Arsenal FC',
             'Burnley': 'Burnley FC', 'Chelsea': 'Chelsea FC', 'Everton': 'Everton FC',
             'Liverpool': 'Liverpool FC', 'Middlesbrough': 'Middlesbrough FC',
             'Southampton': 'Southampton FC', 'Sunderland': 'Sunderland AFC', 'Watford': 'Watford FC'}
    home, away = (names.get(n, n) for n in parsed.groups())
    candidates = [m for m in matches if m['team1'] == home and m['team2'] == away]
    if len(candidates) != 1:
        raise ValueError('fixture identity does not uniquely join local season')
    match = candidates[0]
    info = observation['info']
    if len(info) < 4 or info[0] != 'Premier League':
        raise ValueError('missing explicit competition/date/venue header')
    displayed = re.fullmatch(r'(Mon|Tue|Wed|Thu|Fri|Sat|Sun) (\d{1,2}) ([A-Za-z]{3})', info[2])
    if not displayed:
        raise ValueError('unrecognized displayed played date')
    day_name, day, month_name = displayed.groups()
    month = list(calendar.month_abbr).index(month_name)
    date = dt.date(2016 if month >= 8 else 2017, month, int(day))
    if date.isoformat() != match['date'] or date.strftime('%a') != day_name:
        raise ValueError('source played date disagrees with local fixture')
    if [int(s) for s in observation['score']] != match['score']['ft']:
        raise ValueError('completed result disagrees with local fixture')
    venue_id = aliases.get(info[3])
    if venue_id is None:
        raise ValueError('unmapped explicit venue; no usual-home fallback')
    if venue_id != ground_by_club[home]:
        raise ValueError('explicit venue conflicts with existing season ground context')
    url = observation['url']
    if not re.fullmatch(r'https://www\.premierleague\.com/en/match/\d+/[a-z0-9-]+/overview', url):
        raise ValueError('unrecognized observed official match URL')
    return {'fixture_key': key(match), 'source_url': url,
            'observed_at': '2026-10-17', 'status': 'verified_actual_header',
            'identity_agrees': True, 'played_date_agrees': True,
            'completed_score_agrees': True, 'venue_identity_agrees': True,
            'date_basis': DATE_BASIS,
            'locator': LOCATOR,
            'historical_venue_name_verified': False}


def validate(records, matches):
    expected = {key(m): m for m in matches}
    seen = set()
    urls = set()
    for row in records:
        if row['fixture_key'] not in expected or row['fixture_key'] in seen:
            raise ValueError('unknown or duplicate local fixture key')
        check_url(row['source_url'], official_slug(expected[row['fixture_key']]))
        source_id = row['source_url'].split('/')[5]
        if source_id in urls:
            raise ValueError('source match reused for different fixture')
        if row['status'] != 'verified_actual_header' or not all(row.get(f) is True for f in (
                'identity_agrees', 'played_date_agrees', 'completed_score_agrees', 'venue_identity_agrees')):
            raise ValueError('verification flags incomplete')
        if (row.get('observed_at') != '2026-10-17' or row.get('date_basis') != DATE_BASIS
                or row.get('locator') != LOCATOR or row.get('historical_venue_name_verified') is not False):
            raise ValueError('provenance or historical-name boundary invalid')
        if set(row) != {'fixture_key', 'source_url', 'observed_at', 'status', 'identity_agrees',
                        'played_date_agrees', 'completed_score_agrees', 'venue_identity_agrees',
                        'date_basis', 'locator', 'historical_venue_name_verified'}:
            raise ValueError('ledger must retain own verification notes only')
        seen.add(row['fixture_key'])
        urls.add(source_id)
    return len(seen)


def validate_document(data, matches, references, source_sha256):
    count = validate(data['records'], matches)
    expected = {key(m): m for m in matches}
    if (data.get('season') != '2016/17' or data.get('denominator') != len(matches)
            or data.get('verified') != count or data.get('remaining') != len(matches) - count):
        raise ValueError('top-level counts or season invalid')
    if data.get('local_results_sha256') != source_sha256:
        raise ValueError('local source checkpoint changed')
    if len(references) != len(matches) or {r['fixture_key'] for r in references} != set(expected):
        raise ValueError('discovered reference set incomplete or duplicated')
    refs = {}
    for ref in references:
        check_url(ref['source_url'], official_slug(expected[ref['fixture_key']]))
        refs[ref['fixture_key']] = ref['source_url']
    if len(set(refs.values())) != len(matches):
        raise ValueError('duplicate discovered source URL')
    if any(refs[r['fixture_key']] != r['source_url'] for r in data['records']):
        raise ValueError('verified URL was not discovered in official listing')
    missing = sorted(set(expected) - {r['fixture_key'] for r in data['records']})
    if data.get('remaining_fixture_keys') != missing:
        raise ValueError('explicit remaining key set invalid')
    return count


def main():
    sys.stdin.reconfigure(encoding='utf-8')
    matches = json.loads(SEASON.read_text(encoding='utf-8'))['matches']
    source_sha256 = hashlib.sha256(SEASON.read_bytes()).hexdigest()
    if '--validate' in sys.argv:
        data = json.loads(LEDGER.read_text(encoding='utf-8'))
        refs = json.loads((ROOT / 'primary-fixture-source-links-2016-17.json').read_text(encoding='utf-8'))
        count = validate_document(data, matches, refs, source_sha256)
        print(f'Official primary ledger valid: {count}/{len(matches)}; remaining {len(matches)-count}')
        return
    # Membership source names are retained in the existing source mapping.
    memberships = json.loads((ROOT / 'memberships.json').read_text(encoding='utf-8'))
    contexts = json.loads((ROOT / 'membership-ground-review.json').read_text(encoding='utf-8'))
    ground_by_id = {r['club_id']: r['ground_id'] for r in contexts if r['season'] == '2016/17'}
    ground_by_club = {r['source_team_name']: ground_by_id[r['club_id']] for r in memberships if r['season'] == '2016/17'}
    aliases = json.loads((ROOT / 'venue-audit-2016-17.json').read_text(encoding='utf-8'))['venue_aliases']
    aliases.update({'The MKM Stadium': 'mkm-stadium', 'St. Mary\'s Stadium': 'st-mary-s-stadium',
                    'Swansea.com Stadium': 'liberty-stadium', 'bet365 Stadium': 'britannia-stadium',
                    'Riverside Stadium': 'riverside-stadium'})
    incoming = sys.stdin.read()
    if '--gzip-base64' in sys.argv:
        incoming = gzip.decompress(base64.b64decode(incoming)).decode('utf-8')
    payload = json.loads(incoming)
    if '--links' in sys.argv:
        by_slug = {official_slug(m): key(m) for m in matches}
        refs = []
        for url in payload:
            slug = url.rstrip('/').split('/')[-2]
            if slug not in by_slug:
                raise ValueError('source listing link does not join local identity')
            refs.append({'fixture_key': by_slug[slug], 'source_url': url,
                         'discovery': 'Observed completed-result link in rendered official season matchweek listing',
                         'header_verification': 'Not implied by listing discovery; consult primary-fixture-audit-2016-17.json'})
        if len({r['source_url'] for r in refs}) != len(refs):
            raise ValueError('duplicate discovered URL')
        (ROOT / 'primary-fixture-source-links-2016-17.json').write_text(
            json.dumps(refs, indent=2) + '\n', encoding='utf-8')
        print(f'Checkpointed {len(refs)} observed source references; no new header verification')
        return
    data = json.loads(LEDGER.read_text(encoding='utf-8')) if LEDGER.exists() else {
        'season': '2016/17', 'observed_at': '2026-10-17', 'denominator': 380,
        'rights': 'Private research verification only. PL database/product/automated-feed/public/external-AI reuse not cleared.',
        'retention': 'Existing local fixture keys and own agreement/provenance notes only; no copied pages or fixture venue dataset.',
        'records': []}
    for observation in payload:
        row = review(observation, matches, ground_by_club, aliases)
        old = next((r for r in data['records'] if r['fixture_key'] == row['fixture_key']), None)
        if old is not None and old != row:
            raise ValueError('conflicting repeat observation')
        if old is None:
            data['records'].append(row)
    count = validate(data['records'], matches)
    data['records'].sort(key=lambda r: r['fixture_key'])
    data['verified'] = count
    data['remaining'] = 380 - count
    data['local_results_sha256'] = source_sha256
    data['remaining_fixture_keys'] = sorted({key(m) for m in matches} - {r['fixture_key'] for r in data['records']})
    LEDGER.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'Official header verified: {count}/380; remaining {380-count}')


if __name__ == '__main__':
    main()
