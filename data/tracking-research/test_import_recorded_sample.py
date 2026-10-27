import copy
import json
from pathlib import Path
import shutil
import tempfile
import unittest
from import_recorded_sample import OUT, SAMPLE_FILE, LICENSE_FILE, check, normalize, validate, finite


class RecordedImportTests(unittest.TestCase):
    def test_actual_pinned_sample_and_full_notice_validate_offline(self):
        sample, manifest = check()
        self.assertEqual(len(sample['frames']), 1801)
        self.assertEqual(len(sample['players']), 22)
        self.assertEqual(manifest['parserBytes'], 4092971)
        self.assertFalse(manifest['eventsImported'])
        self.assertFalse(manifest['rawPrefixRetained'])
        self.assertFalse(manifest['fullTrackingObjectDownloaded'])
        self.assertIn('Copyright (c) 2020 SkillCorner', (OUT/LICENSE_FILE).read_text())
        self.assertEqual(sum(not f['players'] for f in sample['frames']), 348)
        self.assertEqual(sum(bool(f['players']) for f in sample['frames']), 1453)
        self.assertTrue(any(abs(p['y']) > 34 or abs(p['x']) > 52.5 for f in sample['frames'] for p in f['players']))
        self.assertNotIn('events', sample)

    def test_artifact_tamper_receipt_url_and_quality_mutation_are_rejected(self):
        sample, _ = check()
        changed = copy.deepcopy(sample)
        changed['frames'][0]['players'][0]['isDetected'] = not changed['frames'][0]['players'][0]['isDetected']
        with self.assertRaisesRegex(ValueError, 'quality counts'):
            validate(changed)
        # Keep reversible test copies inside this writable workspace on Windows.
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent, prefix='.import-test-') as name:
            folder = Path(name)
            self.assertTrue(folder.resolve().is_relative_to(Path(__file__).parent.resolve()))
            for file in ('manifest.json', SAMPLE_FILE, LICENSE_FILE):
                shutil.copyfile(OUT/file, folder/file)
            (folder/SAMPLE_FILE).write_bytes((folder/SAMPLE_FILE).read_bytes()+b' ')
            with self.assertRaisesRegex(ValueError, 'artifact hash'):
                check(folder)
            shutil.copyfile(OUT/SAMPLE_FILE, folder/SAMPLE_FILE)
            manifest = json.loads((folder/'manifest.json').read_text())
            manifest['receipts'][0]['url'] = 'https://example.invalid/other'
            (folder/'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, 'URL/status'):
                check(folder)

    def test_normalization_preserves_quality_ids_empty_frames_and_missingness(self):
        metadata = {'players': [{'id': 7, 'team_id': 1, 'first_name': ' Source ', 'last_name': ' Player ', 'number': 23}],
                    'competition_edition': {'name': 'Test fixture only'}, 'date_time': 'test',
                    'home_team': {'id': 1, 'name': 'Unit home'}, 'away_team': {'id': 2, 'name': 'Unit away'},
                    'pitch_length': 105, 'pitch_width': 68, 'home_team_side': ['left_to_right']}
        row = {'frame': 610, 'period': 1, 'timestamp': '00:01:00.0',
               'player_data': [{'player_id': 7, 'x': -54, 'y': 0, 'is_detected': None}],
               'ball_data': {'x': float('nan'), 'y': 1, 'is_detected': False}}
        empty = {**row, 'frame': 611, 'timestamp': '00:01:00.1', 'player_data': []}
        sample = normalize(metadata, [row, empty])
        self.assertEqual(sample['players'][0]['id'], 7)
        self.assertEqual(sample['players'][0]['name'], 'Source Player')
        self.assertEqual(sample['frames'][0]['players'][0]['x'], -54)
        self.assertIsNone(sample['frames'][0]['players'][0]['isDetected'])
        self.assertIsNone(sample['frames'][0]['ball']['x'])
        self.assertFalse(sample['frames'][0]['ball']['isDetected'])
        self.assertEqual(sample['frames'][1]['frameState'], 'empty-positions')
        self.assertFalse(finite(True))


if __name__ == '__main__':
    unittest.main()
