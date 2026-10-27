# Bounded actual tracking inspection

## Current: approved minimal completed possessions — 2026-10-17

[Separate event importer](import_recorded_events.py) retains only a small curated PP actor/interval selection for the existing pinned match `1874553`, first half 60–240 seconds. The [artifact](../../prototypes/first-experience/assets/tracking/1874553-completed-events.json) and [event receipt](../../prototypes/first-experience/assets/tracking/completed-events-manifest.json) describe the approved scope. Existing tracking JSON, tracking manifest/MIT/importer and historical aggregate reports remain unchanged.

`python -B data/tracking-research/import_recorded_events.py --check` is offline (also the default). Explicit `--fetch` reads only the first 1,001 complete pinned CSV lines, 966,956 exact parser bytes, SHA-256 `77e658496ccf7cf1e08c930fc45e56ac9e8545d558bb7ea326c85a062b1021f8`; no raw prefix/full CSV is retained. Ordinary builds/tests never fetch. First three eligible PP rows per source team, sorted by start frame/end frame/event ID, yields five valid rows, not a forced six. They preserve raw frames/printed clocks and original explicit matching flags, source actor/team joins and full interval finite player coverage. Strict field allowlists discard final targets, outcomes, spatial fields, metrics and scores. The two one-touch rows remain source point intervals.

`dependency.verified` means our interval/frame/clock validation. Its completion clock is an explicitly retrospective replay spoiler cutoff for this minimal projection, not provider-supplied availability, verified live arrival or known-then proof. Other categories and their later dependencies remain deferred. Current verification: 16 Python tracking/import tests pass, including five new importer tests for exact selection/receipt, allowlist stripping, synchronization, identity, full interior coverage, duplicates and tampering. See [prototype verification](../../prototypes/first-experience/README.md). Independent verification passed 64 Node/16 Python tests, 29 syntax checks, both offline import checks, exact portable equality and 24 protected hashes; byte-exact pinned-prefix reproduction produced the same five selected rows. REV-2/F-13 now proves both synthetic extra rows qualify individually and cap removal fails the selected-ID assertion (seven rows instead of five). Final independent review is clean. The local feature and actual static-preview browser gates pass, with the coordinator closing the mobile notes recheck. Full production, physical touch hardware, screen-reader speech and live feeds remain unverified; nothing was published.

## Earlier research checkpoints

## Latest: recorded event semantics review — 2026-10-17

The event mapping review adds primary category/frame/time definitions, exact source/version locators and proposed per-field cursor eligibility. The official tutorial opened the same 2025/02/16, 89-page specification PDF linked by the pinned README; this resolves the earlier access gap without claiming retained PDF bytes/hash, immutability or an exact data-generating version.

The coordinator freshly recaptured `inspect_passage.fetch(write=False)` and compared the **full** normalized report against [passage-summary.json](passage-summary.json), excluding only `observed_on`: exact match, all four receipt hashes and 5,058,830 parser bytes included. The existing 11 Python tracking/import tests pass. All 145 ends, 128/145 original starts and 29 physical starts align; 17 engagement starts remain semantically unresolved despite numerical explanation. All 630 original endpoint role-ID checks join roster/presence, not independently verified identity or coverage through interior gaps. Saved reports, inspectors, tests and imported prototype data are unchanged.

The [local recorded prototype](../../prototypes/first-experience/README.md) was approved and implemented after the earlier research checkpoints below. This review adds no import/runtime events. Completed retrospective annotations after dependency checks are proposed only; no new eligibility/AI tests or coordinate transforms. Earlier sections retain their original no-import and then-unavailable-spec status as dated inspection history, not current prototype status.

## Latest: eventful SkillCorner passage — 2026-10-17

[Passage aggregates](passage-summary.json), [inspector](inspect_passage.py) and [focused tests](test_inspect_passage.py) extend the earlier sample without changing its saved summary or inspector. The selected first-half **01:00–04:00 inclusive** is Brisbane Roar–Adelaide United match `1874553`, the same pinned commit below. It is eventful within the inspected dynamic-event prefix, not a complete official incident feed. No raw data or application import.

```powershell
python data/tracking-research/inspect_passage.py
python data/tracking-research/inspect_passage.py --fetch --no-write
python -m unittest discover -s data/tracking-research -p 'test*.py' -v
```

`--fetch --no-write` independently recaptures measurements without modifying the saved report; `--fetch` saves aggregates. Four exact receipts cover match metadata, the genuine LFS pointer, first 1,001 complete CSV lines (header + 1,000 events) and first 2,411 tracking lines. Total **5,058,830 parser bytes**, under the 20,000,000-byte total per-run cap, not a guarantee about transport buffering. Tracking prefix SHA-256: `172e0c1ab68fb515b93fabbdbc99c4d9cb8d7991c71550a82d5d6fd3d9659ff8`; CSV prefix SHA-256: `77e658496ccf7cf1e08c930fc45e56ac9e8545d558bb7ea326c85a062b1021f8`. Neither is a whole large-file hash.

- **Tracking:** frames 610–2410, 1,801 clocked samples, 1,800 steps of 0.1 seconds; no missing IDs, duplicates or nonmonotonic frame/clock transitions. Measured `frame / 10 − clock = 1.0s` throughout; frame/10 alone is not the match clock.
- **Internal position gaps:** 348 existing clocked frames have zero players and no ball: 1769–1908 (175.9–189.8 seconds inclusive; 140 slots) and 1998–2205 (198.8–219.5; 208 slots). First/last sample spans are 13.9/20.7 seconds; 14.0/20.8 seconds count sampling slots, not exact real-world missing durations. Do not interpolate these as observed positions.
- **Present positions:** 1,453 frames each have 22 finite players, 11 per roster team. All 22 IDs appear in every populated frame; none are unknown/duplicated, with no nonfinite supplied player x/y. This is internal source continuity, not ground-truth identity proof. Player observations: 15,974 detected / 15,992 extrapolated (**50.03% extrapolated**). Ball: 1,178 detected / 275 extrapolated when present. There are 301 nominal-boundary exceedances (x −53.7 to 49.21, y −34.47 to 37.06 metres); retain flags, not clipping or automatic error diagnoses. Metadata says first-half home direction left-to-right; this is source orientation, not independently measured motion.
- **Events:** 145 overlapping inspected-prefix rows: 31 `player_possession`, 69 `passing_option`, 29 `on_ball_engagement`, 16 `off_ball_run`. All are contained by their `frame_start/end` within this interval; 34 shared-start-frame groups remain separate by match-local event ID. The inspected 1,000 rows have monotonic starts, but later unseen rows/category coverage are unknown, so no complete passage-feed claim.
- **Alignment:** all 145 ends align exactly; starts align 128/145. Seventeen engagement starts differ by −0.1 to −4.0 seconds against `frame_start`. All 29 available `frame_physical_start` comparisons align exactly, explaining those 17 numerically without overwriting fields or asserting a universal semantic rule. Example `9_35`: start frame 1672 → 166.2s; physical start 1632 → 162.2s; printed time 02:42.2. The linked specification PDF could not be opened by the web tool; its semantic definitions were not independently inspected.
- **Identity joins/examples:** 630 original-start/end actor/possession/target checks have known roster IDs and actual endpoint presence; no endpoint falls in an empty position span. Interior gaps remain. Six deterministic examples retain category IDs, frame/time, source names, team/shirt and metadata locator: e.g. `8_11`, Bart Vriends (`26766`) targeting Panagiotis Kikianis (`809759`), frames 628–662 / 61.8–65.2s. Targeting is not proof of a received/completed pass. No event-vs-tracking distance comparison: the CSV coordinates require source-specific scaling.

Recommendation is now **conditional gap-aware passage research**, not a continuously complete replay. Keep empty spans, extrapolation and field-basis uncertainty visible. No PL/live source, dataset selection, raw retention or app change. Independent extension review passed without findings: the whole fresh normalized report matched excluding observed_on; all four receipt hashes and 5,058,830 parser bytes matched, eight tests and 141 local links passed. The coordinator also reran eight tests and verified 308 prior data/prototype files plus the three original inspection artifacts unchanged. The earlier three-source review below remains completed at its original scope.

## Previous completed opening-slice checkpoint

2026-10-17. Actual public-source bytes were parsed; only [aggregate findings and byte receipts](inspection-summary.json) are retained. No raw tracking/event dataset, video or body-pose file is vendored, and nothing is imported into the app. This is a technical sample assessment, not selection of an A-League replacement for the intended Premier League experience or shared-submission clearance.

## Reproduce or check

```powershell
python data/tracking-research/inspect_samples.py
python -m unittest discover -s data/tracking-research -p 'test*.py' -v
# Explicit public network inspection; overwrites aggregates, not raw files:
python data/tracking-research/inspect_samples.py --fetch
```

Default mode checks saved summary invariants; it does not independently recapture sources. `--fetch` uses pinned GitHub raw/LFS public URLs and dynamic UTC observation date. Its limit is **5,000,000 bytes returned to the parser per source per run**, not a guarantee about HTTP/TLS buffering or all network traffic. Complete newline-delimited frames/CSV lines are read, then streams closed. SHA-256 receipts distinguish exact consumed prefixes from whole small files; a tracking prefix hash is never the full LFS object hash.

| Publisher/repository | Pinned commit | Actual inspected scope |
|---|---|---|
| [SkillCorner](https://github.com/SkillCorner/opendata/tree/4340d274572876239c154c90bc507a9b3250a656) | `4340d274572876239c154c90bc507a9b3250a656` | Catalogue, match 1874553 roster/metadata, README/licence and LFS pointer; first 300 complete tracking JSONL frames only via public LFS download |
| [Metrica](https://github.com/metrica-sports/sample-data/tree/e706dd506b360d69d9d123d5b8026e7294b13996) | `e706dd506b360d69d9d123d5b8026e7294b13996` | Sample Game 1 first 300 frames per team plus three CSV header lines; whole small event file parsed, alignment assessed only for overlapping events |
| [Last Row](https://github.com/Friends-of-Tracking-Data-FoTD/Last-Row/tree/72129b58d0b866c21ab895d97b426333330f6294) | `72129b58d0b866c21ab895d97b426333330f6294` | Whole small `LIV_[2]-1_CHE.csv` play, not the ten-megabyte main 19-play collection |

Final run parser bytes: SkillCorner **609,262**, Metrica **277,277**, Last Row **588,279**, including the small documentation inputs. Exact URLs/hashes/byte counts are in `receipts`. The SkillCorner Git raw response was a genuine 133-byte LFS pointer to a 90,729,279-byte object, not tracking data. Only its public media prefix was read; the full object/hash was not downloaded/verified. Sandbox DNS initially failed; authorized public reads then succeeded. No access denial was bypassed. An initial parser failure on blank Last Row height was corrected to preserve missingness; three narrow tests distinguish blank/NaN from actual zero.

## Findings and decision boundary

SkillCorner match 1874553 is Brisbane Roar–Adelaide United, 23 November 2024, A-League 2024/25. Frames 0–299 contain ten initial null/unclocked entries, then 290 period-one frames at 0.1-second intervals, 0.0–28.9 match seconds. Each clocked frame has 22 finite x/y positions; all 22 sampled IDs join the supplied 36-player roster, with no duplicate IDs. This checks source consistency, not independent identity accuracy. The catalogue actually has 20 entries while its README says ten.

**47.38% of sampled player observations are flagged extrapolated** (3,023 of 6,380); 3,357 are flagged detected. Ball x/y exists in 290 clocked frames (283 detected, seven extrapolated), absent in the ten initial entries. These initial null entries are not evidence of gaps inside the inspected clocked interval. Of the player observations, 23 exceed the nominal 105×68-metre boundary; observed y reaches 35.19 metres from centre versus half-width 34. Do not clip or declare errors automatically: off-pitch location/estimation/source interpretation remains unverified. Positions are broadcast estimates, not exact ground truth, and no event alignment or whole-game coverage was established.

Metrica supplies 300 matched period-one frames per team, 0.04–12.00 seconds at 25 fps, 11 finite players per side and identical ball coordinates on all shared frames. There are 14 anonymous player columns per team, not 14 present players. Seven event rows overlap the slice: six passes and a kickoff with zero end-frame (not a fabricated duration); five have positive endpoints wholly inside the slice. Start-time/frame arithmetic and anonymous player references agree for those rows. Home x reaches −0.00022 despite nominal normalized 0–1 documentation; retain the value and uncertainty, not a clamp. Real player/team/competition names and verified match date are unavailable.

Last Row's selected play has 4,095 rows: 195 frames × 21 finite-coordinate objects, spanning frame-local 9.7 seconds at documented 20 fps. There are no player names; team labels exist and two local IDs have partial shirt references. No public-name roster, match-clock alignment or complete 22-player coverage is established. Most height cells are blank; nonzero height on local ID 0 is not an independently resolved ball identity. The authors warn that players can be missing and accuracy is unsuitable for research.

**Recommendation:** SkillCorner is a conditional technically usable **recorded, named sample-slice** candidate, with extrapolation/quality visible. Metrica offers a useful anonymous synchronized tracking/event comparison. Last Row is limited-play educational material. None is a live PL feed; no non-PL fixture or dataset is selected for the product. Next: choose an authorized sample explicitly, inspect a relevant noninitial segment/event alignment and coverage/identity limitations, then design the shared time/state interface. Do not attach these positions to Brentford/Chelsea.

SkillCorner's pinned [MIT notice](https://github.com/SkillCorner/opendata/blob/4340d274572876239c154c90bc507a9b3250a656/LICENSE) was inspected and hashed; preserve notices and credit in any permitted derived artifact. Metrica's sample invitation and Last Row's broad-use/credit wording are not a completed operation-specific rights review. Hackathon rules, product/public/external-model uses and assets remain separate gates. No accounts, contact, purchase or publication.

Verification: actual bounded `--fetch` execution and saved-summary check pass; three missing-value tests pass. Independent technical review passed: all three normalized source outputs and receipt byte counts reproduced exactly; three tests and 132 local links passed. F-8 stale wording corrected and coordinator recheck passed. Existing app/schema/reference data are unchanged; no whole-match completeness claim.
