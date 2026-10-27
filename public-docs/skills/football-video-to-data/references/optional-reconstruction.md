# Optional authored reconstruction

Use when the user requests or approves a smooth plausible illustration, including whole-match players-and-ball motion. This is a separate deliverable from evidence extraction. Label the view and exported trajectories `generated`; keep source anchors and estimates separately accessible. Broadcast footage does not recover offscreen positions, passes or actual whole-match movement.

## Input and design

Pin a source/evidence bundle and official fixture-date roster. Use 22 named slots only when two starting XIs are actually supported; otherwise ask for the material roster decision or use explicitly unidentified slots within agreed scope. Source-supported substitutions change occupants of stable generated slots; don't invent named substitutions, cards, goals or assists. Unknown formations/tactical roles remain unknown. Conflicting reports stay attributed.

Implement a compact deterministic generator using a fixed seed and versioned configuration. A pure `state_at(cursor, config, seed)` should return the same positions after play, pause, seek, rewind or reload. Separate factual event staging from generated motion. Generate plausible possession phases, carries/passes, off-ball runs and regrouping around cited anchors; preserve continuity and boundary conditions. This is a construction plan, not a bundled replay UI or physics model.

One authoritative cursor drives players, ball, scoreboard, roster, selection and eligible answers. Include play/pause, speed, seeking, start/end, halves and explicit half-time pause/change of ends. Source-supported substitutions should preserve slot continuity while switching named occupants. Keep end-of-illustration separate from a verified final whistle. Added-time and goal staging conventions must be explicitly authored unless exact source clocks establish them.

Reported minutes are intervals with unknown seconds, not exact goal timestamps. A defensible conservative illustration convention stages a report at the end of its minute window and labels that choice; it does not change the original claim. No global media/football-clock offset is implied. Decide whether a completed-match event index is explicitly retrospective while cursor questions remain cutoff-safe.

## Acceptance checks

Compute checks over every segment boundary and enough samples to reveal jumps: fixed-seed repeatability; pitch bounds; finite positions; continuous players/ball; intended speed bounds; supported roster count; no duplicate active identities; correct source-backed substitutions; consistent clock/score/cursor; half-time behavior and pause/seek equivalence. A jump found by tests calls for a generator fix, not a weaker movement threshold.

Actual football statistics must never be derived from generated paths: no real speed, distance, possession, pass count, assist, contact time or tactical conclusion follows. Generated scene questions say they describe an illustration. A factual query uses only eligible cited evidence available by the cursor; missing facts return unknown.

If this becomes a user-facing app change, test its real running flow with applicable project browser instructions. Do not create a fake host merely to satisfy dogfood. Sampled browser playback proves only the inspected flows, not full-match viewing or physical/football accuracy.

Deliver versioned generated config/seed, source references, qualification/unknowns and a frozen verification receipt alongside the view. No external publication or source-use clearance follows from creating the illustration.
