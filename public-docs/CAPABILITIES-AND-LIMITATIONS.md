# Capabilities and limitations

Matchmate combines a season explorer, conversations about clubs and historical results, and a recorded-match workspace with player details, reported events, generated replay statistics, Match story and recaps. The hosted app runs on Azure and uses Foundry-served models alongside deterministic source answers.

| You can explore | Keep this boundary in mind |
| --- | --- |
| Season tables, played dates, club context and historical results | Coverage comes from prepared reference bundles. The 2026/27 view stops at 20 September 2026 |
| Club/ground questions and historical match conversation | Answers depend on the selected subject, date and available fields. Routing and AI wording still have gaps |
| Three prepared recordings through the shared replay interface | Brentford–Chelsea and Brentford–Liverpool movement is generated; the Australian sample is limited, with estimates and gaps. The pinned Liverpool export is included; the ignored Chelsea/league runtime bundles must be supplied separately |
| Highlights, player selection, source details and supported sequence diagrams | Reported events and authored visuals remain distinct. Source-minute precision does not imply exact seconds |
| Generated team statistics and a current replay insight | These describe completed generated actions at the accepted clock, not actual match performance or a live momentum detector |
| Completed Match story passages and separate reported/generated tables in both Brentford matches | 150 Chelsea / 132 Liverpool passages. Ask keeps the clock/cards and prepares an unsent question; repeating Ask preserves the answer/draft, selecting a row seeks, and rewind limits eligible passages |
| Automatic half-time/full-time recaps and Explain this moment | Recaps use eligible evidence at half-time/full-time. Explain this moment separates selected reports, current pitch facts and generated limitations without a model call |
| A prepared question from an insight | “Ask about this” pauses and prepares. Sending remains an explicit action |
| Fan and Analyst explanation styles | They change wording over the same evidence. This temporary setting is not a saved profile |
| English, Swedish and Spanish interface/source answers | Browser language preference persists. Model wording can vary, and unsupported questions can return clarification or unavailable feedback |
| Generated per-player metres, sorting and a selected-player summary | Distance and scoped roles are UI-only. Unknown roles stay unknown; unavailable distance is not zero |

## What is still missing

There is no connected real-time Premier League feed or automatic event discovery. The current experience uses prepared recordings. Playback and recaps do not establish a live connection to football events.

The prototype has no saved favorites, learned personal football profile, durable personal conversation memory, proactive pen-pal updates or streaming voice. Those remain part of the wider direction for conversations with matches, teams, stadiums and a personal guide.

Complete player-performance statistics, actual running distance, speed and reliable performance ratings are unavailable. A generated replay cannot fill those gaps. Source records may also disagree or lack enough detail; the interface should retain that uncertainty.

## Access and reliability

The hosted app requires an assigned Tester identity and private sign-in instructions. Credentials are not supplied in the source bundle. Session expiry may require signing in again. A separate development environment needs its own authorized inputs, Entra configuration and model permissions.

AI checks can reject a reply or return unavailable feedback. Source and numeric checks help, but a plausible answer can still concern the wrong subject or overinterpret evidence. Inspect the selected context and sources before relying on it.

Continue with the [demo](DEMO.md), [setup guide](GETTING-STARTED.md) or [roadmap](ROADMAP.md).
