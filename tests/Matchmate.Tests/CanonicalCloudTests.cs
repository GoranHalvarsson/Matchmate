using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
namespace Matchmate.Tests;

public sealed class CanonicalCloudTests
{
    [Fact]
    public async Task MetadataCapacityChecksRejectOversizedCountsBeforeArithmeticOrPersistence()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes();
        await new AzureFootballStore(blobs, indexes).AppendAsync(Batch(fixture, 1, Context(fixture)), default);
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!;
        Assert.Throws<RecordingValidationException>(() => AzureFootballStore.ValidateIndex(root with { Value = root.Value with { PayloadBytes = AzureFootballStore.MaxDatasetBytes + 1 } }));
        Assert.Throws<RecordingValidationException>(() => AzureFootballStore.ValidateIndex(root with { Value = root.Value with { Streams = [root.Value.Streams[0] with { Head = long.MaxValue }] } }));
        Assert.Throws<RecordingValidationException>(() => AzureFootballStore.ValidateIndex(root with { Value = root.Value with { Pages = [root.Value.Pages[0] with { Count = int.MaxValue }] } }));
    }
    [Fact]
    public async Task PagedJournalFirstBatchRestartAndLazyReadsAreBounded()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes);
        var first = Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray());
        Assert.True(await store.AppendAsync(first, default)); Assert.False(Assert.Single(await store.CatalogAsync(default)).Closed);
        Assert.NotNull((await store.QueryAsync(Query(fixture, 0), default)).Frame);
        for (var sequence = 2; sequence <= 70; sequence++) Assert.True(await store.AppendAsync(Batch(fixture, sequence, [Frame(fixture, (sequence - 1) * 100)], close: sequence == 70), default));
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value;
        Assert.Equal(3, root.Pages.Length); Assert.Equal(70, root.CommitCount); Assert.All(root.Pages, p => Assert.InRange(p.Bytes, 1, AzureFootballStore.MaxPageBytes));
        var restart = new AzureFootballStore(blobs, indexes); var dataset = Assert.Single(await restart.CatalogAsync(default));
        Assert.True(dataset.Closed); Assert.Equal(0, restart.PayloadReadCount); Assert.Equal(0, restart.RetainedTrackingCount);
        Assert.Equal(70, dataset.HeadSequence); var view = await restart.QueryAsync(Query(fixture, 6900), default);
        Assert.Equal("6900", view.Frame!.SourceFrameId); Assert.Equal(1, restart.PayloadReadCount); Assert.InRange(restart.PayloadReadBytes, 1, AzureFootballStore.MaxArchivedBatchBytes);
        var metadataBytes = restart.MetadataReadBytes;
        for (var i = 0; i < 30; i++) await restart.QueryAsync(Query(fixture, 6900), default);
        Assert.Equal(1, restart.PayloadReadCount); Assert.Equal(metadataBytes, restart.MetadataReadBytes);
        Assert.False(await restart.AppendAsync(first, default)); Assert.Equal(70, Assert.Single(await restart.CatalogAsync(default)).HeadSequence);
        await Assert.ThrowsAsync<RecordingValidationException>(() => restart.AppendAsync(Batch(fixture, 1, Context(fixture)), default));
    }
    [Fact]
    public async Task CrashBeforeFencePreservesFirstReceiptAndOrphansStayUnqueryable()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes { FailNextSwap = true };
        var first = Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray());
        var time = At("12:00"); var failed = new AzureFootballStore(blobs, indexes, () => time);
        await Assert.ThrowsAsync<IOException>(() => failed.AppendAsync(first, default));
        Assert.Empty(await new AzureFootballStore(blobs, indexes).CatalogAsync(default)); Assert.NotEmpty(blobs.Content);
        var retry = new AzureFootballStore(blobs, indexes, () => time.AddHours(1)); Assert.True(await retry.AppendAsync(first, default));
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value; var page = DecodePage(blobs, root.Pages[0]);
        Assert.Equal(time, Assert.Single(page.Entries).Metadata.ReceivedAtUtc); Assert.Equal(time, page.Entries[0].Payload.ReceivedAtUtc);
        Assert.False(await new AzureFootballStore(blobs, indexes).AppendAsync(first, default));
    }
    [Fact]
    public async Task DatasetCASRechecksCanonicalConflictsAcrossStreams()
    {
        var fixture = Fixture(); var other = fixture with { Stream = fixture.Stream with { StreamId = "replay-other" } };
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes();
        var goal = Goal(fixture); var conflict = goal with { Payload = ((MatchEventPayload)goal.Payload) with { TeamId = "team-away" } };
        indexes.BeforeNextSwap = async () => await new AzureFootballStore(blobs, indexes).AppendAsync(Batch(other, 1, Context(other).Append(conflict).ToArray()), default);
        await Assert.ThrowsAsync<RecordingValidationException>(() => new AzureFootballStore(blobs, indexes).AppendAsync(Batch(fixture, 1, Context(fixture).Append(goal).ToArray()), default));
        var catalog = await new AzureFootballStore(blobs, indexes).CatalogAsync(default); Assert.Equal(other.Stream, Assert.Single(catalog).Stream);
        Assert.True(indexes.SwapAttempts >= 2);
    }
    [Fact]
    public async Task CASRetryWithoutConflictCommitsOneSequenceAndThenCheckpoints()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes { RejectNextSwap = true };
        var operations = new List<string>(); indexes.AfterSwap = () => operations.Add("fence");
        var store = new AzureFootballStore(new ObservedBlobs(blobs, operations), indexes);
        await PartitionCheckpoint.ProcessAsync(async token => await store.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()), token), _ => { operations.Add("checkpoint"); return Task.CompletedTask; }, _ => { }, default, TimeSpan.Zero);
        Assert.True(operations.FindIndex(v => v == "payload") < operations.FindIndex(v => v == "page"));
        Assert.True(operations.FindIndex(v => v == "page") < operations.FindIndex(v => v == "fence")); Assert.Equal("checkpoint", operations[^1]);
        Assert.Equal(1, Assert.Single(await store.CatalogAsync(default)).HeadSequence);
    }
    [Fact]
    public async Task SequenceGapHoldsAndTwoRunsAndVersionsStayIsolated()
    {
        var one = Fixture(); var other = one with { Stream = one.Stream with { StreamId = "other-run" } }; var version = Fixture("version-two", "other-version");
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes);
        await Assert.ThrowsAsync<RecordingValidationException>(() => store.AppendAsync(Batch(one, 2, []), default)); Assert.Empty(blobs.Content);
        await store.AppendAsync(Batch(one, 1, Context(one).Append(Goal(one)).ToArray()), default);
        await store.AppendAsync(Batch(other, 1, Context(other)), default); await store.AppendAsync(Batch(version, 1, Context(version)), default);
        Assert.Single((await store.QueryAsync(Query(one, 80000), default)).Events);
        Assert.Empty((await store.QueryAsync(Query(other, 80000), default)).Events); Assert.Empty((await store.QueryAsync(Query(version, 80000), default)).Events);
        await Assert.ThrowsAsync<FootballQueryException>(() => store.QueryAsync(Query(other, 80000) with { DatasetVersion = "wrong" }, default));
    }
    [Fact]
    public async Task CorruptCommittedPagesAndPayloadsFailClosedAndUnknownBlobsAreIgnored()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes);
        var batch = Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()); await store.AppendAsync(batch, default);
        blobs.Content["uncommitted/evil.json.gz"] = [1, 2, 3]; Assert.Single(await new AzureFootballStore(blobs, indexes).CatalogAsync(default));
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value; var pagePath = root.Pages[0].Path; var original = blobs.Content[pagePath].ToArray();
        blobs.Content[pagePath][10] ^= 255;
        // Viewer reads the independently bound prepared projection; recovery/audit still verifies the journal.
        Assert.Single(await new AzureFootballStore(blobs, indexes).CatalogAsync(default));
        await Assert.ThrowsAsync<RecordingValidationException>(() => new AzureFootballStore(blobs, indexes).ReadAsync(default)); blobs.Content[pagePath] = original;
        var payload = DecodePage(blobs, root.Pages[0]).Entries[0].Payload;
        blobs.Content[payload.BlobPath][10] ^= 255;
        var restart = new AzureFootballStore(blobs, indexes); Assert.Single(await restart.CatalogAsync(default));
        await Assert.ThrowsAsync<RecordingValidationException>(() => restart.QueryAsync(Query(fixture, 0), default));
    }
    [Fact]
    public async Task MetadataTimingCannotDisguiseFutureOriginalPayload()
    {
        var fixture = Fixture(); var future = Frame(fixture, 1000); var batch = Batch(fixture, 1, Context(fixture).Append(future).ToArray());
        var meta = FootballLedger.Metadata(new(batch, At("12:00")), "chunk"); var records = meta.Records.ToArray();
        records[^1] = records[^1] with { Header = records[^1].Header with { Time = future.FootballTime! with { StartMs = 0, EndMs = 0 } } };
        var compact = new FootballLedger(false); compact.AcceptMetadata(meta with { Records = records });
        await Assert.ThrowsAsync<RecordingValidationException>(() => compact.QueryAsync(Query(fixture, 0), (_, _) => Task.FromResult(future), default));
        var wrongManifest = meta.Manifest with { IdentityMappings = [] };
        Assert.Throws<RecordingValidationException>(() => new FootballLedger(false).AcceptMetadata(meta with { Manifest = wrongManifest }));
        var wrongContext = meta.Records.ToArray(); wrongContext[0] = wrongContext[0] with { Header = wrongContext[0].Header with { State = "withdrawn" } };
        Assert.Throws<RecordingValidationException>(() => new FootballLedger(false).AcceptMetadata(meta with { Records = wrongContext }));
    }
    [Fact]
    public async Task LazyRevisionEligibilityRunsBeforeLoadingPositions()
    {
        var fixture = Fixture(); var first = Frame(fixture, 0); var revised = Revise(first, 2, 1000, At("12:05"));
        var ledger = new FootballLedger(false); var records = new Dictionary<string, FootballRecord> { ["first"] = first, ["second"] = revised };
        ledger.AcceptMetadata(FootballLedger.Metadata(new(Batch(fixture, 1, Context(fixture).Append(first).ToArray()), At("12:00")), "first"));
        ledger.AcceptMetadata(FootballLedger.Metadata(new(Batch(fixture, 2, [revised]), At("12:05")), "second"));
        var reads = 0;
        Task<FootballRecord> Resolve(FootballPayloadPointer p, CancellationToken _) { reads++; return Task.FromResult(records[p.Path]); }
        Assert.Null((await ledger.QueryAsync(Query(fixture, 50), Resolve, default)).Frame); Assert.Equal(0, reads);
        Assert.NotNull((await ledger.QueryAsync(Query(fixture, 50) with { ApplicationSequence = 1 }, Resolve, default)).Frame); Assert.Equal(1, reads);
        Assert.NotNull((await ledger.QueryAsync(Query(fixture, 1000), Resolve, default)).Frame); Assert.Equal(2, reads);
    }
    [Fact]
    public void BoundedGzipTransportRejectsBombTruncationAndUnknownVersions()
    {
        var fixture = Fixture(); var batch = Batch(fixture, 1, Context(fixture)); var encoded = FootballTransport.Encode(batch);
        Assert.Equal(batch.Digest, FootballTransport.Decode(encoded.Bytes, encoded.ContentType).Digest);
        Assert.Throws<RecordingValidationException>(() => FootballTransport.Decode(encoded.Bytes, "application/json"));
        var gzip = ReplayBatchCodec.Compress(FootballCodec.Encode(batch)); Assert.Throws<RecordingValidationException>(() => FootballTransport.Decode(gzip[..^5], FootballTransport.GzipType));
        var bomb = ReplayBatchCodec.Compress(new byte[FootballCodec.MaxBatchBytes + 1]); Assert.Throws<RecordingValidationException>(() => FootballTransport.Decode(bomb, FootballTransport.GzipType));
        Assert.Throws<RecordingValidationException>(() => FootballTransport.Decode(new byte[FootballCodec.MaxBatchBytes + 1], FootballTransport.GzipType));
    }
    private static FootballMetadataPage DecodePage(MemoryReplayBlobs blobs, FootballPageReference page) => JsonSerializer.Deserialize<FootballMetadataPage>(FootballTransport.Expand(blobs.Content[page.Path], AzureFootballStore.MaxExpandedPageBytes), FootballCodec.Json)!;
    private sealed class ObservedBlobs(MemoryReplayBlobs inner, List<string> operations) : IReplayBlobStore
    {
        public Task<byte[]?> ReadAsync(string path, int cap, CancellationToken token) => inner.ReadAsync(path, cap, token);
        public Task PutImmutableAsync(string path, byte[] bytes, CancellationToken token) { operations.Add(path.Contains("/metadata/") ? "page" : "payload"); return inner.PutImmutableAsync(path, bytes, token); }
    }
}
internal sealed class MemoryFootballIndexes : IFootballIndexStore
{
    private readonly Dictionary<string, VersionedFootballIndex> _items = []; private int _version;
    public bool FailNextSwap, RejectNextSwap; public int SwapAttempts; public Func<Task>? BeforeNextSwap; public Action? AfterSwap;
    public Task<VersionedFootballIndex?> ReadAsync(string datasetId, CancellationToken token) => Task.FromResult(_items.GetValueOrDefault(datasetId));
    public Task<IReadOnlyList<VersionedFootballIndex>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<VersionedFootballIndex>>(_items.Values.ToArray());
    public async Task<bool> CompareExchangeAsync(FootballDatasetIndex candidate, string? expectedETag, CancellationToken token)
    {
        SwapAttempts++; var hook = BeforeNextSwap; BeforeNextSwap = null; if (hook is not null) await hook();
        if (FailNextSwap) { FailNextSwap = false; throw new IOException("simulated crash before fence"); }
        if (RejectNextSwap) { RejectNextSwap = false; return false; }
        if (_items.GetValueOrDefault(candidate.DatasetId)?.ETag != expectedETag) return false;
        _items[candidate.DatasetId] = new(candidate, (++_version).ToString()); AfterSwap?.Invoke(); return true;
    }
}
