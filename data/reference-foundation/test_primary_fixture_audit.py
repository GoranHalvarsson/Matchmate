"""Synthetic rejection cases plus the persisted complete research ledger."""
import copy
import hashlib
import json
import unittest

import audit_primary_fixtures as audit


class PrimaryFixtureAuditTests(unittest.TestCase):
    def setUp(self):
        # Deliberately synthetic result; this is parser behaviour, not source evidence.
        self.matches = [{'date': '2016-08-13', 'team1': 'Arsenal FC',
                         'team2': 'Chelsea FC', 'score': {'ft': [2, 1]}}]
        self.observation = {
            'url': 'https://www.premierleague.com/en/match/123/arsenal-vs-chelsea/overview',
            'heading': 'Arsenal vs Chelsea | Season 2016/2017 | Premier League',
            'info': ['Premier League', 'Matchweek 1', 'Sat 13 Aug', 'Emirates Stadium'],
            'score': ['2', '1']}
        self.grounds = {'Arsenal FC': 'emirates-stadium'}
        self.aliases = {'Emirates Stadium': 'emirates-stadium', 'Old Trafford': 'old-trafford'}

    def review(self, observation=None):
        return audit.review(observation or self.observation, self.matches, self.grounds, self.aliases)

    def test_actual_header_joins_without_retaining_source_score_or_venue(self):
        row = self.review()
        self.assertEqual(row['fixture_key'], '2016-08-13|Arsenal FC|Chelsea FC')
        self.assertTrue(row['venue_identity_agrees'])
        self.assertFalse(row['historical_venue_name_verified'])
        self.assertNotIn('score', row)
        self.assertNotIn('venue', row)

    def test_wrong_season_rejected(self):
        self.observation['heading'] = self.observation['heading'].replace('2016/2017', '2015/2016')
        with self.assertRaisesRegex(ValueError, 'explicit season'):
            self.review()

    def test_actual_date_not_round_label_controls_join(self):
        self.observation['info'][1] = 'Matchweek 27'
        self.assertTrue(self.review()['played_date_agrees'])
        self.observation['info'][2] = 'Sun 14 Aug'
        with self.assertRaisesRegex(ValueError, 'played date disagrees'):
            self.review()

    def test_wrong_score_rejected(self):
        self.observation['score'] = ['1', '2']
        with self.assertRaisesRegex(ValueError, 'completed result disagrees'):
            self.review()

    def test_blank_or_unknown_venue_cannot_fall_back_to_home_ground(self):
        for label in ('', 'Unknown ground'):
            with self.subTest(label=label):
                self.observation['info'][3] = label
                with self.assertRaisesRegex(ValueError, 'no usual-home fallback'):
                    self.review()

    def test_explicit_different_venue_requires_conflict_review(self):
        self.observation['info'][3] = 'Old Trafford'
        with self.assertRaisesRegex(ValueError, 'venue conflicts'):
            self.review()

    def test_stale_header_cannot_be_attached_to_another_source_url(self):
        self.observation['url'] = self.observation['url'].replace('arsenal-vs-chelsea', 'chelsea-vs-arsenal')
        with self.assertRaisesRegex(ValueError, 'rendered fixture heading disagree'):
            self.review()

    def test_duplicate_source_id_rejected_even_with_changed_slug(self):
        other = {'date': '2016-08-14', 'team1': 'Chelsea FC', 'team2': 'Arsenal FC', 'score': {'ft': [1, 0]}}
        row = self.review()
        duplicate = copy.deepcopy(row)
        duplicate['fixture_key'] = audit.key(other)
        duplicate['source_url'] = row['source_url'].replace('arsenal-vs-chelsea', 'chelsea-vs-arsenal')
        with self.assertRaisesRegex(ValueError, 'source match reused'):
            audit.validate([row, duplicate], self.matches + [other])

    def test_provenance_and_historical_name_boundaries_required(self):
        row = self.review()
        for field, value in [('date_basis', ''), ('locator', ''), ('historical_venue_name_verified', True)]:
            changed = {**row, field: value}
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'provenance or historical-name'):
                audit.validate([changed], self.matches)

    def test_complete_persisted_ledger_and_missing_record_detection(self):
        matches = json.loads(audit.SEASON.read_text(encoding='utf-8'))['matches']
        data = json.loads(audit.LEDGER.read_text(encoding='utf-8'))
        refs = json.loads((audit.ROOT / 'primary-fixture-source-links-2016-17.json').read_text(encoding='utf-8'))
        digest = hashlib.sha256(audit.SEASON.read_bytes()).hexdigest()
        self.assertEqual(audit.validate_document(data, matches, refs, digest), 380)
        changed = copy.deepcopy(data)
        changed['records'].pop()
        with self.assertRaisesRegex(ValueError, 'top-level counts'):
            audit.validate_document(changed, matches, refs, digest)
        changed['verified'] -= 1
        changed['remaining'] += 1
        with self.assertRaisesRegex(ValueError, 'remaining key set'):
            audit.validate_document(changed, matches, refs, digest)


if __name__ == '__main__':
    unittest.main()
