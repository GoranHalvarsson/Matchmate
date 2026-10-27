# Judge testing instructions

[Hosted demo — judge access required](https://mm-web-dev.redtree-349789df.swedencentral.azurecontainerapps.io/)

Use the private access instructions arranged for judging. The hosted app requires an assigned Tester identity; this repository contains no credentials. The operator must arrange and verify the judge identities and code-receipt route before judging.

## Sign in

Follow the privately supplied access instructions. The current app uses Microsoft Entra email-code sign-in and requires an assigned **Tester** role. Use the identity and code-receipt route arranged for judging. If your session expires, sign in again through the same route. If sign-in or authorization fails, report it through the private submission channel without sharing a verification code.

[Official Rules §4.8 — Testing](https://github.com/microsoft/insidethegamehackathon/blob/main/OFFICIAL%20RULES.md#48-testing) requires a working project link, login credentials in the testing instructions for a private demo, and free access without restrictions until judging ends. The private access arrangement remains an operator task; these prepared pages do not confirm it is complete.

## Suggested walkthrough

Begin with the [featured replay](DEMO.md#featured-hosted-replay), choosing your preferred interface language. Both prepared Brentford matches have separate reported/generated tables, half/full recaps and Story (150 Chelsea / 132 Liverpool passages). Ask keeps the clock/cards and prepares an unsent question; repeating Ask preserves the answer/draft, while clicking a row seeks to its end. If a step or recording is unavailable, note it in your feedback and continue with the available steps.

1. **Open the illustrated recording.** Go to `/recordings` and select Brentford–Chelsea, recording ID `brentford-existing-illustration`, if listed. Play, pause, seek forward and rewind. The moving players are generated illustration, not observed movements.

2. **Explore a goal.** Select the first reported goal and use its explanation action. Inspect the answer, supporting evidence and diagram, then choose **Show on pitch**. A reported minute does not establish an exact second; the illustrated sequence does not establish real player movement. Rewinding changes the evidence available at the current replay clock.

3. **Inspect players and generated distance.** Select a player on the pitch or in the roster and inspect the selected-player summary. Where shown, distance describes movement in the **generated replay**, not measured match distance or performance. Try each team's distance heading to change that team's ordering. Check the role description and its evidence status: an unverified role remains unverified, and an unavailable distance is different from zero. These player distances and role descriptions are UI details, not additional player-statistics chat capabilities.

4. **Compare explanation styles and languages.** If available, switch between **Fan** and **Analyst** on the recording. They change the wording over the same evidence and replay clock. Try **English**, **Svenska** and **Español** using the shared language selector. Language is a browser preference; Fan/Analyst is a temporary explanation style. Neither is a saved personal football profile.

5. **Try an insight and a conversation.** Play until a current insight appears, if that feature is available. Choose **Ask about this**: it should pause playback and prepare a question without sending it. Read or edit the question, then choose **Send** explicitly. You can also try an available source suggestion or type a question such as “Summarize this match so far.” Inspect the selected context, evidence and any source links. AI wording varies; an exact response is not guaranteed, and an answer may ask for clarification or report that evidence is unavailable.

6. **Optionally explore a season.** Open the home page at `/`. Choose a season, move through played dates, and select a club. Inspect the table and illustrated stadium map, then open a completed result if available. The prepared reference bundle covers 2015/16, 2016/17 and a 2026/27 snapshot through **20 September 2026**; the newest season is not a live table. Keep the selected season, date and club in view when asking a question.

7. **Compare recording evidence, if available.** Switch to the Australian tracking extract. It is a three-minute recorded sample with observed, estimated and missing content. Gaps remain visible; it does not inherit the Brentford illustration's generated statistics or current-insight card.

## How to interpret the demo

Brentford–Chelsea player and ball movement is **synthetic illustration**, not actual tracking. Source-backed reported events are separate from that movement. Generated movement, distance and replay statistics cannot establish actual match metrics or individual performance. The stadium map is also an illustration. See [data and sources](DATA-AND-SOURCES.md) and [capabilities and limitations](CAPABILITIES-AND-LIMITATIONS.md) for the evidence boundaries.

## Report a problem

Use the private submission channel to report the page or recording, selected season/date or replay time, language, action taken and visible result. Include the question text for a chat problem and describe what you expected. Redact email addresses and any sign-in details from screenshots. Never include verification codes, passwords, tokens or other secrets.

Return to the [project overview](README.md) or the [demo guide](DEMO.md).
