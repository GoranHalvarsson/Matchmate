#!/usr/bin/env python3
"""Local football-media receipts, optional offline ASR, and evidence validation."""
import argparse
import dataclasses
import hashlib
import importlib.metadata
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

VERSION = "1.0"
PERIODS = {"1H": 0, "2H": 1, "ET1": 2, "ET2": 3, "PSO": 4}


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def identity(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, allow_nan=False,
                                     separators=(",", ":")).encode()).hexdigest()


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix="." + path.name + ".", suffix=".tmp",
                                         delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(payload)
        temporary.replace(path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def load(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key: " + key)
            result[key] = value
        return result
    def invalid(value):
        raise ValueError("Non-finite JSON number: " + value)
    return json.loads(Path(path).read_text(encoding="utf-8-sig"),
                      object_pairs_hook=pairs, parse_constant=invalid)


def local_file(value):
    path = Path(value).expanduser().resolve()
    if not path.is_file():
        raise ValueError("A readable local source file is required")
    return path


def run(arguments, timeout=120):
    try:
        result = subprocess.run(arguments, check=True, capture_output=True,
                                text=True, timeout=timeout, shell=False)
    except (subprocess.SubprocessError, OSError):
        raise ValueError(Path(arguments[0]).name + " failed; check local input/tool readiness") from None
    return result


def tool_version(name):
    if not shutil.which(name):
        return None
    return run([name, "-version"], timeout=15).stdout.splitlines()[0]


def preflight():
    return {"python": sys.version.split()[0], "helper_version": VERSION,
            "tools": {name: tool_version(name) for name in ("ffmpeg", "ffprobe")},
            "optional_modules": {name: importlib.util.find_spec(name) is not None
                                 for name in ("faster_whisper", "PIL", "cv2", "ultralytics")}}


def probe(path):
    return json.loads(run(["ffprobe", "-v", "error", "-show_entries",
        "format=duration,start_time:stream=index,codec_type,codec_name,width,height,r_frame_rate,sample_rate,start_time,duration,time_base",
        "-of", "json", str(path)]).stdout)


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def number(value):
    try:
        value = float(value)
    except (TypeError, ValueError):
        raise ValueError("Expected finite number") from None
    if not math.isfinite(value):
        raise ValueError("Expected finite number")
    return value


def media_info(path):
    metadata = probe(path)
    duration = number(metadata.get("format", {}).get("duration"))
    origin = number(metadata.get("format", {}).get("start_time", 0))
    if duration <= 0:
        raise ValueError("Source has no finite positive duration")
    return metadata, duration, origin


def manifest(args):
    source = local_file(args.source)
    target = Path(args.output)
    if target.exists():
        raise ValueError("Output already exists; retain it or choose another path")
    if args.source_page:
        parsed = urlsplit(args.source_page)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username or
                parsed.password or parsed.query or parsed.fragment):
            raise ValueError("Use a public HTTPS page without credentials, query or fragment")
    metadata, duration, origin = media_info(source)
    version = tool_version("ffprobe")
    receipt = {"helper_version": VERSION, "source_sha256": sha(source),
               "bytes": source.stat().st_size, "duration_seconds": duration,
               "duration_basis": "FFprobe container duration, not verified decoded/playable coverage",
               "source_pts_origin_seconds": origin, "probe": metadata,
               "ffprobe_version": version, "source_page": args.source_page,
               "license_status": args.license_status, "license_note": args.license_note,
               "origin": args.origin}
    receipt["cache_identity"] = identity(receipt)
    save(target, receipt)
    return receipt


def extract(args):
    source = local_file(args.source)
    output = Path(args.output).resolve()
    receipt_path = output.with_name(output.name + ".receipt.json")
    temporary = output.with_name(output.stem + ".partial" + output.suffix)
    if any(path.exists() for path in (output, receipt_path, temporary)):
        raise ValueError("Extraction output/receipt/partial exists; choose a fresh output")
    _, duration, origin = media_info(source)
    start = number(args.seconds if args.command == "frame" else args.start)
    if not 0 <= start < duration:
        raise ValueError("Requested time outside source")
    settings = {"command": args.command, "start_seconds": start}
    command = ["ffmpeg", "-hide_banner", "-nostdin", "-n", "-ss", str(start)]
    receipt = {"helper_version": VERSION, "source_sha256": sha(source),
               "source_pts_origin_seconds": origin, "ffmpeg_version": tool_version("ffmpeg")}
    if args.command == "frame":
        if output.suffix.lower() not in (".png", ".jpg", ".jpeg"):
            raise ValueError("Frame output must be PNG or JPEG")
        drift_limit = number(args.max_drift)
        if drift_limit < 0:
            raise ValueError("max-drift must be nonnegative")
        settings["max_drift_seconds"] = drift_limit
        command += ["-copyts", "-i", str(source), "-map", "0:v:0", "-frames:v", "1",
                    "-vf", "showinfo", "-fps_mode", "passthrough", str(temporary)]
    else:
        length = number(args.duration)
        if length <= 0 or start + length > duration + 0.001:
            raise ValueError("Audio interval outside source")
        if output.suffix.lower() != ".wav":
            raise ValueError("Audio output must be WAV")
        settings.update(duration_seconds=length, channels=1, sample_rate=16000)
        command += ["-i", str(source), "-t", str(length), "-map", "0:a:0", "-vn",
                    "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(temporary)]
    output.parent.mkdir(parents=True, exist_ok=True)
    result = run(command, timeout=args.timeout)
    if args.command == "frame":
        found = re.search(r"\bn:\s*0\s+pts:\s*-?\d+\s+pts_time:([-+\d.eE]+)", result.stderr)
        if not found:
            raise ValueError("No decoded source PTS; partial retained")
        pts = number(found.group(1))
        if abs(pts - origin - start) > drift_limit:
            raise ValueError("Seek drift exceeds declared maximum; partial retained")
        receipt.update(requested_media_elapsed_seconds=start, decoded_source_pts_seconds=pts,
                       actual_media_elapsed_seconds=pts - origin)
    else:
        _, actual_duration, _ = media_info(temporary)
        if abs(actual_duration - length) > 0.25:
            raise ValueError("Decoded audio duration differs; partial retained")
        receipt.update(requested_media_interval_seconds=[start, start + length],
                       decoded_audio_duration_seconds=actual_duration,
                       timing="Requested source interval; normalized audio starts at zero; not exact contact PTS")
    receipt.update(settings=settings, output_sha256=sha(temporary), bytes=temporary.stat().st_size)
    receipt["cache_identity"] = identity(receipt)
    temporary.replace(output)
    save(receipt_path, receipt)
    return receipt


def chunk_ranges(duration, size, overlap):
    if duration <= 0 or size <= 0 or overlap < 0 or overlap >= size:
        raise ValueError("Invalid chunk duration/overlap")
    return [(i, i * size, min((i + 1) * size, duration), max(0, i * size - overlap),
             min((i + 1) * size + overlap, duration)) for i in range(math.ceil(duration / size))]


def canonical(chunks, media_offset):
    output = []
    for chunk in chunks:
        start, end = chunk["core_interval_seconds"]
        offset = chunk["decoded_interval_seconds"][0]
        for segment in chunk["segments"]:
            words = [dict(word, start=word["start"] + offset, end=word["end"] + offset)
                     for word in segment.get("words") or []]
            selected = [w for w in words if start <= (w["start"] + w["end"]) / 2 < end]
            if words and not selected:
                continue
            first = selected[0]["start"] if selected else offset + segment["start"]
            last = selected[-1]["end"] if selected else offset + segment["end"]
            if not words and not start <= (first + last) / 2 < end:
                continue
            output.append({"id": f"C{chunk['index']:04d}:S{segment['id']:04d}",
                "start_seconds": first, "end_seconds": last,
                "media_elapsed_start_seconds": first + media_offset,
                "media_elapsed_end_seconds": last + media_offset,
                "text": "".join(w["word"] for w in selected).strip() if selected else segment["text"].strip(),
                "raw_text": segment["text"], "words": selected})
    return sorted(output, key=lambda row: (row["start_seconds"], row["end_seconds"], row["id"]))


def stamp(seconds, separator="."):
    milliseconds = round(seconds * 1000)
    hours, milliseconds = divmod(milliseconds, 3600000)
    minutes, milliseconds = divmod(milliseconds, 60000)
    whole_seconds, milliseconds = divmod(milliseconds, 1000)
    return f"{hours:02d}:{minutes:02d}:{whole_seconds:02d}{separator}{milliseconds:03d}"


def transcribe(args):
    audio = local_file(args.audio)
    model = Path(args.model).expanduser().resolve()
    missing = [name for name in ("model.bin", "config.json") if not (model / name).is_file()]
    available = importlib.util.find_spec("faster_whisper") is not None
    ready = not missing and available and bool(shutil.which("ffmpeg")) and bool(shutil.which("ffprobe"))
    if args.check:
        return {"status": "ready" if ready else "blocked", "model_files_missing": missing,
                "faster_whisper_available": available, "ffmpeg_available": bool(shutil.which("ffmpeg")),
                "ffprobe_available": bool(shutil.which("ffprobe")), "model_loaded": False,
                "recognition_run": False, "downloads": False}
    if not ready:
        raise ValueError("ASR preflight blocked; provide installed engine/tools and an existing local model")
    _, duration, audio_origin = media_info(audio)
    if abs(audio_origin) > 0.001:
        raise ValueError("ASR requires normalized zero-start audio; extract WAV first")
    ranges = chunk_ranges(duration, number(args.chunk_seconds), number(args.overlap))
    media_offset = number(args.media_offset)
    if media_offset < 0:
        raise ValueError("media-offset must be nonnegative")
    settings = {"language": args.language, "beam_size": 5, "vad_filter": True,
                "word_timestamps": True, "condition_on_previous_text": False,
                "initial_prompt": None, "hotwords": None}
    model_files = {str(p.relative_to(model)): sha(p) for p in sorted(model.rglob("*")) if p.is_file()}
    context = {"helper_version": VERSION, "audio_sha256": sha(audio), "model_files": model_files,
               "versions": {name: importlib.metadata.version(name) for name in ("faster-whisper", "ctranslate2")},
               "settings": settings, "chunk_seconds": args.chunk_seconds, "overlap": args.overlap,
               "media_offset_seconds": media_offset, "duration_seconds": duration,
               "device": "cpu", "compute_type": "int8", "threads": min(4, os.cpu_count() or 1)}
    cache_id = identity(context)
    directory = Path(args.output)
    progress_path = directory / "progress.json"
    previous = load(progress_path) if progress_path.exists() else None
    if previous and previous.get("cache_identity") != cache_id:
        raise ValueError("ASR cache identity mismatch; choose a fresh output directory")
    if not previous and directory.exists() and any(directory.iterdir()):
        raise ValueError("Unrecognized nonempty ASR directory; choose a fresh directory")
    completed = dict(previous.get("chunks", {})) if previous else {}
    if previous and previous.get("status") == "complete":
        for name, digest in previous["artifacts"].items():
            if sha(directory / name) != digest:
                raise ValueError("Completed ASR artifact hash mismatch")
    chunks, engine = [], None
    progress = {"cache_identity": cache_id, "context": context, "status": "running", "chunks": completed}
    directory.mkdir(parents=True, exist_ok=True)
    for index, start, end, decode_start, decode_end in ranges:
        name = f"chunk-{index:04d}.json"
        target = directory / name
        if name in completed:
            if not target.is_file() or sha(target) != completed[name]:
                raise ValueError("Checkpointed ASR chunk hash mismatch")
            chunk = load(target)
            if (chunk.get("cache_identity") != cache_id or chunk.get("index") != index or
                    chunk.get("core_interval_seconds") != [start, end] or
                    chunk.get("decoded_interval_seconds") != [decode_start, decode_end]):
                raise ValueError("ASR chunk identity/interval mismatch")
        else:
            if engine is None:
                os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
                from faster_whisper import WhisperModel
                engine = WhisperModel(str(model), device="cpu", compute_type="int8",
                                      cpu_threads=context["threads"], local_files_only=True)
            wav = directory / f"chunk-{index:04d}.wav"
            run(["ffmpeg", "-hide_banner", "-nostdin", "-v", "error", "-y", "-ss", str(decode_start),
                 "-i", str(audio), "-t", str(decode_end - decode_start), "-map", "0:a:0", "-ac", "1",
                 "-ar", "16000", "-c:a", "pcm_s16le", str(wav)], timeout=args.timeout)
            segments, info = engine.transcribe(str(wav), **settings)
            chunk = {"cache_identity": cache_id, "index": index, "core_interval_seconds": [start, end],
                     "decoded_interval_seconds": [decode_start, decode_end], "wav_sha256": sha(wav),
                     "info": dataclasses.asdict(info), "segments": [dataclasses.asdict(s) for s in segments]}
            save(target, chunk)
            completed[name] = sha(target)
        chunks.append(chunk)
        save(progress_path, progress)
    rows = canonical(chunks, media_offset)
    save(directory / "transcript.json", {"cache_identity": cache_id, "context": context,
         "evidence_type": "unprompted_machine_transcription_not_human_listening", "segments": rows})
    text = "Machine transcription; raw spelling. Elapsed media times, not football clock.\n\n"
    text += "\n".join(f"[{stamp(r['media_elapsed_start_seconds'])}] {r['id']} {r['text']}" for r in rows) + "\n"
    (directory / "transcript.txt").write_text(text, encoding="utf-8")
    srt = "\n\n".join(f"{i}\n{stamp(r['media_elapsed_start_seconds'], ',')} --> {stamp(r['media_elapsed_end_seconds'], ',')}\n{r['text']}"
                       for i, r in enumerate(rows, 1)) + "\n"
    (directory / "transcript.srt").write_text(srt, encoding="utf-8")
    progress.update(status="complete", artifacts={name: sha(directory / name) for name in
                    ("transcript.json", "transcript.txt", "transcript.srt")}, segment_count=len(rows))
    save(progress_path, progress)
    return {"status": "complete", "chunks": len(chunks), "segments": len(rows), "cache_identity": cache_id,
            "human_listening_claimed": False}


def validate(bundle):
    """Check the documented small interchange contract; never validate factual truth."""
    errors = []
    def check(condition, message):
        if not condition:
            errors.append(message)
    def clock(value):
        return (isinstance(value, dict) and set(value) == {"period", "seconds"} and
                isinstance(value["period"], str) and value["period"] in PERIODS and
                finite(value["seconds"]) and value["seconds"] >= 0)
    def clock_key(value):
        return PERIODS[value["period"]], value["seconds"]
    def interval(value):
        return isinstance(value, list) and len(value) == 2 and all(finite(n) for n in value) and 0 <= value[0] <= value[1] <= duration
    def refs(value):
        return (isinstance(value, list) and all(isinstance(x, str) and x in sources for x in value)
                and len(value) == len(set(value)))
    if not isinstance(bundle, dict):
        return ["Bundle must be an object"]
    check(set(bundle) == {"schema_version", "media", "sources", "records"}, "Unexpected/missing top-level fields")
    check(bundle.get("schema_version") == VERSION, "Unsupported schema version")
    media = bundle.get("media", {})
    if not isinstance(media, dict):
        return errors + ["media must be an object"]
    duration = media.get("duration_seconds")
    check(finite(duration) and duration > 0, "Invalid media duration")
    if not finite(duration) or duration <= 0:
        return errors
    check(bool(re.fullmatch(r"[0-9a-f]{64}", str(media.get("sha256", "")))), "Invalid media SHA256")
    check(finite(media.get("pts_origin_seconds")), "Invalid PTS origin")
    check(media.get("origin") in ("real", "synthetic_test", "synthetic_demo", "unknown"), "Invalid media origin")
    check(media.get("license_status") in ("unknown", "permitted", "restricted"), "Invalid license status")
    raw_sources, rows = bundle.get("sources"), bundle.get("records")
    if not isinstance(raw_sources, list) or not isinstance(rows, list):
        return errors + ["sources and records must be arrays"]
    if any(not isinstance(s, dict) or not isinstance(s.get("id"), str) for s in raw_sources + rows):
        return errors + ["All sources and records require string IDs"]
    sources = {s["id"]: s for s in raw_sources}
    records = {r["id"]: r for r in rows}
    check(len(sources) == len(raw_sources) and len(records) == len(rows), "Duplicate source/record IDs")
    for source in raw_sources:
        check(source.get("kind") in ("frame", "transcript", "report", "roster", "calibration", "generation_plan"), source["id"] + ": invalid source kind")
        check(isinstance(source.get("locator"), str) and bool(source["locator"]), source["id"] + ": missing locator")
        check(source.get("available_at") is None or clock(source["available_at"]), source["id"] + ": invalid availability")
        if source.get("kind") == "calibration":
            check(interval(source.get("scope_media_interval")), source["id"] + ": invalid calibration scope")
    for row in rows:
        label = row["id"] + ": "
        allowed = {"id", "kind", "provenance", "source_refs", "media_interval", "match_clock",
                   "reported_minute", "available_at", "subject", "position", "note",
                   "scope_media_interval", "clock_uncertainty_seconds", "clock_anchor_ref"}
        required = allowed - {"scope_media_interval", "clock_uncertainty_seconds", "clock_anchor_ref"}
        check(required <= set(row) <= allowed, label + "unexpected/missing record fields")
        kind, provenance = row.get("kind"), row.get("provenance")
        check(kind in ("transcript", "clock_anchor", "event", "identity", "position", "gap"), label + "invalid kind")
        check(provenance in ("observed", "reported", "estimated", "generated", "unknown"), label + "invalid provenance")
        source_refs = row.get("source_refs")
        if not refs(source_refs):
            errors.append(label + "invalid source_refs")
            continue
        if provenance != "unknown":
            check(bool(source_refs), label + "evidence needs sources")
        if provenance != "generated":
            check(all(sources[r]["kind"] != "generation_plan" for r in source_refs), label + "generated plan cannot support factual evidence")
        media_interval, match_clock = row.get("media_interval"), row.get("match_clock")
        available = row.get("available_at")
        check(media_interval is None or interval(media_interval), label + "invalid media interval")
        check(match_clock is None or clock(match_clock), label + "invalid match clock")
        check(available is None or clock(available), label + "invalid availability")
        if clock(available):
            if clock(match_clock):
                check(clock_key(available) >= clock_key(match_clock), label + "release precedes evidence")
            for ref in source_refs:
                source_time = sources[ref].get("available_at")
                check(clock(source_time) and clock_key(available) >= clock_key(source_time), label + "release precedes/assumes source availability")
        minute = row.get("reported_minute")
        check(minute is None or isinstance(minute, str) and bool(re.fullmatch(r"\d{1,3}(?:\+\d{1,2})?", minute)), label + "invalid reported minute")
        if kind == "clock_anchor":
            check(provenance == "observed" and interval(media_interval) and media_interval[0] == media_interval[1] and clock(match_clock), label + "clock anchor needs observed point/clock")
            check(interval(row.get("scope_media_interval")) and interval(media_interval) and row["scope_media_interval"][0] <= media_interval[0] <= row["scope_media_interval"][1], label + "invalid clock scope")
            check(finite(row.get("clock_uncertainty_seconds")) and row["clock_uncertainty_seconds"] >= 0, label + "missing clock uncertainty")
            check(any(sources[r]["kind"] == "frame" for r in source_refs), label + "clock needs frame evidence")
        elif media_interval is not None and match_clock is not None:
            anchor_ref = row.get("clock_anchor_ref")
            anchor = records.get(anchor_ref) if isinstance(anchor_ref, str) else None
            valid_anchor = (anchor and anchor.get("kind") == "clock_anchor" and interval(anchor.get("scope_media_interval")) and interval(anchor.get("media_interval")) and clock(anchor.get("match_clock")) and finite(anchor.get("clock_uncertainty_seconds")))
            check(bool(valid_anchor), label + "missing scoped clock anchor")
            if valid_anchor and interval(media_interval) and clock(match_clock):
                scope = anchor["scope_media_interval"]
                check(scope[0] <= media_interval[0] <= media_interval[1] <= scope[1], label + "outside clock scope")
                check(anchor["match_clock"]["period"] == match_clock["period"], label + "clock period mismatch")
                expected = anchor["match_clock"]["seconds"] + media_interval[0] - anchor["media_interval"][0]
                check(abs(match_clock["seconds"] - expected) <= anchor["clock_uncertainty_seconds"], label + "clock mapping mismatch")
                if clock(available):
                    anchor_available = anchor.get("available_at")
                    check(clock(anchor_available) and clock_key(available) >= clock_key(anchor_available), label + "release precedes/assumes clock evidence availability")
        position, subject = row.get("position"), row.get("subject")
        if provenance == "observed":
            check(interval(media_interval) and any(sources[r]["kind"] == "frame" for r in source_refs), label + "direct observation needs frame/interval")
        if kind == "position" and provenance != "unknown":
            check(isinstance(position, dict), label + "known position needs coordinates")
        if provenance == "generated":
            check(kind == "position" and available is None and match_clock is None, label + "generated motion cannot assert/release facts")
            check(any(sources[r]["kind"] == "generation_plan" for r in source_refs), label + "generated motion needs plan")
            check(isinstance(subject, dict) and isinstance(subject.get("track_id"), str)
                  and subject["track_id"].startswith("generated:"), label + "generated slots need separate IDs")
        if kind == "gap" or provenance == "unknown":
            check(position is None and bool(row.get("note")), label + "unknown/gap needs reason and no position")
        if position is not None:
            if not isinstance(position, dict):
                errors.append(label + "position must be an object")
                continue
            check(kind == "position", label + "coordinates on non-position record")
            check(position.get("space") in ("screen_normalized", "pitch_normalized"), label + "invalid coordinate space")
            check(all(finite(position.get(k)) and 0 <= position[k] <= 1 for k in ("x", "y")), label + "invalid coordinates")
            check(position.get("height") is None, label + "v1 does not support measured/generated height")
            check(position.get("vertical_state") in ("unknown", "ground_supported", "airborne"), label + "invalid vertical state")
            if position.get("space") == "pitch_normalized" and provenance != "generated":
                calibration = position.get("calibration_refs")
                check(provenance == "estimated" and position.get("vertical_state") == "ground_supported", label + "pitch projection needs estimated ground support")
                valid_calibration = refs(calibration) and bool(calibration) and all(sources[r]["kind"] == "calibration" for r in calibration)
                check(valid_calibration, label + "missing calibration evidence")
                if valid_calibration:
                    for ref in calibration:
                        dependency = sources[ref]
                        scope = dependency.get("scope_media_interval")
                        check(interval(scope) and interval(media_interval) and scope[0] <= media_interval[0] <= media_interval[1] <= scope[1], label + "outside calibration scope")
                        if clock(available):
                            dependency_available = dependency.get("available_at")
                            check(clock(dependency_available) and clock_key(available) >= clock_key(dependency_available), label + "release precedes/assumes calibration evidence availability")
            if provenance == "observed":
                check(position.get("space") == "screen_normalized" and any(sources[r]["kind"] == "frame" for r in source_refs), label + "observed coordinates need screen/frame evidence")
        if subject is not None:
            if not isinstance(subject, dict):
                errors.append(label + "subject must be an object")
                continue
            track, player = subject.get("track_id"), subject.get("player_id")
            check(all(value is None or isinstance(value, str) and bool(value) for value in (track, player)), label + "invalid subject IDs")
            if kind == "position" and player:
                check(isinstance(track, str) and bool(track), label + "named position needs track ID")
            if track and player and kind != "identity":
                joins = subject.get("identity_refs", [])
                check(isinstance(joins, list) and bool(joins), label + "named track needs identity evidence")
                for join in joins if isinstance(joins, list) else []:
                    evidence = records.get(join) if isinstance(join, str) else None
                    joined_subject = evidence.get("subject") if evidence else None
                    identity_kind = "reported" if provenance == "generated" else "observed"
                    valid = (evidence and evidence.get("kind") == "identity" and isinstance(joined_subject, dict)
                             and joined_subject.get("track_id") == track and joined_subject.get("player_id") == player
                             and evidence.get("provenance") == identity_kind)
                    check(bool(valid), label + "invalid identity join")
                    if valid and provenance == "generated":
                        evidence_sources = evidence.get("source_refs")
                        check(refs(evidence_sources) and any(sources[r]["kind"] == "roster" for r in evidence_sources), label + "generated named slot needs roster")
                    if valid and provenance != "generated":
                        scope = evidence.get("media_interval")
                        check(interval(scope) and interval(media_interval) and scope[0] <= media_interval[0] <= media_interval[1] <= scope[1], label + "identity outside observed scope")
                        if clock(available):
                            joined_available = evidence.get("available_at")
                            check(clock(joined_available) and clock_key(available) >= clock_key(joined_available), label + "release precedes/assumes identity evidence availability")
    return errors


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("preflight")
    item = commands.add_parser("manifest")
    item.add_argument("--source", required=True)
    item.add_argument("--output", required=True)
    item.add_argument("--source-page")
    item.add_argument("--license-status", choices=("unknown", "permitted", "restricted"), default="unknown")
    item.add_argument("--license-note", default="Not reviewed")
    item.add_argument("--origin", choices=("real", "synthetic_test", "synthetic_demo", "unknown"), default="unknown")
    for command in ("frame", "audio"):
        item = commands.add_parser(command)
        item.add_argument("--source", required=True)
        item.add_argument("--output", required=True)
        item.add_argument("--timeout", type=float, default=300)
        if command == "frame":
            item.add_argument("--seconds", type=float, required=True)
            item.add_argument("--max-drift", type=float, default=0.25)
        else:
            item.add_argument("--start", type=float, required=True)
            item.add_argument("--duration", type=float, required=True)
    item = commands.add_parser("asr")
    item.add_argument("--audio", required=True)
    item.add_argument("--model", required=True)
    item.add_argument("--output", required=True)
    item.add_argument("--language", required=True)
    item.add_argument("--media-offset", type=float, default=0)
    item.add_argument("--chunk-seconds", type=float, default=300)
    item.add_argument("--overlap", type=float, default=2)
    item.add_argument("--timeout", type=float, default=300)
    item.add_argument("--check", action="store_true")
    item = commands.add_parser("validate")
    item.add_argument("bundle")
    return result


def main():
    args = parser().parse_args()
    try:
        if args.command == "preflight":
            output = preflight()
        elif args.command == "manifest":
            output = manifest(args)
        elif args.command in ("frame", "audio"):
            output = extract(args)
        elif args.command == "asr":
            output = transcribe(args)
        else:
            errors = validate(load(args.bundle))
            output = {"valid": not errors, "errors": errors, "factual_truth_verified": False}
        print(json.dumps(output, indent=2, ensure_ascii=False, allow_nan=False))
        return 1 if output.get("valid") is False or output.get("status") == "blocked" else 0
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(json.dumps({"error": str(error), "complete": False}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
