using System.Diagnostics;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;

namespace Matchmate.ReplayPublisher;

public sealed record PacedRecordBinding(string OriginalId, string OriginalHash, string DemoId, string DemoHash, long CompletionMs, bool Tracking);
public sealed record PacedDemoSource(int Version, string Policy, string SourceExportHash, FootballManifest SourceManifest,
    FootballStream SourceStream, FootballRecord[] ContextAndActions, PacedRecordBinding[] Records);
public sealed record PacedDemoStage(long Sequence, long ThroughMs);
public sealed record PacedDemoPlan(int Version, string Policy, string SourceExportHash, FootballStream Stream, long DurationMs,
    int StageMs, string SourceSelectionArtifact, PacedDemoStage[] Stages);
public sealed record PacedDemoProgress(long Head, bool Closed, long ThroughMs, long? TrackingThroughMs, int AcceptedActions,
    int PassAttempts, int PassCompleted, int Shots, long ControlledMs, bool FixedCursorTrackingAvailable, bool DuplicateStable);
public sealed record PacedDemoReadiness(bool OfflineValidated, bool CloudWritesPerformed, string SourceExportHash, string ExportHash,
    FootballStream Stream, long FixedCursorMs, int DefaultDelayMs, int ScheduledDelayMs, PacedDemoProgress[] Progress);
public sealed record ValidatedPacedDemo(FootballExport Manifest, PacedDemoPlan Plan, PacedDemoReadiness Readiness);
public sealed record PacedPublishProgress(string AttemptId, long Sequence, string MessageId, int Bytes, int WaitedMs, long ElapsedMs);

// A bounded, explicitly generated replay demonstration, never a provider/live-time adapter.
public static class PacedReplayDemo
{
    public const string Policy = "paced-generated-demo-v1", DatasetId = "paced-generated-demo", PlanFile = "paced-demo.json";
    public const long DurationMs = 300000;
    public const int StageMs = 5000, MaxFrames = 4000, MaxBatches = 128;
    private const string SelectionPath = "paced-demo/source-selection.json", ExportPath = "paced-demo/source-export.json";

    public static async Task<ValidatedPacedDemo> ExportAsync(PacedReplayExportOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Within(options.SourceDirectory, options.OutputDirectory) || Within(options.OutputDirectory, options.SourceDirectory))
            throw new RecordingValidationException("Paced output and source export must be separate, non-nested directories.");
        var validated = await ReplayExport.ValidateAsync(options.SourceDirectory, token);
        var selected = validated.Manifest.Datasets.SingleOrDefault(d => d.Stream.DatasetId == options.SourceDataset)
            ?? throw new RecordingValidationException("Select exactly one existing source dataset.");
        var records = new List<FootballRecord>(); FootballManifest? originalManifest = null; string? openingPeriod = null;
        foreach (var chunk in validated.Manifest.Chunks.Where(c => c.Stream == selected.Stream))
        {
            token.ThrowIfCancellationRequested();
            var batch = FootballTransport.Decode(await ReplayExport.ReadChunkAsync(options.SourceDirectory, chunk, token), chunk.ContentType);
            originalManifest ??= batch.Manifest;
            openingPeriod ??= batch.Records.Select(r => r.Payload).OfType<SchedulePayload>().SingleOrDefault()?.Segments[0].PeriodId;
            records.AddRange(batch.Records.Where(r => r.Payload is FixturePayload or SquadPayload or SchedulePayload
                || r.Payload is TrackingPayload or FootballActionPayload && r.FootballTime is { StartMs: >= 0, EndMs: <= DurationMs } time && time.PeriodId == openingPeriod));
            if (records.Count > MaxFrames + FootballActionPolicy.MaxGeneratedActionRecordIds + 3)
                throw new RecordingValidationException("Paced source selection exceeds the bounded record cap.");
        }
        var sourceManifest = originalManifest ?? throw new RecordingValidationException("Missing source manifest.");
        var schedule = records.Select(r => r.Payload).OfType<SchedulePayload>().Single();
        var opening = schedule.Segments[0];
        if (opening.LocalStartMs != 0 || opening.CursorStartMs != 0 || opening.CursorEndMs < DurationMs
            || !sourceManifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1))
            throw new RecordingValidationException("Source must support a generated first five-minute segment and actions.");
        records = records.Where(r => r.Payload is FixturePayload or SquadPayload or SchedulePayload
            || r.FootballTime?.PeriodId == opening.PeriodId).ToList();
        var timed = records.Where(r => r.Payload is TrackingPayload or FootballActionPayload).ToArray();
        if (timed.Count(r => r.Payload is TrackingPayload) is < 2 or > MaxFrames || !timed.Any(r => r.Payload is FootballActionPayload)
            || timed.Where(r => r.Payload is TrackingPayload).Min(r => r.FootballTime!.StartMs) != 0
            || timed.Where(r => r.Payload is TrackingPayload).Max(r => r.FootballTime!.EndMs) != DurationMs
            || timed.Any(r => !Generated(r)) || records.Any(r => r.Revision != 1 || r.RecordState != "active")
            || records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new RecordingValidationException("Paced source needs distinct active generated samples/actions covering 0–300000 ms without revisions.");
        var version = "paced-v1-" + validated.Readiness.ExportHash;
        var stream = new FootballStream("run-paced-" + validated.Readiness.ExportHash[..16], DatasetId, version, sourceManifest.FixtureId, "replay");
        var mapped = Remap(records, stream);
        var bindings = records.Zip(mapped, (original, demo) => new PacedRecordBinding(original.RecordId, FootballCodec.ContentHash(original), demo.RecordId,
            FootballCodec.ContentHash(demo), demo.FootballTime?.EndMs ?? 0, demo.Payload is TrackingPayload)).ToArray();
        var source = new PacedDemoSource(1, Policy, validated.Readiness.ExportHash, sourceManifest, selected.Stream,
            records.Where(r => r.Payload is not TrackingPayload).ToArray(), bindings);
        var sourceBytes = JsonSerializer.SerializeToUtf8Bytes(source, FootballCodec.Json);
        if (sourceBytes.Length > 4_000_000) throw new RecordingValidationException("Paced source receipt exceeds archive cap.");
        var selectionFile = "sources/" + ReplayBatchCodec.Hash(sourceBytes) + "-paced-selection.json";
        var originalExport = await BoundedFile.ReadAsync(Path.Combine(options.SourceDirectory, "export.json"), 4_000_000, token);
        if (ReplayBatchCodec.Hash(originalExport) != validated.Readiness.ExportHash) throw new RecordingValidationException("Source export changed during selection.");
        var sourceExportFile = "sources/" + validated.Readiness.ExportHash + "-source-export.json";
        var manifest = DemoManifest(source, stream, ReplayBatchCodec.Hash(sourceBytes));
        var batches = new List<FootballBatch>(); var stages = new List<PacedDemoStage>();
        void AddStage(IEnumerable<FootballRecord> rows, long through)
        {
            foreach (var batch in FootballCodec.Batches(manifest, stream, rows).Where(b => !b.CloseStream))
            {
                var next = FootballCodec.Sign(batch with { Sequence = batches.Count + 1 });
                batches.Add(next); stages.Add(new(next.Sequence, through));
            }
        }
        AddStage(mapped.Where(r => r.Payload is not TrackingPayload and not FootballActionPayload), 0);
        for (var through = StageMs; through <= DurationMs; through += StageMs)
            AddStage(mapped.Where(r => r.Payload is TrackingPayload or FootballActionPayload && r.FootballTime!.EndMs <= through
                && (r.FootballTime.EndMs > through - StageMs || through == StageMs && r.FootballTime.EndMs == 0))
                .OrderBy(r => r.FootballTime!.EndMs).ThenBy(r => r.RecordId, StringComparer.Ordinal), through);
        var close = FootballCodec.Sign(new(manifest, stream, batches.Count + 1, [], true, ""));
        batches.Add(close); stages.Add(new(close.Sequence, DurationMs));
        if (batches.Count > MaxBatches) throw new RecordingValidationException("Paced export exceeds bounded batch cap.");
        Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "sources"));
        var artifacts = new List<ExportArtifact>();
        foreach (var artifact in validated.Manifest.Artifacts)
        {
            await ImmutableAsync(options.OutputDirectory, artifact.File, await ReplayExport.ReadArtifactAsync(options.SourceDirectory, artifact, token), token);
            artifacts.Add(artifact);
        }
        await ImmutableAsync(options.OutputDirectory, selectionFile, sourceBytes, token);
        artifacts.Add(new(selectionFile, SelectionPath, ReplayBatchCodec.Hash(sourceBytes), sourceBytes.Length));
        await ImmutableAsync(options.OutputDirectory, sourceExportFile, originalExport, token);
        artifacts.Add(new(sourceExportFile, ExportPath, validated.Readiness.ExportHash, originalExport.Length));
        var chunks = new List<ExportChunk>();
        foreach (var batch in batches)
        {
            var encoded = FootballTransport.Encode(batch); var file = $"{stream.StreamId}--{batch.Sequence:D12}.batch";
            await ImmutableAsync(options.OutputDirectory, file, encoded.Bytes, token);
            chunks.Add(new(file, encoded.ContentType, ReplayBatchCodec.Hash(encoded.Bytes), encoded.Bytes.Length, stream, batch.Sequence, batch.Digest));
        }
        var export = new FootballExport(2, [new(stream, batches.Count, timed.Count(r => r.Payload is TrackingPayload), 0, 0,
            timed.Count(r => r.Payload is FootballActionPayload), selectionFile)], chunks.ToArray(), artifacts.ToArray());
        await ImmutableAsync(options.OutputDirectory, "export.json", JsonSerializer.SerializeToUtf8Bytes(export, FootballCodec.Json), token);
        var plan = new PacedDemoPlan(1, Policy, validated.Readiness.ExportHash, stream, DurationMs, StageMs, selectionFile, stages.ToArray());
        await ImmutableAsync(options.OutputDirectory, PlanFile, JsonSerializer.SerializeToUtf8Bytes(plan, FootballCodec.Json), token);
        return await ValidateAsync(options.OutputDirectory, token);
    }

    public static async Task<ValidatedPacedDemo> ValidateAsync(string directory, CancellationToken token = default)
    {
        var validated = await ReplayExport.ValidateAsync(directory, token);
        var plan = JsonSerializer.Deserialize<PacedDemoPlan>(await BoundedFile.ReadAsync(Path.Combine(directory, PlanFile), 64000, token), FootballCodec.Json)
            ?? throw new RecordingValidationException("Missing paced demo plan.");
        if (plan.Version != 1 || plan.Policy != Policy || plan.DurationMs != DurationMs || plan.StageMs != StageMs
            || plan.Stream.DatasetId != DatasetId || validated.Manifest.Datasets.Length != 1 || validated.Manifest.Datasets[0].Stream != plan.Stream
            || plan.Stages is not { Length: > 2 and <= MaxBatches } || plan.Stages.Length != validated.Manifest.Chunks.Length)
            throw new RecordingValidationException("Invalid bounded paced demo plan.");
        var sourceArtifact = validated.Manifest.Artifacts.SingleOrDefault(a => a.File == plan.SourceSelectionArtifact && a.SourcePath == SelectionPath)
            ?? throw new RecordingValidationException("Missing paced source selection archive.");
        var source = JsonSerializer.Deserialize<PacedDemoSource>(await ReplayExport.ReadArtifactAsync(directory, sourceArtifact, token), FootballCodec.Json)
            ?? throw new RecordingValidationException("Missing paced source selection.");
        var originalArtifact = validated.Manifest.Artifacts.SingleOrDefault(a => a.SourcePath == ExportPath && a.Hash == plan.SourceExportHash)
            ?? throw new RecordingValidationException("Missing pinned original export archive.");
        var original = JsonSerializer.Deserialize<FootballExport>(await ReplayExport.ReadArtifactAsync(directory, originalArtifact, token), FootballCodec.Json);
        if (source.Version != 1 || source.Policy != Policy || source.SourceExportHash != plan.SourceExportHash || original is null
            || !original.Datasets.Any(d => d.Stream == source.SourceStream) || source.SourceStream.DatasetId == DatasetId
            || plan.Stream != new FootballStream("run-paced-" + plan.SourceExportHash[..16], DatasetId, "paced-v1-" + plan.SourceExportHash, source.SourceManifest.FixtureId, "replay")
            || source.Records.Length > MaxFrames + FootballActionPolicy.MaxGeneratedActionRecordIds + 3)
            throw new RecordingValidationException("Paced source export binding mismatch.");
        var expectedManifest = DemoManifest(source, plan.Stream, sourceArtifact.Hash);
        var expectedContext = Remap(source.ContextAndActions, plan.Stream).ToDictionary(r => r.RecordId, FootballCodec.ContentHash, StringComparer.Ordinal);
        var bindings = source.Records.ToDictionary(r => r.DemoId, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        FootballCodec.ValidateBinding(source.SourceManifest, source.SourceStream);
        if (source.Records.Count(r => r.Tracking) is < 2 or > MaxFrames
            || source.ContextAndActions.Any(r => !bindings.TryGetValue(RecordId(r.RecordId), out var binding)
                || binding.OriginalId != r.RecordId || binding.OriginalHash != FootballCodec.ContentHash(r)))
            throw new RecordingValidationException("Paced original canonical selection mismatch.");
        var ledger = new FootballLedger(); var progress = new List<PacedDemoProgress>(); long? trackingThrough = null; var acceptedActions = 0; long priorThrough = 0;
        for (var index = 0; index < plan.Stages.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var stage = plan.Stages[index]; var chunk = validated.Manifest.Chunks[index];
            var batch = FootballTransport.Decode(await ReplayExport.ReadChunkAsync(directory, chunk, token), chunk.ContentType);
            if (stage.Sequence != index + 1 || batch.Sequence != stage.Sequence || stage.ThroughMs < priorThrough || stage.ThroughMs > DurationMs
                || stage.ThroughMs % StageMs != 0 || stage.ThroughMs - priorThrough > StageMs
                || JsonSerializer.Serialize(batch.Manifest, FootballCodec.Json) != JsonSerializer.Serialize(expectedManifest, FootballCodec.Json)
                || batch.CloseStream != (index == plan.Stages.Length - 1) || batch.CloseStream && batch.Records.Length != 0
                || index == 0 && (stage.ThroughMs != 0 || batch.Records.Length != 3 || batch.Records.Any(r => r.Payload is not (FixturePayload or SquadPayload or SchedulePayload))))
                throw new RecordingValidationException("Paced stage/head/header/manifest mismatch.");
            foreach (var record in batch.Records)
            {
                if (!seen.Add(record.RecordId) || !bindings.TryGetValue(record.RecordId, out var binding) || FootballCodec.ContentHash(record) != binding.DemoHash
                    || RecordId(binding.OriginalId) != record.RecordId || binding.Tracking != (record.Payload is TrackingPayload)
                    || record.Payload is MatchEventPayload or PossessionPayload or ReferencePayload or CoveragePayload
                    || record.FootballTime is { } time && (time.StartMs < 0 || time.EndMs > stage.ThroughMs || time.EndMs != binding.CompletionMs)
                    || record.Payload is TrackingPayload or FootballActionPayload && !Generated(record)
                    || record.Payload is not TrackingPayload && (!expectedContext.TryGetValue(record.RecordId, out var expected) || expected != binding.DemoHash))
                    throw new RecordingValidationException("Paced canonical record binding/completion/provenance mismatch.");
                if (record.Payload is TrackingPayload && FootballCodec.ContentHash(record with { DatasetId = source.SourceStream.DatasetId,
                    DatasetVersion = source.SourceStream.DatasetVersion, RecordId = binding.OriginalId }) != binding.OriginalHash)
                    throw new RecordingValidationException("Paced original tracking content binding mismatch.");
                if (record.Payload is TrackingPayload) trackingThrough = Math.Max(trackingThrough ?? 0, record.FootballTime!.EndMs!.Value);
                if (record.Payload is FootballActionPayload) acceptedActions++;
            }
            ledger.Accept(new(batch, DateTimeOffset.UnixEpoch));
            var dataset = ledger.Catalog().Single(); var schedule = dataset.Schedule!;
            var view = ledger.Query(new(plan.Stream.DatasetId, plan.Stream.DatasetVersion, plan.Stream.StreamId, plan.Stream.FixtureId,
                DurationMs, "retrospective_replay", schedule.PolicyId, schedule.PolicyRevision));
            var before = JsonSerializer.Serialize(view, FootballCodec.Json);
            var duplicateStable = !ledger.Accept(new(batch, DateTimeOffset.UnixEpoch)) && before == JsonSerializer.Serialize(ledger.Query(view.Context), FootballCodec.Json);
            var stats = view.IllustrationStatistics ?? throw new RecordingValidationException("Paced generated statistics prerequisites unavailable.");
            if (!duplicateStable || dataset.PendingRecords != 0 || view.Events.Length != 0 || view.Possessions.Length != 0
                || view.HomeGoals != 0 || view.AwayGoals != 0) throw new RecordingValidationException("Paced ingest/retry/report isolation failed.");
            progress.Add(new(dataset.HeadSequence, dataset.Closed, stage.ThroughMs, trackingThrough, acceptedActions,
                stats.Teams.Sum(t => t.PassAttempts), stats.Teams.Sum(t => t.PassCompleted), stats.Teams.Sum(t => t.Shots),
                stats.TotalClassifiedControlMs, view.Frame is not null && !view.TrackingGap, duplicateStable));
            priorThrough = stage.ThroughMs;
        }
        if (seen.Count != bindings.Count || priorThrough != DurationMs || trackingThrough != DurationMs || !progress[^1].Closed
            || progress[^1].AcceptedActions == 0 || progress[^1].PassAttempts == 0 || progress[0].AcceptedActions != 0)
            throw new RecordingValidationException("Incomplete paced demo coverage/progression.");
        var waits = plan.Stages.Zip(plan.Stages.Skip(1)).Count(p => p.Second.ThroughMs > p.First.ThroughMs);
        return new(validated.Manifest, plan, new(true, false, plan.SourceExportHash, validated.Readiness.ExportHash, plan.Stream, DurationMs,
            PacedReplayPublishOptions.DefaultDelayMs, waits * PacedReplayPublishOptions.DefaultDelayMs, progress.ToArray()));
    }

    public static async Task PublishAsync(string directory, int delayMs,
        Func<FootballBatch, FootballTransportBytes, CancellationToken, Task> send,
        Action<PacedPublishProgress>? progress = null, CancellationToken token = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        PacedReplayPublishOptions.ValidateDelay(delayMs);
        var validated = await ValidateAsync(directory, token); var watch = Stopwatch.StartNew(); var attempt = Guid.NewGuid().ToString("N");
        delay ??= (duration, cancellation) => Task.Delay(duration, cancellation); long prior = 0; var waited = 0;
        for (var index = 0; index < validated.Plan.Stages.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var stage = validated.Plan.Stages[index];
            if (stage.ThroughMs > prior) { await delay(TimeSpan.FromMilliseconds(delayMs), token); waited += delayMs; }
            token.ThrowIfCancellationRequested();
            var chunk = validated.Manifest.Chunks[index]; var bytes = await ReplayExport.ReadChunkAsync(directory, chunk, token);
            var batch = FootballTransport.Decode(bytes, chunk.ContentType);
            await send(batch, new(bytes, chunk.ContentType), token);
            progress?.Invoke(new(attempt, batch.Sequence, MessageId(batch), bytes.Length, waited, watch.ElapsedMilliseconds));
            prior = stage.ThroughMs;
        }
    }

    public static string MessageId(FootballBatch batch) => batch.Stream.StreamId + ":" + batch.Sequence + ":" + batch.Digest;
    private static bool Generated(FootballRecord record) => record.Evidence.All(e => e.Origin == "synthetic" && e.Method == "generated" && e.ClaimStatus == "authored")
        && (record.Payload is not TrackingPayload frame || frame.Players.Append(frame.Ball).All(p => p.Method == "generated"));
    private static string RecordId(string id) => "paced-" + ReplayBatchCodec.Hash(System.Text.Encoding.UTF8.GetBytes(id))[..24];
    private static FootballManifest DemoManifest(PacedDemoSource source, FootballStream stream, string selectionHash) => source.SourceManifest with
    {
        DatasetId = stream.DatasetId, DatasetVersion = stream.DatasetVersion, ManifestRevision = 1, Lifecycle = "open",
        Sources = [.. source.SourceManifest.Sources, new("paced-demo-selection", SelectionPath, selectionHash), new("paced-demo-export", ExportPath, source.SourceExportHash)],
        Capabilities = ["tracking_frame", "sparse-generated-windows-only", FootballActionPolicy.GeneratedModelActionsV1, Policy], QueryModes = ["retrospective_replay"],
        IndependentDependencies = [], Normalization = "Synthetic generated five-minute paced demonstration; source reference identities retained. No reported match events, actual score or measured real movement. Original source normalization, for provenance only: " + source.SourceManifest.Normalization
    };
    private static FootballRecord[] Remap(IEnumerable<FootballRecord> source, FootballStream stream)
    {
        var records = source.ToArray(); var firstPeriod = ((SchedulePayload)records.Single(r => r.Payload is SchedulePayload).Payload).Segments[0].PeriodId;
        var actions = records.Where(r => r.Payload is FootballActionPayload).ToArray();
        return records.Select(record => record with
        {
            DatasetId = stream.DatasetId, DatasetVersion = stream.DatasetVersion, RecordId = RecordId(record.RecordId),
            Payload = record.Payload switch
            {
                FixturePayload fixture => fixture with { Label = "Synthetic five-minute demo · " + fixture.Label,
                    Periods = fixture.Periods.Where(p => p.Id == firstPeriod).Select(p => p with { ActualDurationMs = null }).ToArray() },
                SchedulePayload schedule => schedule with { PolicyId = Policy, PolicyRevision = 1, Segments = [new(firstPeriod, 0, DurationMs, 0)],
                    Releases = actions.Select(r => new PresentationRelease(RecordId(r.RecordId), r.FootballTime!.EndMs!.Value)).OrderBy(r => r.CursorMs).ThenBy(r => r.RecordId, StringComparer.Ordinal).ToArray(),
                    Milestones = [new("Synthetic demo start", 0), new("Synthetic demo end", DurationMs)], TimingMethod = "Authored completion buckets; replay delivery, not real provider availability." },
                _ => record.Payload
            }
        }).ToArray();
    }
    private static bool Within(string root, string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task ImmutableAsync(string directory, string file, byte[] bytes, CancellationToken token)
    {
        // Reuse the canonical relative-path guard before any new output write.
        var path = Path.GetFullPath(Path.Combine(directory, file));
        if (Path.IsPathRooted(file) || !Within(directory, path) || path == Path.GetFullPath(directory)) throw new RecordingValidationException("Paced output path escapes root.");
        if (File.Exists(path)) { if (!(await File.ReadAllBytesAsync(path, token)).SequenceEqual(bytes)) throw new RecordingValidationException("Paced export conflict; use a fresh directory."); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true);
    }
}
