import copy
import json
from pathlib import Path
import unittest
from unittest.mock import patch

import validate_annotations as model


class ManualAnnotationTests(unittest.TestCase):
    def setUp(self):
        self.pack = model.load()

    def test_actual_package_provenance_clocks_layers_and_sampling_bounds(self):
        p = self.pack
        self.assertEqual(p['kind'], 'local_manual_annotations_not_application_input')
        self.assertEqual(len(p['evidencePoints']), 16)
        self.assertEqual(p['evidencePoints'][0]['id'], 'E02')
        self.assertEqual(p['evidencePoints'][11]['id'], 'E01')
        self.assertEqual([p['evidencePoints'][i]['clipSeconds'] for i in (0, -1)], [3707.22, 3730])
        self.assertEqual([p['evidencePoints'][i]['displayMatchClock'] for i in (0, -1)], ['60:14', '60:37'])
        self.assertEqual(len(p['samplingGaps']), 15)
        self.assertEqual(len(p['cameraChangeBounds']), 4)
        self.assertEqual(len(p['coarsePositionEstimates']), 4)
        self.assertEqual([r['rawMinute'] for r in p['retrospectiveReportClaims']], ['61', '61'])
        self.assertTrue(all(r['seconds'] is None for r in p['retrospectiveReportClaims']))
        self.assertTrue(all(u['value'] is None for u in p['unknowns']))

    def test_every_observation_waits_until_latest_support_and_reports_never_release(self):
        for observation in self.pack['videoObservations']:
            cutoff = observation['availableAtClipSeconds']
            before = model.eligible_observations(self.pack, cutoff-1e-8)
            self.assertNotIn(observation['id'], [o['id'] for o in before])
            self.assertIn(observation['id'], [o['id'] for o in model.eligible_observations(self.pack, cutoff)])
            after = min(cutoff+1e-8, self.pack['passage']['clipEnd'])
            self.assertIn(observation['id'], [o['id'] for o in model.eligible_observations(self.pack, after)])
        before_corner = model.eligible_observations(self.pack, 3714-1e-8)
        self.assertNotIn('corner_setup', [o['kind'] for o in before_corner])
        self.assertNotIn('first_captured_one_nil', [o['kind'] for o in model.eligible_observations(self.pack, 3730-1e-8)])
        projected = model.eligible_observations(self.pack, 3730)
        self.assertNotIn('R01', [o['id'] for o in projected])
        self.assertNotIn('R02', [o['id'] for o in projected])
        self.assertNotIn('Schuster', json.dumps(projected))
        for o in projected:
            self.assertEqual(set(o), {'id', 'kind', 'availableAtClipSeconds', 'evidenceIds', 'displayMatchClocks', 'actor'})
            if o['actor']:
                self.assertEqual(set(o['actor']), {'playerId', 'publicName', 'teamId', 'shirtNumber'})
        for cursor in (None, True, float('nan'), float('inf'), '3714', 3700, 3731):
            self.assertEqual(model.eligible_observations(self.pack, cursor), [])

    def test_early_unknown_availability_report_release_and_extra_fields_rejected(self):
        for change in ('early', 'unknown', 'report-release', 'report-time', 'invented-contact', 'extra'):
            p = copy.deepcopy(self.pack)
            if change == 'early': p['videoObservations'][5]['availableAtClipSeconds'] = 3712
            if change == 'unknown': p['videoObservations'][5]['availableAtClipSeconds'] = None
            if change == 'report-release': p['retrospectiveReportClaims'][0]['cursorEligible'] = True
            if change == 'report-time': p['retrospectiveReportClaims'][0]['availableAtClipSeconds'] = 3717
            if change == 'invented-contact': p['videoObservations'][5]['kind'] = 'corner_kick_contact'
            if change == 'extra': p['videoObservations'][0]['futureGoalScorer'] = 'FUTURE_SENTINEL'
            with self.subTest(change=change), self.assertRaises(ValueError):
                model.eligible_observations(p, 3730)

    def test_named_identity_requires_source_jersey_join_and_same_capture_legibility(self):
        for change in ('source-shirt', 'source-name', 'e15-name', 'point-e15', 'point-e14'):
            p = copy.deepcopy(self.pack)
            if change == 'source-shirt': p['identityJoins'][0]['shirtNumber'] = 44
            if change == 'source-name': p['identityJoins'][0]['publicName'] = 'Jannik Schuster'
            if change == 'e15-name':
                o = next(o for o in p['videoObservations'] if o['kind'] == 'anonymous_celebration')
                o['identityJoinId'] = 'I19'
            if change in ('point-e15', 'point-e14'):
                point = p['coarsePositionEstimates'][0]
                point['evidenceId'] = 'E15' if change == 'point-e15' else 'E14'
                point['clipSeconds'] = 3720 if change == 'point-e15' else 3719
            with self.subTest(change=change), self.assertRaises(ValueError):
                model.validate(p)

    def test_no_coordinates_fake_precision_minute_conversion_or_exact_goal_time(self):
        for change in ('xy', 'precision', 'wrong-clock', 'minute-clock', 'minute-conversion', 'goal-time', 'score-bool', 'clock-fit'):
            p = copy.deepcopy(self.pack)
            if change == 'xy': p['coarsePositionEstimates'][0]['xMetres'] = 12
            if change == 'precision': p['coarsePositionEstimates'][0]['precision'] = 'metres'
            if change == 'wrong-clock': p['evidencePoints'][0]['displayMatchClock'] = '60:15'
            if change == 'minute-clock': p['evidencePoints'][0]['displayMatchClock'] = '61'
            if change == 'minute-conversion': p['retrospectiveReportClaims'][0]['rawMinute'] = '61:00'
            if change == 'goal-time': p['unknowns'][0]['value'] = 3717
            if change == 'score-bool': p['evidencePoints'][0]['displayedScore'] = [False, False]
            if change == 'clock-fit': p['passage']['clockMapping'] = 'subtract_93_seconds'
            with self.subTest(change=change), self.assertRaises(ValueError):
                model.validate(p)

    def test_positions_exist_only_at_four_exact_points_never_carry_through_gaps_or_cuts(self):
        for point in self.pack['coarsePositionEstimates']:
            ident = next(j['playerId'] for j in self.pack['identityJoins'] if j['id'] == point['identityJoinId'])
            self.assertEqual(model.position_at(self.pack, point['clipSeconds'], ident)['status'], 'coarse_visible_estimate')
            for delta in (-1e-8, 1e-8):
                self.assertEqual(model.position_at(self.pack, point['clipSeconds']+delta, ident)['status'], 'unknown')
        for time in (3707.5, 3708.5, 3709, 3714.5, 3717, 3718.5, 3719, 3720, 3725, 3730):
            self.assertEqual(model.position_at(self.pack, time, 'brentford:anthony')['status'], 'unknown')
        self.assertEqual(model.position_at(self.pack, 3712, 'brentford:schuster')['status'], 'unknown')
        self.assertEqual(model.position_at(self.pack, 3712, 'brentford:anthony')['status'], 'unknown')
        projected = model.eligible_observations(self.pack, 3720)
        self.assertIsNone(next(o for o in projected if o['kind'] == 'anonymous_celebration')['actor'])

    def test_gap_fill_camera_bound_and_unsupported_local_path_rejected(self):
        for change in ('reviewed-gap', 'camera-time', 'path', 'sample-order', 'unstable-capture'):
            p = copy.deepcopy(self.pack)
            if change == 'reviewed-gap': p['samplingGaps'][0]['movementReviewed'] = True
            if change == 'camera-time': p['cameraChangeBounds'][0]['clipBounds'] = [3708, 3708]
            if change == 'path': p['evidencePoints'][0]['image']['file'] = '../../other.jpg'
            if change == 'sample-order': p['evidencePoints'].reverse()
            if change == 'unstable-capture': p['evidencePoints'][0]['capture']['after']['currentTime'] += 1
            with self.subTest(change=change), self.assertRaises(ValueError):
                model.validate(p)

    def test_default_offline_validation_does_not_read_ignored_evidence(self):
        read_bytes = Path.read_bytes
        def no_output_reads(path):
            if 'output' in path.parts:
                raise FileNotFoundError('Ignored evidence deliberately unavailable')
            return read_bytes(path)
        with patch.object(Path, 'read_bytes', no_output_reads):
            model.load()
            self.assertEqual(model.position_at(self.pack, 3712, 'brentford:damsgaard')['status'], 'coarse_visible_estimate')

    def test_explicit_local_evidence_check_and_tamper_detection(self):
        # Ignored screenshots are deliberately not a portable package dependency.
        # The CLI's explicit --verify-evidence remains strict when requested.
        if not all((model.HERE/e['image']['file']).exists() for e in self.pack['evidencePoints']):
            self.skipTest('Explicit local evidence unavailable; portable validation remains covered')
        self.assertTrue(model.verify_evidence(self.pack))
        changed = copy.deepcopy(self.pack)
        changed['evidencePoints'][0]['image']['sha256'] = '0'*64
        with self.assertRaisesRegex(ValueError, 'byte receipt'):
            model.verify_evidence(changed)
        changed = copy.deepcopy(self.pack)
        changed['source']['captureReceipt']['sha256'] = '0'*64
        with self.assertRaisesRegex(ValueError, 'byte receipt'):
            model.verify_evidence(changed)

    def test_availability_and_point_only_assertions_fail_if_guards_are_removed(self):
        source = Path(model.__file__).read_text(encoding='utf-8')
        def before_assertion(api):
            for obs in self.pack['videoObservations']:
                self.assertNotIn(obs['id'], [o['id'] for o in api['eligible_observations'](self.pack, obs['availableAtClipSeconds']-1e-8)])
        def no_hold_assertion(api):
            self.assertEqual(api['position_at'](self.pack, 3707.5, 'brentford:anthony')['status'], 'unknown')
        before_assertion(vars(model)); no_hold_assertion(vars(model))
        for old, new, assertion in [("obs['availableAtClipSeconds'] <= clip_seconds", 'True', before_assertion),
                                    ("p['clipSeconds'] == clip_seconds", "p['clipSeconds'] <= clip_seconds", no_hold_assertion)]:
            self.assertEqual(source.count(old), 1)
            scope = {'__name__': 'annotation_mutant', '__file__': model.__file__}
            exec(compile(source.replace(old, new), '<in-memory guard mutant>', 'exec'), scope)
            with self.assertRaises(AssertionError):
                assertion(scope)


if __name__ == '__main__':
    unittest.main()
