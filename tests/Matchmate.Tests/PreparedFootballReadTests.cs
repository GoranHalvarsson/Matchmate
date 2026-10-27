using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class PreparedFootballReadTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, FootballCodec.Json);
    private static T Decode<T>(MemoryReplayBlobs blobs, FootballReadReference reference) => JsonSerializer.Deserialize<T>(FootballTransport.Expand(blobs.Content[reference.Path], AzureFootballStore.MaxExpandedPageBytes), FootballCodec.Json)!;
    private static FootballReadIndex Index(MemoryReplayBlobs blobs, FootballDatasetIndex root, string stream) => Decode<FootballReadIndex>(blobs,
        Decode<FootballReadCatalog>(blobs, root.DerivedReadView!).Streams.Single(d => d.Dataset.Stream.StreamId == stream).Index);

    [Fact]
    public async Task DirectColdQueryUsesOnlyContextCandidateSeekPagesAndOnePayloadWithoutRecovery()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 59950)).ToArray()), default);
        await writer.AppendAsync(Batch(fixture, 2, [Frame(fixture, 60080)], true), default);
        var reader = new AzureFootballStore(blobs, indexes);
        var view = await reader.QueryAsync(Query(fixture, 60020), default);
        Assert.Equal("59950", view.Frame!.SourceFrameId); Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.RecoveredRecordCount);
        Assert.Equal(5, reader.ReadViewReadCount); Assert.Equal(1, reader.PayloadReadCount); Assert.Equal(0, reader.RetainedTrackingCount);
        Assert.InRange(reader.LastQueryHeaderCount, 1, 5);
        Assert.Equal("60080", (await reader.QueryAsync(Query(fixture, 60080), default)).Frame!.SourceFrameId);
        Assert.True((await reader.QueryAsync(Query(fixture, 60200), default)).TrackingGap);
        var count = reader.ReadViewReadCount; await reader.QueryAsync(Query(fixture, 60080), default); Assert.Equal(count, reader.ReadViewReadCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompleteRevisionGroupsPreventResurrectionAcrossTimeBucketsAndRespectKnowledgeAndApplication(bool withdrawn)
    {
        var fixture = Fixture(); var original = Frame(fixture, 59000); var correction = Revise(original, 2, 61000, At("12:05")) with
        { RecordState = withdrawn ? "withdrawn" : "active", WithdrawalReason = withdrawn ? "Source withdrew sample" : null };
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes); var oracle = new FootballLedger();
        var batches = new[] { Batch(fixture, 1, Context(fixture).Append(original).ToArray()), Batch(fixture, 2, [correction], true) };
        foreach (var batch in batches) { await store.AppendAsync(batch, default); oracle.Accept(new(batch, At("13:00"))); }
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value; var seek = Index(blobs, root, fixture.Stream.StreamId);
        Assert.Equal(new long[] { 0, 1 }, seek.Seek.Select(s => s.Minute));
        Assert.All(seek.Seek, s => Assert.Equal(2, Decode<FootballReadPage>(blobs, s.Page).Groups.Single().Members.Length));
        var reader = new AzureFootballStore(blobs, indexes);
        foreach (var query in new[] { Query(fixture, 59000), Query(fixture, 59000) with { ApplicationSequence = 1 }, Query(fixture, 61000),
            Query(fixture, 59000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") }, Query(fixture, 61000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:06") } })
            Assert.Equal(Json(oracle.Query(query)), Json(await reader.QueryAsync(query, default)));
        Assert.Equal(0, reader.MetadataReadBytes);
    }

    [Fact]
    public async Task CrossStreamCanonicalParentArrivalResolvesPendingChildWithoutGivingParentMembership()
    {
        var fixture = Fixture(); var other = fixture with { Stream = fixture.Stream with { StreamId = "other-parent" } }; var parent = Goal(fixture); var child = Revise(parent, 2, 70000, At("12:05"));
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes); var oracle = new FootballLedger();
        async Task Add(FootballBatch batch) { await writer.AppendAsync(batch, default); oracle.Accept(new(batch, At("13:00"))); }
        await Add(Batch(fixture, 1, Context(fixture).Append(child).ToArray()));
        var reader = new AzureFootballStore(blobs, indexes); Assert.Equal(1, Assert.Single(await reader.CatalogAsync(default)).PendingRecords);
        Assert.Empty((await reader.QueryAsync(Query(fixture, 80000), default)).Events);
        await Add(Batch(other, 1, Context(other).Append(parent).ToArray(), true));
        Assert.Equal(0, (await reader.CatalogAsync(default)).Single(d => d.Stream == fixture.Stream).PendingRecords);
        foreach (var query in new[] { Query(fixture, 65000), Query(fixture, 80000), Query(other, 65000), Query(fixture, 80000) with { ApplicationSequence = 1 } })
            Assert.Equal(Json(oracle.Query(query)), Json(await reader.QueryAsync(query, default)));
        Assert.Equal(2, Assert.Single((await reader.QueryAsync(Query(fixture, 80000), default)).Events).Revision);
    }

    [Fact]
    public async Task PossessionKeepsSameStreamDependencyGroupOutsideSeekWindowAndCrossStreamExistenceAloneDoesNotGrantMembership()
    {
        var fixture = Fixture(); var other = fixture with { Stream = fixture.Stream with { StreamId = "dependency-source" } }; var dependency = Frame(fixture, 0);
        var possession = Record(fixture, "completed-possession", new PossessionPayload("person-one", "team-home", "0", "10", true, true, 60000, dependency.RecordId), new("period-one", 0, 60000, "source_interval", true));
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes); var oracle = new FootballLedger();
        async Task Add(FootballBatch batch) { await writer.AppendAsync(batch, default); oracle.Accept(new(batch, At("13:00"))); }
        await Add(Batch(fixture, 1, Context(fixture).Append(possession).ToArray()));
        await Add(Batch(other, 1, Context(other).Append(dependency).ToArray(), true));
        Assert.Empty((await new AzureFootballStore(blobs, indexes).QueryAsync(Query(fixture, 60000), default)).Possessions);
        await Add(Batch(fixture, 2, [dependency], true));
        var reader = new AzureFootballStore(blobs, indexes);
        foreach (var query in new[] { Query(fixture, 60000), Query(fixture, 60000) with { ApplicationSequence = 1 }, Query(fixture, 59999) })
            Assert.Equal(Json(oracle.Query(query)), Json(await reader.QueryAsync(query, default)));
        Assert.Single((await reader.QueryAsync(Query(fixture, 60000), default)).Possessions); Assert.Null((await reader.QueryAsync(Query(fixture, 60000), default)).Frame);
    }

    [Fact]
    public async Task MigrationIsExplicitPreservesJournalAndPreparedRestartSkipsRecovery()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new ManualIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray(), true), default);
        var prepared = indexes.Roots[fixture.Manifest.DatasetId]; var legacy = prepared with { Value = prepared.Value with { DerivedReadView = null }, ETag = "legacy" }; indexes.Roots[fixture.Manifest.DatasetId] = legacy;
        var journal = blobs.Content.Where(k => !k.Key.Contains("/read-v1/")).ToDictionary(k => k.Key, k => k.Value.ToArray());
        var reader = new AzureFootballStore(blobs, indexes);
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.CatalogAsync(default)); Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.ReadViewWriteCount);
        var receipt = await reader.PrepareReadViewsAsync(default); Assert.Equal(1, receipt.Published); Assert.Equal(1, receipt.Streams);
        Assert.Equal(AzureFootballStore.SourceFenceHash(legacy.Value), AzureFootballStore.SourceFenceHash(indexes.Roots[fixture.Manifest.DatasetId].Value));
        Assert.All(journal, pair => Assert.Equal(pair.Value, blobs.Content[pair.Key]));
        var restart = new AzureFootballStore(blobs, indexes); var second = await restart.PrepareReadViewsAsync(default);
        Assert.Equal(0, second.Published); Assert.Equal(0, second.PageWriteCalls); Assert.Equal(0, restart.MetadataReadBytes); Assert.Equal(0, restart.RecoveredRecordCount);
        Assert.NotNull((await restart.QueryAsync(Query(fixture, 0), default)).Frame);
    }

    [Theory]
    [InlineData("catalog")] [InlineData("index")] [InlineData("context")] [InlineData("seek")]
    public async Task CorruptPreparedDocumentsFailClosedWithoutJournalFallback(string kind)
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()), default);
        var root = (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value; var descriptor = Decode<FootballReadCatalog>(blobs, root.DerivedReadView!).Streams.Single(); var index = Decode<FootballReadIndex>(blobs, descriptor.Index);
        var reference = kind switch { "catalog" => root.DerivedReadView!, "index" => descriptor.Index, "context" => index.Context, _ => index.Seek.Single().Page };
        blobs.Content[reference.Path][10] ^= 255;
        var reader = new AzureFootballStore(blobs, indexes);
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.QueryAsync(Query(fixture, 0), default)); Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.PayloadReadCount);
    }

    [Fact]
    public async Task SourceFenceMismatchRewindAndDisappearedMembershipRejectAfterObservedFence()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new ManualIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()), default); var old = indexes.Roots[fixture.Manifest.DatasetId];
        var reader = new AzureFootballStore(blobs, indexes); await reader.CatalogAsync(default);
        await writer.AppendAsync(Batch(fixture, 2, [Frame(fixture, 100)], true), default); var current = indexes.Roots[fixture.Manifest.DatasetId]; await reader.QueryAsync(Query(fixture, 100), default);
        indexes.Roots[fixture.Manifest.DatasetId] = old;
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.CatalogAsync(default));
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.QueryAsync(Query(fixture, 0), default));
        indexes.Roots.Clear(); await Assert.ThrowsAsync<RecordingValidationException>(() => reader.CatalogAsync(default));
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.QueryAsync(Query(fixture, 0), default));
        indexes.Roots[fixture.Manifest.DatasetId] = current with { Value = current.Value with { Streams = [current.Value.Streams[0] with { Closed = false }] } };
        await Assert.ThrowsAsync<RecordingValidationException>(() => new AzureFootballStore(blobs, indexes).CatalogAsync(default));
    }

    [Fact]
    public async Task IncrementalAppendReusesUnchangedContextAndOtherMinutePagesAndCasFailureNeverPublishesCandidate()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()), default);
        var old = Index(blobs, (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value, fixture.Stream.StreamId); var writes = writer.ReadViewWriteCount;
        await writer.AppendAsync(Batch(fixture, 2, [Frame(fixture, 60000)]), default);
        var current = Index(blobs, (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value, fixture.Stream.StreamId);
        Assert.Equal(old.Context, current.Context); Assert.Equal(old.Seek.Single().Page, current.Seek.Single(s => s.Minute == 0).Page); Assert.Equal(3, writer.ReadViewWriteCount - writes);
        indexes.FailNextSwap = true; await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(Batch(fixture, 3, [Frame(fixture, 60100)], true), default));
        Assert.Equal(2, Assert.Single(await new AzureFootballStore(blobs, indexes).CatalogAsync(default)).HeadSequence);
        Assert.Equal("60000", (await new AzureFootballStore(blobs, indexes).QueryAsync(Query(fixture, 60100), default)).Frame!.SourceFrameId);
        Assert.True(await writer.AppendAsync(Batch(fixture, 3, [Frame(fixture, 60100)], true), default));
        Assert.Equal("60100", (await new AzureFootballStore(blobs, indexes).QueryAsync(Query(fixture, 60100), default)).Frame!.SourceFrameId);
    }

    [Fact]
    public async Task MixedRevisionTypesDuplicatedAcrossContextAndAdjacentBucketsCompareContentNotArrayIdentity()
    {
        var fixture = Fixture(); var original = Frame(fixture, 59950); var goal = Goal(fixture) with
        { RecordId = original.RecordId, Revision = 2, SupersedesRevision = 1, FootballTime = new("period-one", 60000, 60000, "source_exact", true) };
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes); var oracle = new FootballLedger();
        foreach (var batch in new[] { Batch(fixture, 1, Context(fixture).Append(original).ToArray()), Batch(fixture, 2, [goal], true) })
        { await store.AppendAsync(batch, default); oracle.Accept(new(batch, At("13:00"))); }
        var reader = new AzureFootballStore(blobs, indexes);
        foreach (var query in new[] { Query(fixture, 59950), Query(fixture, 60020) with { ApplicationSequence = 1 } })
            Assert.Equal(Json(oracle.Query(query)), Json(await reader.QueryAsync(query, default)));
        // The original ledger does not turn a type-changing event into a tracking payload.
        var rejected = await Assert.ThrowsAsync<RecordingValidationException>(() => reader.QueryAsync(Query(fixture, 60020), default));
        Assert.Contains("Lazy tracking payload", rejected.Message);
    }

    [Fact]
    public async Task ContextEffectiveDateUnknownAvailabilityAndCancelledQueryKeepLedgerSemantics()
    {
        var fixture = Fixture(); var reference = Record(fixture, "reference", new ReferencePayload("person-one", "person", "Before"), null) with
        { FixtureId = null, EffectiveTime = new("calendar_date", "2026-01-01", "2026-02-01", "calendar_date", false) };
        var goal = Goal(fixture); var unknown = Revise(goal, 2, 70000, null);
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes); var oracle = new FootballLedger();
        foreach (var batch in new[] { Batch(fixture, 1, Context(fixture).Concat([reference, goal]).ToArray()), Batch(fixture, 2, [unknown], true) })
        { await store.AppendAsync(batch, default); oracle.Accept(new(batch, At("13:00"))); }
        var reader = new AzureFootballStore(blobs, indexes);
        foreach (var query in new[] { Query(fixture, 80000) with { EffectiveDate = "2026-01-31" }, Query(fixture, 80000) with { EffectiveDate = "2026-02-01" },
            Query(fixture, 65000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:01") } })
            Assert.Equal(Json(oracle.Query(query)), Json(await reader.QueryAsync(query, default)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.QueryAsync(Query(fixture, 0), cancellation.Token));
    }

    [Fact]
    public async Task ExistingWriterBuilderTracksOtherWritersCommittedUpdatesBeforeNextAppend()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var first = new AzureFootballStore(blobs, indexes); var second = new AzureFootballStore(blobs, indexes);
        await first.AppendAsync(Batch(fixture, 1, Context(fixture).Append(Frame(fixture, 0)).ToArray()), default);
        await second.AppendAsync(Batch(fixture, 2, [Frame(fixture, 100)]), default);
        await first.AppendAsync(Batch(fixture, 3, [Frame(fixture, 200)], true), default);
        var reader = new AzureFootballStore(blobs, indexes); Assert.Equal(3, Assert.Single(await reader.CatalogAsync(default)).HeadSequence);
        Assert.Equal("100", (await reader.QueryAsync(Query(fixture, 100), default)).Frame!.SourceFrameId);
        Assert.Equal("200", (await reader.QueryAsync(Query(fixture, 200), default)).Frame!.SourceFrameId);
    }

    [Fact]
    public async Task CachedPreparedCatalogStillChecksExactReferenceByteLength()
    {
        var fixture = Fixture(); var blobs = new MemoryReplayBlobs(); var indexes = new ManualIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        await writer.AppendAsync(Batch(fixture, 1, Context(fixture)), default);
        var reader = new AzureFootballStore(blobs, indexes); await reader.CatalogAsync(default); var root = indexes.Roots[fixture.Manifest.DatasetId];
        indexes.Roots[fixture.Manifest.DatasetId] = root with { Value = root.Value with { DerivedReadView = root.Value.DerivedReadView! with { Bytes = root.Value.DerivedReadView.Bytes + 1 } } };
        await Assert.ThrowsAsync<RecordingValidationException>(() => reader.CatalogAsync(default));
    }

    [Fact]
    public async Task EqualTimeFramesKeepOriginalDeliveryTieOrderDespiteContextDependencyAndAlphabeticPageOrder()
    {
        var fixture = Fixture(); var first = Frame(fixture, 0) with { RecordId = "z-first", Payload = ((TrackingPayload)Frame(fixture, 0).Payload) with { SourceFrameId = "first" } };
        var second = Frame(fixture, 0) with { RecordId = "a-second", Payload = ((TrackingPayload)Frame(fixture, 0).Payload) with { SourceFrameId = "second" } };
        var possession = Record(fixture, "completed", new PossessionPayload("person-one", "team-home", "0", "0", true, true, 0, second.RecordId), new("period-one", 0, 0, "source_interval", true));
        var batch = Batch(fixture, 1, Context(fixture).Concat([first, second, possession]).ToArray(), true);
        var oracle = new FootballLedger(); oracle.Accept(new(batch, At("13:00")));
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); await new AzureFootballStore(blobs, indexes).AppendAsync(batch, default);
        var view = await new AzureFootballStore(blobs, indexes).QueryAsync(Query(fixture, 0), default);
        Assert.Equal("first", view.Frame!.SourceFrameId); Assert.Equal(Json(oracle.Query(Query(fixture, 0))), Json(view));
    }

    [Fact]
    public async Task EqualTimeEventsKeepSelectedStreamDeliveryOrderRatherThanAlphabeticGroupOrder()
    {
        var fixture = Fixture(); var first = Goal(fixture) with { RecordId = "z-first-event" }; var second = Goal(fixture) with { RecordId = "a-second-event" };
        var batch = Batch(fixture, 1, Context(fixture).Concat([first, second]).ToArray(), true);
        var oracle = new FootballLedger(); oracle.Accept(new(batch, At("13:00")));
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); await new AzureFootballStore(blobs, indexes).AppendAsync(batch, default);
        var view = await new AzureFootballStore(blobs, indexes).QueryAsync(Query(fixture, 80000), default);
        Assert.Equal(new[] { "z-first-event", "a-second-event" }, view.Events.Select(e => e.RecordId));
        Assert.Equal(Json(oracle.Query(Query(fixture, 80000))), Json(view));
    }

    private sealed class ManualIndexes : IFootballIndexStore
    {
        public readonly Dictionary<string, VersionedFootballIndex> Roots = []; private int _version;
        public Task<VersionedFootballIndex?> ReadAsync(string id, CancellationToken token) => Task.FromResult(Roots.GetValueOrDefault(id));
        public Task<IReadOnlyList<VersionedFootballIndex>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<VersionedFootballIndex>>(Roots.Values.ToArray());
        public Task<bool> CompareExchangeAsync(FootballDatasetIndex value, string? expected, CancellationToken token)
        { if (Roots.GetValueOrDefault(value.DatasetId)?.ETag != expected) return Task.FromResult(false); Roots[value.DatasetId] = new(value, (++_version).ToString()); return Task.FromResult(true); }
    }
}
