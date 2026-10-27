# Data and sources

Matchmate needs to say both what it knows and how it knows it. A reported goal, an estimated position and an authored run can share a screen without becoming the same kind of evidence.

## What the prototype uses

| Material | Recorded source status | Interpretation boundary |
| --- | --- | --- |
| Premier League seasons | Pinned OpenFootball community reference files for 2015/16, 2016/17 and a 2026/27 snapshot through 20 September 2026 | Tables and result context are computed from those files; they are not a live or independently verified official feed |
| Brentford–Chelsea reported events and roster | Prepared records with Premier League and club-report references | Reported minutes and source distinctions remain; the records are separate from generated movement |
| Brentford–Chelsea pitch replay | Authored full-match player/ball illustration over a 98-minute timeline | Positions, runs, exact staging and replay statistics are generated, not measurements of the actual match |
| Brentford–Liverpool replay | Report-backed goals/cards/substitutions plus a separate generated illustration | Generated actions, control, distance and Story passages do not measure the real match; reported facts retain their source/time precision |
| Australian tracking extract | A pinned three-minute SkillCorner sample with observed, estimated and missing content | Coverage is limited to the extract; missing positions and ball data do not become invented observations |
| Player descriptions | A bounded catalog of source-scoped descriptions and explicit unverified roles | A listed category or report description is not proof of a player's tactical role throughout the match |

Each source supports only the fields and precision recorded from it. Community season files, publisher reports and generated illustration retain their distinct status.

## Reading generated statistics

Passes, shots and illustrated control are calculated from completed generated actions eligible at the replay cursor. They describe the authored illustration. They are not actual match passing, shooting or possession statistics, and they cannot establish tactical causes or player quality.

Per-player distance sums eligible consecutive generated movement steps for that person, respecting participation and continuity boundaries. It appears in the replay interface only. It is not actual distance covered, speed, effort or a performance rating, and it is not currently exposed as a new chat statistic.

Generated statistics and completed Story passages are available for both prepared Brentford matches. The prepared Liverpool action extension contains 727 generated actions and 132 completed passages; Chelsea has 150 passages. Those authored calculations remain separate from reported match events and from actual match statistics.

## Calculation examples

**The numbers below are illustrative arithmetic only. They are not observed demo values or claims about a football match.** Code calculates these measures from eligible evidence at the accepted replay cursor; AI does not estimate the totals.

| Measure | Illustrative calculation | What counts, and what stays out |
| --- | --- | --- |
| Pass accuracy / completion rate | Four completed passes out of five finished attempts: `4 ÷ 5 × 100 = 80%` | Count an attempt and its outcome only once the generated action finishes. An open sixth attempt stays out. No finished attempts means unavailable, rather than 0%. |
| Illustrated control share | Team A has 60 seconds of completed classified control and Team B has 40: `60 ÷ (60 + 40) × 100 = 60%` | The denominator is 100 classified seconds. If the timeline also has 20 seconds of pauses/restarts, 10 unknown seconds and a 30-second unfinished spell, those 60 seconds stay out of the denominator. This is not measured possession. |
| Generated player distance | Three eligible consecutive positions in metres, `(0, 0) → (1, 0) → (1, 1)`, add `1 + 1 = 2 m` | Sum straight-line steps for the same person across valid consecutive samples, within the same period and slot while participating. Missing/invalid samples, period or slot changes break continuity; excessive jumps are excluded. Never draw an extra connecting step across a gap. |

Shots similarly count completed eligible generated shot actions. Rewinding recalculates the available totals at the earlier cursor. Generated actions do not create reported goals, and these calculations cannot establish actual player effort or explain a tactical cause. The [three-question walkthrough](DEMO.md#three-exact-questions) keeps those distinctions visible.

## Source references

The existing evidence records point to:

- [OpenFootball football.json](https://github.com/openfootball/football.json) for the community season files.
- [SkillCorner's pinned sample tracking file](https://raw.githubusercontent.com/SkillCorner/opendata/4340d274572876239c154c90bc507a9b3250a656/data/matches/1874553/1874553_tracking_extrapolated.jsonl) for the Australian sample's source recording.
- [Premier League match overview](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/overview), [lineups](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/lineups) and [commentary](https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/commentary) for the prepared Brentford–Chelsea records.
- [Brentford's Chelsea match report](https://www.brentfordfc.com/en/news/match-reports-brentford-3-chelsea-0-premier-league-jaidon-anthony-igor-thiago-fabio-carvalho) for scoped report descriptions.
- Liverpool's pinned [Premier League overview](https://www.premierleague.com/en/match/2645256/brentford-vs-liverpool/overview), [lineups](https://www.premierleague.com/en/match/2645256/brentford-vs-liverpool/lineups) and [commentary](https://www.premierleague.com/en/match/2645256/brentford-vs-liverpool/commentary), plus [Brentford's report](https://www.brentfordfc.com/en/news/match-reports-brentford-1-liverpool-2-premier-league-17-11-2026) and [Liverpool's report](https://www.liverpoolfc.com/news/brentford-liverpool-premier-league-match-report), for the prepared Liverpool records. Source links identify provenance; they do not grant reuse permission.

The source bundle includes application code, app screenshots, reference captures, generated distance/action packs, the pinned Liverpool generated export and goal review, prototype tracking samples and club/brand media. It excludes original video, the ignored Chelsea/league runtime bundles, the local canonical store and arbitrary outputs. Neither inclusion nor a listed source approves redistribution or trademark use; source terms and submission permissions must be resolved before reuse.

For the practical consequences in the interface and chat, read [Capabilities and limitations](CAPABILITIES-AND-LIMITATIONS.md).
