import copy
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from import_recorded_events import FILE, MANIFEST, FIELDS, PREFIX_BYTES, POLICY, check, normalize, validate, validate_entry
from import_recorded_sample import OUT, check as tracking_check


class RecordedEventImportTests(unittest.TestCase):
    def setUp(self):
        self.sample, _ = tracking_check()
        self.bundle, self.manifest = check()

    def row(self, event):
        return {'event_id': event['sourceId'], 'match_id': str(self.bundle['matchId']), 'event_type': 'player_possession',
                'event_subtype': '', 'frame_physical_start': '', 'period': str(event['period']),
                'player_id': str(event['actorId']), 'team_id': str(event['teamId']),
                'frame_start': str(event['frameStart']), 'frame_end': str(event['frameEnd']),
                'time_start': event['timeStart'], 'time_end': event['timeEnd'],
                'is_player_possession_start_matched': 'True', 'is_player_possession_end_matched': 'True',
                'player_targeted_name': 'FUTURE TARGET MUST NOT IMPORT', 'pass_outcome': 'FUTURE OUTCOME',
                'x_start': '99', 'speed_avg': '40', 'team_score': '9'}

    def test_actual_bounded_receipt_exact_selection_and_raw_clocks(self):
        self.assertEqual(self.manifest['receipt']['bytes'], PREFIX_BYTES)
        self.assertEqual([(e['sourceId'], e['sourceLine'], e['frameStart'], e['frameEnd'], e['end']) for e in self.bundle['annotations']],
                         [('8_11', 56, 628, 662, 65.2), ('8_12', 60, 677, 702, 69.2), ('8_13', 65, 719, 738, 72.8),
                          ('8_28', 138, 1323, 1323, 131.3), ('8_37', 176, 1687, 1687, 167.7)])
        for e in self.bundle['annotations']:
            self.assertEqual(set(e), FIELDS)
            self.assertEqual(e['dependency'], {'kind': POLICY, 'verified': True, 'frame': e['frameEnd'], 'clock': e['end']})

    def test_normalizer_allowlist_and_fixed_first_three_per_team(self):
        rows = [self.row(e) for e in self.bundle['annotations']]
        # Explicit synthetic extra PP intervals reuse a covered source slot solely to test selection.
        extras = [dict(rows[0], event_id=f'8_{n}', frame_start=str(800+n), frame_end=str(800+n),
                       time_start=printed, time_end=printed) for n, printed in ((91, '01:28.1'), (92, '01:28.2'))]
        # Prove these are valid candidates before combining them with the first
        # three team rows; otherwise rejection could hide a missing selection cap.
        for extra in extras:
            qualified = normalize([extra], self.sample)['annotations']
            self.assertEqual([e['sourceId'] for e in qualified], [extra['event_id']])
            self.assertEqual(qualified[0]['start'], next(f['clock'] for f in self.sample['frames'] if f['frameId'] == int(extra['frame_start'])))
        normalized = normalize(list(reversed(rows+extras)), self.sample)
        self.assertEqual([e['sourceId'] for e in normalized['annotations']], [e['sourceId'] for e in self.bundle['annotations']])
        self.assertNotIn('FUTURE', json.dumps(normalized))
        self.assertNotIn('target', json.dumps(normalized))
        self.assertEqual(len(normalized['annotations']), 5)

    def test_candidate_alignment_identity_flags_and_dependencies_fail_closed(self):
        original = self.bundle['annotations'][0]
        changes = [{'period': 2}, {'actorId': -1}, {'teamId': -1}, {'startMatched': False}, {'endMatched': None},
                   {'start': 59}, {'end': 241}, {'timeEnd': '01:05.3'}, {'frameStart': 629}, {'frameEnd': 661},
                   {'dependency': {'kind': POLICY, 'verified': True, 'frame': 662, 'clock': 66}},
                   {'dependency': {'kind': POLICY, 'verified': False, 'frame': 662, 'clock': 65.2}},
                   {'pass_outcome': 'future'}, {'dependency': {}}, {'sourceLine': 1002}]
        for change in changes:
            with self.subTest(change=change), self.assertRaises(ValueError):
                validate_entry({**original, **change}, self.sample)
        for key, value in [('match_id', '0'), ('period', '2'), ('event_subtype', 'unknown'), ('frame_physical_start', '627'),
                           ('is_player_possession_end_matched', ''), ('team_id', '-1'), ('time_end', '01:05.3')]:
            self.assertEqual(normalize([{**self.row(original), key: value}], self.sample)['annotations'], [])

    def test_missing_empty_partial_nonfinite_and_duplicate_interval_frames_withheld(self):
        event = self.bundle['annotations'][0]
        for mutation in ('missing', 'empty', 'partial', 'nonfinite', 'wrong-period', 'clock', 'duplicate'):
            sample = copy.deepcopy(self.sample)
            index = next(i for i, f in enumerate(sample['frames']) if f['frameId'] == 640)
            frame = sample['frames'][index]
            if mutation == 'missing': sample['frames'].pop(index)
            if mutation == 'empty': frame['players'] = []
            if mutation == 'partial': frame['players'].pop()
            if mutation == 'nonfinite': frame['players'][0]['x'] = float('nan')
            if mutation == 'wrong-period': frame['period'] = 2
            if mutation == 'clock': frame['clock'] += .1
            if mutation == 'duplicate': sample['frames'].append(copy.deepcopy(frame))
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_entry(event, sample)

    def test_duplicate_bundle_identity_unknown_policy_and_tamper_rejected(self):
        for change in ({'matchId': -1}, {'pin': 'wrong'}, {'period': 2}, {'policy': 'unknown'}, {'annotations': self.bundle['annotations']*2}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                validate({**self.bundle, **change}, self.sample)
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            normalize([self.row(self.bundle['annotations'][0])]*2, self.sample)
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent, prefix='.event-test-') as name:
            folder = Path(name)
            for file in (FILE, MANIFEST): shutil.copyfile(OUT/file, folder/file)
            (folder/FILE).write_bytes((folder/FILE).read_bytes()+b' ')
            with self.assertRaisesRegex(ValueError, 'artifact hash'): check(folder)


if __name__ == '__main__':
    unittest.main()
