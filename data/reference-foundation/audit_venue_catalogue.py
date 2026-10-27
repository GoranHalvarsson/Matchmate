"""Audit explicitly supplied web-tool text in memory; no network or file writes.
CLI stdin: JSON [{"pattern": "Round 1", "text": "<returned public text>"}, ...].
Prints aggregate metadata only. Raw web text/fixture rows are never emitted/saved.
"""
import hashlib
import json
import re
import sys
from datetime import datetime
from pathlib import Path


def audit_windows(windows, expected, team_aliases, venue_aliases):
    lines, references, fixture_keys = {}, {}, {}
    appearances = 0
    for window in windows:
        for match in re.finditer(r"L(\d+):\s*([\s\S]*?)(?=\sL\d+:|$)", window["text"]):
            cells = re.sub(r"cite[^†]+†([^]+)", r"\1", match[2]).strip().split("|")
            cells = [c.strip() for c in cells]
            if not cells or not re.fullmatch(r"\d\d/\d\d/\d{4}", cells[0]):
                continue
            if len(cells) != 6 or not re.fullmatch(r"\d+ \d+", cells[2]):
                raise ValueError("malformed dated fixture row")
            if not cells[4]:
                raise ValueError("blank source venue")
            if cells[4] not in venue_aliases:
                raise ValueError("unmapped source venue; home ground is not a fallback")
            if cells[1] not in team_aliases or cells[3] not in team_aliases:
                raise ValueError("unmapped source club")
            day = datetime.strptime(cells[0], "%d/%m/%Y").date().isoformat()
            row = {"away": team_aliases[cells[3]], "date": day, "home": team_aliases[cells[1]], "score": [int(x) for x in cells[2].split()], "venue": cells[4]}
            key = (day, row["home"], row["away"])
            line = int(match[1]); appearances += 1
            ref = re.search(r"cite([^†]+)†", match[2])
            if ref:
                if ref[1] in references and references[ref[1]] != key:
                    raise ValueError("conflicting source match reference")
                references[ref[1]] = key
            if line in lines:
                if lines[line] != row:
                    raise ValueError("conflicting overlapping source row")
                continue
            if key in fixture_keys:
                raise ValueError("duplicate source fixture key on distinct lines")
            lines[line] = row; fixture_keys[key] = row
    targets = {(m["date"], m["team1"], m["team2"]): m for m in expected}
    if len(targets) != len(expected):
        raise ValueError("duplicate expected fixture key")
    missing, extra = set(targets)-set(fixture_keys), set(fixture_keys)-set(targets)
    if missing or extra:
        raise ValueError(f"fixture key mismatch: missing={len(missing)} extra={len(extra)}")
    if any(row["score"] != targets[key]["score"]["ft"] for key, row in fixture_keys.items()):
        raise ValueError("source score mismatch")
    ordered = [fixture_keys[key] for key in sorted(fixture_keys)]
    canonical = "".join(json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":"))+"\n" for row in ordered)
    keys = "".join("|".join(key)+"\n" for key in sorted(fixture_keys))
    return {"rows":len(lines), "exactJoined":len(lines), "overlapping_repeats":appearances-len(lines), "conflicting_repeats":0, "duplicate_fixture_keys_with_different_source_lines":0, "missing":[], "extra":[], "scoreMismatches":0, "blankVenues":0, "canonical_extracted_rows_sha256":hashlib.sha256(canonical.encode()).hexdigest(), "joined_fixture_keys_sha256":hashlib.sha256(keys.encode()).hexdigest()}


def validate_aggregate(audit, expected, groundids):
    assert audit["rows"] == audit["uniqueRows"] == audit["exactJoined"] == len(expected) == 380
    assert audit["duplicates"] == audit["blankVenues"] == audit["scoreMismatches"] == audit["conflicting_repeats"] == audit["duplicate_fixture_keys_with_different_source_lines"] == 0
    assert audit["missing"] == audit["extra"] == []
    keys = "".join("|".join((m["date"],m["team1"],m["team2"]))+"\n" for m in sorted(expected,key=lambda m:(m["date"],m["team1"],m["team2"])))
    assert hashlib.sha256(keys.encode()).hexdigest() == audit["joined_fixture_keys_sha256"]
    assert sum(w["parsed_match_rows"] for w in audit["windows"]) == 380+audit["overlapping_repeats"]
    assert len(audit["venuesByHomeClub"]) == audit["homeClubs"] == 20
    for club, data in audit["venuesByHomeClub"].items():
        assert sum(m["team1"] == club for m in expected) == data["count"] == 19
        assert len(data["venues"]) == 1 and sum(data["venues"].values()) == 19
        assert all(audit["venue_aliases"][label] in groundids for label in data["venues"])
    for field in ["web_text_sha256","canonical_extracted_rows_sha256"]:
        assert re.fullmatch(r"[a-f0-9]{64}",audit[field])


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    base = Path(__file__).resolve().parent
    metadata = json.loads((base/"venue-audit-2016-17.json").read_text(encoding="utf-8"))
    expected = json.loads((base.parent.parent/"prototypes/first-experience/assets/seasons/2016-17.json").read_text(encoding="utf-8"))["matches"]
    print(json.dumps(audit_windows(json.load(sys.stdin), expected, metadata["teamAliasMap"], metadata["venue_aliases"]),indent=2))
