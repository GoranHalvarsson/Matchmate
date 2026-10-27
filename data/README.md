# Data contract review — step 1

Version 0.3.0, 2026-10-15. Authored fictional examples only; no provider data imported. These files do not drive the UI or implement full match replay, an ecosystem query engine or an MCP server. Start with the new [two-season/location review](SEASON-REVIEW.md) for season selection, map-ready records and limited historical queries.

The original [example bundle](examples/review-bundle.json), migrated to 0.3.0, now has 67 revisions. It illustrates identities, a dated transfer and fee components, a historical affiliation correction, a fixture starting, pass resolution, corrected goalscorer, a two-second movement sample, weather, explicit coverage gaps and synthetic club-ecosystem records.

## Suggested review

| Find this record ID in the example | What to look at |
| --- | --- |
| `player-h6` | Stable player identity; date of birth rather than a permanently stored age |
| `affiliation-h6-harbour` | Two revisions: end date unknown originally, added when the future transfer is announced |
| `affiliation-h6-riverside` | Known on June 15, effective July 1; announcement and membership are different |
| `fee-h6-base` / `fee-h6-bonus` | Guaranteed GBP 2m versus conditional GBP 0.5m, without claiming either has been paid |
| `event-p1` / `event-p1-resolution` | Pass released first; successful reception is a separate later event |
| `event-g1` | Same goal ID and event time, corrected scorer in a later-released revision |
| `coverage-tracking` | Only H6 for two seconds; no implication of complete player or ball movement |

The fictional fixture is represented through a partial in-progress passage; its name containing `history` does not imply a supplied final result. A completed-fixture state and full squad will be populated later.

## Files

- [Bundle schema](schemas/review-bundle.schema.json): small Draft 2020-12 entry point joining 42 record contracts across focused modules; 11 named event payload variants. All references resolve locally.
- [Example JSON](examples/review-bundle.json): editable synthetic records with stable IDs and separate revision IDs.
- [Validator](tools/validate_bundle.py): schema/formats, typed references, basic revision chains, intervals and clock/pitch relationship checks.
- [Contract tests](tests/test_contract.py): invalid dates, dangling references, wrong entity types, future-knowledge timestamps and other malformed input cases. These are not the 18 scenario/query acceptance tests.

## Reading a record

### Schema map

| File | Responsibility |
| --- | --- |
| [Common](schemas/common.schema.json) | Shared identity, revision, provenance, time, sources, clock, coordinates and money shapes |
| [People](schemas/people.schema.json) | People, dated affiliations and player profiles |
| [Clubs and competitions](schemas/clubs-competitions.schema.json) | Clubs and locality, competitions, dated seasons, membership and rulesets |
| [Venues](schemas/venues.schema.json) | Stadiums, club use, pitches and pitch configuration |
| [Fixtures](schemas/fixtures.schema.json) | Fixtures, periods, participants, participation intervals and attacking direction |
| [Match events](schemas/match-events.schema.json) | Event record and separately named pass, foul, card, goal, substitution, interruption and penalty payloads |
| [Tracking](schemas/tracking.schema.json) | Position samples for people and the active ball |
| [Balls](schemas/balls.schema.json) | Ball models, physical balls and usage |
| [Transfers and finance](schemas/transfers-finance.schema.json) | Transfers, financial terms and payments |
| [Context](schemas/context.schema.json) | Observations and evidence-linked tactical assessments |
| [Coverage](schemas/coverage.schema.json) | Availability boundaries and explicit score baselines |
| [Club ecosystem](schemas/club-ecosystem.schema.json) | Supporter groups and recognition, public organisational contacts, academies, programmes, facilities, departments, staff relationships, match assignments and attendance/capacity observations |

These are connected contracts, not separate databases or services. IDs join records across domains. Shared definitions prevent time, identity and units from drifting. Required envelope fields and closed-object rules remain explicit on each record.

The offline validator expands relative file/JSON Pointer references before checking shapes and relationships. Its deliberately limited loader rejects network references, paths outside the schema directory, cycles, scope-changing IDs/anchors and reference siblings. It is not a general-purpose JSON Schema resolver. Keep modules acyclic and use plain `$ref` nodes. Other Draft 2020-12 consumers can load the relative references using the bundle file's location as the base URI.

The earlier modular split preserved version 0.1.0. Season/location contracts advanced it to 0.2.0; ecosystem contracts now advance it to 0.3.0 and migrate both synthetic example bundles. Editing the schemas is the source of truth; temporary authoring scripts are not build inputs.

`record_id` identifies the fact/entity; `revision_id` identifies one version. `supersedes_revision_id` links a correction or update. `valid_from` / `valid_to` describe when the fact applies (end exclusive). `recorded_at` is the actual authoring/import time; it must not be backdated to pretend we received a historical live feed.

`published_at` is unknown (`null`) for our authored examples. `synthetic_released_at` is the fictional replay knowledge boundary. For example, both G1 revisions describe the goal at 15:20:30 UTC, but the corrected scorer is released at 15:20:40. A future transfer can be announced before it becomes effective. Upcoming fixture metadata is distinct from future play.

The first H6 affiliation has no known end date. A later revision adds its end date when the transfer is announced. That prevents a 2025 query from discovering a future departure by reading an overwritten profile.

Match clock addresses contain period ID, milliseconds within that period and sequence. Absolute UTC record times can be related to the period start only where that mapping is explicitly supported. Coordinates use a fixed pitch corner and metres; optional z is null when absent. Out-of-pitch locations can be valid, for example for coaches or substitutes.

Financial values use integer minor units plus an explicit currency and decimal scale: GBP 200,000,000 minor units at scale 2 means GBP 2,000,000. Fees and payments are different record types. Unknown is null, never zero. Attendance and match capacity likewise make unknown explicit and keep allocation, ticket sales/distribution and reported admissions distinct. `claim_status=authored` identifies fictional claims even where their fictional fee disclosure is confirmed.

Every record cites a source. `x-reference` and `x-revision-reference` are local validator annotations supplementing standard JSON Schema. External consumers must implement equivalent relationship checks; JSON Schema alone does not enforce them. The review bundle requires complete revision chains; it is not a streaming ingestion format.

## What is deliberately incomplete

Only five players are listed. There is no complete lineup, active-player reconstruction, full-match event history, full player/ball tracking, ball swap history, actual payment, current final result or query engine. Coverage records make these gaps explicit. No running-distance, completed-pass or score aggregate is presented as a computed output.

These schemas cover several football domains; this first contract is not the final implementation of every field. The ecosystem records are contracts only: they do not select a provider, import real club data, resolve historic staff, calculate occupancy or provide an ecosystem query engine. A facility-use record may document a dated shared site even if another club operates it. The validator checks that referenced records have a compatible interval and scope, but does not produce a historic view or establish query correctness. Provider mappings, detailed venue/career stories, sanctions/appeals, possession intervals, comprehensive shots/restarts, VAR chains, provider retention permissions and versioned metric/snapshot outputs need further contracts. Context observations support weather, pitch, watering, travel, facilities, crowd and staff observations with provenance; they do not establish causal claims.

Before the next step, review whether these record shapes are understandable. Full sample population is step 2; historical queries and the 18 scenario checks follow afterward. A schema pass is not a football completeness or factual correctness guarantee.

## Local checks

Requires Python 3.12+ and the package in requirements.txt. Install into a local virtual environment if needed; do not change an existing global environment.

```powershell
py -3.12 -m venv .codex-temp/data-venv
.codex-temp/data-venv/Scripts/python.exe -m pip install -r data/requirements.txt
.codex-temp/data-venv/Scripts/python.exe data/tools/validate_bundle.py
.codex-temp/data-venv/Scripts/python.exe -m unittest discover -s data/tests -v
```

Existing Python environments with the dependency may run the two validation commands directly. The checks do not call providers, databases or models.

Schema reference: [JSON Schema Draft 2020-12](https://json-schema.org/draft/2020-12). This interchange format does not select a production database or framework.
