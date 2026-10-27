# Offline generated-passage audit

Run from the repository root with Node 24 or later:

```powershell
node --test tools/tests/story-passages.test.cjs
node tools/story-passages/run.cjs
```

This reads the **existing** Chelsea `generated-model-actions-v1` export and its archived canonical batches. It never executes the generator, imports recordings, calls providers, or changes the app. Required local inputs are `.local/shared-inputs/brentford-full-v5/` and `.local/canonical-brentford-v5/`; absent inputs fail explicitly. Liverpool has no supported action capability.

Outputs stay under ignored `.local/story-passages-trial/`: exact core source byte copies, before/after SHA-256 receipts for every read input, a flat snapshot, all passages, one representative per exact fact signature, evidence manifest and summary. Rerunning replaces only these audit outputs. Archive chunk hashes are verified against the existing export index. This establishes an offline hash-verified archive, **not** current live-ledger acceptance, full schema validation, or a known-then query.

The module accepts only a closed, active revision-1 flat snapshot at its exact archived head. Correction chains, withdrawals, duplicate IDs, manifest revisions, knowledge-as-of and partial heads are rejected. The real ledger must resolve those before a future production projection; this spike does not reproduce that resolver. Source and snapshot fingerprints, fixture/dataset/version/stream, presentation policy/revision and head must match the query. Every returned member has its action ID, revision, fingerprint and evidence IDs. Canonical record fingerprints use this audit's JSON serialization, not the app codec.

One completed team-control spell is one passage; spells never merge across turnovers, periods or gaps. A pass/shot belongs to exactly one containing same-team, same-period spell. Turnovers and shots must terminate the spell. Unsupported contradictory intervals fail closed. Audit caps are 60 seconds and 12 actions per spell; oversized spells are rejected rather than silently truncated. The existing input fits these caps. A passage appears only at the maximum completion/release of its spell and all members; rewind removes it. Pending final labels/counts/durations are never returned. Missing capability/invalid authority returns `unavailable` with null facts, distinct from an available empty prefix with zero counts. Pass completion percent is null when no attempts exist.

`provisional-count-rubric-v1` is frozen before output. Its nonoverlapping definitions are:

| Label | Exact rule |
| --- | --- |
| Shot-ending passage | One or more explicit shots, with the final action a shot; takes precedence. |
| Passing sequence | No shots and at least three completed passes. |
| Brief exchange | No shots and one or two completed passes. |
| Unclear | Valid evidence with no shots and no completed passes. |

The three-pass boundary is a provisional editorial choice, not a learned or validated football threshold. The deterministic baseline and a second expression of these rules are compared on every eligible passage. Agreement checks implementation consistency, not label accuracy. A sensible future presentation can consolidate Brief exchange and Passing sequence into a passing description with explicit counts; no model is needed to choose either provisional bucket.

Diversity uses ordered action-kind/outcome patterns and exact duration/count patterns, stripping record/person/slot IDs, team, period and absolute start time. These are optimistic diversity bounds: six routine duration variants do not establish six independent football situations. No representative is called held out after inspecting the corpus. Synthetic boundary fixtures live only in the test file and are never mixed into football-label cases. No source regeneration or padding creates 24 cases. No model-quality or accuracy result is claimed; the utility gate stops before a provider trial when code captures every supported distinction.
