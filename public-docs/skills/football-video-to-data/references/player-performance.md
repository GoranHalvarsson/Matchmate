# Player-performance evidence review

Use this mode to explain a player's supported contributions from footage, commentary and attributed reports. Start with the requested player or participant group and the material already available. A useful brief supplies concrete examples and proportionate interpretation; it does not need an invented rating or exhaustive human viewing claim.

## Scope and reuse

Distinguish fixture rosters from actual participants. Include starters and substitutes who entered when reviewing everyone who played; keep unused bench players separate. A roster entry alone proves neither an appearance nor a contribution. Preserve substitution uncertainty and do not turn a reported minute into an exact minutes-played total. A completed participant census must not expose later substitutions in an earlier-cursor answer.

Inventory existing media manifests, ASR checkpoints, transcripts, reports, lineups, clock anchors and inspected samples. Reuse results only when their source, engine/model/settings identity and coverage receipts match, as described in [extraction and tools](extraction-and-tools.md). A matching transcript is a search index, not a reason to rerun recognition. Search literal names, plausible ASR variants and adjacent context; variants are candidate locators, not canonical identities. Separate current play, repeated replay, historical discussion and conditional commentary before selecting review windows.

## Review actions and identities

Review targeted original frames at native resolution and relevant audio where available; expand an uncertain window only as needed. Record which samples and audio were actually inspected. Machine transcription, sampled review and complete human viewing remain distinct claims.

Join named observations through readable shirts/names and defensible continuous visual scope, checked against the fixture-date roster. A readable identity at one instant does not identify an unseen earlier contact, an intermediate passer or a player after a cut. A celebration, expected position or similar ASR name is insufficient. Anonymous actors stay anonymous. An attributed report can support a reported action independently of whether wide footage verifies its contacts; preserve that distinction rather than relabelling the report as observed.

Deduplicate repeated footage and repeated commentary about the same incident. Keep accepted, disputed, rejected and unresolved candidates with source locators and reasons. Do not silently promote a withheld offside, foul, marking assignment, intent or blame claim into the brief.

## Keep clocks and availability separate

Follow [the evidence contract](data-contract.md) for media intervals, identity scope and reviewed clock anchors. Source PTS, elapsed media time, derivative ASR offsets, original period-local football clock, displayed clock text, reported minutes and authored replay time serve different purposes. Edited breaks, replay cuts and clock resets prevent a single convenient offset from aligning a whole recording. A clock displayed during a replay may describe current match time rather than the replayed action.

Withhold unknown original action times from cursor-specific facts. Do not fit an arbitrary timestamp or stretch an anchor's reviewed scope to satisfy validation. A supported but unaligned observation may belong in a separately requested completed-match retrospective with explicit timing uncertainty; it must not become an already-happened fact at a paused moment.

An authored viewer cursor and conservative retrospective release boundary are presentation policy, not measured contact time or source availability. When integrating, map a reviewed period-local boundary through the host's existing schedule. Keep unknown contract `available_at` values null; never fill them with inspection time or authored release time. Unknown availability cannot support live or known-then claims.

## Prepare the brief and assessment

Reuse existing contract records and source references for accepted events, identities, positions and gaps where applicable. Keep the logical brief and review receipt linked to those records; do not introduce a parallel interchange schema. Before viewer questions, prepare versioned briefs bound to source fingerprints, canonical identities and reviewed coverage. Preserve time precision, contradictions and qualifications; validation checks structure and declared scope, not factual truth. Keep observed, reported, estimated and generated material distinct; generated illustration cannot support factual assessments.

Distinguish sparse reviewed observations from a complete provider statistical feed. Mention counts, selected examples and repeated clips are not pass, shot, chance or offside totals. Missing statistics are unknown, not zero. Compute statistics in code only when eligible coverage and metric definitions support them. A chance setup is not automatically a formal goal assist.

Lead the assessment with supported contributions and concrete examples, then label football interpretation and give one concise coverage boundary. A reported layoff leading to a chance can illustrate useful link play without establishing an overall performance verdict. Distinguish limited negative evidence from absence of evidence; avoid invented ratings, psychological intent, tactical assignments and individual blame.

## Optional host integration

When integration is requested, map this evidence into the host's common typed contract and evidence projection. Prepare bounded, versioned briefs ahead of viewer queries, bound to fixture, player crosswalk, source fingerprints, coverage and the host's revision/application snapshot. Mismatched or incomplete bindings stay unavailable. Named-player and UI-selected-player paths should consume the same eligible brief.

Do not compensate for missing facts with per-question video scans, repeated ASR, full recording reimports or an extra analysis-model call. Preserve ordinary cursor boundaries and any explicitly authorised retrospective exception without granting unrelated future facts. Check relevant named/UI/follow-up parity, earlier-cursor withholding and actual application answers if integrating. Report source, unit and live verification separately; a prepared example is not proof of live answer quality.
