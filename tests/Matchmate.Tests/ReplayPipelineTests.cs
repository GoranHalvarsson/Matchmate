using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;

namespace Matchmate.Tests;

public sealed class ReplayPipelineTests
{
    [Fact]
    public async Task RestartAndOutOfOrderBatchesReconstructOnlyCompleteValidatedRecording()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog(); var source = Source();
        var batches = ReplayBatchCodec.Create("unit-replay", source, 1);
        await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batches[0], DateTimeOffset.UtcNow, default);
        Assert.Empty(await catalog.ListAsync(default));
        // Completion can arrive early and a fresh processor has no in-memory batch ownership.
        await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batches[^1], DateTimeOffset.UtcNow, default);
        Assert.Empty(await catalog.ListAsync(default));
        foreach (var batch in batches.Skip(1).SkipLast(1).Reverse()) await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batch, DateTimeOffset.UtcNow, default);
        var store = new BlobReplayDatasetStore(blobs, catalog); var descriptor = Assert.Single(await store.CatalogAsync(default));
        var snapshot = await store.SnapshotAsync("unit-replay", descriptor.Metadata.Version, default);
        Assert.NotNull(snapshot); Assert.Equal(130, snapshot.Frames.Length); Assert.Equal(5, snapshot.Metadata.EmptyFrames);
        Assert.Equal("synthetic", snapshot.Metadata.Origin);
        Assert.Equal("estimated", ReplayQuery.At(snapshot, .1).Players[1].Position.Quality);
        Assert.True(ReplayQuery.At(snapshot, 3.1).FullGap);
        Assert.Equal(source.Document.Frames.Select(frame => frame.FrameId), snapshot.Frames.Select(frame => frame.FrameId));
    }

    [Fact]
    public async Task DuplicateImportsKeepPointerVersionStatisticsAndOriginalIngestTimeStable()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog(); var source = Source();
        var firstTime = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        foreach (var batch in ReplayBatchCodec.Create("unit-replay", source, 1)) await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batch, firstTime, default);
        var first = await catalog.ReadAsync("unit-replay", default);
        foreach (var batch in ReplayBatchCodec.Create("unit-replay", source, 1)) await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batch, firstTime.AddHours(1), default);
        var second = await catalog.ReadAsync("unit-replay", default);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\"frames\"", JsonSerializer.Serialize(second!.Value, ReplayBatchCodec.Json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialCorruptAndStalePublicationsNeverReplaceGoodCatalogData()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        await Publish(blobs, catalog, Source(), 2);
        var good = await catalog.ReadAsync("unit-replay", default);
        var partial = ReplayBatchCodec.Create("unit-replay", Source("different"), 3);
        await new DurableReplayProcessor(blobs, catalog).ProcessAsync(partial[0], DateTimeOffset.UtcNow, default);
        Assert.Equal(good, await catalog.ReadAsync("unit-replay", default));
        var corrupt = partial[1] with { ContentHash = new string('0', 64) };
        await Assert.ThrowsAsync<RecordingValidationException>(() => new DurableReplayProcessor(blobs, catalog).ProcessAsync(corrupt, DateTimeOffset.UtcNow, default));
        await Publish(blobs, catalog, Source("older"), 1);
        Assert.Equal(good, await catalog.ReadAsync("unit-replay", default));
        await Publish(blobs, catalog, Source("different"), 3);
        Assert.Equal(3, (await catalog.ReadAsync("unit-replay", default))!.Value.PublicationRevision);
    }

    [Fact]
    public async Task ContentConflictsAndEqualRevisionReplacementAreRejected()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog(); var source = Source();
        var original = ReplayBatchCodec.Create("unit-replay", source, 1);
        await new DurableReplayProcessor(blobs, catalog).ProcessAsync(original[0], DateTimeOffset.UtcNow, default);
        var changed = source with { Document = source.Document with { Label = "Changed within same immutable source version" } };
        var conflict = ReplayBatchCodec.Create("unit-replay", changed, 1);
        await Assert.ThrowsAsync<RecordingValidationException>(() => new DurableReplayProcessor(blobs, catalog).ProcessAsync(conflict[0], DateTimeOffset.UtcNow, default));
        await Publish(blobs, catalog, source, 1);
        await Assert.ThrowsAsync<RecordingValidationException>(() => Publish(blobs, catalog, Source("replacement"), 1));
    }

    [Fact]
    public async Task IdenticalHigherRevisionAdvancesFenceAndCannotThenBeReusedForDifferentData()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        await Publish(blobs, catalog, Source(), 1);
        await Publish(blobs, catalog, Source(), 2);
        var pointer = (await catalog.ReadAsync("unit-replay", default))!.Value;
        Assert.Equal(2, pointer.PublicationRevision); Assert.Equal(2, pointer.Descriptor.PublicationRevision);
        await Assert.ThrowsAsync<RecordingValidationException>(() => Publish(blobs, catalog, Source("different"), 2));
    }

    [Fact]
    public async Task CatalogCompareExchangeRetriesAndCannotOverwriteConcurrentHigherRevision()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog { RejectNextSwap = true };
        await Publish(blobs, catalog, Source(), 1);
        Assert.Equal(2, catalog.SwapAttempts);
        var higher = (await catalog.ReadAsync("unit-replay", default))!.Value with { PublicationRevision = 5 };
        catalog.BeforeNextSwap = () => catalog.Force(higher);
        await Publish(blobs, catalog, Source("candidate"), 2);
        Assert.Equal(5, (await catalog.ReadAsync("unit-replay", default))!.Value.PublicationRevision);
    }

    [Fact]
    public async Task BlobCacheIsPerDatasetVersionAndIntegrityFailureIsNotCached()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        await Publish(blobs, catalog, Source(), 1);
        await Publish(blobs, catalog, Source("other"), 1, "unit-other");
        var store = new BlobReplayDatasetStore(blobs, catalog);
        var first = await store.SnapshotAsync("unit-replay", null, default); var second = await store.SnapshotAsync("unit-other", null, default);
        Assert.NotSame(first, second); Assert.NotEqual(first!.Metadata.Version, second!.Metadata.Version);
        await Assert.ThrowsAsync<ReplayVersionChangedException>(() => store.SnapshotAsync("unit-replay", second.Metadata.Version, default));
        var pointer = (await catalog.ReadAsync("unit-replay", default))!.Value;
        blobs.Content[pointer.SnapshotBlob][0] ^= 0xff;
        await Assert.ThrowsAsync<InvalidDataException>(() => new BlobReplayDatasetStore(blobs, catalog).SnapshotAsync("unit-replay", null, default));
    }

    [Fact]
    public async Task FailedPartitionNeverCheckpointsOrReturnsBeforeCancellation()
    {
        using var cancellation = new CancellationTokenSource(); var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var checkpoints = 0;
        var held = PartitionCheckpoint.ProcessAsync(_ => throw new RecordingValidationException("bad envelope"), _ => { checkpoints++; return Task.CompletedTask; },
            _ => failed.TrySetResult(), cancellation.Token, TimeSpan.FromMilliseconds(1));
        await failed.Task; Assert.False(held.IsCompleted); Assert.Equal(0, checkpoints);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held); Assert.Equal(0, checkpoints);
    }

    [Fact]
    public async Task TransientFailureRetriesSameWorkAndCheckpointsOnlyAfterSuccess()
    {
        var operations = new List<string>(); var attempts = 0;
        await PartitionCheckpoint.ProcessAsync(_ =>
        {
            operations.Add("process" + ++attempts); if (attempts < 3) throw new IOException("transient"); return Task.CompletedTask;
        }, _ => { operations.Add("checkpoint"); return Task.CompletedTask; }, _ => { }, default, TimeSpan.Zero);
        Assert.Equal(new[] { "process1", "process2", "process3", "checkpoint" }, operations);
    }

    [Fact]
    public void EnvelopeHashIsDeterministicAndRejectsMalformedIdentityAndOversizedInput()
    {
        var one = ReplayBatchCodec.Create("unit-replay", Source(), 1); var two = ReplayBatchCodec.Create("unit-replay", Source(), 1);
        Assert.Equal(ReplayBatchCodec.Encode(one[0]), ReplayBatchCodec.Encode(two[0]));
        Assert.Throws<RecordingValidationException>(() => ReplayBatchCodec.Create("unit-replay\n", Source(), 1));
        Assert.Throws<RecordingValidationException>(() => ReplayBatchCodec.Decode(new byte[ReplayBatchCodec.MaxEnvelopeBytes + 1]));
        Assert.Throws<RecordingValidationException>(() => ReplayBatchCodec.Decode("null"u8.ToArray()));
    }

    internal static async Task Publish(MemoryReplayBlobs blobs, MemoryReplayCatalog catalog, SourceRecording source, long revision, string datasetId = "unit-replay")
    {
        foreach (var batch in ReplayBatchCodec.Create(datasetId, source, revision)) await new DurableReplayProcessor(blobs, catalog).ProcessAsync(batch, DateTimeOffset.UtcNow, default);
    }
    internal static SourceRecording Source(string label = "fixture")
    {
        var frames = Enumerable.Range(0, 130).Select(index => new RecordedFrame(index, 1, "source-" + index, Math.Round(index * .1, 8),
            index is >= 30 and < 35 ? [] : [new(1, 1.5, -2.5, true, null), new(2, -3.5, 4.5, false, null)],
            new Position(null, null, null, "unknown-ball"), index is >= 30 and < 35 ? "empty-positions" : "positions-present")).ToArray();
        var document = new SourceDocument(1, "unit-fixture", "unit-pin", 42, label, "retrospective", "Unit fixture", "2026-01-01", new(1, "Home"), new(2, "Away"),
            new(105, 68, "metres", "centre"), 1, 0, 12.9, .1, [new(1, 1, 1, "One", "unit", "unit"), new(2, 2, 2, "Two", "unit", "unit")], frames, "deferred", "Synthetic test fixture only.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ReplayBatchCodec.Json);
        return new(document, ReplayBatchCodec.Hash(bytes), "synthetic", "Unit fixture licence", "https://example.invalid/unit", bytes.Length, ReplayBatchCodec.Compress(bytes).Length);
    }
}

internal sealed class MemoryReplayBlobs : IReplayBlobStore
{
    internal readonly Dictionary<string, byte[]> Content = [];
    public Task PutImmutableAsync(string path, byte[] bytes, CancellationToken token)
    {
        if (Content.TryGetValue(path, out var old) && !old.SequenceEqual(bytes)) throw new RecordingValidationException("Immutable conflict.");
        Content[path] = bytes.ToArray(); return Task.CompletedTask;
    }
    public Task<byte[]?> ReadAsync(string path, int cap, CancellationToken token)
    {
        var bytes = Content.GetValueOrDefault(path); if (bytes?.Length > cap) throw new RecordingValidationException("Cap exceeded.");
        return Task.FromResult(bytes?.ToArray());
    }
}
internal sealed class MemoryReplayCatalog : IReplayCatalog
{
    private readonly Dictionary<string, VersionedReplayPointer> _items = [];
    private int _version;
    internal bool RejectNextSwap;
    internal int SwapAttempts;
    internal Action? BeforeNextSwap;
    public Task<VersionedReplayPointer?> ReadAsync(string datasetId, CancellationToken token) => Task.FromResult(_items.GetValueOrDefault(datasetId));
    public Task<IReadOnlyList<ReplayPointer>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ReplayPointer>>(_items.Values.Select(item => item.Value).ToArray());
    public Task<bool> CompareExchangeAsync(ReplayPointer candidate, string? expectedETag, CancellationToken token)
    {
        SwapAttempts++; var hook = BeforeNextSwap; BeforeNextSwap = null; hook?.Invoke();
        if (RejectNextSwap) { RejectNextSwap = false; return Task.FromResult(false); }
        if (_items.GetValueOrDefault(candidate.DatasetId)?.ETag != expectedETag) return Task.FromResult(false);
        Force(candidate); return Task.FromResult(true);
    }
    internal void Force(ReplayPointer candidate) => _items[candidate.DatasetId] = new(candidate, (++_version).ToString());
}
