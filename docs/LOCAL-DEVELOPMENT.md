# Local development

The [active full-input implementation](AZURE-PIPELINE.md#complete-available-inputs) runs separate Aspire Worker/API/Blazor applications. Both existing datasets enter the same typed record/batch contract, validator, incremental paged journal and query API. Each committed batch is queryable before stream closure. The complete local regression imports 58,801 existing-model Brentford frames/all 14 reports and all 1,801 available Australian frames/five possessions; it recovers closed streams and resolves tracking lazily. SDK checks, every-frame canonical source parity, independent source review and the actual restarted signed-in desktop app now pass. Azure proof remains separate. The earlier 90-test/desktop pass covered the superseded sparse slice; historical measurements below retain that boundary.

Actual full-input desktop checks on 2026-10-18: Australian closed head 30 and Brentford closed head 965; ordinary 08:20/25:00, authored half-time and 98:00 end retain 22 positions. Playback advances and settles after Pause. The 90+4 report appears at authored 5,760,000 ms and rewind removes future reports. Switching clears prior recording/player/cursor context. Australian empty frame 175,900 ms and continuation preserve zero positions/missing ball; 100,000 ms restores observed/estimated data. Independent screenshot/DOM dogfood judgment passes this scope; prototype design fidelity, mobile and AI remain unproved.

## Start in Visual Studio

1. Use .NET 11 RC2 SDK `11.0.100-rc.2.26504.105`, with prerelease support in a compatible Visual Studio. `global.json` pins RC2 with `latestPatch` and `allowPrerelease=true`, matching CI and Docker. See [getting started](../public-docs/GETTING-STARTED.md) for visitor setup instructions; earlier RC1/RC2 verification remains historical evidence below.
2. Run `node tools/export-brentford-full.cjs` from the repository root once. It exports the unchanged existing pure model at 100 ms across 0–5,880 seconds into ignored `.local/shared-inputs/brentford-full-v1/manifest.json` and 919 bounded gzip chunks. This export already exists and has been independently compared with the original model. Do not run the legacy sparse exporter for the active app. The full export is immutable: a changed input/exporter needs a new versioned output namespace rather than overwriting this receipt.
3. Open [Matchmate.slnx](../Matchmate.slnx), set **Matchmate.AppHost** as startup project and select **http**.
4. Start it and open **web HTTPS** at `https://localhost:7410` from the Aspire dashboard at `http://localhost:15400`. Web HTTP 5410 redirects to HTTPS; API/Worker remain loopback 5411/5412. Sign in with the selected tester email. The new root route opens the league explorer; use either recording link or `/recordings` for the shared replay. Refresh recordings while the initial import progresses. The measured first full import takes about three minutes; wait for both streams to close before assessing complete coverage. A running old process must be stopped/restarted to load new source wiring.

Aspire 13.5.3 starts/connects the separate processes and supplies API discovery. Full commits live in ignored `.local/football-full-v1`; the earlier `.local/football` sparse journal is preserved. Raw inputs and the Node export stay outside service images. Replay processing/storage remains local; Web sign-in uses the existing External ID setup and server-side local credential. Keep local API/Worker on loopback. Hosted service authentication is separate; hosted canonical processing requires an explicit `Pipeline:Enabled=true`, with Bicep defaulting it off. Web's fixed 7410 origin must match registered callbacks. Browser review uses the real app started by the user in Visual Studio; no substitute environment is launched.

The Web `https` Development launch profile sets `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`, and AppHost explicitly selects that profile. Server-side model requests therefore require Azure CLI and an existing developer `az login` with the expected tenant/subscription and inference access; this is separate from the tester's website sign-in. This setup needs no API keys; the app does not start an interactive Azure login. Azure.Identity 1.21 supports this [specific-credential selection](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/credential-chains#use-a-specific-credential).

This local-profile setting removes broad credential-chain search; it adds no token cache or warmup. Remove it to restore the default chain for another developer environment. Launches without this profile retain normal credential selection unless their caller sets the variable, and hosted Web's explicit managed identity is unchanged. A real AppHost restart and first-reply measurement are still required; a first reply in less than five seconds remains unverified.

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet run --project src/Matchmate.AppHost --launch-profile http
```

## Local grounded match chat — routed integration

The replay uses a circuit-scoped authenticated service: Decision-1 selects a fixed workflow, the existing executor reads eligible evidence once and one narrator explains the prepared bundle. The narrator advertises no tools, rejects returned tool calls and disables SDK retries. **User-approved Foundry setup and local inference are now active through local user-secret endpoints/deployment names/Enabled settings, using Entra credentials without an API key.** Committed defaults remain disabled. Source questions remain deterministic. Existing Web managed-identity inference roles are ready, but application deployment stays stopped. Earlier connected-model browser checks passed; measuring the credential-reuse change awaits the requested real AppHost restart.

Current routing candidate (2026-10-21): the revised criteria reached 27/28 on separately authored calibration cases at 0.85/0.20. Rescoring those captured calibration probabilities at 0.80/0.20 gave 28/28; these are calibration results, not held-out accuracy. The defaults are now **0.80 minimum probability / 0.20 margin**, retaining the revised criteria. The fresh unseen 63-case held-out evaluation remains pending. The calibration analysis is retained in local operator records and is not included here.

Historical first model evaluation: separate tuning improved 6/8 → 8/8 with criteria-only changes before its untouched held-out run, using 0.85/0.20 thresholds. **That held-out routing was 44/60 (73.33%), below the 90% target**; preserve those results and never tune from its held-out failures. Two actual recording/Luna smokes passed structured/reference/numeric checks with correct source limits, not general truth validation. Independent infra/adapter/runner review and coordinator 215/215 regression passed at that checkpoint. Local setup and evaluation receipts record HTTP timings/token costs separately from evaluator pacing. Local testing is authorized; broad launch is not established by calibration results.

After model-use authorization, configure `DecisionRouting:Enabled`, `Endpoint`, `Deployment`, `TimeoutSeconds`, `MinimumProbability` and `MinimumMargin`, plus `MatchChat:Enabled`, `Endpoint`, `Deployment` and `TimeoutSeconds`, using local user secrets or environment variables, then restart Web. Endpoints must be HTTPS Azure resource roots under `services.ai.azure.com`, `cognitiveservices.azure.com` or `openai.azure.com`, with no alternate port, path, user info, query or fragment. Production transports disable redirects. Local router/narrator share one DI singleton `DefaultAzureCredential`; hosted Web retains its explicit `ManagedIdentityCredential`. The narrator lazily reuses its SDK client per adapter while keeping request messages separate. Optional `ApiKey` values are accepted only in Development/Testing. Disabled/incomplete configuration performs no credential lookup or request. Never store keys in appsettings. The narrator deployment must support strict JSON structured replies; tool support is unnecessary.

The dedicated router calls the [documented Decision endpoint](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision), not chat completions, with one Choice question over the registry IDs. The guide omits the probability JSON shape; actual live responses confirm the implemented complete normalized map. Top-probability/margin defaults are 0.80/0.20 and are not an accuracy guarantee; low values clarify and malformed/provider responses fail closed. Original held-out/tuning artifacts remain historical; the new independent calibration and fresh held-out set are separate. Do not reuse held-out failures for tuning.

Ownership comes from Blazor's authenticated principal; the session captures owner/generation/context, supplies the executor's authoritative validity callback and checks ownership again before committing. Router inputs contain compact question/context/UI-selected-ID pointers, never auth IDs, raw evidence, roster/schedule or old assistant facts. The protected API still authenticates the Web service; a future chat API needs a delegated-user authorization design. Unsupported/clarify/live-unavailable routes cannot select arbitrary arguments or substitute recordings. Errors add no fallback summary.

Memory remains ephemeral and bounded to eight accepted turns. Follow-ups carry prior question/selected-subject/original-clarification pointers, never old assistant facts. Pending clarification saves one original question; its next reply is untrusted context, and handled clarification clears the submitted draft without adding an evidence-less narrator answer. Any context change, identity/reset/cancel/disposal clears pending clarification. Rewind removes future turns; head/application/version/domain changes invalidate history. Asking still pauses replay. There is no durable profile/history or training.

Bounds: 1,000 characters per submitted/original question, 1,800 answer characters, 700 narrator output tokens, one routing inference, at most one football read and one narrator inference. Router responses are capped at 16 KiB; its default deadline is 15 seconds. The entire session request defaults to 30 seconds, configurable within 1–60; stale/late/cancelled results cannot commit. Evidence retains the existing 12,000-character limit and the executor's 32,000-character bundle cap. Numeric/reference checks mitigate errors but do **not** prove metric/entity/unit/time attribution or prose truth. RC2 build and 215 focused offline checks pass. Independent source review passes with no findings (97/97 Decision/session cases rerun, 22 hashes matched). Real user-restarted disconnected desktop/mobile checks pass for both recordings; report: `.azure/routed-chat-dogfood.md`. The final universal source footer was not loaded in that running build and has source/render-test evidence only. These earlier disconnected browser checks do not establish connected-model UX. Narrow live results are recorded above; general narrator quality and end-to-end chat latency remain unverified.

## Email sign-in configuration and boundary

Web uses ASP.NET Core OIDC `11.0.0-rc.2.26504.105` with code flow/PKCE, `openid profile email`, standard discovery-based issuer/audience/signature validation and a secure HttpOnly/Lax session cookie. The friendly Authority is `https://matchmatefans.ciamlogin.com/519cfcb5-8944-4028-a4ef-22d6beecccb9/v2.0`; discovery's actual tenant-specific issuer is authoritative. Claims stay unmapped (`name`/`roles`); Tester is required by the Web fallback policy, replay route and Blazor connection. Tokens are not saved. The cookie expires after one hour without sliding renewal; connections close when their authentication expires. Sign-out is an antiforgery-protected POST, including for signed-in accounts without Tester access. Anonymous sign-in/denied/error/health pages remain available.

Public settings are `Authentication:ExternalId:Authority` and `ClientId` in Web appsettings. `Authentication:ExternalId:ClientSecret` is required from **UserSecretsId `matchmate-web-development`**, already populated by the coordinator. Missing configuration stops startup with a field-specific diagnostic; there is no anonymous bypass. Do not print or commit the credential. The coordinator stored the same 90-day credential in Key Vault `matchmate-web-client-secret`; expiry is **2027-01-16 12:43:38 UTC**, with rotation still operational work. No Key Vault SDK loading is implemented here.

Registered HTTPS callbacks are `https://localhost:7410/signin-oidc` and `https://localhost:7410/signout-callback-oidc`; HTTP 5410 registration was retained during setup. The coordinator also declared and granted only the standard Graph OIDC `openid/profile/email` scopes after the first real attempt reached an admin-approval prompt. These scopes provide sign-in, not Graph data access; assignment-required and the sole Tester assignment remain in force. The initial `Set-MatchmateTesterIdentity.ps1` bootstrap assertions predate the new callbacks/credential/consent and intentionally fail closed: **do not rerun that initial provisioning script against the integrated app**.

**Real browser sign-in verified:** the user started the actual AppHost in Visual Studio, entered the email OTP after the consent correction, and Chrome returned to `https://localhost:7410/` with the protected replay, Matchmate tester preview and Sign out visible. This verifies email sign-in, callback and Tester access on the build before the latest nonTester sign-out/status-response fixes. Coordinator verified playback/player interaction and POST logout through Microsoft account selection back to sign-in; subsequent GET `/` redirected to sign-in. After final-build restart, a new anonymous tab again rendered the correct sign-in page. Latest nonTester/status fixes have automated coverage, without another OTP. Official guidance: [Blazor OIDC](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc?view=aspnetcore-10.0), [OIDC web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0). This does not authorize exposing the local API or claim deployed Azure applications.

## Data flow and recovery

**Historical original snapshot foundation:** the details in this section describe the earlier Australian-only DTO/snapshot path. An intermediate shared slice used common records and `.local/football`; the active full-input implementation now uses the separate `.local/football-full-v1` journal described above.

- Source: unchanged tracking JSON `prototypes/first-experience/assets/tracking/1874553-60-240.json` (separately retained local input), local `manifest.json` and local `SKILLCORNER-LICENSE.txt`. Adapter checks byte/hash receipts. It materializes this bounded file first, then exposes metadata plus `IAsyncEnumerable<RecordedFrame>`; Worker actually enumerates it through `RecordingAccumulator.Accept`. This per-frame boundary is reusable, but no live adapter/Event Hubs client exists.
- Store: ignored `.local/replay`, content-addressed JSON and atomically replaced `latest.json`. Failed/cancelled import preserves the prior valid publication. Readers verify/load complete versions and cache by publication hash. This replaceable local provider is not a production Cosmos/Blob implementation.
- API: `/api/matches/1874553/metadata` once; `/api/matches/1874553/frame?cursor=60.1` returns a bounded current view. It reads only Worker publication, never source JSON. Invalid cursor = 400, unknown match = 404, absent/corrupt publication = 503.
- Web: per-circuit cursor; native pitch/player roster/details; play/pause/scrub; gap pause/continue/skip/return. All 22 identities remain visible. Missing positions are neither drawn nor carried from earlier frames; ball quality is independent. A new circuit resets to passage start.

`LocalReplay:SourceDirectory` (Worker) and `LocalReplay:StoreDirectory` (Worker/API) override paths. AppHost resolves paths from its project directory, independent of caller cwd. Replay storage contains no user data; Web identity uses a protected session cookie, with no profile/history persistence. `/alive` is liveness; API `/health` also requires valid publication. Web retries startup not-ready responses and offers manual retry. Import failure stays unhealthy and appears in Worker logs: correct source/configuration and restart Worker. Original pinned files remain unchanged.

## Measurements and limits

Pinned extracted JSON: **2,865,871 bytes**, SHA-256 `291fa78ef599c34dac6ee04bb8c140fe802882fb6ccca6baa95d8eac69258cd6`. .NET 11 `GZipStream` / `CompressionLevel.Optimal`: **262,072 bytes**. Compressor choices affect size; this is not the original provider archive or an Azure budget.

The inclusive 60–240 s passage has **1,801 frames**, nominal **0.1 s cadence**, **22 identities**, **348 empty player frames**, **15,974 detected** and **15,992 estimated** player positions. Counts/cadence are not measured delivery/write rates, full-match/season capacity or Cosmos RU. Detailed chunk/query/storage sizing continues alongside implementation.

This is **real-origin retrospective Australian A-League data**, including source estimates, not synthetic/live/Premier League. Original source coordinates use metres about pitch centre, x right/y up; common query coordinates use the declared fixed top-left normalization while retaining the original archive. Finite unknown-quality coordinates remain unknown; absent positions stay missing; outside-pitch values are preserved/labelled. Floor-at-cursor queries do not select later frames; this does not establish source known-then availability.

Shared selected-event/possession migration and actual Azure football adapters are verified for both complete inputs. Grounded AI, private memory and unsupported speed/distance/tactics remain later work. Hosting and managed-identity API access, complete dataset integration, duplicate/restart behavior and sampled authenticated hosted desktop views pass; original source-use limits stay explicit. The agreed architecture preserves first-Azure Event Hubs, shared selective Cosmos match facts, Blob tracking, separate private Cosmos memory, then profile-aware proactivity and final synthetic demonstration last.

## Verification

**Historical foundation checks follow below.** Current post-REV-1 evidence includes the independent full RC1/RC2 matrix, code review and actual authenticated desktop interaction/dogfood checks. Subsequent full-recording cloud proof passes for both datasets. Native POC UI changes retain a separate build/browser/review gate.

```powershell
dotnet build Matchmate.slnx
dotnet test tests/Matchmate.Tests/Matchmate.Tests.csproj
```

The sandbox required build-server isolation; testhost ran with normal local process permissions:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
$env:MSBUILDUSESERVER='0'
dotnet build Matchmate.slnx -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/Matchmate.Tests/Matchmate.Tests.csproj -m:1 -nr:false -p:UseSharedCompilation=false
```

All **36 tests pass**: the original 23 cover replay/data/store/API and invariant Home rendering; 13 new authentication cases cover anonymous/nonTester/Tester access (including negotiation), cookie expiry, code/PKCE/local returns, missing-credential refusal, token-validation defaults and POST antiforgery logout/cookie clearing/fixed destination. Standard `WebApplicationFactory` uses real production handlers with protected local test tickets and static protocol metadata; no provider requests, email delivery, substitute browser host or live sign-in occurs in tests. F-001/REV-1 remains independently closed from the earlier 23-test run. The coordinator independently passed all 36 tests; the reviewer passed 13 auth cases with no code/security findings. Initial-build OTP/replay/logout and final-build anonymous smoke are verified as described above; F-002 documentation corrections await reviewer reread.

If the real app locks normal build outputs, leave it running and verify separately in ignored output:

```powershell
dotnet build Matchmate.slnx --artifacts-path .local/auth-verification -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/Matchmate.Tests/Matchmate.Tests.csproj --artifacts-path .local/auth-verification --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
```

The latest full isolated build passed with zero warnings/errors and all 36 tests passed with no skips. The user restarted the final real app and anonymous sign-in smoke passed. The latest status-response/nonTester fixes also have automated coverage; no second OTP was requested.
## Historical league reference for the native POC experience

Generate the existing historical reference bundle once before starting AppHost:

```powershell
node tools/export-league-reference.cjs
```

This uses the two existing pinned season files and existing club/ground/crest mappings. It creates `.local/league-reference-v1/league-reference.json` (153,117 bytes) and a separate local parity receipt. Original inputs remain unchanged; neither generated file belongs in Git or application images. The reference API computes the season table and historical match context from this bundle. It preserves corrected end-of-played-date semantics, same-day results, shared ranks and period-specific club context. The completed season opens paused; playing from the end restarts at the beginning.

Both recorded-match views continue to use the common football API and full recording journal. The new native league/map/season interface is a separate UI milestone; source/backend parity alone is not rendered-design verification.

Native routes are `/` for the league, `/league/{season}/matches/{match}` for a historical result and `/recordings/{datasetId}` for a recording. League query parameters preserve season, played-date index and club on return. Table and illustrated map share selection; grounds and crests follow the selected season. Historical match context uses available results, form and table impact rather than invented lineups or tracking.

The recording page separates the full retrospective highlight index from facts eligible at the current clock. Source question chips produce deterministic answers from the selected context; model conversation and personal memory are not connected. Original additional video/reviewed-capture research modes remain separate until their data has typed API support. Actual desktop/mobile fidelity and the new hosted UI require their own real-app checks.
