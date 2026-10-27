# Season and location review

Contract 0.3.0, 2026-10-15. [Example records](examples/seasons-bundle.json) contain 34 revisions across two partial synthetic seasons. This is a data review, not a working map UI.

The competition has a stable Premier League demo identity. Bristol, Leeds and Sheffield provide real geographic context. Club identities, stadiums, street addresses, approximate coordinates, season boundaries, memberships, fixtures and player history are authored fiction. They do not describe actual Premier League seasons. Postal codes and capacities are unknown rather than invented numbers. Dates run August 1 through May 31 in each fictional season, not verified historical schedules.

## Expected browsing results

| Select | Participating sample clubs | Harbour home marker | Sample matches |
| --- | --- | --- | --- |
| 2022/23 at September 10, 2022 | Harbour (Bristol), Riverside (Leeds) | Lantern Ground: 51.45, -2.60 | Two fixtures from season-2022 |
| 2023/24 at September 10, 2023 | Harbour (Bristol), Moor (Sheffield) | Beacon Ground: 51.49, -2.57 | Two fixtures from season-2023 |

Riverside explicitly does not participate in the second synthetic season; Moor explicitly does not participate in the first. No relegation/promotion cause is asserted. Both seasons have partial membership coverage: these are not full league rosters. Absence from an incomplete sample never establishes non-participation.

Harbour's move is announced on June 1, 2023 and becomes effective July 1. Before announcement, the old ground has no known closing date. After announcement but before July 1, Lantern is still the active home. At the boundary Beacon becomes active; the old revision must not reappear.

One 2023/24 fixture has Moor as the nominal home club but takes place at Riverbank Ground, separate from Moor's home marker. The stadium and pitch on the fixture identify its venue. A venue outside the selected league's club home grounds remains a valid match marker.

Alex Reed is 22 and affiliated with Harbour, shirt 6, at the 2022 sample fixture; 23 and affiliated with Moor, shirt 8, at the 2023 fixture. Transfer knowledge is released June 15, 2023, effective July 1. These affiliations do not by themselves establish lineup participation.

## Contract changes

- Competition identity is separate from competition-season identity.
- Seasons specify start/end dates, ruleset and membership coverage. Dates are inclusive UTC calendar bounds for this review contract; timezone metadata supports later local display. No date inference from the season label.
- Membership records join a club and season with participating/not_participating status and the existing valid/knowledge times.
- Club locality stores town/city, region, country and country code; the whole locality can be unknown.
- Stadiums add a nullable address with locality; existing coordinates identify the venue. Fictional markers must be labeled synthetic in any later UI.
- Dated club-stadium use remains separate from fixture venues and supports temporary homes and shared grounds.

The earlier 0.2.0 update migrated the review bundle: geography remains explicitly unknown; three competition/membership records were added, bringing it to 53 revisions. Existing match-event examples were preserved. The later 0.3.0 ecosystem update keeps this two-season synthetic bundle compatible through its version migration; it does not add ecosystem queries or real data. There are now 42 record kinds across the contract.

## Executable checks

[season_views.py](tools/season_views.py) provides `season_view(bundle, season_id, at, known_at)` and `player_at_fixture(bundle, person_id, fixture_id, known_at)`. Both validate input, require explicit timestamps and return source revision IDs alongside data. They support synthetic release-time queries only, not production provider ingestion, general bitemporal queries or full match replay. The season view reports incomplete coverage and keeps club ground markers separate from fixture venue markers. Unknown venue details return null rather than a guessed location.

Run the full suite using the commands in [data README](README.md), plus:

```powershell
py -3.12 data/tools/validate_bundle.py data/examples/seasons-bundle.json
```

Checks cover season membership, ground coordinates, season-isolated fixtures, neutral venue, age/affiliation, announcement/effective-date boundaries, future-knowledge exclusion and invalid relationships. No tables/results are computed: sample fixtures remain scheduled, without invented scores. Full squads, event passages, map UI and Decision-1 routing remain future work.
