# One-match enrichment research

## Latest lineup and statistics audit — 2026-10-17

`lineup_statistics_audit` adds 40 match-specific shirt mappings, 18 broad source categories printed in the substitute list and two captains. The 22 starters have no explicit individual category label in the inspected view; all 40 individual tactical roles remain unknown. Broad labels and source formation diagrams are not measured match positions. Numbers come from the actual match page, not a current-squad backfill.

Eleven team metric pairs preserve source labels/units and completed-match team-summary scope. Operational definitions, xG methodology, certified period definitions and per-half/time-series values are not supplied. Explicit PL red-card totals 0/0 are newer source claims, separate from preserved earlier null/absence checkpoints. Printed inside-box shots 2/1 and outside-box shots 4/4 do not partition total shots 13/12; those rows remain excluded with unresolved label/consistency status. No values are repaired.

Validator and 46 tests pass; [pre-statistics checkpoint](timeline-audit-checkpoint.json), three earlier canonical checkpoints and 19 external scope hashes match. Independent review completed without findings: rendered Lineups/Stats verified all 40 shirts, 18 broad bench labels, two captains, 11 metric pairs and anomaly/unit/explicit-zero boundaries. Validator, 46 tests, four prior-layer hashes, 19 scope hashes and 128 local links pass; eight regressions were sensitive to seven guard removals. Earlier sections are historical checkpoints. No app/schema/reference changes or raw page/feed retention; product/external-AI reuse remains uncleared.

## Previous completed timeline audit — 2026-10-17

`timeline_audit` records all three goals, nine changes and two listed yellows from the fully reached displayed PL commentary. Earlier batches were loaded by scrolling until the lineup announcement/first-half opening appeared; the final match-end boundary was also inspected. This establishes review of the displayed list, not a complete official feed. Source-reported goal finish attributes are descriptions, not tracking. Schuster is the sole explicit formal goal-assist credit; rebound/cross narratives do not establish other formal assists.

Announced minimum added time (one/five minutes) is separate from period-end labels (45+2/90+7), with seconds unknown. The rejected Barco appeal remains minute-unknown. No displayed penalty, VAR or red-card entry was found; actual totals remain null. Same-minute substitution order is unknown, and this is retrospective corrected evidence, not known-then playback. Existing league precedence and all report disagreements remain preserved.

Validator and 36 tests pass; [pre-timeline checkpoint](league-assessment-checkpoint.json), both earlier canonical checkpoints and 19 external scope hashes match. Independent timeline review completed without findings: the full rendered PL list was independently reached from opening through match end, corroborating event counts, goal/substitution/card details, explicit assist, finish attributes and stoppage observations. Validator, 36 tests, three prior-layer hashes, 19 scope hashes and 123 local links pass; eight targeted regressions were sensitive to seven guard removals. Earlier sections are historical checkpoints. No app, schema, reference, raw-page, video or feed changes; reuse remains uncleared.

## Previous completed league-record assessment — 2026-10-17

A separate `league_record_assessment` selects **Thiago 83′** by explicit competition-record precedence, retaining the club report’s 80′ claim. It selects **Chelsea 3-4-2-1** and **Brentford 4-2-3-1** as PL source classifications. Both formation disagreements remain unresolved as source classifications; no measured movement or majority-vote truth is asserted. The prior two-source assessment below is a preserved dated checkpoint, superseded for practical goal-minute selection.

Official commentary corroborates yellow cards Thiago 84′ and Sangaré 85′, without a complete-card audit. Match Info prints referee Andy Madley, retained with Chelsea’s Andrew Madley as an apparent name variant for this fixture/role, with no legal identity resolution. The [source registry](sources.json) records four rendered public PL pages and semantic locators; web-only extraction returned shells. No video, hidden API or raw page retention. Reuse remains uncleared.

Validator and 28 tests pass. The [preceding extension checkpoint](public-name-extension-checkpoint.json), original-claim checkpoint and all 19 scope hashes match. Independent review completed without findings: all four PL pages independently inspected in the rendered browser; 83-minute goal, formation labels, yellow cards at 84/85 and referee Andy Madley confirmed. Validator, 28 tests, both claim hashes, 19 scope hashes and 118 local links pass; three guard-removal sensitivity checks passed. Earlier review outcomes below remain historical at their recorded scope.

## Public names and second-report review — 2026-10-17

All 40 roster entries now have cited public football names. Local IDs and the original source-printed names remain unchanged; global identity resolution remains false. These are not asserted legal names, affiliation intervals or new player profiles. Chelsea names use a dated season announcement (19 entries) and an explicit retrospective about this match (Emiliano Martinez). Brentford names use its observed official squad; dated report membership remains separate.

The second official actual report corroborates the rosters, nine changes, scorers, result, venue and attendance. Two disagreements remain **unresolved**, with no canonical value: Thiago’s goal minute (Chelsea 83, Brentford 80) and Chelsea’s formation (4-2-3-1 versus 3-4-2-1). Original Chelsea claims are retained as attributed claims, not silently preferred truth. Referee and reported-booking details remain single-source; Carvalho’s numeric goal minute is not supplied by the second report. Fourteen category checks distinguish agreement, conflict and not covered.

The [original-claim checkpoint](original-claims-checkpoint.json) verifies canonical preservation of the preceding match JSON after stripping only the new identity/crosscheck additions. Validator and 22 tests pass; all 19 app/schema/reference scope hashes remain unchanged. Independent review completed without findings: 40 public names checked against four official sources, both unresolved conflicts retain null canonical values, original-claim and 19 baseline hashes match, 22 tests and 112 local links pass, and five guard-removal regressions were sensitive. The earlier F-7 review below remains completed at its original scope.

2026-10-17. Local, bounded evidence pack for **Brentford 3–0 Chelsea, 18 September 2026**. Selected for rich accessible official coverage, not because it is the latest match. This is research, not application input, a licensed feed, a public replay or a known-then reconstruction. Existing schemas, prototype and reference foundation are unchanged.

- [Match claims](brentford-chelsea-2026-09-18.json): source-printed identities, claim-level locators and explicit unknowns.
- [Source registry](sources.json): official URLs, source types, publication dates where established and observation date.
- [Coverage and rights](SOURCE-AND-COVERAGE.md): evidence boundaries and gaps.
- [Scope baseline](scope-baseline.json): 19 pre-edit SHA-256 checkpoints, verified offline.
- [Validator](validate.py) and [rejection tests](test_validate.py).

The validator derives 22 starters, 18 bench players, nine substitutions (four Chelsea, five Brentford), three goals reconciling the final score, and two reported bookings. Player IDs are local source-name references, not resolved global profiles. Existing club and ground IDs are reused; no full player identity is guessed.

Run from the repository root:

```powershell
python data/match-enrichment/validate.py
python -m unittest discover -s data/match-enrichment -p 'test*.py' -v
```

Verified: validator and 16 tests pass; all 19 baseline hashes match. Independent review completed with no remaining findings: F-7 citation validation fixed and independently rechecked; 16 tests and 19 baseline hashes pass. All 107 local targets resolve. Review included exact rejection reproductions and guard-removal sensitivity, not a fresh recapture of every source. No article bodies, videos or feeds retained; no account, purchase, contact, publication or UI change. Source visibility is recorded here for review; an in-product source interface is not implemented.
