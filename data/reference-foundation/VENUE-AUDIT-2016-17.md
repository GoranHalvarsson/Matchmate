# 2016/17 fixture venue catalogue audit

Subsequent [official primary-header audit](PRIMARY-FIXTURE-AUDIT-2016-17.md) now covers all 380 actual fixtures; the three-case/377-gap statements below describe this earlier catalogue audit scope. Its completed independent gate and original capture hashes remain unchanged.

Observed 2026-10-17. **380/380 catalogue fixtures checked; zero catalogue-level missing venues.** This is third-party catalogue evidence, not 380 independently verified primary match reports or reuse clearance. No application/schema/source-result changes.

## Actual audit

The standard web tool returned [BDFutbol's public season page](https://www.bdfutbol.com/en/t/teng2016-17.html). Six bounded find windows (Round 1, 7, 14, 20, 30, 35) supplied 621 dated-row appearances. We checked overlapping source lines and match references: 241 identical repeats, zero conflicting repeats, 380 unique fixture keys, zero distinct-line duplicate fixtures. No usual-home-ground inference was used.

Every parsed row explicitly supplied played date, home/away clubs, full-time score and stadium. Normalizing the 20 source club names and joining on **played date + home + away** matched all 380 existing 2016/17 results: zero missing/extra fixtures, zero score disagreements and zero blank venues. Each of 20 home clubs has 19 records at one venue identity in this catalogue. Dates span 13 August 2016–21 May 2017. No alternate home venue found here is not universal proof that no venue exception existed.

Venue aliases map reported labels to existing ground identities, independently of club. BDFutbol's Kingston Communications Stadium and Riverside labels are retained as source labels; they do not override the separately sourced KCOM/Riverside naming evidence. Liverpool's accessible official season archive also backfills some current stadium names, so it was not used to certify exact historical names or claimed as an additional 38-row audit.

## Provenance and reproducibility

[Aggregate metadata](venue-audit-2016-17.json) retains source URL, exact alias maps, coverage counts and six window hashes. Combined exact **web-returned text**, not raw HTML, hashes to `7fcc012164113906cf44fe93278bcd5b729b9ec6274e7b82a1e3168193a4f236`. Canonical parsed/normalized rows hash to `7bf70b015f1c2421e2a0258fc45e7899abcdbd8bb4101d1de666f65663f01781`. Matched fixture-key set hashes to `2278786eec2e62af41ca310c5ff442a442a1b9808ac5e98f832036ef20e3e33b` and is independently reproducible from the unchanged local source. The metadata specifies byte ordering and LF/JSON conventions.

Actual capture/join ran in transient tool memory. [Audit method](audit_venue_catalogue.py) provides a network-free stdin parser and aggregate checker for subsequently supplied legitimate web-response windows; it emits aggregates only. Its seven authored synthetic tests cover overlap, missing fixtures, duplicates, conflicts, wrong dates, blank venues and unknown venue labels. The persisted Python CLI was also run against six fresh legitimate web responses using explicit UTF-8 stdin: exit 0, 380 exact joins, and canonical-row/key digests exactly matching the original capture. Original web-response snapshot hashes describe the first capture; fresh tool-wrapper strings are not claimed byte-identical. Inputs were immediately discarded. Offline validation verifies the stored aggregate against local fixture keys, club counts and known ground IDs; it cannot independently re-fetch or prove an unretained third-party snapshot. A future full audit must obtain source responses again; changing web output can change snapshot hashes.

Direct shell HTTP requests returned 403 and were not retried, spoofed or routed through alternate endpoints. Successfully returned standard web content was used as authorized. No source HTML/text, parsed/canonical 380-row fixture export or provider credentials were retained. The [official BDFutbol data service](https://www.bdfutbol.com/en/c/api2.html) advertises authenticated paid API access; no open licence or public-product/external-AI reuse permission was established. No API subscription, payment or contact occurred.

## Primary checks and remaining gaps

Added [Liverpool's actual Burnley report](https://www.liverpoolfc.com/news/first-team/232534-match-report-liverpool-beaten-by-burnley-at-turf-moor): 20 August 2016 at Turf Moor. A separately attributed contemporary [ITV announcement](https://www.itv.com/news/granada/update/2016-07-07/liverpools-opener-against-burnley-switched-from-anfield-to-turf-moor/) describes the home/away fixture reversal for Anfield works. This was a reversed fixture arrangement before play, not an invented neutral-site relocation.

Three selected actual 2016/17 fixtures now have primary venue corroboration (West Ham–Bournemouth, Tottenham–United and Burnley–Liverpool); **377/380 remain outside this fixture-level primary audit**. All 380 have source-catalogue venue evidence. European Wembley use stays outside the Premier League denominator. The 2026/27 actual-venue gap remains **379/380**, plus one separately planned fixture; future fixtures cannot be certified as played.

Validator and all 19 scoped tests pass; source/prototype/licence hash checkpoints remain unchanged. Independent gate passed with no findings: validator and 19 tests, 112 local links across nine pages, aggregate aliases and local-source hash checked. Saved fresh-source canonical replay evidence was accepted; no independent full source recapture is claimed. Historical naming, rights and primary corroboration limits remain explicit.
