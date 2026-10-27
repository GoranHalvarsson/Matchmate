"""Negative cases guard against accepting plausible-looking but invalid records."""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from validate_bundle import DATA, load_schema, read_json, validate


class ContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.example = read_json(DATA / 'examples/review-bundle.json')

    def setUp(self):
        self.bundle = copy.deepcopy(self.example)

    def record(self, revision):
        return next(r for r in self.bundle['records'] if r['revision_id'] == revision)

    def rejected(self):
        self.assertTrue(validate(self.bundle))

    def test_example(self):
        self.assertEqual([], validate(self.bundle))

    def test_unknown_field(self):
        self.record('player-h6-v1')['payload']['current_age'] = 26
        self.rejected()

    def test_invalid_date(self):
        self.record('player-h6-v1')['payload']['birth_date'] = '2000-02-31'
        self.rejected()

    def test_missing_source(self):
        self.record('player-h6-v1')['source_id'] = 'source-missing'
        self.rejected()

    def test_wrong_reference_type(self):
        self.record('affiliation-h6-harbour-v1')['payload']['person_id'] = 'club-harbour'
        self.rejected()

    def test_duplicate_revision(self):
        self.bundle['records'].append(copy.deepcopy(self.bundle['records'][0]))
        self.rejected()

    def test_invalid_interval(self):
        self.record('affiliation-h6-harbour-v1')['valid_to'] = '2024-01-01T00:00:00Z'
        self.rejected()

    def test_retroactive_release(self):
        self.record('event-g1-v2')['synthetic_released_at'] = '2025-10-18T15:20:29Z'
        self.rejected()

    def test_cross_entity_revision(self):
        self.record('event-g1-v2')['supersedes_revision_id'] = 'event-p1-v1'
        self.rejected()

    def test_wrong_fixture_clock(self):
        self.record('event-p1-v1')['payload']['fixture_id'] = 'fixture-upcoming-001'
        self.rejected()

    def test_undisclosed_is_not_zero(self):
        p = self.record('fee-h6-base-v1')['payload']
        p['disclosure'] = 'undisclosed'
        p['money']['amount_minor'] = 0
        self.rejected()

    def test_unknown_money_is_valid(self):
        p = self.record('fee-h6-base-v1')['payload']
        p.update(disclosure='undisclosed', money=None, percentage=None)
        self.assertEqual([], validate(self.bundle))

    def test_future_announcement_can_precede_effective_date(self):
        r = self.record('affiliation-h6-riverside-v1')
        self.assertLess(r['synthetic_released_at'], r['valid_from'])
        self.assertEqual([], validate(self.bundle))

    def test_goal_is_not_a_pass(self):
        self.record('event-p1-resolution-v1')['payload']['details']['pass_event_id'] = 'event-g1'
        self.rejected()

    def test_resolution_cannot_precede_release(self):
        self.record('event-p1-resolution-v1')['payload']['clock']['elapsed_ms'] = 1019999
        self.rejected()

    def test_revision_cannot_change_player_identity(self):
        self.record('affiliation-h6-harbour-v2')['payload']['person_id'] = 'player-h7'
        self.rejected()

    def test_authored_source_cannot_be_relabelled_real(self):
        r = self.record('player-h6-v1')
        r.update(provenance='sourced', claim_status='confirmed', synthetic_released_at=None)
        self.rejected()

    def test_ecosystem_examples_cover_every_new_contract_with_synthetic_data(self):
        ecosystem_kinds = {
            'supporter_group', 'public_contact_channel', 'supporter_group_recognition',
            'academy', 'team_programme', 'facility', 'facility_use', 'support_department',
            'staff_appointment', 'coaching_staff_relationship', 'match_staff_assignment',
            'attendance_observation', 'match_capacity'
        }
        examples = [r for r in self.bundle['records'] if r['kind'] in ecosystem_kinds]
        self.assertEqual(ecosystem_kinds, {r['kind'] for r in examples})
        self.assertTrue(all(r['provenance'] == 'synthetic' and r['claim_status'] == 'authored' for r in examples))
        self.assertEqual([], validate(self.bundle))

    def test_ecosystem_nested_owner_reference_is_typed(self):
        owner = self.record('contact-supporter-group-harbour-web-v1')['payload']['owner']
        owner['supporter_group_id'] = 'club-harbour'
        self.rejected()

    def test_ecosystem_public_web_contact_requires_a_uri(self):
        self.record('contact-supporter-group-harbour-web-v1')['payload']['value'] = 'not a public URL'
        self.rejected()

    def test_ecosystem_staff_scope_cannot_cross_clubs(self):
        self.record('staff-appointment-h1-head-v1')['payload']['club_id'] = 'club-riverside'
        self.rejected()

    def test_ecosystem_facility_use_allows_a_dated_shared_facility(self):
        self.record('facility-harbour-training-v1')['payload']['operating_club_id'] = 'club-riverside'
        self.assertEqual([], validate(self.bundle))

    def test_ecosystem_coaching_relationship_rejects_incompatible_team_scope(self):
        self.record('staff-appointment-h2-assistant-v1')['payload']['scope'] = {
            'scope_kind': 'academy',
            'academy_id': 'academy-harbour'
        }
        self.rejected()

    def test_ecosystem_reporting_line_cannot_report_to_self(self):
        relationship = self.record('coaching-relationship-h2-h1-v1')['payload']
        relationship['relationship_type'] = 'reports_to'
        relationship['lead_staff_appointment_id'] = relationship['staff_appointment_id']
        self.rejected()

    def test_ecosystem_match_assignment_requires_its_appointment_person(self):
        assignment = self.record('match-staff-h1-history-v1')['payload']
        assignment['subject']['person_id'] = 'coach-h2'
        self.rejected()

    def test_ecosystem_revision_cannot_change_appointment_subject(self):
        revision = copy.deepcopy(self.record('staff-appointment-h1-head-v1'))
        revision.update(
            revision_id='staff-appointment-h1-head-v2',
            supersedes_revision_id='staff-appointment-h1-head-v1',
            synthetic_released_at='2025-01-01T00:00:01Z'
        )
        revision['payload']['person_id'] = 'player-h6'
        self.bundle['records'].append(revision)
        self.rejected()

    def test_ecosystem_revision_requires_a_later_synthetic_release(self):
        revision = copy.deepcopy(self.record('attendance-history-total-v1'))
        revision.update(
            revision_id='attendance-history-total-v2',
            supersedes_revision_id='attendance-history-total-v1'
        )
        revision['payload']['count'] = 12001
        self.bundle['records'].append(revision)
        self.rejected()

    def test_ecosystem_unknown_attendance_and_capacity_are_not_zero(self):
        attendance = self.record('attendance-history-total-v1')['payload']
        attendance.update(count_status='unknown', count=0, counting_basis='unknown')
        self.rejected()
        self.setUp()
        capacity = self.record('capacity-history-v1')['payload']
        capacity.update(capacity_status='unknown', available_capacity=0, basis='unknown')
        self.rejected()

    def test_ecosystem_allocation_cannot_be_reported_attendance(self):
        attendance = self.record('attendance-history-total-v1')['payload']
        attendance['counting_basis'] = 'allocation'
        self.rejected()


class SchemaModuleTests(unittest.TestCase):
    def test_all_modules_are_valid_and_resolve(self):
        for file in (DATA / 'schemas').glob('*.schema.json'):
            with self.subTest(file=file.name):
                load_schema(file)

    def test_reference_failures_are_explicit(self):
        for reference in ('https://example.com/schema.json', '../outside.json',
                          '#/$defs/missing', '#/$defs/loop'):
            with self.subTest(reference=reference), tempfile.TemporaryDirectory(dir=DATA, prefix='.contract-test-') as directory:
                path = Path(directory) / 'test.schema.json'
                path.write_text(json.dumps({'$defs': {'loop': {'$ref': reference}}}), encoding='utf-8')
                with self.assertRaises(ValueError):
                    load_schema(path)

    def test_relative_reference_uses_its_own_document(self):
        with tempfile.TemporaryDirectory(dir=DATA, prefix='.contract-test-') as directory:
            root = Path(directory)
            (root / 'entry.json').write_text(json.dumps({'$ref': 'other.json#/$defs/value'}))
            (root / 'other.json').write_text(json.dumps({'$defs': {
                'value': {'$ref': '#/$defs/number'}, 'number': {'type': 'integer'}}}))
            self.assertEqual({'type': 'integer'}, load_schema(root / 'entry.json'))

    def test_shared_envelope_remains_closed(self):
        bundle = read_json(DATA / 'examples/review-bundle.json')
        bundle['records'][0]['unexpected'] = True
        self.assertTrue(validate(bundle))


if __name__ == '__main__':
    unittest.main()
