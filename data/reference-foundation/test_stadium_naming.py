import json
import unittest
from pathlib import Path
from validate_naming import validate, validate_periods, validate_transition_research, validate_original_research

class NamingReviewTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((Path(__file__).parent / 'stadium-naming-review.json').read_text(encoding='utf-8'))

    def test_complete_review_and_season_context_sets(self):
        self.assertEqual(validate(self.data)['grounds'], 34)
        self.assertEqual(validate(self.data)['supported_season_contexts'], 60)

    def periods(self):
        return json.loads((Path(__file__).parent / 'stadium-naming-periods.json').read_text(encoding='utf-8'))

    def test_period_collection_and_unknown_ends(self):
        self.assertEqual(validate_periods(self.periods(), self.data)['source_asserted_periods'], 3)
        self.assertTrue(all(p['end']['value'] is None for p in self.periods()['periods']))

    def test_planned_commitment_cannot_become_actual_end(self):
        periods = self.periods()
        p = next(p for p in periods['periods'] if p['planned_contract_end'] and p['status'] == 'source_asserted_period')
        p['end'] = dict(value='2026', precision='year', inclusivity='unspecified', semantics='actual_end', evidence_ids=p['primary_evidence_ids'])
        with self.assertRaisesRegex(AssertionError, 'planned commitment cannot become actual end'):
            validate_periods(periods, self.data)

    def test_continuity_cannot_be_inferred_from_match_sighting(self):
        periods = self.periods()
        p = next(p for p in periods['periods'] if p['status'] == 'source_asserted_period')
        p['primary_evidence_ids'] = [next(e['evidence_id'] for e in self.data['evidence'] if e['name'] == p['name'] and e['evidence_kind'] == 'season_directory')]
        for k in ['start','asserted_through']: p[k]['evidence_ids'] = p['primary_evidence_ids']
        with self.assertRaisesRegex(AssertionError, 'period boundary not supported at source precision'):
            validate_periods(periods, self.data)

    def test_year_only_period_cannot_gain_exact_day(self):
        periods = self.periods()
        p = next(p for p in periods['periods'] if p['status'] == 'source_asserted_period')
        p['start'].update(value=p['start']['value']+'-01-01', precision='day')
        with self.assertRaisesRegex(AssertionError, 'period boundary not supported at source precision'):
            validate_periods(periods, self.data)

    def test_transition_inventory_and_secondary_day_promotion(self):
        inventory = json.loads((Path(__file__).parent / 'naming-transition-research.json').read_text(encoding='utf-8'))
        self.assertEqual(validate_transition_research(inventory, self.data)['chains_checked'], 12)
        row = next(r for r in inventory['transitions'] if r['ground_id'] == 'britannia-stadium')
        row['exact_effective_day_newly_resolved'] = True
        with self.assertRaisesRegex(AssertionError, 'exact effective day requires primary naming-event evidence'):
            validate_transition_research(inventory, self.data)

    def test_prior_prefix_hashes_detect_substantive_mutation(self):
        self.assertEqual(validate_original_research()['new_primary_notes'], 4)
        self.data['evidence'][0]['basis'] += ' changed'
        with self.assertRaisesRegex(AssertionError, 'prior record prefix changed'):
            validate_original_research(review_data=self.data)

    def test_retrospective_cannot_become_original_announcement(self):
        inventory = json.loads((Path(__file__).parent / 'original-announcement-research.json').read_text(encoding='utf-8'))
        row = next(r for r in inventory['chains'] if r['ground_id'] == 'liberty-stadium')
        row['original_announcement_newly_recovered'] = True
        with self.assertRaisesRegex(AssertionError, 'retrospective is not an original announcement'):
            validate_original_research(inventory)

    def test_cross_season_name_evidence_rejected(self):
        row = next(r for r in self.data['season_contexts'] if r['membership_id'] == '2016/17|stoke')
        old = next(e for e in self.data['evidence'] if e['name'] == 'Britannia Stadium')
        row['evidence_ids'] = [old['evidence_id']]
        with self.assertRaisesRegex(AssertionError, 'naming evidence not reviewed for this season'):
            validate(self.data)

    def test_usage_cannot_become_effective_date(self):
        e = next(e for e in self.data['evidence'] if e['name'] == 'Britannia Stadium')
        e.update(effective_from='2015-08-09', effective_precision='day')
        with self.assertRaisesRegex(AssertionError, 'usage date is not effective date'):
            validate(self.data)

    def test_year_precision_and_unknown_end_preserved(self):
        e = next(e for e in self.data['evidence'] if e['effective_precision'] == 'year')
        e['effective_from'] += '-01-01'
        with self.assertRaisesRegex(AssertionError, 'naming precision overstated'):
            validate(self.data)
        self.setUp()
        self.data['evidence'][0]['effective_to'] = '2027-01-01'
        with self.assertRaisesRegex(AssertionError, 'inferred naming interval end'):
            validate(self.data)

    def test_historic_names_and_conflict_are_not_current_backfills(self):
        def names(key):
            row = next(r for r in self.data['season_contexts'] if r['membership_id'] == key)
            return {e['name'] for e in self.data['evidence'] if e['evidence_id'] in row['evidence_ids']}
        self.assertIn('Britannia Stadium', names('2015/16|stoke'))
        self.assertNotIn('bet365 Stadium', names('2015/16|stoke'))
        self.assertIn('bet365 Stadium', names('2016/17|stoke'))
        self.assertIn('Liberty Stadium', names('2016/17|swansea'))
        self.assertNotIn('Swansea.com Stadium', names('2016/17|swansea'))
        self.assertIn('KCOM Stadium', names('2016/17|hull'))
        self.assertNotIn('MKM Stadium', names('2016/17|hull'))
        self.assertEqual(self.data['conflicts'][0]['source_label'], 'KC Stadium')
        cbs = next(e for e in self.data['evidence'] if e['effective_from'] == '2021-07-01')
        self.assertIsNone(cbs['publication_date'])
        self.assertEqual(len(cbs['supporting_sources']), 1)

if __name__ == '__main__':
    unittest.main()
