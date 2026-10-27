# Matchmate

**Football, in conversation.**

Matchmate explores conversations with matches, teams and stadiums, grounded in the football evidence available to them. The current prototype combines a Premier League season explorer, club and historical-result conversations, and a recorded-match workspace with pitch replay, player details and chat. A personal football guide, voice and live-source integration remain part of the wider direction.

**Built on Microsoft and Azure.** Matchmate runs .NET and Blazor services on Azure Container Apps, with Aspire for development, Event Hubs for incoming records, and Cosmos DB and Blob Storage for football data. **Microsoft Foundry serves all three deployed models:** Microsoft Decision-1 for workflow selection, OpenAI GPT-6 Luna for narration, and OpenAI GPT-6.1 Sol available as a historical-narrator override. The deployed app currently selects Decision-1 and Luna; historical conversation uses Luna unless the Sol override is configured. Microsoft Entra handles tester sign-in, and separate managed identities provide keyless AI/data access. Read the [Azure deployment and identity architecture](public-docs/ARCHITECTURE.md#azure-deployment-and-identity).

Try the [hosted demo — judge access required](https://mm-web-dev.redtree-349789df.swedencentral.azurecontainerapps.io/) and read the [judge testing instructions](public-docs/JUDGE-TESTING.md). Judge identities and private access remain pending.

Start with the [project overview](public-docs/README.md), [demo walkthrough](public-docs/DEMO.md) and [getting started](public-docs/GETTING-STARTED.md). The interface supports English, Swedish and Spanish, with Fan and Analyst explanation styles over the same replay evidence.

Prepared recordings and a generated illustration demonstrate parts of the experience. Generated movement and statistics do not measure the real match; [data and sources](public-docs/DATA-AND-SOURCES.md) and [capabilities and limitations](public-docs/CAPABILITIES-AND-LIMITATIONS.md) explain the boundaries. A new environment also needs prepared inputs, tester sign-in and any enabled model access; a successful build alone does not establish a working demo.

For media preparation, read [from video to replay](public-docs/VIDEO-TO-REPLAY.md) and the included [football-video-to-data skill](public-docs/skills/football-video-to-data/SKILL.md). It supports reviewable evidence preparation, with separate work required for application integration and live tracking.

Explore both prepared Brentford replays with separate reported/generated tables, half-time/full-time recaps and Match story. Ask about a passage while keeping the visible clock and cards, or select a row to seek to its end. Generated movement and statistics illustrate the match rather than measure it; there is no connected live Premier League feed.