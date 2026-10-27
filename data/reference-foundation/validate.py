"""Validate only this reference batch; no network, writes, app or schema imports."""
import csv
import hashlib
import json
import re
from pathlib import Path

BASE = Path(__file__).resolve().parent
ROOT = BASE.parent.parent

def read(name):
    return json.loads((BASE / name).read_text(encoding="utf-8"))

def validate():
    clubs, members, grounds, associations, reviews, sources = [read(name) for name in ["clubs.json", "memberships.json", "grounds.json", "ground-associations.json", "membership-ground-review.json", "sources.json"]]
    audit, aliases = read("audit.json"), read("aliases.json")["mapping"]
    ids = lambda rows, key: {r[key] for r in rows}
    for rows, key, count in [(clubs,"club_id",30),(members,"membership_id",60),(grounds,"ground_id",audit["counts"]["grounds"]),(associations,"association_id",audit["counts"]["ground_associations"]),(reviews,"membership_id",60),(sources,"source_id",audit["counts"]["sources"])]:
        assert len(rows) == len(ids(rows,key)) == count, (key,"count/duplicate")
    clubids, groundids, sourceids = ids(clubs,"club_id"), ids(grounds,"ground_id"), ids(sources,"source_id")
    assert ids(members,"club_id") == clubids
    for season in ["2015/16","2016/17","2026/27"]:
        subset = [m for m in members if m["season"] == season]
        assert len(subset) == len(ids(subset,"club_id")) == 20
    for m in members:
        assert aliases[m["source_team_name"]] == m["club_id"]
        assert m["source_team_name"] in audit["source_team_sets"][m["source_id"]]
        assert m["source_id"] in sourceids and m["observed_at"] == "2026-10-17"
        assert m["valid_from"] is None and m["valid_to"] is None
    for source in sources:
        path = BASE / source["local_path"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == source["sha256"], path
        assert path.stat().st_size == source["bytes"]
        assert source["license"] and source["source_url"] and source["observed_at"] == "2026-10-17"
        if source["kind"] == "revision_pinned_wikidata_entity":
            entity = next(iter(json.loads(path.read_text(encoding="utf-8"))["entities"].values()))
            assert entity["lastrevid"] == source["revision_id"]
            if source.get("capture_kind") == "lossless_entity_extract":
                parent = next(s for s in sources if s["source_id"] == source["parent_source_id"])
                assert json.loads((BASE/parent["local_path"]).read_text(encoding="utf-8"))["entities"][entity["id"]] == entity
    for source in sources[:3]:
        path = BASE / source["local_path"]
        if path.suffix == ".json":
            raw = json.loads(path.read_text(encoding="utf-8")); names = {m[k] for m in raw["matches"] for k in ["team1","team2"]}
            assert len(raw["matches"]) == 380
        else:
            pairs = []
            for line in path.read_text(encoding="utf-8").splitlines():
                if " v " not in line: continue
                line = re.sub(r"^\s*(?:\d\d:\d\d\s+)?", "", line)
                home, away = re.split(r"\s+v\s+",line,maxsplit=1)
                pairs.append((home.strip(),re.split(r"\s+\d+[-–]\d+",away)[0].strip()))
            assert len(pairs) == 380
            names = {name for pair in pairs for name in pair}
        assert names == set(audit["source_team_sets"][source["source_id"]])
    for ground in grounds:
        assert -90 <= ground["latitude"] <= 90 and -180 <= ground["longitude"] <= 180
        source = next(s for s in sources if s["source_id"] == ground["source_id"])
        entity = json.loads((BASE/source["local_path"]).read_text(encoding="utf-8"))["entities"][ground["wikidata_id"]]
        claim = next(c for c in entity["claims"]["P625"] if c["id"] == ground["claim_id"])
        value = claim["mainsnak"]["datavalue"]["value"]
        assert (value["latitude"],value["longitude"],value.get("precision")) == (ground["latitude"],ground["longitude"],ground["source_coordinate_precision"])
    for a in associations:
        assert a["club_id"] in clubids and a["ground_id"] in groundids and a["source_id"] in sourceids
        if a["evidence_status"] == "undated_current_observation_only": assert a["valid_from"] is None and a["valid_to"] is None
        assert "not an executable interval" in a["boundary_semantics"]
        source = next(s for s in sources if s["source_id"] == a["source_id"])
        entity = next(iter(json.loads((BASE/source["local_path"]).read_text(encoding="utf-8"))["entities"].values()))
        claim = next(c for c in entity["claims"][a.get("source_property","P115")] if c["id"] == a["claim_id"])
        expected_ground = next(g["wikidata_id"] for g in grounds if g["ground_id"] == a["ground_id"])
        assert a["source_rank"] == claim["rank"] and a["source_reference_count"] == len(claim.get("references",[]))
        if a.get("source_property") == "P466": assert entity["id"] == expected_ground
        expected_target = next(c["wikidata_id"] for c in clubs if c["club_id"] == a["club_id"]) if a.get("source_property") == "P466" else expected_ground
        assert claim["mainsnak"]["datavalue"]["value"]["id"] == expected_target
        for property_id, field in [("P580","source_start_time"),("P582","source_end_time")]:
            qualifiers = claim.get("qualifiers",{}).get(property_id,[])
            assert (qualifiers[0]["datavalue"]["value"] if qualifiers else None) == a[field]
        for raw_field, label_field in [("source_start_time","valid_from"),("source_end_time","valid_to")]:
            value = a[raw_field]
            if value:
                assert value["precision"] in [9,10,11]
                length = {9:4,10:7,11:10}[value["precision"]]
                assert a[label_field] == value["time"][1:1+length]
                assert "-00" not in a[label_field]
    assert sum(r["ground_id"] is None for r in reviews) == audit["counts"]["dated_ground_gaps"]
    for r in reviews:
        assert r["membership_id"] in ids(members,"membership_id")
        if r["ground_id"] and r["ground_evidence_status"] == "primary_reference_season_context_not_interval":
            note = next(n for n in read("primary-ground-evidence.json") if n["note_id"] == r["primary_note_id"])
            assert (r["club_id"], r["ground_id"]) == (note["club_id"], note["ground_id"])
            assert r["season"] in note["applicable_seasons"], "primary evidence not reviewed for this season"
            assert r["association_id"] is None and r["source_id"] is None
        elif r["ground_id"]:
            a = next(a for a in associations if a["association_id"] == r["association_id"])
            assert a["evidence_status"] == "dated_source_claim"
            assert (r["club_id"],r["ground_id"],r["source_id"]) == (a["club_id"],a["ground_id"],a["source_id"])
        else:
            assert r["gap_reason"] and r["research_status"]
    for stem, rows in [("memberships",members),("membership-ground-review",reviews)]:
        with (BASE/(stem+".csv")).open(encoding="utf-8",newline="") as f: actual=list(csv.DictReader(f))
        assert actual == [{k:"" if v is None else str(v) for k,v in row.items()} for row in rows]
    for checkpoint in audit["unchanged_prototype_checkpoints"]:
        assert hashlib.sha256((ROOT/checkpoint["path"]).read_bytes()).hexdigest() == checkpoint["sha256"], "prototype changed"
    licence = audit["license_checkpoint"]
    assert hashlib.sha256((BASE/licence["path"]).read_bytes()).hexdigest() == licence["sha256"]
    assert len({g["wikidata_id"] for g in grounds}) == len(grounds) <= 40
    for club in clubs:
        if club.get("identity_lookup_status") != "identified_football_club": continue
        source = next(s for s in sources if s["source_id"] == club["identity_source_id"])
        entity = json.loads((BASE/source["local_path"]).read_text(encoding="utf-8"))["entities"][club["wikidata_id"]]
        types = [c["mainsnak"].get("datavalue",{}).get("value",{}).get("id") for c in entity["claims"]["P31"]]
        if "Q476028" not in types:
            subtype = next(s for s in sources if s["source_id"] == "wikidata-Q103229495")
            rawtype = json.loads((BASE/subtype["local_path"]).read_text(encoding="utf-8"))["entities"]["Q103229495"]
            assert "Q103229495" in types and any(c["mainsnak"].get("datavalue",{}).get("value",{}).get("id") == "Q476028" for c in rawtype["claims"]["P279"])
        assert entity["labels"]["en"]["value"] == club["source_english_label"]
        for field, prop in [("official_website_claims","P856"),("locality_candidates","P131"),("country_candidates","P17")]:
            for item in club[field]:
                claim = next(c for c in entity["claims"][prop] if c["id"] == item["claim_id"])
                assert claim["mainsnak"]["datavalue"]["value"] == item["value"]
                assert item["source_id"] == club["identity_source_id"]
    for ground in grounds:
        source = next(s for s in sources if s["source_id"] == ground["source_id"])
        entity = json.loads((BASE/source["local_path"]).read_text(encoding="utf-8"))["entities"][ground["wikidata_id"]]
        for field, prop in [("administrative_area_claims","P131"),("country_claims","P17"),("location_claims","P276"),("street_address_claims","P6375"),("legacy_address_claims","P969")]:
            for item in ground[field]:
                claim = next(c for c in entity["claims"][prop] if c["id"] == item["claim_id"])
                assert claim["mainsnak"]["datavalue"]["value"] == item["value"]
    for corroboration in read("membership-corroboration.json"):
        assert set(corroboration["club_ids"]) == {m["club_id"] for m in members if m["season"] == corroboration["season"]}
        assert corroboration["observed_at"] == "2026-10-17" and corroboration["source_url"].startswith("https://www.premierleague.com/")
    review_text = (BASE/"MEMBERSHIP-REVIEW.md").read_text(encoding="utf-8")
    assert len(re.findall(r"\| (?:2015/16|2016/17|2026/27) \|",review_text)) == 60
    assert all(c["locality"] is None and c["address"] is None and c["region"] is None for c in clubs)
    validate_supplements(clubs, grounds, sources)
    from audit_venue_catalogue import validate_aggregate
    validate_aggregate(read("venue-audit-2016-17.json"), json.loads((ROOT/"prototypes/first-experience/assets/seasons/2016-17.json").read_text(encoding="utf-8"))["matches"], groundids)
    return audit["counts"]

def validate_supplements(clubs, grounds, captured_sources):
    addresses = read("supplemental-address-reviews.json")
    localities = read("supplemental-club-localities.json")
    names = read("supplemental-ground-name-reviews.json")
    events = read("supplemental-naming-events.json")
    metadata = read("supplemental-sources.json")
    venue = read("supplemental-fixture-venue-audit.json")
    sources = {s["source_id"]:s for s in captured_sources + metadata}
    clubids = {c["club_id"] for c in clubs}; groundids = {g["ground_id"] for g in grounds}
    assert len(metadata) == len({s["source_id"] for s in metadata})
    for s in metadata:
        assert s["observed_at"] == "2026-10-17" and s["source_url"].startswith("https://")
        assert s["reviewed_scope"] and s["inspection_scope"] and s["rights_status"]
        assert s["raw_page_retained"] is False
    original_gaps = read("remaining-field-gaps.json")["ground_street_address_claim_gaps"]
    assert len(addresses) == len({a["ground_id"] for a in addresses}) == 20
    assert {a["ground_id"] for a in addresses} == set(original_gaps)
    for a in addresses:
        assert a["ground_id"] in groundids and a["source_id"] in sources and a["published_address"]
        assert a["effective_from"] is None and a["effective_to"] is None, "supplemental address must not gain invented validity bounds"
        assert a["observed_at"] == "2026-10-17" and a["temporal_status"] and a["address_role"]
        if a["street_line"] is None: assert a["unresolved_reason"]
        for other in a.get("alternative_addresses",[]): assert other["source_id"] in sources and other["role"]
    assert len(localities) == len({x["club_id"] for x in localities}) == 30
    assert {x["club_id"] for x in localities} == clubids
    for x in localities:
        c = next(c for c in clubs if c["club_id"] == x["club_id"])
        assert x["source_id"] == c["identity_source_id"] and x["source_description"] == c["source_english_description"]
        assert x["reported_club_base"] == x["source_description"].split(" in ",1)[1].rsplit(", ",1)[0]
        assert x["reported_country"] == x["source_description"].rsplit(", ",1)[1]
        if x["football_home_town_source_id"] == x["source_id"]:
            assert x["football_home_town"] == x["reported_club_base"], "football locality differs from cited club source"
        else:
            s = sources[x["football_home_town_source_id"]]
            assert (s["supports_club_id"],s["supports_locality"]) == (x["club_id"],x["football_home_town"]), "football locality differs from cited primary source"
        assert x["stadium_municipality"] is None and x["legal_office_address"] is None
        assert x["effective_from"] is None and x["effective_to"] is None
    assert len(names) == len({x["ground_id"] for x in names}) == len(grounds)
    for n in names:
        g = next(g for g in grounds if g["ground_id"] == n["ground_id"])
        assert (n["source_observed_name"],n["source_id"]) == (g["name"],g["source_id"])
        assert n["complete_historical_intervals"] is False
        assert set(n["naming_event_ids"]) == {e["event_id"] for e in events if e["ground_id"] == n["ground_id"]}
    assert len(events) == len({e["event_id"] for e in events})
    for e in events:
        assert e["ground_id"] in groundids and e["source_id"] in sources
        assert e["effective_to"] is None and e["date_basis"]
        precision = e["date_precision"]
        assert precision in ["day","month","year","season_context"]
        if precision == "season_context": assert e["effective_from"] is None
        else:
            pattern = {"day":r"\d{4}-\d{2}-\d{2}","month":r"\d{4}-\d{2}","year":r"\d{4}"}[precision]
            assert re.fullmatch(pattern,e["effective_from"]), "naming date exceeds source precision"
    assert venue["total_source_fixture_denominator"] == sum(s["denominator"] for s in venue["season_coverage"]) == 1140
    cases = venue["cases"]
    assert len(cases) == len({c["case_id"] for c in cases})
    for c in cases:
        assert c["home_club_id"] in clubids and (c["away_club_id"] is None or c["away_club_id"] in clubids)
        assert c["ground_id"] in groundids or (c["ground_id"] is None and c["ground_name_as_source"])
        assert c["counted_in_premier_league_denominator"] == (c["competition"] == "Premier League")
        assert c["event_status"] in ["actual","planned"]
        for sid in c["source_ids"]:
            assert sources[sid]["evidence_event_status"] == c["event_status"], "planned venue source cannot become actual confirmation"
    for row in venue["season_coverage"]:
        assert row["denominator"] == 380 and row["denominator_source_id"] in sources
        selected = [c for c in cases if c["season"] == row["season"] and c["counted_in_premier_league_denominator"]]
        assert row["individual_actual_pl_cases"] == sum(c["event_status"] == "actual" for c in selected)
        assert row["individual_planned_pl_cases"] == sum(c["event_status"] == "planned" for c in selected)
        assert row["unchecked_primary_actual_fixture_venue_records"] == 380-row["primary_verified_actual_fixture_venues"]
        assert 0 <= row["catalogue_venue_records_checked"] <= 380
    catalogue = venue["catalogue_audit"]
    assert catalogue["reviewed_records"] == catalogue["records_with_stadium"] == catalogue["exact_date_home_away_matches_existing_source"] == 380
    assert catalogue["unmatched"] == 0 and re.fullmatch(r"[a-f0-9]{64}",catalogue["sha256"])
    assert catalogue["commit"] in catalogue["source_url"] and catalogue["retained_content"].startswith("Aggregate")

if __name__ == "__main__":
    print(json.dumps({"validated":validate()},indent=2))

