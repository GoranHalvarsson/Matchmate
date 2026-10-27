# From video to replay

The vision is for incoming live football data to update the pitch, calculated statistics, timely insights and conversation together. A fan could follow the match as it unfolds, pause to ask what happened, and get an explanation grounded in the evidence available at that point.

Today's recorded tracking sample and generated full-match illustration exercise parts of that shared path. They demonstrate replay, calculations and conversation over prepared inputs; they do not establish an operating live video-analysis service or a connected real-time Premier League feed.

## The workflow

1. **Combine video, commentary and official records.** Start with an authorized recording or accessible replay. Review commentary alongside the footage, using a transcript to locate useful passages. Add fixture-date lineups, substitutions and match reports as separately attributed evidence. A report can support an event without proving that a particular person or contact is visible in a frame.

2. **Align the clocks.** Keep video timestamps, elapsed recording time, football period/clock and reported event minutes separate. Scoreboard anchors help connect them, but cuts, replays and half-time can break a simple offset. Preserve the original precision: a reported minute does not reveal an exact second.

3. **Annotate identities, events and visible positions.** Review the relevant frames and surrounding action. Join a player name only where readable identity evidence or a reviewed continuation supports it. Record supported events and, where camera geometry and visibility allow, qualified position estimates. An object track number is not a named player; offscreen players and unsupported ball positions remain unknown.

4. **Keep uncertainty and gaps.** Retain conflicting accounts, uncertain identities, occlusion and intervals the camera cannot support. Review samples and validate the evidence structure before handoff. Validation can catch inconsistent records; it cannot prove that an annotation is true. Do not fill extraction gaps with invented observations or call sampled review a complete human viewing.

5. **Optionally author a separate movement illustration.** When a smooth replay is wanted, a deterministic generator can create plausible player and ball movement around supported event anchors. Label those paths, exact staging and derived replay statistics as generated. The current Brentford–Chelsea view uses this approach. Generated distance, passes or control describe the illustration and cannot establish actual performance or explain a real tactical cause by themselves.

6. **Import through an adapter into replay and chat.** A Matchmate adapter converts prepared records into the common contract while preserving source, identity, timing, quality and generated labels. Validation and Worker processing feed the API used by the pitch, calculations, insights and conversation. The viewer's replay cursor limits eligible current-clock evidence. The portable extraction outputs still need this application-specific adapter; the skill is not a one-command Matchmate importer.

This joins evidence and presentation without pretending the broadcast contains everything. Read [Data and sources](DATA-AND-SOURCES.md) for the current inputs and [Capabilities and limitations](CAPABILITIES-AND-LIMITATIONS.md) for what the prototype can support today.

## Tools behind the preparation

The included [football-video-to-data skill](skills/football-video-to-data/SKILL.md) describes the workflow and supplies a small Python helper, `scripts/media_tools.py`:

| Tool or activity | What it contributes |
| --- | --- |
| Python helper and FFmpeg/FFprobe | Readiness checks, a hashed media manifest, timestamped frame samples, audio extraction and receipts for review |
| Optional local faster-whisper transcription | Commentary candidates and timestamps from an already available local model; raw results need review because recognition can omit or mishear speech |
| Browser, frame and source review | Inspection of the accessible player, actual images and attributed records; this is separate from running transcription |
| Evidence validation | Structural and conservative timing, identity and provenance checks, with remaining gaps recorded |

The helper does not install packages, download footage or fetch model weights. Media operations require existing FFmpeg/FFprobe tools; transcription additionally requires the optional local engine and compatible model. The references discuss bounded calibration and tracking trials, but the bundle contains no pretrained weights, continuous tracking service or generic automatic recovery of all 22 named players. The optional movement generator and Matchmate adapters are separate application work.

The bundle contains instructions, helper code, tests and a synthetic contract example. It contains no match video, transcript, player dataset or model. Accessible playback does not itself grant permission to redistribute or submit source material; source-use decisions stay separate from the technical workflow.

## From prepared recordings to live inputs

A permitted **structured live feed** would need an adapter that maps incoming observations into the shared records, preserving source clocks, availability, revisions and coverage. The intended downstream path is the same processing, calculations, pitch, insights and conversation described in the [architecture](ARCHITECTURE.md). A viewer pause must remain separate from ingestion of new observations.

**Live video alone** needs an additional continuous analysis layer: decoding, camera calibration, detection, tracking, identity association, event review and quality control before it can supply those records. Camera cuts and unseen players still impose evidence limits. That integration is not implemented, and the offline preparation helpers do not supply it.

The recorded and generated demonstrations prove bounded parts of the downstream experience. Live-source access, integration, sustained processing and real-time quality still need their own implementation and verification. The [roadmap](ROADMAP.md) keeps that work separate from what is available now.
