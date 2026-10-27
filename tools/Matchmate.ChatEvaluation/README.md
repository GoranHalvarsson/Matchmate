# Bounded real chat evaluation

This console references the production Decision router, workflow executor and narrator. It loads the existing bounded Australian/Brentford adapters through the actual ledger. It is an inference evaluation tool, not a web/browser host or an authentication test. It never creates Azure resources, reads API keys or changes app settings.

Without `--live`, every mode checks frozen fixture hashes and recording contexts, then exits with **zero credential lookup or inference**. `--tuning` defaults to 8 cases; `--heldout` defaults to 60; `--narrator` uses two actual-recording summary smoke questions. `--limit 1` supports a first-response probe without claiming a full held-out result. Frozen held-out `dataset`/`selected`/`original` context is honored. The context-free tuning examples use a fixed Brentford cursor and selected player for every case; this assumption is recorded here and does not depend on expected labels. No thresholds or labels are modified.

For a separately authored frozen calibration or fresh held-out set, use `--fixture .azure/FILE.json --fixture-sha256 SHA256` with `--tuning` or `--heldout`. Both flags are required together; the original built-in files and hashes remain the defaults. The file must be at most 256 KiB with 1–100 unique cases in `{ "cases": [...] }`; each case uses `id`, `question`, `expected`, `category`, `dataset` (`australian`/`brentford`), `selected`, and optional `original`. Case fields, hash and recording contexts are checked before credentials or inference. Keep calibration separate from hidden held-out labels. Optional `--minimum-probability` and `--minimum-margin` apply only to `--tuning`; held-out/narrator modes reject them and use production defaults. Reports and offline output record the exact thresholds; no production configuration is changed.

```powershell
# Offline check only
./.local/dotnet-rc2/dotnet.exe run --project tools/Matchmate.ChatEvaluation -- --heldout

# Explicitly authorized first real Decision response (public endpoint/deployment arguments only)
./.local/dotnet-rc2/dotnet.exe run --project tools/Matchmate.ChatEvaluation -- --tuning --limit 1 --live --decision-endpoint https://RESOURCE.services.ai.azure.com/ --decision-deployment matchmate-decision-1 --out .azure/decision-first.json

# Full held-out evaluation: same arguments, --heldout, no --limit. Never tune from those results.
# Narrator smoke additionally requires --narrator-endpoint https://RESOURCE.openai.azure.com/ --narrator-deployment matchmate-luna, and uses --narrator instead of --tuning.
```

Live mode uses `AzureCliCredential` and requires the coordinator's existing Azure sign-in and scoped inference access. Decision requests are serialized at least 2.2 seconds apart and capped at 100/process. Narrator requests are at least 10 seconds apart, at most 10/process (currently only two smoke cases), with an additional conservative rolling 30,000-TPM reservation based on request bytes plus output/overhead. Responses such as 429 remain visible; there are no automatic retries. Fifteen-minute total and provider deadlines bound the run; Ctrl+C saves completed rows.

Reports under ignored workspace `.azure/` include the fixture hash, executed/available counts, per-category label outcomes, route/abstention/status, stage timings and bounded sanitized response JSON including actual model/usage where supplied. Authentication headers and request bodies are never logged; sensitive response fields are redacted. Non-JSON response bodies are represented only by a hash. Partial runs are labelled partial. Narrative reference/numeric checks are not proof of answer truth. This does not verify the real authenticated UI, prove pricing or close quality/calibration gates by itself.
