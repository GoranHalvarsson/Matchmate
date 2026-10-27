"""Offline checks and tiny research projections, never application input or an API.

Default checks use the JSON and existing prior-research dependencies only.
--verify-evidence additionally checks ignored local screenshots/capture receipts.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re

HERE = Path(__file__).resolve().parent
FILE = HERE / 'brentford-chelsea-2026-09-18-first-goal.json'
URL = 'https://www.brentfordfc.com/en/video/0_2hoy7vw7?provider=stream'
FIXTURE = 'premier-league:2026-27:2026-09-18:brentford:chelsea'
TIMES = {'E01': 3717.22, 'E02': 3707.22, **{f'E{i:02}': 3705+i for i in range(3, 16)}, 'E16': 3730}
CLOCKS = {'E01': '60:24', **{f'E{i:02}': f'60:{i+12:02}' for i in range(2, 16)}, 'E16': '60:37'}
LEGIBLE = {'E01': 19, 'E02': 19, 'E03': 19, 'E07': 24, 'E14': 19}
LANDMARKS = {'E01': 'goalmouth', 'E02': 'goalmouth', 'E03': 'goalmouth', 'E07': 'corner_boundary'}
KINDS = {'goalmouth_cluster', 'legible_jersey', 'obscured_restart', 'corner_setup', 'crowded_wide_goalmouth', 'goalmouth_reactions', 'legible_jersey_reaction', 'anonymous_celebration', 'first_captured_one_nil'}
NAMED_KINDS = {'legible_jersey', 'legible_jersey_reaction'}
POLICY = {'observations': 'last_supporting_capture_retrospective_projection', 'positions': 'exact_capture_only_no_hold_or_interpolation', 'reports': 'static_retrospective_never_cursor_released', 'liveKnownThen': False, 'measuredTracking': False}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def exact(obj, keys, label):
    require(isinstance(obj, dict) and set(obj) == set(keys.split()), label+' field allowlist')


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def text(value, label, maximum=600):
    require(isinstance(value, str) and 0 < len(value) <= maximum, label)


def file_ref(record, path, label, read=False):
    exact(record, 'file bytes sha256', label)
    require(record['file'] == path and type(record['bytes']) is int and 0 < record['bytes'] < 10_000_000, label+' bounded path/bytes')
    require(isinstance(record['sha256'], str) and re.fullmatch(r'[0-9a-f]{64}', record['sha256']), label+' SHA256')
    if read:
        body = (HERE/path).read_bytes()
        require(len(body) == record['bytes'] and hashlib.sha256(body).hexdigest() == record['sha256'], label+' byte receipt')


def unique(records, label):
    require(isinstance(records, list) and all(isinstance(r, dict) and isinstance(r.get('id'), str) for r in records), label+' records')
    require(len({r['id'] for r in records}) == len(records), label+' duplicate IDs')
    return {r['id']: r for r in records}


def source_at(obj, path):
    """Resolve only explicit local JSON source locators, never evaluate expressions."""
    require(isinstance(path, str) and re.fullmatch(r'\$(?:\.[A-Za-z_]+|\[\d+\])+', path), 'source JSON locator')
    for name, index in re.findall(r'\.([A-Za-z_]+)|\[(\d+)\]', path):
        obj = obj[name] if name else obj[int(index)]
    return obj


def validate(pack):
    exact(pack, 'schemaVersion kind fixture source passage dependencies policy evidencePoints identityJoins retrospectiveReportClaims videoObservations coarsePositionEstimates cameraChangeBounds samplingGaps unknowns limits', 'package')
    require(type(pack['schemaVersion']) is int and pack['schemaVersion'] == 1 and pack['kind'] == 'local_manual_annotations_not_application_input', 'package version/purpose')
    require(pack['fixture'] == {'id': FIXTURE, 'officialMatchId': 2645236, 'competition': 'Premier League', 'date': '2026-09-18', 'homeId': 'brentford', 'awayId': 'chelsea'}, 'fixture identity')
    exact(pack['source'], 'videoUrl reviewedOn method captureReceipt captureHashManifest mediaFilesRetained', 'source')
    require(pack['source']['videoUrl'] == URL and pack['source']['reviewedOn'] == '2026-10-17' and pack['source']['method'] == 'sampled_paused_browser_keyframes' and pack['source']['mediaFilesRetained'] is False, 'source scope')
    file_ref(pack['source']['captureReceipt'], '../../output/premier-league-first-goal/evidence/capture-receipt.json', 'capture receipt')
    file_ref(pack['source']['captureHashManifest'], '../../output/premier-league-first-goal/evidence-hashes.json', 'capture hash manifest')
    require(pack['passage'] == {'clipStart': 3707.22, 'clipEnd': 3730, 'displayMatchClockStart': '60:14', 'displayMatchClockEnd': '60:37', 'sampleCount': 16, 'continuousReview': False, 'clockMapping': 'separate_DOM_clip_time_and_displayed_second_no_global_fit'}, 'passage clocks/sampling scope')
    require(pack['policy'] == POLICY and pack['policy']['liveKnownThen'] is False and pack['policy']['measuredTracking'] is False and pack['passage']['continuousReview'] is False, 'conservative projection policy')
    exact(pack['dependencies'], 'researchPack sourceRegistry', 'dependencies')
    for kind, filename in [('researchPack', 'brentford-chelsea-2026-09-18.json'), ('sourceRegistry', 'sources.json')]:
        file_ref(pack['dependencies'][kind], '../match-enrichment/'+filename, 'prior '+kind, read=True)
    research = json.loads((HERE/pack['dependencies']['researchPack']['file']).read_text(encoding='utf-8'))
    registry = json.loads((HERE/pack['dependencies']['sourceRegistry']['file']).read_text(encoding='utf-8'))
    source_ids = {s['id'] for s in registry}
    evidence = unique(pack['evidencePoints'], 'evidence')
    require(set(evidence) == set(TIMES), 'approved E01-E16 evidence only')
    require([e['clipSeconds'] for e in pack['evidencePoints']] == sorted(TIMES.values()), 'chronological clip order')
    for ident, e in evidence.items():
        exact(e, 'id sourceUrl clipSeconds displayMatchClock displayClockPrecision displayedScore image capture legibleJerseys positionLandmark', 'evidence '+ident)
        require(e['sourceUrl'] == URL and finite(e['clipSeconds']) and e['clipSeconds'] == TIMES[ident], 'exact DOM clip timestamp')
        require(e['displayMatchClock'] == CLOCKS[ident] and e['displayClockPrecision'] == 'second', 'source displayed clock/precision')
        require(isinstance(e['displayedScore'], list) and all(type(n) is int and 0 <= n <= 99 for n in e['displayedScore']) and e['displayedScore'] == ([1, 0] if ident == 'E16' else [0, 0]), 'captured scoreboard, not goal time')
        suffix = str(TIMES[ident])
        file_ref(e['image'], f'../../output/premier-league-first-goal/evidence/{ident}-t{suffix}.jpg', 'image '+ident)
        exact(e['capture'], 'before after', 'capture stability')
        for state in e['capture'].values():
            exact(state, 'currentTime paused readyState', 'paused DOM state')
            require(finite(state['currentTime']) and state['currentTime'] == e['clipSeconds'] and state['paused'] is True and type(state['readyState']) is int and state['readyState'] == 4, 'same paused ready DOM timestamp')
        expected = [{'teamId': 'brentford', 'shirtNumber': LEGIBLE[ident]}] if ident in LEGIBLE else []
        require(e['legibleJerseys'] == expected, 'reviewed same-capture jersey legibility')
        require(e['positionLandmark'] == LANDMARKS.get(ident), 'reviewed visible position geometry')
    joins = unique(pack['identityJoins'], 'identity joins')
    require(set(joins) == {'I19', 'I24'}, 'bounded identity joins')
    for join in joins.values():
        exact(join, 'id playerId teamId shirtNumber publicName jerseySourcePath nameSourcePath basis faceRecognition', 'identity join')
        require(join['basis'] == 'same_capture_legible_team_and_jersey_plus_prior_source_pack' and join['faceRecognition'] is False, 'identity method')
        row = source_at(research, join['jerseySourcePath'])
        name = source_at(research, join['nameSourcePath'])
        require((join['id'], join['jerseySourcePath'], join['nameSourcePath']) in [('I19', '$.lineup_statistics_audit.lineup_rows[7]', '$.players[28].identity_resolution'), ('I24', '$.lineup_statistics_audit.lineup_rows[8]', '$.players[27].identity_resolution')], 'approved source join locators')
        require(join['playerId'] == row['player'] and join['teamId'] == row['club_id'] and type(join['shirtNumber']) is int and join['shirtNumber'] == row['shirt_number'] and join['publicName'] == name['public_name'], 'prior source jersey/name join')
        require(row['evidence']['source'] in source_ids and name['evidence']['source'] in source_ids, 'registered identity sources')
    reports = unique(pack['retrospectiveReportClaims'], 'report claims')
    require(set(reports) == {'R01', 'R02'}, 'bounded report claims')
    common = 'id kind playerId rawMinute seconds precision sourcePath sourceId sourceLocator cursorEligible availableAtClipSeconds'
    for r in reports.values():
        exact(r, common+(' bodyPart location sequence assistPlayerId assistShirtNumber assistSourceLocator' if r['id'] == 'R02' else ''), 'static report')
        require(r['cursorEligible'] is False and r['availableAtClipSeconds'] is None, 'report claim never cursor released')
        require(r['rawMinute'] == '61' and r['seconds'] is None and r['precision'] == 'reported_minute', 'raw reported minute, no invented exact goal time')
        expected = ('reported_goal', '$.goals[0]') if r['id'] == 'R01' else ('reported_finish_and_assist', '$.timeline_audit.goals[0]')
        require((r['kind'], r['sourcePath']) == expected, 'report claim source locator')
        source = source_at(research, r['sourcePath'])
        require(r['playerId'] == source['player'] and r['sourceId'] == source['evidence']['source'] and r['sourceId'] in source_ids and r['sourceLocator'] == source['evidence']['locator'] and r['rawMinute'] == source['time']['printed'] and source['time']['seconds'] is None, 'prior report claim evidence')
        if r['id'] == 'R02':
            attrs, assist = source['finish_attributes'], source['formal_assist']
            require((r['bodyPart'], r['location'], r['sequence']) == (attrs['body_part'], attrs['source_location_description'], attrs['source_sequence']) and r['assistPlayerId'] == assist['player'] and r['assistSourceLocator'] == assist['evidence']['locator'] and r['assistShirtNumber'] == research['lineup_statistics_audit']['lineup_rows'][2]['shirt_number'] == 44, 'source-reported finish/assist only')
    observations = unique(pack['videoObservations'], 'video observations')
    require(len(observations) == 12 and set().union(*(set(o['evidenceIds']) for o in observations.values())) == set(evidence), 'bounded observations cover all sampled evidence')
    for o in observations.values():
        exact(o, 'id kind evidenceIds availableAtClipSeconds identityJoinId note', 'video observation')
        refs = o['evidenceIds']
        require(o['kind'] in KINDS and isinstance(refs, list) and refs and len(set(refs)) == len(refs) and all(r in evidence for r in refs), 'supported video observation references/category')
        require(finite(o['availableAtClipSeconds']) and o['availableAtClipSeconds'] == max(evidence[r]['clipSeconds'] for r in refs), 'last supporting capture availability')
        text(o['note'], 'review note')
        if o['kind'] in NAMED_KINDS:
            require(len(refs) == 1 and o['identityJoinId'] in joins, 'named observation same-capture join')
            join = joins[o['identityJoinId']]
            require({'teamId': join['teamId'], 'shirtNumber': join['shirtNumber']} in evidence[refs[0]]['legibleJerseys'], 'named actor requires legible jersey in same capture')
        else:
            require(o['identityJoinId'] is None, 'anonymous observation cannot inherit identity')
        if o['kind'] == 'corner_setup':
            require(refs == ['E07', 'E08', 'E09'], 'corner setup only, no observed kick contact')
        if o['kind'] == 'first_captured_one_nil':
            require(refs == ['E16'], 'first captured score is not goal time')
        if o['kind'] == 'anonymous_celebration':
            require(refs == ['E15'], 'anonymous E15 cannot inherit cross-cut identity')
    positions = unique(pack['coarsePositionEstimates'], 'point estimates')
    require(len(positions) == 4, 'bounded point estimates')
    point_keys = set()
    for p in positions.values():
        exact(p, 'id identityJoinId evidenceId clipSeconds zone method precision uncertainty availability', 'point estimate')
        require(p['identityJoinId'] in joins and p['evidenceId'] in evidence, 'point identity/evidence')
        e, join = evidence[p['evidenceId']], joins[p['identityJoinId']]
        require({'teamId': join['teamId'], 'shirtNumber': join['shirtNumber']} in e['legibleJerseys'], 'named position needs same-capture legible jersey')
        zone = {'goalmouth': 'near_goalmouth_cluster', 'corner_boundary': 'corner_boundary'}.get(e['positionLandmark'])
        require(zone is not None and p['zone'] == zone, 'position needs supported visible landmark')
        require(finite(p['clipSeconds']) and p['clipSeconds'] == e['clipSeconds'] and p['method'] == 'visible_pitch_landmark_coarse_zone' and p['precision'] == 'coarse_zone' and p['availability'] == 'exact_capture_only', 'point-only coarse position, no fake precision')
        text(p['uncertainty'], 'position uncertainty')
        point_keys.add((p['evidenceId'], p['identityJoinId']))
    require(point_keys == {('E02', 'I19'), ('E03', 'I19'), ('E01', 'I19'), ('E07', 'I24')}, 'exact four supported point estimates')
    expected_cuts = [('E03', 'E04', 'camera_change_between_captures'), ('E09', 'E10', 'camera_change_between_captures'), ('E13', 'E14', 'camera_change_between_captures'), ('E15', 'E16', 'group_shot_transition_intervening_shots_unknown')]
    require(pack['cameraChangeBounds'] == [{'afterEvidenceId': a, 'beforeEvidenceId': b, 'clipBounds': [evidence[a]['clipSeconds'], evidence[b]['clipSeconds']], 'kind': kind} for a, b, kind in expected_cuts], 'camera change bounds, not exact cut times')
    require(pack['samplingGaps'] == [{'afterEvidenceId': a['id'], 'beforeEvidenceId': b['id'], 'clipBoundsExclusive': [a['clipSeconds'], b['clipSeconds']], 'movementReviewed': False} for a, b in zip(pack['evidencePoints'], pack['evidencePoints'][1:])], 'every between-capture movement interval remains unreviewed')
    require(isinstance(pack['unknowns'], list) and {u['kind'] for u in pack['unknowns']} == {'exact_goal_or_line_crossing_time', 'assist_or_header_contact_actor', 'kick_contact_time', 'anonymous_e15_identity', 'e14_position', 'other_players_and_unsampled_positions'} and len(pack['unknowns']) == 6, 'explicit bounded unknowns')
    for unknown in pack['unknowns']:
        exact(unknown, 'kind value reason', 'unknown')
        require(unknown['value'] is None, 'unknown cannot acquire invented value')
        text(unknown['reason'], 'unknown reason')
    require(isinstance(pack['limits'], list) and len(pack['limits']) == 7, 'scope limits')
    for limit in pack['limits']:
        text(limit, 'scope limit')
    return pack


def eligible_observations(pack, clip_seconds):
    """Retrospective source observations only; report claims never enter this list."""
    validate(pack)
    if not finite(clip_seconds) or not pack['passage']['clipStart'] <= clip_seconds <= pack['passage']['clipEnd']:
        return []
    evidence = {e['id']: e for e in pack['evidencePoints']}
    joins = {j['id']: j for j in pack['identityJoins']}
    result = []
    for obs in pack['videoObservations']:
        if obs['availableAtClipSeconds'] <= clip_seconds:
            actor = joins.get(obs['identityJoinId'])
            result.append({'id': obs['id'], 'kind': obs['kind'], 'availableAtClipSeconds': obs['availableAtClipSeconds'], 'evidenceIds': list(obs['evidenceIds']), 'displayMatchClocks': [evidence[e]['displayMatchClock'] for e in obs['evidenceIds']], 'actor': None if actor is None else {'playerId': actor['playerId'], 'publicName': actor['publicName'], 'teamId': actor['teamId'], 'shirtNumber': actor['shirtNumber']}})
    return result


def position_at(pack, clip_seconds, player_id):
    """Only a reviewed exact point is a position; no interpolation or carry forward."""
    validate(pack)
    joins = {j['id']: j for j in pack['identityJoins']}
    if finite(clip_seconds):
        for p in pack['coarsePositionEstimates']:
            join = joins[p['identityJoinId']]
            if p['clipSeconds'] == clip_seconds and join['playerId'] == player_id:
                return {'status': 'coarse_visible_estimate', 'playerId': player_id, 'publicName': join['publicName'], 'clipSeconds': p['clipSeconds'], 'evidenceId': p['evidenceId'], 'zone': p['zone'], 'precision': 'coarse_zone', 'uncertainty': p['uncertainty']}
    return {'status': 'unknown', 'playerId': player_id, 'reason': 'No supported point at this exact cursor; no hold, interpolation or identity inference across gaps/cuts.'}


def verify_evidence(pack):
    validate(pack)
    for key in ('captureReceipt', 'captureHashManifest'):
        r = pack['source'][key]
        file_ref(r, r['file'], key, read=True)
    receipt = json.loads((HERE/pack['source']['captureReceipt']['file']).read_text(encoding='utf-8'))
    hashes = json.loads((HERE/pack['source']['captureHashManifest']['file']).read_text(encoding='utf-8'))
    require(receipt['source'] == URL and len(receipt['captures']) == len(hashes) == 16, 'original capture receipt scope')
    by_id = {c['label']: c for c in receipt['captures']}
    by_hash = {h['id']: h for h in hashes}
    require(set(by_id) == set(by_hash) == set(TIMES), 'capture metadata identities')
    for e in pack['evidencePoints']:
        file_ref(e['image'], e['image']['file'], 'screenshot '+e['id'], read=True)
        c, h = by_id[e['id']], by_hash[e['id']]
        require(c['before'] == [e['capture']['before']] and c['after'] == [e['capture']['after']] and Path(c['file']).name == Path(e['image']['file']).name, 'original stable capture metadata')
        require(h == {'id': e['id'], 'clip_seconds': e['clipSeconds'], 'image': Path(e['image']['file']).name, 'bytes': e['image']['bytes'], 'sha256': e['image']['sha256'], 'stable': True}, 'original screenshot hash manifest')
    return True


def load(path=FILE):
    return validate(json.loads(Path(path).read_text(encoding='utf-8')))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify-evidence', action='store_true', help='also check ignored local screenshots and original capture receipts')
    args = parser.parse_args()
    package = load()
    if args.verify_evidence:
        verify_evidence(package)
    print(json.dumps({'kind': package['kind'], 'evidencePoints': len(package['evidencePoints']), 'observations': len(package['videoObservations']), 'pointEstimates': len(package['coarsePositionEstimates']), 'reportClaimsCursorReleased': False, 'localEvidenceVerified': args.verify_evidence}))
