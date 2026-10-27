using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;

namespace Matchmate.Core;

public interface IReplayDatasetStore
{
    Task<IReadOnlyList<ReplayDataset>> CatalogAsync(CancellationToken token);
    Task<ReplayDataset?> DescriptorAsync(string datasetId, CancellationToken token);
    Task<ReplaySnapshot?> SnapshotAsync(string datasetId, string? version, CancellationToken token);
}
public sealed class ReplayVersionChangedException() : Exception("The dataset version changed; reload its metadata.");
public static partial class DatasetIdentity
{
    public const string Australian = "skillcorner-1874553-60-240";
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,79}\\z", RegexOptions.CultureInvariant)] private static partial Regex Pattern();
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}

public sealed record ReplayBatch(int ContractVersion, string DatasetId, string Version, long PublicationRevision,
    string Kind, int BatchIndex, int BatchCount, int FrameCount, SourceRecording Header, RecordedFrame[] Frames, string ContentHash);

public static class ReplayBatchCodec
{
    public const int FramesPerBatch = 64, MaxEnvelopeBytes = 512_000, MaxSnapshotBytes = 20_000_000;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Prefix(ReplayBatch batch) => $"datasets/{batch.DatasetId}/{batch.Version}/r{batch.PublicationRevision}";
    public static string BatchPath(ReplayBatch batch, int index) => $"{Prefix(batch)}/batches/{index:D5}.json";
    public static string CompletionPath(ReplayBatch batch) => $"{Prefix(batch)}/complete.json";

    public static IReadOnlyList<ReplayBatch> Create(string datasetId, SourceRecording recording, long revision)
    {
        var snapshot = new ReplayProcessor().Process(recording);
        var header = recording with { Document = recording.Document with { Frames = [] } };
        var count = (snapshot.Frames.Length + FramesPerBatch - 1) / FramesPerBatch;
        var result = new List<ReplayBatch>();
        for (var index = 0; index < count; index++) result.Add(Sign(new(1, datasetId, snapshot.Metadata.Version, revision,
            "frames", index, count, snapshot.Frames.Length, header, snapshot.Frames.Skip(index * FramesPerBatch).Take(FramesPerBatch).ToArray(), "")));
        result.Add(Sign(new(1, datasetId, snapshot.Metadata.Version, revision, "complete", count, count, snapshot.Frames.Length, header, [], "")));
        foreach (var batch in result) Validate(batch);
        return result;
    }
    private static ReplayBatch Sign(ReplayBatch batch) => batch with { ContentHash = Hash(JsonSerializer.SerializeToUtf8Bytes(batch with { ContentHash = "" }, Json)) };
    public static byte[] Encode(ReplayBatch batch)
    {
        Validate(batch);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Json);
        if (bytes.Length > MaxEnvelopeBytes) throw new RecordingValidationException("Batch exceeds transport cap.");
        return bytes;
    }
    public static ReplayBatch Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxEnvelopeBytes) throw new RecordingValidationException("Invalid transport size.");
        try
        {
            var batch = JsonSerializer.Deserialize<ReplayBatch>(bytes.Span, Json) ?? throw new RecordingValidationException("Missing batch.");
            Validate(batch); return batch;
        }
        catch (JsonException) { throw new RecordingValidationException("Malformed transport envelope."); }
    }
    public static void Validate(ReplayBatch batch)
    {
        if (batch is null || batch.ContractVersion != 1 || !DatasetIdentity.IsValid(batch.DatasetId)
            || batch.PublicationRevision <= 0 || batch.Header?.Document is null || batch.Header.Document.Frames is not { Length: 0 }
            || batch.FrameCount is < 1 or > 50_001 || batch.BatchCount != (batch.FrameCount + FramesPerBatch - 1) / FramesPerBatch
            || batch.Header.Hash is not { Length: 64 } || !batch.Header.Hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || batch.Version != $"local-replay-v1-{batch.Header.Hash}" || batch.Frames is null
            || batch.Header.Origin is not ("real" or "synthetic") || string.IsNullOrWhiteSpace(batch.Header.License)
            || batch.ContentHash is not { Length: 64 } || Sign(batch).ContentHash != batch.ContentHash)
            throw new RecordingValidationException("Invalid batch identity, header, revision or integrity.");
        if (batch.Kind == "complete")
        {
            if (batch.BatchIndex != batch.BatchCount || batch.Frames.Length != 0) throw new RecordingValidationException("Invalid completion marker.");
        }
        else if (batch.Kind == "frames" && batch.BatchIndex >= 0 && batch.BatchIndex < batch.BatchCount
            && batch.Frames.Length == Math.Min(FramesPerBatch, batch.FrameCount - batch.BatchIndex * FramesPerBatch))
            ReplayProcessor.ValidateSource(batch.Header.Document with { Frames = batch.Frames });
        else throw new RecordingValidationException("Invalid batch index/count/kind.");
        if (JsonSerializer.SerializeToUtf8Bytes(batch, Json).Length > MaxEnvelopeBytes) throw new RecordingValidationException("Oversized batch.");
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream input, int cap, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > cap) throw new RecordingValidationException("Stored replay exceeds cap.");
            await result.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return result.ToArray();
    }
    public static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes);
        return output.ToArray();
    }
}

public interface IReplayBlobStore
{
    // Existing different content is a validation failure, never an overwrite.
    Task PutImmutableAsync(string path, byte[] bytes, CancellationToken token);
    Task<byte[]?> ReadAsync(string path, int cap, CancellationToken token);
}
public sealed record ReplayPointer(string DatasetId, string Version, long PublicationRevision, string SnapshotBlob,
    string SnapshotHash, int UncompressedBytes, int CompressedBytes, ReplayDataset Descriptor)
{ public string Id => DatasetId; }
public sealed record VersionedReplayPointer(ReplayPointer Value, string ETag);
public interface IReplayCatalog
{
    Task<VersionedReplayPointer?> ReadAsync(string datasetId, CancellationToken token);
    Task<IReadOnlyList<ReplayPointer>> ListAsync(CancellationToken token);
    Task<bool> CompareExchangeAsync(ReplayPointer candidate, string? expectedETag, CancellationToken token);
}

// No in-memory batch ownership: a new processor can reconstruct from durable batch blobs after any checkpoint.
public sealed class DurableReplayProcessor(IReplayBlobStore blobs, IReplayCatalog catalog)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string> ProcessAsync(ReplayBatch batch, DateTimeOffset ingestedAtUtc, CancellationToken token)
    {
        ReplayBatchCodec.Validate(batch);
        await _gate.WaitAsync(token);
        try
        {
            await blobs.PutImmutableAsync(batch.Kind == "complete" ? ReplayBatchCodec.CompletionPath(batch) : ReplayBatchCodec.BatchPath(batch, batch.BatchIndex), ReplayBatchCodec.Encode(batch), token);
            var current = await catalog.ReadAsync(batch.DatasetId, token);
            if (current is not null && current.Value.PublicationRevision > batch.PublicationRevision) return "stale";
            var completionBytes = await blobs.ReadAsync(ReplayBatchCodec.CompletionPath(batch), ReplayBatchCodec.MaxEnvelopeBytes, token);
            if (completionBytes is null) return "durable-partial";
            var completion = ReplayBatchCodec.Decode(completionBytes);
            var frames = new List<RecordedFrame>(batch.FrameCount);
            var headerHash = ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(batch.Header, ReplayBatchCodec.Json));
            for (var index = 0; index < batch.BatchCount; index++)
            {
                var bytes = await blobs.ReadAsync(ReplayBatchCodec.BatchPath(batch, index), ReplayBatchCodec.MaxEnvelopeBytes, token);
                if (bytes is null) return "durable-partial";
                var part = ReplayBatchCodec.Decode(bytes);
                if (part.Kind != "frames" || part.BatchIndex != index || part.BatchCount != batch.BatchCount || part.FrameCount != batch.FrameCount
                    || part.DatasetId != batch.DatasetId || part.Version != batch.Version || part.PublicationRevision != batch.PublicationRevision
                    || ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(part.Header, ReplayBatchCodec.Json)) != headerHash)
                    throw new RecordingValidationException("Conflicting durable publication batches.");
                frames.AddRange(part.Frames);
            }
            if (completion.Kind != "complete" || completion.DatasetId != batch.DatasetId || completion.Version != batch.Version
                || completion.PublicationRevision != batch.PublicationRevision || completion.BatchCount != batch.BatchCount
                || completion.FrameCount != batch.FrameCount
                || ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(completion.Header, ReplayBatchCodec.Json)) != headerHash)
                throw new RecordingValidationException("Conflicting completion marker.");
            if (frames.Count != batch.FrameCount) throw new RecordingValidationException("Incomplete recording frame count.");
            var accumulator = new RecordingAccumulator(batch.Header);
            foreach (var frame in frames) accumulator.Accept(frame);
            var snapshot = accumulator.Complete();
            var raw = JsonSerializer.SerializeToUtf8Bytes(snapshot, ReplayBatchCodec.Json);
            if (raw.Length > ReplayBatchCodec.MaxSnapshotBytes) throw new RecordingValidationException("Snapshot exceeds cap.");
            var compressed = ReplayBatchCodec.Compress(raw); var hash = ReplayBatchCodec.Hash(raw);
            var path = $"{ReplayBatchCodec.Prefix(batch)}/snapshot-{hash}.json.gz";
            await blobs.PutImmutableAsync(path, compressed, token);
            var descriptor = new ReplayDataset(batch.DatasetId, batch.PublicationRevision, snapshot.Metadata, ingestedAtUtc);
            var candidate = new ReplayPointer(batch.DatasetId, batch.Version, batch.PublicationRevision, path, hash, raw.Length, compressed.Length, descriptor);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                current = await catalog.ReadAsync(batch.DatasetId, token);
                if (current is not null)
                {
                    if (current.Value.PublicationRevision > candidate.PublicationRevision) return "stale";
                    if (current.Value.PublicationRevision == candidate.PublicationRevision)
                    {
                        if (current.Value.Version == candidate.Version && current.Value.SnapshotHash == hash) return "duplicate";
                        throw new RecordingValidationException("A publication revision cannot replace different data.");
                    }
                }
                if (await catalog.CompareExchangeAsync(candidate, current?.ETag, token)) return "published";
            }
            throw new IOException("Replay catalog contention; do not checkpoint until retried.");
        }
        finally { _gate.Release(); }
    }
}
