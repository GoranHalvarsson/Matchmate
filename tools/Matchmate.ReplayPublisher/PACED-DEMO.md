# Paced generated replay demonstration

This operator-only path derives the first five football minutes of generated positions/actions from a validated existing canonical export. It creates the separate `paced-generated-demo` dataset and deterministic stream/version. Real reported events and possession annotations are excluded. The roster remains source reference information. Generated movement, passes and control spells do not measure the real match, and an empty reported score is not a claim of an actual 0–0 score.

Prepare and inspect entirely offline:

```powershell
dotnet run --project tools/Matchmate.ReplayPublisher -- --export-paced-demo .local/paced-demo-v1 --source-export .local/canonical-brentford-v5
dotnet run --project tools/Matchmate.ReplayPublisher -- --preflight-paced .local/paced-demo-v1
dotnet run --project tools/Matchmate.ReplayPublisher -- --publish-paced .local/paced-demo-v1 --dry-run
```

The source export is fully validated before selection. Source files remain unchanged. The new archive retains source artifacts, the pinned original export manifest and a canonical selection receipt. The first batch contains only fixture, roster and schedule metadata. Each subsequent stage contains samples/actions completed by that five-second football boundary; crossing-cutoff actions are excluded whole. Contiguous batches use the normal `FootballTransport`, validator and ledger. An empty terminal batch closes the stream. Repeating an identical export produces identical files; changing an existing output requires a fresh directory.

Offline preflight accepts each batch through `FootballLedger`, queries the fixed five-minute cursor and retries the identical batch. Its receipt records head, open/closed state, committed tracking frontier, completed pass/control/shot statistics and duplicate stability. It never sends to Azure. First-five-minute shots may be zero; no shot demonstration is promised.

Only after explicit authorization for cloud writes, the existing Event Hub/Worker path can receive the staged export:

```powershell
dotnet run --project tools/Matchmate.ReplayPublisher -- --publish-paced .local/paced-demo-v1 --delay-ms 2000
```

The publisher uses the existing Azure CLI identity, source archive, dataset partition key and `stream:sequence:digest` message IDs. There are 60 waits of two seconds by default (120 seconds scheduled). The permitted 100–2500 ms delay caps scheduled waiting at 150 seconds; network/Worker delays are additional. Each successful send logs an attempt ID, sequence/message ID, elapsed time and accumulated wait. Ctrl+C cancels before further sends. Already accepted batches persist; retry the same export to preserve duplicate identities. Default `--publish` remains unchanged.

For actual browser verification, run the real app in Visual Studio and sign in with the test account. Open `/recordings/paced-generated-demo` and seek to its five-minute end. The direct dataset route identifies the separate demo unambiguously. Its bounded generated-window capability preserves the existing generated-source label. Existing open-stream polling refreshes every five seconds while retaining that cursor, so completed generated statistics can accumulate as batches arrive. Tracking at that fixed cursor remains unavailable until its final sample arrives; the current insight card also requires an eligible frame, so its progressive appearance is not guaranteed before the end. A moving live frontier is not implemented. Sending is not proof of durable Worker acceptance, refreshed hosted catalog or browser behavior; verify those separately. Replaying a previously closed deterministic demo stream is an idempotent retry, not a new progressive run.
