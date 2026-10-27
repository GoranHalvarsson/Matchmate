using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;

namespace Matchmate.ReplayPublisher;

public sealed record ExportChunk(string File, string ContentType, string Hash, int Bytes, FootballStream Stream, long Sequence, string Digest);
public sealed record ExportDataset(FootballStream Stream, long Batches, int Frames, int Reports, int Possessions, int Actions = 0, string? SourceManifestArtifact = null);
public sealed record ExportArtifact(string File, string SourcePath, string Hash, int Bytes);
public sealed record FootballExport(int Version, ExportDataset[] Datasets, ExportChunk[] Chunks, ExportArtifact[] Artifacts);
public sealed record ExportDatasetReadiness(FootballStream Stream, int ManifestRevision, long Head, bool Closed, int PendingRecords,
    int Frames, int Reports, int Possessions, int Actions, string[] Capabilities, string Normalization);
public sealed record ExportReadiness(bool OfflineValidated, bool CloudWritesPerformed, int ExportVersion, string ExportHash,
    ExportDatasetReadiness[] Datasets, int SourceArtifacts, long TransportBytes);
public sealed record ValidatedExport(FootballExport Manifest, ExportReadiness Readiness);

public static class ReplayExport
{
    private const int MaxBrentfordNonTrackingRecords = FootballActionPolicy.MaxGeneratedActionRecordIds + 32;
    public static async Task<ValidatedExport> ExportAsync(ReplayExportOptions options, string root, CancellationToken token = default)
    {
        var inputDirectory = Path.GetDirectoryName(options.BrentfordManifest)!;
        if (Within(inputDirectory, options.OutputDirectory)) throw new RecordingValidationException("Export output must be outside the prepared input directory.");
        // Resolve the bounded archive before writing output. The source adapter validates all frame/policy pins while streaming.
        var archiveInputs = await BrentfordArchiveAsync(options.BrentfordManifest, root, token);
        var sources = new List<IStreamingFootballSource>();
        if (!options.BrentfordOnly) sources.Add(new FullAustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")));
        sources.Add(new FullBrentfordFootballSource(options.BrentfordManifest));
        Directory.CreateDirectory(options.OutputDirectory);
        var chunks = new List<ExportChunk>(); var datasets = new List<ExportDataset>(); var archives = new List<ExportArtifact>();
        foreach (var source in sources)
        {
            var frames = 0; var reports = 0; var possessions = 0; var actions = 0; FootballBatch? last = null;
            await foreach (var batch in source.ReadBatchesAsync(token))
            {
                var encoded = FootballTransport.Encode(batch); var file = $"{batch.Stream.StreamId}--{batch.Sequence:D12}.batch";
                await ImmutableAsync(Path.Combine(options.OutputDirectory, file), encoded.Bytes, token);
                chunks.Add(new(file, encoded.ContentType, ReplayBatchCodec.Hash(encoded.Bytes), encoded.Bytes.Length, batch.Stream, batch.Sequence, batch.Digest));
                frames += batch.Records.Count(r => r.Payload is TrackingPayload); reports += batch.Records.Count(r => r.Payload is MatchEventPayload);
                possessions += batch.Records.Count(r => r.Payload is PossessionPayload); actions += batch.Records.Count(r => r.Payload is FootballActionPayload); last = batch;
            }
            if (last is null || !last.CloseStream) throw new RecordingValidationException("Source did not close completely.");
            datasets.Add(new(last.Stream, last.Sequence, frames, reports, possessions, actions));
        }
        if (!options.BrentfordOnly)
        {
            foreach (var path in new[] { "1874553-60-240.json", "manifest.json", "SKILLCORNER-LICENSE.txt", "1874553-completed-events.json", "completed-events-manifest.json" })
                archiveInputs.Add(new(Path.Combine(LocalPaths.Source, path), "australian/" + path, null));
        }
        Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "sources"));
        foreach (var input in archiveInputs)
        {
            var bytes = await BoundedFile.ReadAsync(input.Path, 4_000_000, token); var hash = ReplayBatchCodec.Hash(bytes);
            if (input.Hash is not null && hash != input.Hash) throw new RecordingValidationException("Source archive input pin mismatch: " + input.Locator);
            var file = "sources/" + hash + "-" + Path.GetFileName(input.Path);
            await ImmutableAsync(SafePath(options.OutputDirectory, file), bytes, token);
            archives.Add(new(file, input.Locator, hash, bytes.Length));
        }
        var sourceManifest = archives.Single(a => a.SourcePath == "brentford/manifest.json");
        datasets = datasets.Select(d => d.Stream.DatasetId == "brentford-existing-illustration" ? d with { SourceManifestArtifact = sourceManifest.File } : d).ToList();
        var manifest = new FootballExport(2, datasets.ToArray(), chunks.ToArray(), archives.ToArray());
        await ImmutableAsync(Path.Combine(options.OutputDirectory, "export.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, FootballCodec.Json), token);
        return await ValidateAsync(options.OutputDirectory, token);
    }

    public static async Task<ValidatedExport> ValidateAsync(string directory, CancellationToken token = default)
    {
        var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, "export.json"), 8_000_000, token);
        var manifest = JsonSerializer.Deserialize<FootballExport>(bytes, FootballCodec.Json) ?? throw new RecordingValidationException("Missing export.");
        if (manifest.Version is not (1 or 2) || manifest.Datasets is not { Length: > 0 and <= AzureFootballStore.MaxDatasets }
            || manifest.Chunks is not { Length: > 0 } || manifest.Chunks.Length > AzureFootballStore.MaxBatchesPerDataset * AzureFootballStore.MaxDatasets
            || manifest.Artifacts is not { Length: > 0 and <= 32 } || manifest.Chunks.Sum(c => (long)c.Bytes) > AzureFootballStore.MaxDatasetBytes
            || manifest.Artifacts.Select(a => a.File).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Length
            || manifest.Artifacts.Select(a => a.SourcePath).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Length)
            throw new RecordingValidationException("Export capacity/version/archive mismatch.");
        var ledger = new FootballLedger(false); var counts = new Dictionary<string, (int Frames, int Reports, int Possessions, int Actions, int Batches)>();
        var members = new HashSet<(string, long)>();
        var sourceBoundRecords = new Dictionary<string, Dictionary<(string Id, int Revision), FootballRecord>>();
        foreach (var chunk in manifest.Chunks)
        {
            var batch = FootballTransport.Decode(await ReadChunkAsync(directory, chunk, token), chunk.ContentType);
            if (batch.Stream != chunk.Stream || batch.Sequence != chunk.Sequence || batch.Digest != chunk.Digest
                || chunk.File != $"{batch.Stream.StreamId}--{batch.Sequence:D12}.batch" || !members.Add((batch.Stream.StreamId, batch.Sequence)))
                throw new RecordingValidationException("Export chunk binding/duplicate mismatch.");
            ledger.Accept(new(batch, DateTimeOffset.UnixEpoch));
            if (manifest.Version == 2 && batch.Manifest.DatasetId == "brentford-existing-illustration")
            {
                if (!sourceBoundRecords.TryGetValue(batch.Stream.StreamId, out var records)) sourceBoundRecords[batch.Stream.StreamId] = records = [];
                foreach (var record in batch.Records.Where(r => r.Payload is not TrackingPayload))
                    if (records.Count >= MaxBrentfordNonTrackingRecords || !records.TryAdd((record.RecordId, record.Revision), record))
                        throw new RecordingValidationException("Source-bound canonical record capacity/identity mismatch.");
            }
            var prior = counts.GetValueOrDefault(batch.Stream.StreamId);
            counts[batch.Stream.StreamId] = (prior.Frames + batch.Records.Count(r => r.Payload is TrackingPayload), prior.Reports + batch.Records.Count(r => r.Payload is MatchEventPayload),
                prior.Possessions + batch.Records.Count(r => r.Payload is PossessionPayload), prior.Actions + batch.Records.Count(r => r.Payload is FootballActionPayload), prior.Batches + 1);
        }
        var catalog = ledger.Catalog();
        if (catalog.Length != manifest.Datasets.Length || manifest.Datasets.Select(d => d.Stream.StreamId).Distinct().Count() != catalog.Length)
            throw new RecordingValidationException("Incomplete export stream catalog.");
        foreach (var artifact in manifest.Artifacts) await ReadArtifactAsync(directory, artifact, token);
        var readiness = new List<ExportDatasetReadiness>();
        foreach (var expected in manifest.Datasets)
        {
            var actual = catalog.SingleOrDefault(d => d.Stream == expected.Stream); var count = counts.GetValueOrDefault(expected.Stream.StreamId);
            if (actual is null || !actual.Closed || actual.PendingRecords != 0 || actual.HeadSequence != expected.Batches || count.Batches != expected.Batches
                || (count.Frames, count.Reports, count.Possessions) != (expected.Frames, expected.Reports, expected.Possessions)
                || manifest.Version == 2 && count.Actions != expected.Actions) throw new RecordingValidationException("Incomplete export coverage counts/closure.");
            if (manifest.Version == 2 && actual.Manifest.DatasetId == "brentford-existing-illustration")
            {
                IEnumerable<FootballRecord> transported = sourceBoundRecords.TryGetValue(actual.Stream.StreamId, out var records) ? records.Values : [];
                await ValidateBrentfordArchiveAsync(directory, manifest, expected, actual, transported, token);
            }
            readiness.Add(new(actual.Stream, actual.Manifest.ManifestRevision, actual.HeadSequence, actual.Closed, actual.PendingRecords,
                count.Frames, count.Reports, count.Possessions, count.Actions, actual.Manifest.Capabilities, actual.Manifest.Normalization));
        }
        return new(manifest, new(true, false, manifest.Version, ReplayBatchCodec.Hash(bytes), readiness.ToArray(), manifest.Artifacts.Length, manifest.Chunks.Sum(c => (long)c.Bytes)));
    }

    private static async Task ValidateBrentfordArchiveAsync(string directory, FootballExport export, ExportDataset expected, FootballDataset actual,
        IEnumerable<FootballRecord> sourceBoundRecords, CancellationToken token)
    {
        var archived = export.Artifacts.SingleOrDefault(a => a.File == expected.SourceManifestArtifact && a.SourcePath == "brentford/manifest.json")
            ?? throw new RecordingValidationException("Missing Brentford source manifest archive.");
        var bytes = await ReadArtifactAsync(directory, archived, token); var node = JsonNode.Parse(bytes)!.AsObject();
        if (node["exportVersion"]?.GetValue<int>() != 2 || node["fullCoverage"]?.GetValue<bool>() != true || node["frameCount"]?.GetValue<int>() != 58801
            || expected.Frames != 58801 || expected.Reports != 14 || node["cadenceMs"]?.GetValue<int>() != 100 || node["startMs"]?.GetValue<long>() != 0 || node["endMs"]?.GetValue<long>() != 5880000)
            throw new RecordingValidationException("Archived complete-source coverage mismatch.");
        foreach (var source in actual.Manifest.Sources)
            if (!export.Artifacts.Any(a => a.SourcePath == source.Locator && a.Hash == source.ArtifactHash)) throw new RecordingValidationException("Missing pinned source archive: " + source.SourceId);
        foreach (var receipt in node["sourceReceipts"]?.AsArray() ?? [])
            if (!export.Artifacts.Any(a => a.SourcePath == receipt!["sourcePath"]!.GetValue<string>() && a.Hash == receipt["sha256"]!.GetValue<string>()))
                throw new RecordingValidationException("Missing reviewed source receipt archive.");
        node["frames"] = new JsonArray();
        if (node["generatedActions"] is { } actions)
        {
            var action = export.Artifacts.SingleOrDefault(a => a.SourcePath == "generated-actions.json" && a.Hash == actions["sha256"]!.GetValue<string>())
                ?? throw new RecordingValidationException("Missing generated action archive.");
            node["generatedActionData"] = JsonNode.Parse(await ReadArtifactAsync(directory, action, token));
        }
        using var normalizedInput = JsonDocument.Parse(node.ToJsonString());
        var normalized = BrentfordFootballSource.Adapt(normalizedInput.RootElement, true);
        if (normalized.Stream != actual.Stream || JsonSerializer.Serialize(normalized.Manifest, FootballCodec.Json) != JsonSerializer.Serialize(actual.Manifest, FootballCodec.Json)
            || normalized.Records.Count(r => r.Payload is FootballActionPayload) != expected.Actions)
            throw new RecordingValidationException("Archived source manifest/capability/stream binding mismatch.");
        ValidateDerivedNonTrackingRecords(sourceBoundRecords, normalized.Records);
    }

    private static void ValidateDerivedNonTrackingRecords(IEnumerable<FootballRecord> transported, IEnumerable<FootballRecord> derived)
    {
        var expected = derived.Where(r => r.Payload is not TrackingPayload).ToDictionary(r => (r.RecordId, r.Revision), CanonicalRecordHash);
        var actual = transported.Where(r => r.Payload is not TrackingPayload).ToArray();
        if (actual.Length != expected.Count || actual.Select(r => (r.RecordId, r.Revision)).Distinct().Count() != actual.Length
            || actual.Any(r => !expected.TryGetValue((r.RecordId, r.Revision), out var hash) || CanonicalRecordHash(r) != hash))
            throw new RecordingValidationException("Canonical non-tracking record source binding mismatch.");
    }

    private static string CanonicalRecordHash(FootballRecord record) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(record, FootballCodec.Json));

    private sealed record ArchiveInput(string Path, string Locator, string? Hash);
    private static async Task<List<ArchiveInput>> BrentfordArchiveAsync(string manifestPath, string root, CancellationToken token)
    {
        var bytes = await BoundedFile.ReadAsync(manifestPath, 2_000_000, token); using var document = JsonDocument.Parse(bytes); var input = document.RootElement;
        var directory = Path.GetDirectoryName(manifestPath)!; var hashes = input.GetProperty("hashes");
        const string official = "data/official-match-records/brentford-chelsea-2026-09-18.json", config = "data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json", model = "prototypes/first-experience/pl-reconstructed-model.js";
        var result = new List<ArchiveInput> { new(manifestPath, "brentford/manifest.json", ReplayBatchCodec.Hash(bytes)),
            new(Path.Combine(directory, "official-record.json"), official, hashes.GetProperty(official).GetString()), new(Path.Combine(directory, "authored-config.json"), config, hashes.GetProperty(config).GetString()),
            new(Path.Combine(root, model), model, hashes.GetProperty(model).GetString()) };
        if (input.TryGetProperty("enrichment", out var enrichment))
        {
            var path = $"data/match-enrichment/brentford-chelsea-2026-09-18-event-context-v{enrichment.GetProperty("version").GetInt32()}.json";
            result.Add(new(Path.Combine(directory, "event-enrichment.json"), path, hashes.GetProperty(path).GetString()));
            var index = 0;
            foreach (var receipt in input.GetProperty("sourceReceipts").EnumerateArray())
            {
                var file = $"source-input-{index++:D3}.json";
                if (index > 4 || receipt.GetProperty("file").GetString() != file) throw new RecordingValidationException("Invalid reviewed source archive receipt.");
                result.Add(new(Path.Combine(directory, file), receipt.GetProperty("sourcePath").GetString()!, receipt.GetProperty("sha256").GetString()));
            }
        }
        if (input.TryGetProperty("sequences", out var sequences))
        {
            var path = $"data/match-enrichment/brentford-chelsea-2026-09-18-sequences-v{sequences.GetProperty("version").GetInt32()}.json";
            result.Add(new(Path.Combine(directory, "event-sequences.json"), path, hashes.GetProperty(path).GetString()));
        }
        if (input.TryGetProperty("generatedActions", out var actions)) result.Add(new(Path.Combine(directory, "generated-actions.json"), "generated-actions.json", actions.GetProperty("sha256").GetString()));
        return result;
    }

    public static async Task<byte[]> ReadChunkAsync(string directory, ExportChunk chunk, CancellationToken token = default)
    {
        if (chunk.Bytes is < 1 or > FootballCodec.MaxBatchBytes) throw new RecordingValidationException("Export transport cap exceeded.");
        var bytes = await BoundedFile.ReadAsync(SafePath(directory, chunk.File), FootballCodec.MaxBatchBytes, token);
        if (bytes.Length != chunk.Bytes || ReplayBatchCodec.Hash(bytes) != chunk.Hash) throw new RecordingValidationException("Export chunk digest mismatch."); return bytes;
    }
    public static async Task<byte[]> ReadArtifactAsync(string directory, ExportArtifact artifact, CancellationToken token = default)
    {
        var bytes = await BoundedFile.ReadAsync(SafePath(directory, artifact.File), 4_000_000, token);
        if (bytes.Length != artifact.Bytes || ReplayBatchCodec.Hash(bytes) != artifact.Hash) throw new RecordingValidationException("Source archive digest mismatch."); return bytes;
    }
    private static bool Within(string directory, string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(directory), PathComparison)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string SafePath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new RecordingValidationException("Export path must be relative.");
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!Within(directory, path) || path == Path.GetFullPath(directory)) throw new RecordingValidationException("Export path escapes root."); return path;
    }
    private static async Task ImmutableAsync(string path, byte[] bytes, CancellationToken token)
    {
        if (File.Exists(path)) { if (!(await File.ReadAllBytesAsync(path, token)).SequenceEqual(bytes)) throw new RecordingValidationException("Export conflict; use a fresh directory."); return; }
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true);
    }
}
