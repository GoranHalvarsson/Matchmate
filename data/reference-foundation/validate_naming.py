"""Validate bounded naming research, not executable historical name intervals."""
import json
import re
import hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def validate(data=None):
    data = data or json.loads((ROOT / 'stadium-naming-review.json').read_text(encoding='utf-8'))
    grounds = {r['ground_id'] for r in json.loads((ROOT / 'grounds.json').read_text(encoding='utf-8'))}
    memberships = {r['membership_id']: r for r in json.loads((ROOT / 'membership-ground-review.json').read_text(encoding='utf-8'))}
    evidence = {r['evidence_id']: r for r in data['evidence']}
    assert len(evidence) == len(data['evidence']), 'duplicate naming evidence'
    for e in evidence.values():
        assert e['ground_id'] in grounds and e['name'].strip()
        assert e['source_url'].startswith('https://') and e['locator'] and e['basis']
        assert e['observed_at'] == '2026-10-17' and e['known_then'] is False
        assert e['effective_to'] is None, 'inferred naming interval end'
        assert e['name_kind'] in ['commercial', 'traditional', 'official_unspecified', 'directory_label']
        assert e['evidence_kind'] in ['dated_usage', 'season_directory', 'current_usage', 'retrospective_usage', 'naming_event', 'planned_usage']
        assert set(e['applicable_seasons']) <= {'2015/16', '2016/17', '2026/27'}
        if e['effective_from'] is not None:
            assert e['evidence_kind'] == 'naming_event', 'usage date is not effective date'
            pattern = {'day': r'\d{4}-\d{2}-\d{2}', 'month': r'\d{4}-\d{2}', 'year': r'\d{4}'}[e['effective_precision']]
            assert re.fullmatch(pattern, e['effective_from']), 'naming precision overstated'
        else:
            assert e['effective_precision'] is None
    reviews = data['ground_reviews']
    assert len(reviews) == len(grounds) and {r['ground_id'] for r in reviews} == grounds, 'ground review coverage'
    for r in reviews:
        assert r['research_note'] and r['remaining_gap'] and r['complete_intervals'] is False
        assert r['status'] in ['primary_name_evidence', 'researched_gap']
        assert bool(r['evidence_ids']) == (r['status'] == 'primary_name_evidence')
        assert all(evidence[i]['ground_id'] == r['ground_id'] for i in r['evidence_ids'])
    contexts = data['season_contexts']
    assert len(contexts) == len(memberships) and {r['membership_id'] for r in contexts} == set(memberships), 'season context coverage'
    for r in contexts:
        m = memberships[r['membership_id']]
        assert r['ground_id'] == m['ground_id'] and r['season'] == m['season']
        assert r['status'] in ['supported_usage', 'researched_gap']
        assert bool(r['evidence_ids']) == (r['status'] == 'supported_usage')
        for i in r['evidence_ids']:
            assert evidence[i]['ground_id'] == r['ground_id'], 'wrong naming ground'
            assert r['season'] in evidence[i]['applicable_seasons'], 'naming evidence not reviewed for this season'
        assert r['whole_season_interval_verified'] is False
    counts = {'grounds': len(reviews), 'primary_ground_reviews': sum(bool(r['evidence_ids']) for r in reviews), 'season_contexts': len(contexts), 'supported_season_contexts': sum(bool(r['evidence_ids']) for r in contexts), 'evidence_records': len(evidence)}
    assert data['counts'] == counts, 'stale naming counts'
    outcomes = data.get('research_outcomes', [])
    assert len(outcomes) == len(grounds) and {r['ground_id'] for r in outcomes} == grounds, 'ground-specific research outcomes required'
    for r in outcomes:
        assert r['outcome'] and r['checked_scope'] and r['checked_evidence_ids']
        assert all(evidence[i]['ground_id'] == r['ground_id'] for i in r['checked_evidence_ids'])
    return counts

def validate_periods(period_data=None, review_data=None):
    review_data = review_data or json.loads((ROOT / 'stadium-naming-review.json').read_text(encoding='utf-8'))
    period_data = period_data or json.loads((ROOT / 'stadium-naming-periods.json').read_text(encoding='utf-8'))
    evidence = {e['evidence_id']: e for e in review_data['evidence']}
    periods = period_data['periods']
    assert len(periods) == len({p['period_id'] for p in periods}), 'duplicate naming period'
    for p in periods:
        assert p['status'] in ['source_asserted_period', 'usage_observations', 'effective_start_only']
        assert p['scope'] in ['commercial', 'traditional', 'competition_clean', 'unspecified']
        assert 'overlap_allowed' not in p, 'naming scopes are not unrestricted overlap permission'
        assert p['limitation'] and p['primary_evidence_ids']
        for i in p['primary_evidence_ids']:
            assert evidence[i]['ground_id'] == p['ground_id'] and evidence[i]['name'] == p['name'], 'period identity mismatch'
        for key in ['start', 'end', 'asserted_through']:
            if key not in p: continue
            b = p[key]
            assert b['inclusivity'] in ['inclusive', 'exclusive', 'unspecified']
            if b['value'] is None:
                assert b['precision'] is None and b['semantics'] == 'unknown' and not b['evidence_ids'], 'unknown boundary must remain unknown'
            else:
                assert set(b['evidence_ids']) <= set(p['primary_evidence_ids']) and b['evidence_ids']
                pattern = {'day': r'\d{4}-\d{2}-\d{2}', 'month': r'\d{4}-\d{2}', 'year': r'\d{4}'}[b['precision']]
                assert re.fullmatch(pattern, b['value']), 'period boundary exceeds source precision'
        if p['status'] == 'effective_start_only':
            assert p['end']['value'] is None, 'start-only assertion cannot gain actual end'
            assert any(evidence[i]['evidence_kind'] == 'naming_event' and evidence[i]['effective_from'] == p['start']['value'] and evidence[i]['effective_precision'] == p['start']['precision'] for i in p['primary_evidence_ids']), 'effective start requires matching naming event'
        if p['status'] == 'source_asserted_period':
            assert p.get('asserted_through') and p['asserted_through']['value'], 'continuity requires explicit source assertion checkpoint'
            assert p.get('continuity_basis'), 'separate usage sightings do not establish continuity'
            assertions = [evidence[i].get('period_assertion') for i in p['primary_evidence_ids']]
            assert any(a and all(a[k] == {'value': p[k]['value'], 'precision': p[k]['precision']} for k in ['start', 'asserted_through']) for a in assertions), 'period boundary not supported at source precision'
        if p['status'] == 'usage_observations':
            assert p['start']['value'] is None and p['end']['value'] is None, 'usage observations do not establish effective boundaries'
        planned = p['planned_contract_end']
        if planned:
            assert planned['status'] == 'announced_commitment_not_actual_end' and planned['source_text']
            assert p['end']['value'] is None, 'planned commitment cannot become actual end'
    return {'period_records': len(periods), 'source_asserted_periods': sum(p['status'] == 'source_asserted_period' for p in periods)}

def validate_transition_research(inventory=None, review_data=None):
    inventory = inventory or json.loads((ROOT / 'naming-transition-research.json').read_text(encoding='utf-8'))
    review_data = review_data or json.loads((ROOT / 'stadium-naming-review.json').read_text(encoding='utf-8'))
    evidence = {e['evidence_id']: e for e in review_data['evidence']}
    rows = inventory['transitions']
    assert len(rows) == len({r['ground_id'] for r in rows}), 'duplicate transition chain'
    for r in rows:
        assert r['status'] in ['resolved', 'precision_improved', 'unresolved']
        assert r['source_checks'] and r['remaining_boundary'] and r['selected_season_impact'] and r['next_evidence_needed'] and r['evidence_level']
        assert all(evidence[i]['ground_id'] == r['ground_id'] for i in r['primary_evidence_ids']), 'transition evidence identity mismatch'
        if r['secondary_lead']:
            assert 'not_promoted' in r['secondary_lead']['status'] or 'not_primary_confirmation' in r['secondary_lead']['status']
        if r['exact_effective_day_newly_resolved']:
            assert any(evidence[i]['evidence_kind'] == 'naming_event' and evidence[i]['effective_precision'] == 'day' for i in r['primary_evidence_ids']), 'exact effective day requires primary naming-event evidence'
        if r['status'] == 'precision_improved': assert r['primary_evidence_ids'], 'improvement requires primary evidence'
    counts = {'chains_checked': len(rows), **{s: sum(r['status'] == s for r in rows) for s in ['precision_improved', 'resolved', 'unresolved']}}
    assert inventory['counts'] == counts, 'stale transition counts'
    return counts

def validate_original_research(inventory=None, review_data=None, period_data=None):
    inventory = inventory or json.loads((ROOT / 'original-announcement-research.json').read_text(encoding='utf-8'))
    review_data = review_data or json.loads((ROOT / 'stadium-naming-review.json').read_text(encoding='utf-8'))
    period_data = period_data or json.loads((ROOT / 'stadium-naming-periods.json').read_text(encoding='utf-8'))
    evidence = {e['evidence_id']: e for e in review_data['evidence']}
    for filename, obj in [('stadium-naming-review.json', review_data), ('stadium-naming-periods.json', period_data)]:
        c = inventory['prefix_preservation'][filename]
        payload = json.dumps(obj[c['key']][:c['prefix_count']], ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()
        assert hashlib.sha256(payload).hexdigest() == c['canonical_sha256'], 'prior record prefix changed'
    rows = inventory['chains']
    assert len(rows) == len({r['ground_id'] for r in rows}) == inventory['counts']['chains_checked']
    for r in rows:
        assert r['attempts'] and r['outcome']
        assert all(a['target_url'].startswith('https://') and a['result'] and a['inspection_note'] for a in r['attempts'])
        assert all(evidence[i]['ground_id'] == r['ground_id'] for i in r['new_evidence_ids'])
        if r['original_announcement_newly_recovered']:
            assert any(evidence[i].get('source_type') == 'original_announcement' for i in r['new_evidence_ids']), 'retrospective is not an original announcement'
    assert sum(r['original_announcement_newly_recovered'] for r in rows) == inventory['counts']['new_original_announcements_recovered']
    assert sum(len(r['new_evidence_ids']) for r in rows) == inventory['counts']['new_primary_notes']
    return inventory['counts']

if __name__ == '__main__':
    print(json.dumps(validate(), indent=2))
    print(json.dumps(validate_periods(), indent=2))
    print(json.dumps(validate_transition_research(), indent=2))
    print(json.dumps(validate_original_research(), indent=2))
