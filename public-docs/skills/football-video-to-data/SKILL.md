---
name: football-video-to-data
description: Extract timestamped commentary, roster and event evidence, clock anchors, qualified position estimates and player-contribution briefs from football video files or accessible replay URLs. Use for footage analysis or planning a separately labelled generated replay; this is not an automated live tracking service.
---

# Football video to data

Turn football footage into reviewable evidence, then optionally build a clearly labelled generated replay. A broadcast cannot recover players outside its view. Missing positions stay unknown in extraction; invented motion belongs only in the optional illustration.

This folder is a portable skill, not an application adapter, replay UI, or ready real-time computer-vision engine. Its Python helper creates media receipts, extracts frames/audio, optionally runs local speech recognition, and validates a small evidence contract. Frame review, identity joins, calibration, football interpretation and integration require separate work.

## Required tools

| Task | User-provided tools |
| --- | --- |
| Validate evidence and run offline tests | Python 3.10+; standard library only |
| Inspect local media; extract frames/audio | FFmpeg and FFprobe on PATH |
| Inspect a replay URL and official records | An accessible browser or page reader, with authorized access |
| Review pixels or listen to selected audio | A visual inspector and, when needed, an audio player; record who or what inspected each sample |
| Optional speech recognition | Installed `faster-whisper`/CTranslate2 and a compatible existing local model directory |
| Optional contact sheets or estimated positions | Existing Pillow/OpenCV or a separately chosen detector/calibration stack; see the relevant reference |

Nothing here automatically installs tools, fetches models, downloads media, or supplies provider access. A missing dependency is a reported limitation. Public playback does not grant reuse or redistribution rights. A `permitted` license-status value records a reviewed assertion; it does not grant a license.

## Choose the deliverable

- For transcription, event/roster review, source acquisition, or frame sampling, read [extraction and tools](references/extraction-and-tools.md).
- Before producing structured evidence, read [the data contract](references/data-contract.md), including its small synthetic example. Its validator checks declared structure and conservative provenance/time/identity invariants; it cannot establish factual truth.
- For source-backed player contributions, read [player-performance review](references/player-performance.md).
- For estimated pitch positions, read [tracking and quality control](references/tracking-and-qc.md). Trial representative camera views before committing to a long run.
- Only when requested or approved, read [optional reconstruction](references/optional-reconstruction.md) for generated players-and-ball movement around cited source anchors.

## Workflow

1. Identify the source, authorized access, source-use status, requested coverage and output. For a URL, inspect the ordinary accessible page/player; acquire only an authorized accessible resource. Keep credentials, signed URLs and raw media out of shared artifacts. Stop on access/encryption restrictions.
2. Run `scripts/media_tools.py preflight`, then create a hashed local media manifest. Check existing receipts before processing. Keep source identity, origin, versions, coverage and use constraints with the run.
3. When commentary is needed, reuse a receipt-backed ASR result with matching media, model, engine and settings. Otherwise run an unprompted pass over the requested coverage using an explicit local model path. Preserve raw spelling and chunk results; use commentary candidates to target frames. Machine transcription is neither human listening nor proof of the commentator's claim.
4. Review actual frames and relevant audio where available. Align media PTS, elapsed media time and period-local match time only through reviewed clock anchors with bounded scopes. Replays, edits, half-time and clock resets need separate treatment; a reported minute never supplies an exact second.
5. Join official fixture-date rosters/reports as a separate attributed layer. A roster confirms possible identities; a readable shirt/name and defensible visual continuity support a bounded tracklet join. Keep unknown actors, disputed contacts, repeated footage and missing positions visible.
6. Validate the evidence bundle and freeze a handoff receipt. Report inspected samples, decoded/submitted coverage, hashes, versions, failures, assumptions, gaps and use status. Respect the task's agreed budget and stopping condition; do not silently rerun expensive inference.

Keep origin (`real`/synthetic), method (`observed`/`reported`/`estimated`/`generated`/`unknown`) and delivery (recorded replay/live) distinct. Do not label recorded media as live. Event time, source availability and authored replay time are different. Unknown availability cannot support known-then claims. Consumers must withhold future facts, later corrections and dependencies that were unavailable at their cutoff. Compute factual statistics only from eligible evidence in code; generated paths never supply actual football facts.

## Output and synthetic example

Deliver the contract's `schema_version`, `media`, `sources` and `records` in an evidence JSON file. Link the media manifest, frame/audio/ASR receipts and a concise review receipt describing coverage, sample locations, identity/clock decisions, unresolved gaps and validation results. A player brief links those evidence IDs; a generated reconstruction has its own versioned seed/configuration and persistent `generated` label. The helper does not create the reviewed evidence bundle or an app adapter automatically.

[tests/fixtures/evidence-valid.json](tests/fixtures/evidence-valid.json) is a small synthetic contract example: a clock anchor, one bounded identity and estimated position, an airborne screen-only ball, commentary/report/roster claims, a camera gap and one generated point. Its all-zero hash, locators, identities and observations are invented test values; there is no associated match, media or official record. Validation demonstrates structure, not evidence collection or football accuracy.

Run these from this skill directory with an available Python 3.10+ interpreter. Replace filenames and the illustrative local model path with your own permitted inputs; outputs belong in the task workspace:

```sh
python scripts/media_tools.py preflight
python scripts/media_tools.py manifest --source match.mp4 --output run/media.json --origin real --license-status unknown
python scripts/media_tools.py frame --source match.mp4 --seconds 12.4 --output run/frame-12.4.png
python scripts/media_tools.py audio --source match.mp4 --start 0 --duration 120 --output run/commentary.wav
python scripts/media_tools.py asr --audio run/commentary.wav --model local-models/whisper-model --output run/asr --language en --check
python scripts/media_tools.py validate tests/fixtures/evidence-valid.json
python -B -m unittest discover -s tests -v
```

`--check` reports ASR readiness without loading a model. Remove it to run recognition after readiness and scope are confirmed. Run outputs may contain source-derived/private information; review them separately before sharing. No external publication, paid processing, application integration or source-use clearance follows from using this skill. Follow the host's applicable instructions without imposing a particular agent, model, workstation or deployment choice.
