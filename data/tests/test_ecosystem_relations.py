"""Temporal regressions from independent ecosystem review."""
import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from validate_bundle import DATA, read_json
from ecosystem_relations import check_ecosystem_relations


class EcosystemRelationshipTests(unittest.TestCase):
    def setUp(self):
        self.records = read_json(DATA / 'examples/review-bundle.json')['records']

    def kind(self, kind):
        return next(r for r in self.records if r['kind'] == kind)

    def duplicate(self, record):
        other = copy.deepcopy(record)
        other['record_id'] += '-other'
        other['revision_id'] += '-other'
        self.records.append(other)
        return other

    def test_baseline(self):
        self.assertEqual([], check_ecosystem_relations(self.records))

    def test_appointments_must_share_an_instant(self):
        relationship = self.kind('coaching_staff_relationship')['payload']
        staff = next(r for r in self.records if r['record_id'] == relationship['staff_appointment_id'])
        lead = next(r for r in self.records if r['record_id'] == relationship['lead_staff_appointment_id'])
        staff['valid_to'] = lead['valid_from'] = '2025-06-01T00:00:00Z'
        self.assertTrue(any('no common active interval' in e for e in check_ecosystem_relations(self.records)))
        lead['valid_from'] = '2025-05-31T23:59:59Z'
        self.assertEqual([], check_ecosystem_relations(self.records))

    def test_relationship_scope_must_share_the_common_interval(self):
        relationship = self.kind('coaching_staff_relationship')
        team = self.kind('team_programme')
        team['valid_to'] = '2025-06-01T00:00:00Z'
        relationship['payload']['scope'] = {'scope_kind': 'team_programme', 'team_programme_id': team['record_id']}
        for r in self.records:
            if r['kind'] == 'staff_appointment':
                r['valid_from'] = '2025-06-01T00:00:00Z'
                r['payload']['scope'] = {'scope_kind': 'club', 'club_id': r['payload']['club_id']}
        self.assertTrue(any('no common active interval' in e for e in check_ecosystem_relations(self.records)))

    def test_facility_and_department_cycles(self):
        for kind, field in [('facility', 'parent_facility_id'), ('support_department', 'reports_to_department_id')]:
            with self.subTest(kind=kind):
                self.setUp()
                first = self.kind(kind)
                second = self.duplicate(first)
                first['payload'][field] = second['record_id']
                second['payload'][field] = first['record_id']
                self.assertIn(f'{kind}: active hierarchy cycle', check_ecosystem_relations(self.records))

    def test_reporting_cycle_but_not_historical_reversal(self):
        first = self.kind('coaching_staff_relationship')
        first['payload']['relationship_type'] = 'reports_to'
        second = self.duplicate(first)
        p = second['payload']
        p['staff_appointment_id'], p['lead_staff_appointment_id'] = p['lead_staff_appointment_id'], p['staff_appointment_id']
        self.assertIn('coaching_staff_relationship: active reporting cycle', check_ecosystem_relations(self.records))
        first['valid_to'] = second['valid_from'] = '2025-06-01T00:00:00Z'
        self.assertEqual([], check_ecosystem_relations(self.records))

    def test_superseded_open_hierarchy_does_not_create_a_cycle(self):
        for kind, field in [('facility', 'parent_facility_id'),
                            ('support_department', 'reports_to_department_id'),
                            ('coaching_staff_relationship', None)]:
            for provenance in ['synthetic', 'sourced']:
                with self.subTest(kind=kind, provenance=provenance):
                    self.setUp()
                    first = self.kind(kind)
                    second = self.duplicate(first)
                    if field:
                        first['payload'][field] = second['record_id']
                        second['payload'][field] = first['record_id']
                    else:
                        first['payload']['relationship_type'] = 'reports_to'
                        second['payload']['relationship_type'] = 'reports_to'
                        p = second['payload']
                        p['staff_appointment_id'], p['lead_staff_appointment_id'] = p['lead_staff_appointment_id'], p['staff_appointment_id']
                    boundary = '2025-06-01T00:00:00Z'
                    clock = 'synthetic_released_at' if provenance == 'synthetic' else 'recorded_at'
                    first['provenance'] = second['provenance'] = provenance
                    first[clock] = '2025-01-01T00:00:00Z'
                    if field:
                        # The target entity already exists before it acquires
                        # the reverse parent relationship at the boundary.
                        second_root = copy.deepcopy(second)
                        second_root['revision_id'] += '-root'
                        second_root['payload'][field] = None
                        second_root[clock] = first[clock]
                        second['supersedes_revision_id'] = second_root['revision_id']
                        self.records.append(second_root)
                    second[clock] = second['valid_from'] = boundary
                    correction = copy.deepcopy(first)
                    correction['revision_id'] += '-v2'
                    correction['supersedes_revision_id'] = first['revision_id']
                    correction[clock] = correction['valid_to'] = boundary
                    self.records.append(correction)
                    self.assertEqual([], check_ecosystem_relations(self.records))
                    # If the reversal is known before the correction, the open
                    # original and reversal really do coexist in a prior view.
                    second[clock] = '2025-05-31T23:59:59Z'
                    self.assertTrue(any('cycle' in e for e in check_ecosystem_relations(self.records)))


if __name__ == '__main__':
    unittest.main()
