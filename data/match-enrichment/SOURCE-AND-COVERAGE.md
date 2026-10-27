# Source coverage and reuse boundaries

## Latest match lineup/statistics inspection — 2026-10-17

[PL Lineups](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/lineups) prints all 40 shirt numbers; its substitute lists explicitly provide 18 broad categories. Starters have diagram/name/number entries without explicit individual role labels. Janelt and Colwill carry captain markers. No tactical-role or tracking inference is made.

The observed [Stats tab](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/stats) provides 11 core metric pairs: possession, xG, total shots, shots on target, big chances, corners, saves, red/yellow cards, fouls and offsides. These are source-reported completed-match team summaries, not a certified per-half or cursor view. Detailed operational definitions and xG model are unavailable. Explicit red-card 0/0 differs from earlier nonmention: original null claims stay preserved. Attack inside-box 2/1 and outside-box 4/4 do not partition total shots 13/12; both rows are retained only as excluded unresolved anomalies. No silent correction, definition invention or full-feed completeness follows. No per-player statistics table was exposed in the inspected Stats view; player-profile season totals were not substituted.

Independent review completed without findings: rendered Lineups/Stats verified all 40 shirts, 18 broad bench labels, two captains, 11 metric pairs and anomaly/unit/explicit-zero boundaries. Validator, 46 tests, four prior-layer hashes, 19 scope hashes and 128 local links pass; eight regressions were sensitive to seven guard removals.

## Previous completed entire displayed commentary audit — 2026-10-17

The [PL commentary](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/commentary) was scrolled from match end to lineup announcement/first-half opening, loading earlier batches. Its displayed groups contain three goals, nine substitutions and two yellow cards. Goal 61 explicitly credits Schuster and describes Anthony’s header after a corner. Goal 83 describes Thiago’s right-foot finish after a fast break; 90+4 describes Carvalho’s right-foot finish. The latter entries do not explicitly award assists. Club descriptions of a rebound and deflected cross remain narrative, not formal assist credits.

Substitution minute/on/off pairs agree with both club lists. Yellow cards are listed at 84 (Thiago, celebration) and 85 (Sangaré, foul). The view announces one added minute at 45 and five at 90, while period ends are printed 45+2 and 90+7: these are different observations, not exact durations. No additional red-card, penalty or VAR entry was found in this display; actual totals remain unestablished. Chelsea line 35 separately reports Barco’s rejected appeal without a minute. The prior historical coverage matrices below are superseded only within this narrow current audit scope. No full-feed completeness, same-minute sequence, tracking, known-then availability or reuse clearance is asserted.

Independent timeline review completed without findings: the full rendered PL list was independently reached from opening through match end, corroborating event counts, goal/substitution/card details, explicit assist, finish attributes and stoppage observations. Validator, 36 tests, three prior-layer hashes, 19 scope hashes and 123 local links pass; eight targeted regressions were sensitive to seven guard removals.

## Previous completed PL record inspection — 2026-10-17

Public rendered [overview](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/overview) scoreboard/report and [commentary](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/commentary) Goal 83 group support practical selection of Thiago 83′, with explicit competition-record precedence. Commentary Yellow Card groups identify Thiago 84′ and Sangaré 85′. [Lineups](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/lineups) label Chelsea 3-4-2-1 and Brentford 4-2-3-1. These are source classifications: earlier club formations remain attributed disagreements. [Match Info](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/info), Match Officials section, prints Andy Madley; Chelsea prints Andrew. Apparent same-role variant retained, not resolved legal identity.

Pages were discovered via visible official Matchweeks/tab controls and read after client hydration in a background browser. Web text extraction alone exposed shells. No videos or nonpublic endpoints used. No new claim of complete events, other-card absence, actual kickoff or reuse clearance. The earlier coverage matrix is the preserved pre-PL checkpoint; the new assessment is the current corroboration layer.

Independent review completed without findings: all four PL pages independently inspected in the rendered browser; 83-minute goal, formation labels, yellow cards at 84/85 and referee Andy Madley confirmed. Validator, 28 tests, both claim hashes, 19 scope hashes and 118 local links pass; three guard-removal sensitivity checks passed.

## Second actual report and public-name extension

[Brentford’s actual report](https://www.brentfordfc.com/en/news/match-reports-brentford-3-chelsea-0-premier-league-jaidon-anthony-igor-thiago-fabio-carvalho), dated 18 September, supports the same roster/substitution pairs and attendance but disagrees on one goal minute and Chelsea’s formation. Both cited claims are retained, canonical values null. Its lack of a numeric Carvalho goal minute, referee field and booking summary leaves those categories uncorroborated rather than absent. It explicitly identifies Keith Andrews as head coach; earlier source claims remain preserved.

Public-name evidence: [Chelsea season names](https://www.chelseafc.com/en/news/article/chelsea-squad-numbers-confirmed-for-the-2026-27-season), [match retrospective](https://www.chelseafc.com/en/news/article/chelsea-vs-bournemouth-key-statistics-and-facts) and [Brentford official squad](https://www.brentfordfc.com/en/teams/mens). The first two publication dates come from dated headings in official indexed search text, not the opened body extraction. The last is observed on 17 October, with publication date unknown: it supports public names, not backdated membership. All participation claims still cite dated actual match evidence. No legal-name completeness or external identity registry linkage is claimed. Source reuse restrictions remain unchanged.

Observed 2026-10-17; retrospective corrected-source research. Publication timestamps are unknown where not exposed. Observation is not evidence that these facts were available at kickoff. See [registry](sources.json) and [claims](brentford-chelsea-2026-09-18.json) for exact citations and locators.

| Category | Established scope | Boundary |
|---|---|---|
| Fixture | Chelsea final report; Brentford dated match hub and replay title | Completed result and played date; no video inspected |
| Venue | Actual report prints Gtech Community Stadium | Ground identity reused; no fixture coordinates inferred |
| XI / bench / formation | Chelsea report team lists, lines 49–52 | 11 starters and nine bench names per side; formations are not tracking or individual position coordinates |
| Substitutions | Team-list parentheticals | Nine reported changes; simultaneous minute ordering unknown |
| Goals | Scorer summary, line 53 | Three; minutes including 90+4, no seconds, assists or goal-type completeness claim |
| Cards | Booked summary, line 54 | Two reported bookings; red cards and complete card totals unestablished |
| Penalties | Rejected appeal in narrative, line 35 | Not an awarded penalty; awarded/taken/scored totals unknown |
| Coaches | Explicit manager/head-coach role sources plus match context | Role evidence separate from reaction title; physical matchday presence and complete staff list not verified |
| Referee / attendance | Report lines 56–57 | Andrew Madley; crowd 17,193. Counting definition, allocations, home/away split unknown |
| Kickoff / other officials | Preview only | Scheduled 20:00 BST, not verified actual kickoff; other actual officials unknown |
| Event feed / tracking | Not collected | No complete event stream, positions, ball movement, lineups-derived tracking or inferred zeros |

Sources: [Chelsea actual report](https://www.chelseafc.com/en/news/article/match-report-brentford-3-0-chelsea), [Brentford actual-match index](https://www.brentfordfc.com/en/video/match/brentford-v-chelsea-premier-league-20260918), [dated preview](https://www.brentfordfc.com/en/news/match-previews-brentford-v-chelsea-premier-league-18-09-2026), and [explicit head-coach role](https://www.brentfordfc.com/en/news/match-previews-brentford-v-tottenham-hotspur-premier-league-22-08-2026).

## Rights and next step

[Brentford terms](https://www.brentfordfc.com/en/terms-of-use), sections 8.1–8.2, reserve content/data rights and restrict extraction/reproduction; the fixture provision in 8.6 does not clear all rich content. Chelsea article-specific reuse permission has not been established. Public accessibility does not establish product display, redistribution, commercial, replay, retention or external-AI permission. Flags mean **not cleared**, not a legal conclusion. This pack retains bounded own verification notes and source-printed fact references, not articles, images, video or a feed. Further product use needs rights review and source reconciliation. No broader season completeness is claimed.
