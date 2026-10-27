# Evidence contract 1.0

This small interchange contract is for a task's extraction/review handoff. It is not an application's runtime schema, a complete sports ontology, or a truth checker. An application adapter must separately map it into the host's validated contract without losing provenance, identities, time precision, availability or gaps. Run `python scripts/media_tools.py validate bundle.json`. The included [synthetic example](../tests/fixtures/evidence-valid.json) is executable documentation with invented claims and no associated media. The CLI uses only the standard library and rejects non-finite numbers/duplicate JSON keys.

## Bundle and provenance

Required top-level fields: `schema_version` (`"1.0"`), `media`, `sources`, `records`.

`media` contains `sha256` (lowercase 64 hex), `duration_seconds` (positive), `pts_origin_seconds` (finite, may be negative), `origin` (`real`, `synthetic_test`, `synthetic_demo`, `unknown`), and `license_status` (`unknown`, `permitted`, `restricted`). Add source/license receipts alongside the bundle; a permitted value is a recorded assertion, never an automatic clearance check.

Each `sources` item has a unique string `id`, `kind`, nonempty `locator`, and `available_at`. Kinds: `frame`, `transcript`, `report`, `roster`, `calibration`, `generation_plan`. Locators refer to actual local receipts, exact frame/ASR IDs or public citations. Keep session-bearing URLs out of portable artifacts. A source's availability is a reviewed clock object or null; null means unknown/retrospective-only for live cutoff purposes.

Calibration sources also require `scope_media_interval`, a bounded `[start,end]` interval of elapsed media seconds with reviewed camera support. Each projected position must lie within every cited calibration's scope. A point calibration supports that instant only; it does not silently extend across camera movement or cuts.

Each record has all these fields; use null/empty arrays where information is missing:

| Field | Meaning |
| --- | --- |
| `id` | Unique stable evidence ID |
| `kind` | `transcript`, `clock_anchor`, `event`, `identity`, `position`, `gap` |
| `provenance` | `observed`, `reported`, `estimated`, `generated`, `unknown` |
| `source_refs` | IDs in `sources`; required for every non-unknown row |
| `media_interval` | `[start,end]` elapsed source-media seconds within duration, or null |
| `match_clock` | `{"period":"1H","seconds":10}` or null; seconds elapsed within that period |
| `reported_minute` | String such as `"61"` or `"90+4"`; seconds remain unknown |
| `available_at` | Earliest defensible period/clock for factual release, or null |
| `subject` | `{"track_id":null,"player_id":null,"identity_refs":[]}` or null |
| `position` | Coordinate object below, or null |
| `note` | Brief claim, qualification or reason for a gap |

These are distinct provenance classes, not confidence scores. `observed` means reviewed pixels at a cited scope; `reported` includes attributed commentary/official report/roster claims; `estimated` includes calibrated uncertain positions; `generated` means authored illustration; `unknown` contains a reason and no coordinates. ASR is machine recognition of speech, never proof that the commentator's football claim happened. Report conflicts remain separate rows. A gap is not zero, explicit absence, or evidence of an uneventful interval. Entirely unknown positions are valid outputs, not a reason to invent coverage.

## Timing and cutoff

Media elapsed time is source PTS minus the declared origin. ASR derivative offsets and actual decoded frame PTS belong in their own receipts. A period clock uses `1H`, `2H`, `ET1`, `ET2`, `PSO`; its seconds start at zero for each period (so second-half elapsed 60 corresponds to displayed 46:00 in ordinary play). Preserve displayed clock text separately in the cited observation. Reported minutes are claims, not clock conversions; an 83rd-minute report normally describes the 82:00–82:59 interval but does not establish a contact second.

A `clock_anchor` is an observed point: equal media interval endpoints plus a reviewed `match_clock`, frame source, nonnegative `clock_uncertainty_seconds`, and `scope_media_interval`. The scope is a reviewed continuous section where a 1× mapping is defensible, excluding replay/edited breaks. Other records that contain both media and match clock require `clock_anchor_ref` and must lie inside that anchor's scope/period and mapping uncertainty. A single displayed-clock point does not authorize a whole-match scope. Use separate anchors/scopes around half-time, replays, edits or clock resets; withhold a mapping when unverified.

`available_at` must be at least the record clock and every cited source's availability, compared by period then seconds. This includes all `position.calibration_refs`, whether or not duplicated in `source_refs`; each must resolve to a calibration source. If any required source availability is unknown, keep the record availability null and use it only retrospectively. A consumer must enforce this predicate and the underlying source endpoint coverage; the validator cannot discover a falsely declared availability. Estimated retrospective reconstruction that depends on later frames must wait for those frames. Generated positions never enter factual eligibility.

## Identity and coordinates

`track_id` is a bounded anonymous association, never automatically a named player. An observed named `identity` row cites readable pixel evidence and its actual media scope. A named position must cite that row in `subject.identity_refs`, match both IDs and stay inside its scope. The validator rejects a point join copied across an unseen gap. Human review must still establish that the cited text/continuation is defensible. Official rosters validate possible names/shirts, not visual identity by themselves.

Position object: `space` (`screen_normalized` or `pitch_normalized`), `x`/`y` in [0,1], `height` null, `vertical_state` (`unknown`, `ground_supported`, `airborne`), and `calibration_refs` (source IDs, empty for screen-only). Axes: screen x right/y down; pitch x along the agreed field axis/y across it, with orientation/dimensions in the calibration receipt.

Observed coordinates require frame evidence and screen space. Real pitch positions are `estimated`, require cited calibration and ground support, and retain uncertainty in the receipt/note. A screen ball can be airborne with unknown height; this does not authorize ground coordinates. Contract 1.0 deliberately supports no height coordinates. No hold/interpolation across gaps is implied.

Generated positions require a `generation_plan` source, a `generated:` track namespace, and null factual `match_clock`/`available_at`. Their media interval may be null because an authored replay clock is not media time. Named generated slots cite separate `reported` identity rows backed by roster sources; these assign illustrated slots without identifying observed tracklets. Generated movement never supports factual evidence rows or statistics. Full replay configuration/seed and illustration clock belong in the separate reconstruction artifact.

## Limits

The validator catches structural, timing, scope, source-reference, coordinate and basic provenance errors. It does not read pixels/audio, verify citations/licenses, measure calibration accuracy, discover hidden future information, guarantee exhaustive event coverage, or calculate match statistics. Freeze the actual bundle, source artifacts, sample-review receipt and validator result together. Preserve unknowns, failed gates and separate corrections instead of making the validator's pass a completeness claim.

## Read the synthetic example

The [fixture](../tests/fixtures/evidence-valid.json) contains invented values only; its all-zero media hash is a test sentinel, not an input fingerprint. No video, audio, calibrated pitch, person or official match record accompanies it.

- `clock-1` maps elapsed media 10 seconds to first-half 0 seconds within a declared synthetic continuous scope. It does not authorize mapping another period or a camera cut.
- `identity-1` supports `shot1-personA` as `synthetic-player-1` only at elapsed 20 seconds. `person-point` uses that point identity and point calibration; neither can be carried across the gap.
- `ball-screen` supplies only screen coordinates with unknown height. `commentary-1` is a raw machine-transcript claim with unknown football clock and availability. `reported-goal` supplies a reported minute with unknown seconds and availability.
- `gap-1` withholds a camera-cut interval. `roster-slot-1` assigns a synthetic roster identity to `generated:slot-1`, separately from observed tracklets. `illustration-point` is generated and has no factual release clock.

The example demonstrates the interchange format and its separations, not an automated end-to-end analysis result or real-data permission. Replace every source, fingerprint, identity and observation with reviewed material for an actual run.
