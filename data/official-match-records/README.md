# Pinned official match reference

The user-approved local prototype import for Brentford–Chelsea, Premier League 2026/27, 18 September 2026 (PL `2645236`) is separate from both the original research pack and estimated video positions. It is a **completed-match reference**, with later events and final statistics, never an as-of-video snapshot.

- `brentford-chelsea-2026-09-18.json`: allowlisted fixture/result, 40 named match-roster rows, source formation classifications and retained disagreements, 11 accepted statistic pairs, 3 goals/9 substitutions/2 cards, attendance/venue/scheduled kickoff and 6 officials.
- `brentford-chelsea-2026-09-18-receipt.json`: pinned artifact receipt and exact input hashes/bytes. Original enrichment/source files remain unchanged. The Martinez name locator is corrected only in this projection; no name or match-team reassignment occurs.
- `brentford-chelsea-2026-09-18-info-supplement.json`: bounded rendered Info-tab observation on 18 October 2026, independently supplied by the coordinator. The published source URL and local review-receipt pointer are retained. Normal builds require no ignored output.
- `project-official-match.cjs`: reproducible offline allowlist. `node data/official-match-records/project-official-match.cjs --check` reproduces pinned JSON/receipt from existing local inputs. Without flags it validates the pinned output alone. `--write` deliberately regenerates it.

Reported minutes retain null seconds; no clock conversion, complete incident feed, live arrival, role inference, name-to-pitch join or player movement is supplied. Unknown assists remain unknown. Premier League's Thiago 83′ and club-report 80′ disagreement remains visible in sources, as do formation discrepancies. Published red-card zero is a reported value, not nonmention. Inconsistent inside/outside-box shot rows remain excluded.

The first goal links only to the existing 3708–3719 **video passage**. Its reviewed-frame scoreboard displays 0–0; finish/contact and exact goal time have not been verified or reconstructed. This association is not an exact event-second or identity join. Sources are public official pages, but external publication/reuse and hackathon submission permissions remain separate. See [data and sources](../../public-docs/DATA-AND-SOURCES.md) for broader source boundaries.
