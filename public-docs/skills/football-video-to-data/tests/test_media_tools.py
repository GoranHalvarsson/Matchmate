import argparse
import copy
import dataclasses
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("media_tools", ROOT / "scripts" / "media_tools.py")
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.bundle = media.load(ROOT / "tests" / "fixtures" / "evidence-valid.json")

    def row(self, name):
        return next(row for row in self.bundle["records"] if row["id"] == name)

    def test_valid_and_all_unknown_are_valid_without_completeness_claim(self):
        self.assertEqual([], media.validate(self.bundle))
        self.bundle["records"] = [self.row("gap-1")]
        self.assertEqual([], media.validate(self.bundle))

    def test_rejects_unsupported_evidence_states(self):
        mutations = [
            ("gap-1", "position", self.row("ball-screen")["position"]),
            ("reported-goal", "available_at", {"period": "2H", "seconds": 3000}),
            ("person-point", "available_at", {"period": "1H", "seconds": 9}),
            ("person-point", "source_refs", ["MISSING"]),
            ("reported-goal", "reported_minute", "83:02"),
            ("illustration-point", "provenance", "observed"),
            ("illustration-point", "kind", "event"),
            ("person-point", "clock_anchor_ref", "MISSING"),
            ("person-point", "match_clock", {"period": "2H", "seconds": 10}),
            ("person-point", "match_clock", {"period": "1H", "seconds": 19}),
            ("person-point", "source_refs", [{"id": "bad"}]),
            ("person-point", "match_clock", {"period": [], "seconds": 10}),
        ]
        original = copy.deepcopy(self.bundle)
        for name, field, value in mutations:
            with self.subTest(name=name, field=field, value=value):
                self.bundle = copy.deepcopy(original)
                self.row(name)[field] = value
                self.assertTrue(media.validate(self.bundle))

    def test_rejects_unsupported_coordinates(self):
        for field, value in (("x", 1.1), ("height", 2), ("calibration_refs", []),
                             ("vertical_state", "airborne")):
            with self.subTest(field=field):
                bundle = copy.deepcopy(self.bundle)
                bundle["records"][2]["position"][field] = value
                self.assertTrue(media.validate(bundle))

    def test_identity_point_cannot_propagate(self):
        self.row("person-point")["media_interval"] = [21, 21]
        self.row("person-point")["match_clock"]["seconds"] = 11
        self.row("person-point")["available_at"]["seconds"] = 11
        self.assertTrue(any("identity outside" in e for e in media.validate(self.bundle)))

    def test_named_position_cannot_skip_identity_by_omitting_track(self):
        self.bundle["records"] = [row for row in self.bundle["records"] if row["id"] != "identity-1"]
        self.row("person-point")["subject"].update(track_id=None, identity_refs=[])
        self.assertTrue(media.validate(self.bundle))

    def test_generated_position_requires_subject_with_separate_track_namespace(self):
        for subject in (None, {"track_id": None, "player_id": None, "identity_refs": []},
                        {"track_id": "shot1-personA", "player_id": None, "identity_refs": []}):
            with self.subTest(subject=subject):
                self.row("illustration-point")["subject"] = subject
                self.assertTrue(media.validate(self.bundle))
        self.row("illustration-point")["subject"] = {
            "track_id": "generated:slot-1", "player_id": None, "identity_refs": []}
        self.assertEqual([], media.validate(self.bundle))

    def test_clock_anchor_cannot_cross_scope(self):
        self.row("ball-screen").update(media_interval=[31, 31],
            match_clock={"period": "1H", "seconds": 21}, clock_anchor_ref="clock-1")
        self.assertTrue(any("outside clock scope" in e for e in media.validate(self.bundle)))

    def test_release_waits_for_identity_and_clock_dependencies(self):
        self.row("identity-1")["available_at"] = {"period": "1H", "seconds": 11}
        self.assertTrue(any("identity evidence availability" in e for e in media.validate(self.bundle)))
        self.row("identity-1")["available_at"]["seconds"] = 10
        self.row("clock-1")["available_at"] = {"period": "1H", "seconds": 11}
        self.assertTrue(any("clock evidence availability" in e for e in media.validate(self.bundle)))

    def test_calibration_cutoff_is_checked_without_duplicate_source_ref(self):
        self.row("person-point")["source_refs"].remove("CAL")
        calibration = next(s for s in self.bundle["sources"] if s["id"] == "CAL")
        self.assertEqual([], media.validate(self.bundle))
        for future in ({"period": "1H", "seconds": 60}, None):
            with self.subTest(available_at=future):
                calibration["available_at"] = future
                self.assertTrue(any("calibration evidence availability" in e for e in media.validate(self.bundle)))
        self.row("person-point")["available_at"] = None
        self.assertEqual([], media.validate(self.bundle))

    def test_calibration_dependencies_require_real_refs_kind_and_scope(self):
        original = copy.deepcopy(self.bundle)
        for field, value in (("kind", "frame"), ("scope_media_interval", [21, 22]),
                             ("scope_media_interval", None)):
            with self.subTest(field=field, value=value):
                self.bundle = copy.deepcopy(original)
                calibration = next(s for s in self.bundle["sources"] if s["id"] == "CAL")
                calibration[field] = value
                self.assertTrue(media.validate(self.bundle))
        self.bundle = copy.deepcopy(original)
        self.row("person-point")["position"]["calibration_refs"] = ["MISSING"]
        self.assertTrue(media.validate(self.bundle))

    def test_generated_named_slot_uses_separate_roster_identity(self):
        self.bundle["sources"].append({"id": "ROS", "kind": "roster", "locator": "synthetic roster", "available_at": None})
        row = copy.deepcopy(self.row("identity-1"))
        row.update(id="generated-identity", provenance="reported", source_refs=["ROS"], media_interval=None)
        row["available_at"] = None
        row["subject"]["track_id"] = "generated:slot-1"
        self.bundle["records"].append(row)
        self.row("illustration-point")["subject"].update(player_id="synthetic-player-1", identity_refs=[row["id"]])
        self.assertEqual([], media.validate(self.bundle))
        self.row("illustration-point")["subject"]["track_id"] = "shot1-personA"
        self.assertTrue(media.validate(self.bundle))

    def test_duplicate_ids_and_extra_fact_fields_fail(self):
        self.bundle["records"].append(copy.deepcopy(self.bundle["records"][0]))
        self.assertTrue(media.validate(self.bundle))
        self.bundle["records"].pop()
        self.row("illustration-point")["actual_passes"] = 20
        self.assertTrue(media.validate(self.bundle))

    def test_strict_json_rejects_nan_and_duplicate_keys(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            for value in ('{"x":NaN}', '{"x":1,"x":2}'):
                path.write_text(value)
                with self.assertRaises(ValueError):
                    media.load(path)


class HelperTests(unittest.TestCase):
    def test_atomic_save_preserves_preexisting_temp_file(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "manifest.json"
            sentinel = Path(directory) / "manifest.json.tmp"
            sentinel.write_bytes(b"unrelated existing partial")
            media.save(target, {"new": "receipt"})
            self.assertEqual({"new": "receipt"}, media.load(target))
            self.assertEqual(b"unrelated existing partial", sentinel.read_bytes())
            self.assertEqual({"manifest.json", "manifest.json.tmp"}, {p.name for p in Path(directory).iterdir()})

    def test_failed_atomic_replace_cleans_only_its_own_temp(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "manifest.json"
            target.write_bytes(b"old output")
            sentinel = Path(directory) / "manifest.json.tmp"
            sentinel.write_bytes(b"unrelated existing partial")
            with mock.patch.object(Path, "replace", side_effect=OSError("fixture replace failure")):
                with self.assertRaisesRegex(OSError, "fixture replace failure"):
                    media.save(target, {"new": "receipt"})
            self.assertEqual(b"old output", target.read_bytes())
            self.assertEqual(b"unrelated existing partial", sentinel.read_bytes())
            self.assertEqual({"manifest.json", "manifest.json.tmp"}, {p.name for p in Path(directory).iterdir()})

    def test_cache_identity_uses_contents_and_settings_not_dict_order(self):
        self.assertEqual(media.identity({"a": 1, "b": 2}), media.identity({"b": 2, "a": 1}))
        self.assertNotEqual(media.identity({"a": 1}), media.identity({"a": 2}))

    def test_half_open_chunk_ownership_preserves_raw_words(self):
        self.assertEqual([(0, 0, 2, 0, 2.5), (1, 2, 4, 1.5, 4.5), (2, 4, 5, 3.5, 5)], media.chunk_ranges(5, 2, 0.5))
        chunks = [
            {"index": 0, "core_interval_seconds": [0, 2], "decoded_interval_seconds": [0, 2.5],
             "segments": [{"id": 0, "start": 1.8, "end": 2.2, "text": " Rawname", "words": [{"start": 1.8, "end": 2.2, "word": " Rawname"}]}]},
            {"index": 1, "core_interval_seconds": [2, 4], "decoded_interval_seconds": [1.5, 4.5],
             "segments": [{"id": 0, "start": .3, "end": .7, "text": " Rawname", "words": [{"start": .3, "end": .7, "word": " Rawname"}]}]}
        ]
        original = copy.deepcopy(chunks)
        rows = media.canonical(chunks, 100)
        self.assertEqual(1, len(rows))
        self.assertEqual("Rawname", rows[0]["text"])
        self.assertEqual(101.8, rows[0]["media_elapsed_start_seconds"])
        self.assertEqual(original, chunks)

    def test_timestamp_carries_milliseconds(self):
        self.assertEqual("01:00:00,000", media.stamp(3599.9999, ","))

    def test_subprocess_uses_argument_list_without_shell(self):
        with mock.patch.object(media.subprocess, "run", return_value=types.SimpleNamespace(stdout="", stderr="")) as call:
            media.run(["ffmpeg", "-i", "a; touch bad.mp4"])
            self.assertFalse(call.call_args.kwargs["shell"])
            self.assertEqual("a; touch bad.mp4", call.call_args.args[0][-1])


@dataclasses.dataclass
class FakeInfo:
    language: str = "en"


@dataclasses.dataclass
class FakeSegment:
    id: int = 0
    start: float = .1
    end: float = .3
    text: str = " Rawname"
    words: list = dataclasses.field(default_factory=lambda: [{"start": .1, "end": .3, "word": " Rawname"}])


class AsrTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        audio = self.root / "input.wav"
        audio.write_bytes(b"synthetic fake audio")
        model = self.root / "model"
        model.mkdir()
        for name in ("model.bin", "config.json"):
            (model / name).write_bytes(b"synthetic fake model")
        self.args = argparse.Namespace(audio=str(audio), model=str(model), output=str(self.root / "asr"),
            language="en", check=False, chunk_seconds=1, overlap=.1, media_offset=10, timeout=5)
        self.engine = mock.Mock()
        self.engine.transcribe.side_effect = lambda *a, **kw: (iter([FakeSegment()]), FakeInfo())
        self.factory = mock.Mock(return_value=self.engine)
        fake_module = types.ModuleType("faster_whisper")
        fake_module.WhisperModel = self.factory
        def fake_run(command, **kwargs):
            Path(command[-1]).write_bytes(b"synthetic decoded wav")
            return types.SimpleNamespace(stdout="", stderr="")
        patches = [mock.patch.dict(sys.modules, {"faster_whisper": fake_module}),
                   mock.patch.object(media.importlib.util, "find_spec", return_value=object()),
                   mock.patch.object(media.importlib.metadata, "version", return_value="fixture-version"),
                   mock.patch.object(media.shutil, "which", return_value="fixture-tool"),
                   mock.patch.object(media, "media_info", return_value=({}, 2, 0)),
                   mock.patch.object(media, "run", side_effect=fake_run)]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def test_preflight_does_not_load_or_download_model(self):
        self.args.check = True
        result = media.transcribe(self.args)
        self.assertEqual("ready", result["status"])
        self.factory.assert_not_called()
        self.assertFalse(result["downloads"])

    def test_mock_asr_completes_and_resumes_without_recognition(self):
        self.assertEqual("complete", media.transcribe(self.args)["status"])
        self.assertEqual(2, self.engine.transcribe.call_count)
        self.assertTrue(self.factory.call_args.kwargs["local_files_only"])
        for call in self.engine.transcribe.call_args_list:
            self.assertIsNone(call.kwargs["initial_prompt"])
            self.assertIsNone(call.kwargs["hotwords"])
        self.factory.reset_mock()
        self.assertEqual("complete", media.transcribe(self.args)["status"])
        self.factory.assert_not_called()
        progress = media.load(self.root / "asr" / "progress.json")
        self.assertEqual(2, progress["segment_count"])
        self.assertEqual(3, len(progress["artifacts"]))

    def test_stale_or_corrupted_cache_stops(self):
        media.transcribe(self.args)
        self.args.media_offset = 11
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            media.transcribe(self.args)
        self.args.media_offset = 10
        (self.root / "asr" / "chunk-0000.json").write_text("{}")
        with self.assertRaisesRegex(ValueError, "chunk hash mismatch"):
            media.transcribe(self.args)

    def test_failed_final_write_cannot_publish_completion(self):
        real_save = media.save
        def fail_completion(path, value):
            if Path(path).name == "progress.json" and value.get("status") == "complete":
                raise OSError("fixture write failure")
            real_save(path, value)
        with mock.patch.object(media, "save", side_effect=fail_completion):
            with self.assertRaisesRegex(OSError, "fixture write failure"):
                media.transcribe(self.args)
        self.assertEqual("running", media.load(self.root / "asr" / "progress.json")["status"])


if __name__ == "__main__":
    unittest.main()
