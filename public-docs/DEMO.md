# Try Matchmate

Open the [hosted demo — judge access required](https://mm-web-dev.redtree-349789df.swedencentral.azurecontainerapps.io/) and follow the [judge testing instructions](JUDGE-TESTING.md). Use the privately arranged Tester identity. Developers setting up another instance can follow [Getting started](GETTING-STARTED.md).

## Featured hosted replay

Start at `/recordings` and choose either prepared illustration. Explore reported events alongside generated replay statistics, half-time/full-time recaps and Match story. Ask about a passage while keeping the visible clock/cards, select a row to seek to its end, and rewind to see later evidence disappear.

| Recording | Route | Replay end |
| --- | --- | --- |
| Brentford–Liverpool generated illustration | `/recordings/brentford-liverpool-illustration` | Replay **109:00**, reported score **1–2**, **132** generated Story passages |
| Brentford–Chelsea generated illustration | `/recordings/brentford-existing-illustration` | Replay **98:00**, reported score **3–0**, **150** generated Story passages |

Reported match facts remain separate from generated geometry and statistics. The animation and generated totals do not measure the real match. Select a reported goal, inspect its source explanation and available sequence, and use **Show on pitch** to view the illustration.

Chelsea's recap can include qualified generated statistics, while Liverpool's reported recap stays distinct from its separate generated table. Both use evidence eligible at the replay clock; neither connects to a live Premier League feed.

## Explore a season

Open the league explorer at `/`. Choose a season and move through its played dates. The table, club selection and illustrated stadium map share the same season context. Open a completed result to explore its available match context, then return to the season.

The available reference bundle covers 2015/16, 2016/17 and a 2026/27 snapshot through **20 September 2026**. The newest season is not a live table. The map is an illustration; it is not a surveyed stadium map.

Try a source suggestion first, then a question about the selected club's listed ground or its results so far. Watch the selected season and date: an answer is only useful if it concerns the right club at the right point in that season. A question outside the available evidence may need clarification or remain unanswered.

## Open a recorded match

Go to `/recordings` and choose Brentford–Chelsea. Play, pause, seek and rewind. Select a reported highlight or a player, and inspect the supporting details.

The moving players and ball are **generated illustration**. Reported goals, bookings and substitutions are separate source-backed records. A reported minute does not establish an exact second, and the animation does not show the players' measured movements.

Look at the generated replay statistics, then switch between **Fan** and **Analyst**. The wording changes; the underlying evidence and clock stay the same. When a current insight appears, choose **Ask about this**. It pauses playback and prepares a question. Read or edit it, then choose **Ask Matchmate** yourself.

Select a player and inspect the compact summary above the roster. Where available, metres describe movement in the generated replay. Each team's distance heading changes that team's ordering. An unavailable value is different from zero, and an unverified role remains visibly unverified. Player distance and these role descriptions are currently UI details, not new chat capabilities.

## Three exact questions

Use the same context for all three: `/recordings` → **Brentford–Chelsea generated illustration** (`brentford-existing-illustration`) → **English** → **Fan** → choose **End** and pause at replay **98:00**. The pitch's football clock shows **96:00** at this point; these are different clocks. Choose **New chat** between questions and leave players/highlights unselected for these team-level questions.

| Context to keep visible | Exact question or action | Expected evidence and response shape | Boundary to explain |
| --- | --- | --- | --- |
| Brentford–Chelsea, accepted clock 98:00, English | Choose **Show replay statistics**, or type exactly **“Show replay statistics”** and choose **Ask Matchmate** | A code-formatted overview of both teams' pass attempts/completions, shots and illustrated control, with supporting evidence | The reply should identify the generated replay and its current clock. Control shares use completed classified control spells, excluding pauses, restarts, unknown time and unfinished spells. |
| Keep that recording and paused clock | Type **“How many shots are in this generated replay?”**, then choose **Ask Matchmate** | A focused shots answer for the two teams, after routing to the same validated statistics evidence | It counts completed generated shot actions at this cursor. It cannot establish actual shots, shots on target or individual-player totals. |
| Keep that recording and paused clock | Type **“What is Chelsea’s pass accuracy?”**, then choose **Ask Matchmate** | A focused Chelsea answer showing completed passes, finished attempts and their calculated percentage | The denominator is finished generated pass attempts. With none, the rate is unavailable. It is not Chelsea's measured match passing accuracy. |

The overview uses a direct source route. The two typed questions use Decision-1 to select the statistics workflow; code retrieves, calculates and formats the evidence without a generative narrator. Look for **Source answer**, supporting evidence and the answer's clock. [The full replay screenshot](assets/matchmate-replay-full.jpg) shows a pass-accuracy reply.

If a reply is unavailable, asks for clarification, names a different context or drops the generated qualification, record the question, recording, clock and visible outcome. Continue with the evidence panel and keep the failure visible. For the arithmetic behind the display, see [calculation examples](DATA-AND-SOURCES.md#calculation-examples).

## Try the Story controls

Open either Brentford match and pause after completed passages become eligible. **Ask** keeps the visible clock and cards, selects the passage and prepares an unsent question. Send explicitly to read its generated passage facts. Repeating Ask preserves the existing answer and custom draft. Selecting the row itself seeks to the passage end; rewind withholds later passages and recomputes eligible tables/recaps. Chelsea has **150** completed passages and Liverpool **132** at replay end.

Use **Explain this moment** to read a deterministic snapshot that separates selected reported events from current pitch facts and generated limitations.

## Compare the other recordings

Brentford–Liverpool has its own report-backed match events and a separate generated illustration; its movement/statistics are not measurements of the actual match. Then switch to the Australian tracking extract. It is a three-minute recorded sample with observed, estimated and missing content. Gaps remain gaps. It does not acquire the Brentford illustration's statistics or insight card.

Use the shared language selector to try English, Svenska or Español. Language is a browser preference. Fan/Analyst is a temporary explanation style, and neither is a saved personal football profile.

The demo is strongest when you open the evidence behind an answer. [Data and sources](DATA-AND-SOURCES.md) explains what each recording can support; [limitations](CAPABILITIES-AND-LIMITATIONS.md) explains what remains open.
