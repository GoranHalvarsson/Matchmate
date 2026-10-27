# Extraction and tools

## Preflight and receipts

The bundled helpers need Python 3.10+; media operations additionally need FFmpeg/FFprobe on PATH. `preflight` reports tools and optional Python modules without importing heavy packages. It does not prove GPU, codec, model, or provider readiness. Use the host's existing Python/isolated environment; do not auto-install anything.

For a local file, `manifest` records SHA-256, bytes, selected FFprobe metadata, source/license fields and a content/settings/tool cache identity. Its `duration_seconds` is container-reported, not verified playable/decoded coverage; a nonzero timestamp origin can affect that field. Check actual derivative endpoints/coverage before asserting complete processing. It excludes embedded tags, filenames from probe output, and session URLs. Optional `--source-page` accepts a public HTTPS page without query, fragment or credentials; keep any full query-bearing locator in a separate private receipt. `--license-status` records `unknown`, `permitted` or `restricted`; it is a user's/researcher's declaration, not legal clearance. Add `--license-note` for the source terms and intended use. Public playback alone does not establish redistribution or submission rights.

For an accessible URL, first inspect the page/player with available browser tools. Confirm the requested recording, duration, ordinary media resources and authorization. Acquire using the site's supported route or ordinary accessible clear media; keep credentials and signed URLs in ignored private artifacts, not commands printed into a shared transcript. FFmpeg can remux a confirmed accessible recording, but the helpers deliberately accept local files only. A failed public resource, authentication requirement, DRM/encryption, or expired URL calls for a supported source or user decision. Do not fabricate a local success or reuse another user's credentials.

Checkpoint identity must cover source bytes, model files, actual engine versions, settings, chunk boundaries and code/algorithm version. Reuse only matching results; a filename or output's existence is insufficient. Preserve failed attempts and their scope, then decide whether another bounded run is justified.

## Frames and audio

`frame` seeks an elapsed media locator, preserves FFmpeg source timestamps with `-copyts`, and records the first decoded `showinfo` PTS. The receipt separates requested elapsed time, source origin, actual PTS and actual elapsed time. It rejects excessive seek drift (default 0.25 s; choose a justified `--max-drift` for low-frame-rate material). VFR, nonzero starts, edits and unequal audio/video endpoints need explicit receipts. An extracted still is a sample, not evidence that it was inspected.

`audio` extracts a requested interval into mono 16 kHz PCM WAV and records actual decoded duration. This is an ASR derivative with a declared source interval; it does not claim exact contact/speech PTS alignment. Both helpers use argument-list subprocesses, explicit stream maps, local paths, timeouts, temporary files and non-overwriting outputs. They write adjacent `.receipt.json` files. No shell interpolation is used.

Equivalent FFmpeg commands for inspection, with values supplied as safe argument lists in automation:

```sh
ffprobe -v error -show_format -show_streams -of json match.mp4
ffmpeg -hide_banner -nostdin -n -ss 12.4 -copyts -i match.mp4 -map 0:v:0 -frames:v 1 -vf showinfo -fps_mode passthrough frame.png
ffmpeg -hide_banner -nostdin -n -ss 0 -i match.mp4 -t 120 -map 0:a:0 -vn -ac 1 -ar 16000 -c:a pcm_s16le commentary.wav
```

Make contact sheets with Pillow when useful: use the default/found local font, label actual receipt PTS, preserve aspect ratio, and retain per-frame hashes. OpenCV can decode/inspect images and evaluate calibration geometry. Neither tool establishes visual truth. Inspect originals at native size when text, contact or a landmark matters.

## Optional local ASR

Use an already cached compatible faster-whisper model directory. `media_tools.py asr` requires `model.bin` and `config.json`, hashes local model files and records installed faster-whisper/CTranslate2 versions. It sets offline environment flags and `local_files_only=True`. A missing module/model is a failed preflight, never permission to download one. Do not pass expected roster names, reports or goal times as recognition hints.

```sh
python scripts/media_tools.py asr --audio commentary.wav --model local-models/faster-whisper-small.en --output run/asr --language en --media-offset 0 --check
python scripts/media_tools.py asr --audio commentary.wav --model local-models/faster-whisper-small.en --output run/asr --language en --media-offset 0
```

`--media-offset` is the source elapsed time at the start of the extracted audio, taken from its receipt; use zero for a full normalized recording. ASR requires zero-start normalized audio and stops on a nonzero origin; use the audio extraction command first. The script records input-relative and offset-adjusted elapsed media timestamps, not source PTS or football-clock time. Default core intervals are 300 s with 2 s overlapping context, CPU/int8, at most four threads, beam 5, VAD, word timestamps and no prior-text conditioning. These are a starting profile, not proven optimal for every language/recording. `--language` is explicit; choose a compatible multilingual model for other languages.

Each raw chunk is saved atomically with a shared cache identity. A canonical transcript assigns each word's midpoint to one half-open core interval; segment midpoint is the fallback when no words exist. Overlapping hypotheses stay in raw files. Recognition can omit, repeat or hallucinate speech, especially at boundaries/crowd noise; coverage means all decoded input was submitted, not that every word was recovered. Diagnostic scores are not calibrated fact confidence. Retain TXT, SRT, raw JSON and separate human corrections with source references.

Interrupted runs reuse only matching chunks whose hashes occur in the progress checkpoint. A chunk written just before a crash but not checkpointed can be recomputed; no silent adoption of an unverified cache occurs. A completed checkpoint hashes the final artifacts. A corrupted/mismatched cache stops instead of overwriting prior work. Use a new output directory for deliberate configuration changes.

## Event and roster review

Read the full transcript once, classify cue hits with adjacent context (current play, replay/repeated description, history, generic/conditional, uncertain), then sample dense event windows. Repeated footage/commentary is not a second incident. Expand ambiguous passages only as needed. Preserve raw ASR spelling and every material report conflict.

Official fixture-date lineups, substitutions, shirt numbers, venue and reports are a separate attributed layer. A readable shirt/name or a reviewed continuation can support a bounded visual identity join. Roster compatibility, ASR similarity, formation expectation or scorer celebration alone cannot identify a person. Reviewer receipts name the actual frames/audio reviewed, reviewer/tool attribution and uncertainty. Never claim full-match human viewing from contact sheets.

## Official upstream references

Recheck the installed version's official documentation when executing unfamiliar commands; pins/weights/licenses belong in run receipts. These links are upstream references, not claims that a particular version is installed or verified:

- [FFmpeg CLI](https://ffmpeg.org/ffmpeg.html), [FFprobe](https://ffmpeg.org/ffprobe.html), [showinfo filter](https://ffmpeg.org/ffmpeg-filters.html#showinfo)
- [faster-whisper source and usage](https://github.com/SYSTRAN/faster-whisper)
- [Pillow Image API](https://pillow.readthedocs.io/en/stable/reference/Image.html)
- [OpenCV camera geometry](https://docs.opencv.org/4.x/d9/d0c/group__calib3d.html)

The helper accepts local media only. URL reading/acquisition, frame inspection, football review and app integration use separately provided tools and are not automated by these commands.
