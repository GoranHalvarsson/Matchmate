using System.IO.Compression;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public sealed class LocalReplayDatasetStore(IReplayStore store) : IReplayDatasetStore
{
    public async Task<IReadOnlyList<ReplayDataset>> CatalogAsync(CancellationToken token)
    {
        var snapshot = await store.ReadAsync(token);
        return snapshot is null ? [] : [Describe(snapshot)];
    }
    public async Task<ReplayDataset?> DescriptorAsync(string datasetId, CancellationToken token) =>
        datasetId == DatasetIdentity.Australian && await store.ReadAsync(token) is { } snapshot ? Describe(snapshot) : null;
    public async Task<ReplaySnapshot?> SnapshotAsync(string datasetId, string? version, CancellationToken token)
    {
        if (datasetId != DatasetIdentity.Australian) return null;
        var snapshot = await store.ReadAsync(token);
        if (snapshot is not null && version is not null && snapshot.Metadata.Version != version) throw new ReplayVersionChangedException();
        return snapshot;
    }
    private static ReplayDataset Describe(ReplaySnapshot snapshot) => new(DatasetIdentity.Australian, 1, snapshot.Metadata, DateTimeOffset.UnixEpoch);
}
public sealed class EmptyReplayDatasetStore : IReplayDatasetStore
{
    public Task<IReadOnlyList<ReplayDataset>> CatalogAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ReplayDataset>>([]);
    public Task<ReplayDataset?> DescriptorAsync(string datasetId, CancellationToken token) => Task.FromResult<ReplayDataset?>(null);
    public Task<ReplaySnapshot?> SnapshotAsync(string datasetId, string? version, CancellationToken token) => Task.FromResult<ReplaySnapshot?>(null);
}

public sealed class BlobReplayDatasetStore(IReplayBlobStore blobs, IReplayCatalog catalog) : IReplayDatasetStore
{
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, ReplaySnapshot> _cache = [];
    private readonly Queue<string> _cacheOrder = [];
    public async Task<IReadOnlyList<ReplayDataset>> CatalogAsync(CancellationToken token) =>
        (await catalog.ListAsync(token)).Select(ValidatePointer).OrderBy(pointer => pointer.DatasetId, StringComparer.Ordinal).Select(pointer => pointer.Descriptor).ToArray();
    public async Task<ReplayDataset?> DescriptorAsync(string datasetId, CancellationToken token)
    {
        var pointer = await catalog.ReadAsync(datasetId, token);
        if (pointer is not null && pointer.Value.DatasetId != datasetId) throw new RecordingValidationException("Dataset identity mismatch.");
        return pointer is null ? null : ValidatePointer(pointer.Value).Descriptor;
    }
    public async Task<ReplaySnapshot?> SnapshotAsync(string datasetId, string? version, CancellationToken token)
    {
        var current = await catalog.ReadAsync(datasetId, token);
        if (current is null) return null;
        var pointer = ValidatePointer(current.Value);
        if (pointer.DatasetId != datasetId) throw new RecordingValidationException("Dataset identity mismatch.");
        if (version is not null && pointer.Version != version) throw new ReplayVersionChangedException();
        var cacheKey = $"{datasetId}:{pointer.Version}:{pointer.SnapshotHash}";
        await _cacheGate.WaitAsync(token);
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
            var bytes = await blobs.ReadAsync(pointer.SnapshotBlob, ReplayBatchCodec.MaxSnapshotBytes, token)
                ?? throw new RecordingValidationException("Published snapshot blob is missing.");
            if (bytes.Length != pointer.CompressedBytes) throw new RecordingValidationException("Snapshot compressed-size mismatch.");
            using var stream = new MemoryStream(bytes);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            var raw = await ReplayBatchCodec.ReadBoundedAsync(gzip, ReplayBatchCodec.MaxSnapshotBytes, token);
            if (raw.Length != pointer.UncompressedBytes || ReplayBatchCodec.Hash(raw) != pointer.SnapshotHash)
                throw new RecordingValidationException("Published snapshot integrity mismatch.");
            var snapshot = JsonSerializer.Deserialize<ReplaySnapshot>(raw, ReplayBatchCodec.Json)
                ?? throw new RecordingValidationException("Missing snapshot.");
            AtomicReplayStore.Validate(snapshot);
            if (snapshot.Metadata.Version != pointer.Version || !JsonSerializer.SerializeToUtf8Bytes(snapshot.Metadata, ReplayBatchCodec.Json)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(pointer.Descriptor.Metadata, ReplayBatchCodec.Json)))
                throw new RecordingValidationException("Catalog/snapshot metadata mismatch.");
            while (_cache.Count >= 2) _cache.Remove(_cacheOrder.Dequeue());
            _cache.Add(cacheKey, snapshot); _cacheOrder.Enqueue(cacheKey);
            return snapshot;
        }
        finally { _cacheGate.Release(); }
    }
    private static ReplayPointer ValidatePointer(ReplayPointer pointer)
    {
        if (pointer is null || !DatasetIdentity.IsValid(pointer.DatasetId) || pointer.PublicationRevision <= 0
            || pointer.Descriptor?.Metadata is null || pointer.Descriptor.DatasetId != pointer.DatasetId
            || pointer.Descriptor.PublicationRevision != pointer.PublicationRevision || pointer.Descriptor.Metadata.Version != pointer.Version
            || pointer.Descriptor.Metadata.SourceHash is not { Length: 64 } || !pointer.Descriptor.Metadata.SourceHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || pointer.Version != $"local-replay-v1-{pointer.Descriptor.Metadata.SourceHash}"
            || pointer.SnapshotHash is not { Length: 64 } || !pointer.SnapshotHash.All(Uri.IsHexDigit)
            || pointer.UncompressedBytes is < 1 or > ReplayBatchCodec.MaxSnapshotBytes
            || pointer.CompressedBytes is < 1 or > ReplayBatchCodec.MaxSnapshotBytes
            || pointer.SnapshotBlob != $"datasets/{pointer.DatasetId}/{pointer.Version}/r{pointer.PublicationRevision}/snapshot-{pointer.SnapshotHash}.json.gz")
            throw new RecordingValidationException("Invalid catalog pointer.");
        return pointer;
    }
}
