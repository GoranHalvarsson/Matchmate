# Getting started

This source bundle includes application source, tests, infrastructure, visitor documentation, a portable media-preparation skill and checked-in data/prototype artifacts. It excludes private project notes, credentials, original video and the ignored Chelsea/league runtime bundles. Redistribution of included third-party material remains unresolved; inclusion is not permission to publish or reuse it. For the separate preparation workflow and tools, read [From video to replay](VIDEO-TO-REPLAY.md).

## Build the source

Use the .NET SDK selected by `global.json`: **11.0.100-rc.2.26504.105**, with prerelease support enabled. The application targets `net11.0`; `src/Matchmate.AppHost/Matchmate.AppHost.csproj` selects **Aspire 13.5.3**. For the Visual Studio workflow, use a version that supports this prerelease SDK and Aspire projects.

From the source root:

```powershell
dotnet --version
dotnet restore Matchmate.slnx
dotnet build Matchmate.slnx --no-restore
```

A running development environment also needs prepared replay inputs, tester sign-in and any enabled model access. Complete the steps below after building.

## Prepare the local inputs

The local Worker and API share file storage configured by `src/Matchmate.AppHost/Program.cs`. A complete demo needs the authorized inputs below. The checked-in Australian samples and pinned Liverpool generated export are included; the ignored Chelsea export and league bundles are excluded. Embedded distance/action packs support calculations alongside the Worker ingestion manifests.

| Input | Expected source-relative location |
| --- | --- |
| Brentford full illustration export (excluded) | `.local/shared-inputs/brentford-full-v5/manifest.json` and its referenced chunks |
| Brentford–Liverpool pinned generated export (included) | `output/brentford-liverpool-generated/dataset-v2/manifest.json` and its referenced chunks |
| Brentford–Liverpool pinned goal review (included) | `output/brentford-liverpool-goal-review/goal-review.v1.json` |
| Australian tracking sample (included) | `prototypes/first-experience/assets/tracking/1874553-60-240.json` with its supporting metadata/provenance files |
| Australian completed-event annotations (included) | `prototypes/first-experience/assets/tracking/1874553-completed-events.json` |
| League reference bundle (excluded) | `.local/league-reference-v2/league-reference.json` |

With all required original inputs present, these existing Node.js scripts prepare the replay export and league bundles:

```powershell
node tools/export-brentford-full.cjs
node tools/export-league-reference.cjs
node tools/export-current-league-reference.cjs
```

The first script's current default is `brentford-full-v5`. It does not prepare the separate Liverpool export; that pinned export is included at the path above. Original media and arbitrary generated outputs are excluded, and recreating the export requires a separately authorized preparation workflow. The league scripts run in order because v2 composes the historical v1 bundle. They validate pinned inputs and preserve immutable outputs. If an input is missing or a source/hash check fails, stop and resolve that prerequisite; do not delete validation or substitute invented data to make the command pass. An operator can instead prepare the approved inputs before a demo.

The ignored `.local/football-full-v1` canonical journal/store is also excluded. The Worker builds its local store from the supplied/imported inputs; checked-in Liverpool inputs do not supply the missing Chelsea export or league bundles.

## Import cloud recordings

Deploying the application does not import recordings from your local store. For an authorized cloud import, prepare an approved canonical export bound to the intended source and revision, then use the existing ReplayPublisher preflight and publish pipeline. Use the configured environment's existing operator credentials; keep secrets out of the export and repository.

From the source root, replace `APPROVED_EXPORT_DIRECTORY` with that prepared export directory:

```powershell
dotnet run --project tools/Matchmate.ReplayPublisher -- --preflight APPROVED_EXPORT_DIRECTORY
dotnet run --project tools/Matchmate.ReplayPublisher -- --publish APPROVED_EXPORT_DIRECTORY
```

Run publish only after preflight succeeds and the import is authorized. These commands validate and send an existing export; they do not create a Liverpool export. Event Hub send completion alone does not prove the Worker durably accepted the recording. Inspect the cloud catalogue and full committed head, confirm the intended revision is closed with zero pending batches, and compare accepted cloud queries with the approved local source. Preserve other datasets and runtime configuration.

Application deployment and recording import are separate operations. Use the [demo walkthrough](DEMO.md#featured-hosted-replay) after the intended recordings appear in the catalogue.

## Keep the three access paths separate

| Access path | Credential and purpose |
| --- | --- |
| Developer/operator Azure access | Local development and authorized publisher operations use `AzureCliCredential` after `az login`. The signed-in Azure identity needs the appropriate data/inference permissions; this does not sign a tester into the website. |
| Hosted service access | Web, API and Worker each use their own user-assigned managed identity. Web calls the internal API with its `Replay.Read` app token and calls Foundry with its scoped Cognitive Services User role. Worker/API use scoped Event Hubs, Blob and Cosmos roles. |
| Browser tester access | Microsoft Entra External ID email-code sign-in, followed by a server cookie and the Tester role. It does not grant Azure operator or model permissions. |

The [deployment and identity architecture](ARCHITECTURE.md#azure-deployment-and-identity) shows the full path. AI/data connections are keyless; Web still needs its confidential OIDC client secret. Locally keep it in user secrets; hosted Web receives it through a Key Vault reference, and its cookie Data Protection keys persist in Blob Storage. Configure your own environment; the keys below name settings and do not supply credentials or tenant/resource identifiers.

## Configure tester sign-in

The Web app requires Microsoft Entra External ID sign-in and the **Tester** role. It has no anonymous replay mode. Use your own configured identity application or an operator-provided tester setup.

Open **Manage User Secrets** for `src/Matchmate.Web/Matchmate.Web.csproj`, and configure:

| Key | Value to supply |
| --- | --- |
| `Authentication:ExternalId:Authority` | Your HTTPS External ID authority |
| `Authentication:ExternalId:ClientId` | Your Web application's client ID |
| `Authentication:ExternalId:ClientSecret` | Its server-side credential, kept in local secret storage |

Register the local HTTPS callbacks `https://localhost:7410/signin-oidc` and `https://localhost:7410/signout-callback-oidc`. The intended test account needs the app's Tester assignment. Do not copy another environment's identifiers or credentials into source control.

## Connect AI when available

Source suggestions use deterministic evidence paths. Free-text AI conversation additionally needs suitable model deployments and inference permission. Committed model defaults are disabled.

Configure `DecisionRouting:Enabled`, `DecisionRouting:Endpoint` and `DecisionRouting:Deployment`, plus `MatchChat:Enabled`, `MatchChat:Endpoint` and `MatchChat:Deployment`, through Web user secrets or environment variables. All configured models are served through **Microsoft Foundry**: Microsoft Decision-1 selects a fixed workflow through its Microsoft provider endpoint; Azure-hosted OpenAI GPT-6 Luna narrates prepared replay/league evidence through `AzureOpenAIClient` Chat Completions. Narrators return strict JSON over supplied evidence; they do not control tools.

**Microsoft Decision-1, OpenAI GPT-6 Luna and OpenAI GPT-6.1 Sol are deployed in Foundry.** The running Web app selects Decision-1 for workflow routing and Luna as its shared narrator. To use Sol specifically for historical narration, configure `HistoricalReplyModel:Deployment` with the intended deployed model. It shares the `MatchChat` connection settings; without an override, historical narration uses `MatchChat:Deployment`. Read the [model roles](ARCHITECTURE.md#workflow-selection-and-model-roles). Story grouping and source summaries remain code-owned; Decision-1 passage classification is postponed.

Endpoints must be HTTPS Azure resource roots on `services.ai.azure.com`, `cognitiveservices.azure.com` or `openai.azure.com`. Deployment names and access must come from your own configured environment. The Web Development launch profile selects `AzureCliCredential`, so install Azure CLI and sign in as a developer identity with inference access before starting:

```powershell
az login
```

This Azure CLI identity is used locally; hosted services select their explicit user-assigned managed identities instead. Developer/operator Azure authentication remains separate from tester browser sign-in. This guide supplies setting names and placeholders, not services, permissions or model access. Offline Python/FFmpeg/faster-whisper/Node.js preparation is separate tooling, not a Microsoft-hosted runtime service.

## Start the real app

Trust the local development certificate if needed:

```powershell
dotnet dev-certs https --trust
```

Open `Matchmate.slnx` in Visual Studio. Set **Matchmate.AppHost** as the startup project, select **http**, and start it. Alternatively, after completing the same prerequisites:

```powershell
dotnet run --project src/Matchmate.AppHost --launch-profile http
```

The Aspire dashboard is at `http://localhost:15400`. Open **web HTTPS**, `https://localhost:7410`, and sign in with the assigned tester account. Keep the local API and Worker on loopback. Allow the Worker to import the prepared recordings before expecting full replay coverage; inspect its logs for missing-input errors.

For a prepared judge demo, the operator needs to supply the running instance, working tester access, prepared inputs and any enabled model access separately. An expired browser session requires normal sign-in again. Use the [demo walkthrough](DEMO.md) once the app is ready.

## Ready when

Use this checklist when preparing your own instance.

| Check | Ready when you can observe | If it fails |
| --- | --- | --- |
| Prepared inputs and import | Worker import finishes, the intended recording is listed, and seeking produces the expected replay view rather than pending or missing coverage | Inspect Worker logs and resolve the missing authorized input or validation failure. Keep hash/source validation intact; do not fill missing evidence with invented data. |
| Tester sign-in | The intended tester signs in through the configured route and reaches the protected recording page | Check the identity application's callbacks and Tester assignment with the operator. Use normal sign-in again after expiry; keep codes and credentials private. |
| Source response | In Brentford–Chelsea at accepted clock 98:00, **Show replay statistics** returns qualified generated totals and supporting evidence for that recording and clock | Check import/API availability and the accepted context. Record the visible failure; do not present a saved answer as a successful live response. This direct source route does not require a model call. |
| Optional typed-question routing and AI | With configured model access, the [exact typed questions](DEMO.md#three-exact-questions) return the intended evidence; any additional AI explanation remains grounded and visibly qualified | Check model enablement, deployment settings and developer inference access. If unavailable, demonstrate the working source controls and state that typed routing/AI is unavailable. The listed statistics replies use code after routing, so they do not verify generative answer quality. |
| Presentation instance | A fresh sign-in and walkthrough work on the exact local or hosted instance intended for the audience | Resolve access or feature gaps before marking the presentation ready. Record which instance and steps were checked; repeat after a relevant build or configuration change. |

For browser verification, use the real app running in Visual Studio and the intended test account. Follow the [three presentation questions](DEMO.md#three-exact-questions), checking the recording, replay clock, generated labels and sources in each reply.