# Shared recorded football pipeline

Both existing inputs use one canonical football contract and one processing/query path. The active implementation replaces the earlier Australian-only snapshot publisher and the later sparse diagnostic export. [Scope and evidence](#verification-boundary) distinguish source parity, local checks, deployment and actual browser results. **Canonical Azure ingestion, duplicate delivery, Worker/API restart and sampled authenticated hosted desktop views are verified on 2026-10-19.**

## Complete available inputs

- Australian SkillCorner recording: all 1,801 frames from 60–240 seconds, 22 people and five possessions. Preserve observed/estimated/unknown methods, independent ball quality and all 348 empty player frames. This is a three-minute real-origin extract.
- Brentford–Chelsea: all 58,801 samples at 100 ms over the existing model's authored 0–5,880-second timeline, 40 roster people and all 14 selected source reports: three goals, nine substitutions and two yellow cards. Movement, slot assignment and precise staging are generated, not actual tracking. Reported-minute facts remain separate from authored display releases; the 90+4 goal releases at authored 5,760 seconds.

The original recording, official JSON, authored configuration and movement model stay unchanged. Other official reference fields, including 11 statistics, formations and the completed result, remain in the privately archived original artifact; they are not yet typed API projections. Preserving these files does not make every field available through the replay endpoint.

## Common path and permissions

Operator publisher → existing Event Hubs `match-observations` → managed-identity Worker → immutable Blob accepted batches/metadata pages → small Cosmos per-dataset commit fence → protected common API → Blazor.

Dataset ID is the Event Hubs partition key. Both sources share `FootballCodec`, `FootballLedger`, `IFootballStore` and the same query eligibility rules. Bounded gzip transport is decoded through that canonical codec. The API retains compact metadata and resolves only the selected tracking chunk, with a bounded payload cache; it does not reconstruct all tracking on every view.

The operator applies [pipeline-access.bicep](../infra/container-apps/pipeline-access.bicep) after validation and independent review. It creates `football/replays` with partition key `/datasetId` and six scoped roles. Worker receives recordings/checkpoint Blob access, Event Hubs receiver and football-container Cosmos contributor. API receives recordings read and football-container Cosmos reader. Web keeps its existing internal API authorization and has no direct football-store role. GitHub CI cannot administer data access.

[apps.bicep](../infra/container-apps/apps.bicep) and the existing private [deployment workflow](../.github/workflows/deploy-development.yml) enable the pipeline explicitly. The workflow's `pipeline_enabled` input defaults to false. Hosted configuration requires Blob/Cosmos endpoints, `football`/`replays`, runtime identity IDs and the Worker Event Hubs host/hub/consumer group. `RecordedImport:Enabled=false` remains required in Azure. Source files are excluded from Git and service images. Reuse the existing subscription, Sweden Central resources and shared registry; no new service tier is part of this proof.

## Operator export, publish and inspect

Prepared source versions are immutable. Select the intended reviewed manifest explicitly; the publisher never silently selects the older v1 input. Do not regenerate or overwrite an existing receipt to update an export. The earlier sparse export is historical.

Use .NET 11 RC2 SDK `11.0.100-rc.2.26504.105` with consistent RC2 output. Run from the repository root. Prepare and validate locally before considering a separately authorized publish:

```powershell
dotnet run --project tools/Matchmate.ReplayPublisher -c Release -- --export-local .local/canonical-brentford-v5 --brentford-manifest .local/shared-inputs/brentford-full-v5/manifest.json --dataset brentford-existing-illustration
dotnet run --project tools/Matchmate.ReplayPublisher -c Release -- --preflight .local/canonical-brentford-v5
```

`--validate-export DIRECTORY` is an alias for `--preflight DIRECTORY`. Omit `--dataset` to export both recordings; the explicit Brentford manifest remains required. The old two-argument export form fails with a manifest-selection message. Use a fresh output namespace when inputs change.

`--export-local` and `--preflight` perform no cloud operation. Export streams the selected complete sources into immutable canonical chunks and a v2 receipt. The bounded private archive includes the selected source manifest, original official/config/model inputs, and its pinned reviewed context, goal-participant receipts, event sequences and generated-action input. It does not scan for or copy unrelated videos/transcripts. Preflight checks transport/archive hashes, source-manifest/capability and full canonical non-tracking record bindings, coverage counts, complete closure and zero pending records. Its JSON report lists dataset/version/stream, manifest revision, expected head, capabilities and counts. Local validation does not prove hosted consumption.

Only after separate authorization for external publication:

```powershell
dotnet run --project tools/Matchmate.ReplayPublisher -c Release -- --publish .local/canonical-brentford-v5
dotnet run --project tools/Matchmate.ReplayPublisher -c Release -- --inspect
```

`--publish` performs the same complete validation before creating Azure clients, then retains the existing immutable archive paths, Event Hubs message IDs and partition keys. Legacy v1 export receipts remain readable. The command archives permitted inputs privately in Blob, then sends canonical batches over AMQP WebSockets on port 443 using the existing authorized Azure CLI session through `AzureCliCredential`; it never starts sign-in or uses account keys/connection strings. Hosted services use their own managed identities. This process adds a versioned stream; it does not delete Cosmos records or overwrite committed v1 payloads.

Sending is not durable-consumption proof. `--inspect` reads actual Cosmos membership, Blob metadata and common start/milestone/middle/end views. A newer manifest can become the selected UI version while import is still open: health or catalog presence alone is insufficient. For the prepared v5 input, require the exact expected version/stream, head **979**, **Closed=true**, **PendingRecords=0**, **58,801 frames**, **14 reports**, **881 generated actions**, and shared-sequence/generated-action capabilities. Compare the preflight report with durable membership and actual API/browser behavior before claiming parity. Verify every selected stream closed with all expected counts, labels and hashes before claiming full ingestion.

Repeat an authorized identical publication to test duplicate handling, compare commit/acceptance receipts, then perform any separately authorized Worker/API restart/recovery check. Corrections require explicit source revisions and stream/version rules; do not overwrite immutable payloads or reuse a sequence for different content.

## Durability, capacity and readiness

Blob payload and metadata pages are persisted before the Cosmos ETag compare-and-swap commits membership. Only committed membership is queryable. A dataset-wide fence serializes competing streams; retries refresh/revalidate. Immutable receipts preserve the first acceptance timestamp across crash retries and duplicates. Event Hubs checkpoints advance last. A failure holds/retries that partition; there is no automatic skipping/deletion of a bad event or exactly-once claim.

The journal uses metadata pages of at most 32 batches, a maximum of 256 pages per dataset, eight streams per dataset, eight datasets, 128 KB root documents and a 1 GB payload safety cap per dataset. Transport/expanded canonical batch caps remain 512 KB; archived receipt/page expansion has its own explicit bounds. These are safety bounds for the current proof, not a season-scale capacity commitment. Copy-on-write metadata leaves unreferenced immutable candidates; cleanup/retention and Azure RU/cost measurements remain operational follow-ups.

`GET /api/football/datasets` and `POST /api/football/view` share the existing `Replay.Read` authorization. Queries carry dataset/version/stream/fixture/cursor/presentation policy and knowledge mode. Unknown source availability cannot support known-then reconstruction; the current completed inputs reject that mode. Corrections select the latest available revision before applying its football/display boundaries, so rewind never resurrects superseded facts.

`/alive` means liveness. API readiness queries common football data. Web readiness uses its managed identity to call the internal API and validate catalog/view context. An `awaiting-pipeline` connectivity result is not a full-data pass. The actual signed-in browser must also select both recordings, traverse ordinary non-milestone cursors and event boundaries, show gaps/generated labels and recover after switching/restart.

All three apps retain min 0/max 1. The Worker uses a managed-identity Event Hubs scaler; record and restore temporary warming. Min zero is a scaling setting, not a complete standby/cost guarantee. Follow the [standby and recovery plan](#standby-and-recovery) when finished.

## Standby and recovery

Keep persistent Blob recordings, Cosmos data, Key Vault, Entra identity and the shared registry. Min-zero scaling reduces idle application compute but does not pause Event Hubs or guarantee zero total cost. A fuller standby/recreation operation must explicitly scope disposable runtime/transport resources and retain Bicep configuration and durable source/commit receipts. Recreating an Event Hub is not the same stream: checkpoint/offset recovery must be planned, or replay the immutable canonical exports idempotently. No standby deletion or restore drill is claimed by this guide.

## Verification boundary

**Historical proof — 19 October 2026, v1 inputs.** The full local regression imports all available frames/events in 995 batches, recovers two closed streams without reading tracking payloads and resolves representative views from at most one tracking chunk. Every canonical output frame independently matches the original source/model with zero identity/coordinate/quality mismatches. Isolated SDK builds and complete-input tests pass on RC1/RC2; after a narrow legacy duplicate-file fix, 103 remaining tests pass per SDK plus repeated atomic cases. Independent final source review has no new concrete finding. The actual restarted signed-in desktop app and independent dogfood judgment now pass full timeline/playback/event/rewind/gap/switching checks. The reviewed Azure deployment workflow succeeded at `b66ad9a`. All 995 committed transport digests match the export; both full streams close with zero pending records. Duplicate and post-restart index/query readbacks are identical; scoped identities and min 0/max 1 are verified. Hosted tester desktop checks pass both recordings, the full Brentford endpoint, final-goal release/rewind and Australian gaps. Initial restart recovery returned one 503 before HTTP 200 Healthy; cold startup can exceed the existing 10-second client timeout. Native POC design migration has its own subsequent verification.

**Current v5 preparation — 25 October 2026, offline only.** The explicitly selected v5 export is validated locally at head 979 / Closed=true / PendingRecords=0, with 58,801 frames, 14 reports, 881 generated actions and the shared-sequence/generated-action capabilities. The historical Azure/duplicate/restart/browser proof above does not establish hosted acceptance of v5. Live import and hosted model enablement remain pending separate authorization and readback; see [cloud model configuration](CLOUD-AI-CONFIGURATION.md) for the configuration readiness boundary.
## Historical browsing reference bundle

The native league and map experience reads the existing two-season reference bundle through the protected API. Local and hosted adapters use the same validated model; standings, club summaries and historical match context are calculated in code. This supplies browsing context alongside the common recording pipeline.

After local export and review, the existing production operator tool publishes it privately:

```powershell
dotnet tools/Matchmate.ReplayPublisher/bin/Release/net11.0/Matchmate.ReplayPublisher.dll --publish-reference .local/league-reference-v1/league-reference.json
```

Use the equivalent built DLL path for an isolated artifacts directory. The fixed immutable Blob is `recordings/football/reference/poc-league-v1.json`, bounded to 2 MB. Existing operator credentials write it; the API's existing recordings reader identity reads it. No additional storage service, key or role is required. Raw reference data stays outside the repository and container images. Deployment assumes that the reviewed bundle has been seeded; missing or invalid data returns an explicit unavailable response.

On 2026-10-19 the 153,117-byte bundle was published and downloaded with identical SHA-256 `32982d9bc2bc272fdd6b7d0c97227326f47d2ac680e62c80dc97aae828f5bbb4`. All 760 results and 40 club-season mappings independently match the original model; 208 snapshots and 4,160 club summaries pass cross-model parity. Actual hosted reference endpoints and native UI are verified separately after deployment.
