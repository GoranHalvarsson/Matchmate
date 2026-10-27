using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

// Local evidence using the existing, unpublished source. CI uses the inline synthetic fixtures in ReplayPipelineTests.
public sealed class AustralianRecordingImportTests(RecordingFixture fixture, ITestOutputHelper output) : IClassFixture<RecordingFixture>
{
    [Fact]
    public async Task ExistingAustralianRecordingRoundTripsDurableTransportWithoutChangingSourceFacts()
    {
        var envelopes = ReplayBatchCodec.Create(DatasetIdentity.Australian, fixture.Source, 1);
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        foreach (var envelope in envelopes)
            await new DurableReplayProcessor(blobs, catalog).ProcessAsync(ReplayBatchCodec.Decode(ReplayBatchCodec.Encode(envelope)), DateTimeOffset.UtcNow, default);
        var first = (await catalog.ReadAsync(DatasetIdentity.Australian, default))!;
        var store = new BlobReplayDatasetStore(blobs, catalog);
        var descriptor = Assert.Single(await store.CatalogAsync(default));
        var recording = (await store.SnapshotAsync(descriptor.DatasetId, descriptor.Metadata.Version, default))!;
        Assert.Equal(1801, recording.Frames.Length); Assert.Equal(348, recording.Metadata.EmptyFrames); Assert.Equal(22, recording.Metadata.Players.Length);
        Assert.Equal(610, recording.Frames[0].FrameId); Assert.Equal(2410, recording.Frames[^1].FrameId);
        Assert.Equal(60, recording.Metadata.Start); Assert.Equal(240, recording.Metadata.End);
        Assert.Equal("real", recording.Metadata.Origin); Assert.Equal(SkillCornerFileSource.SourceHash, recording.Metadata.SourceHash);
        Assert.Equal(fixture.Snapshot.Frames.Sum(f => f.Players.Count(p => p.IsDetected == true)), recording.Frames.Sum(f => f.Players.Count(p => p.IsDetected == true)));
        Assert.Equal(fixture.Snapshot.Frames.Sum(f => f.Players.Count(p => p.IsDetected == false)), recording.Frames.Sum(f => f.Players.Count(p => p.IsDetected == false)));
        Assert.True(ReplayQuery.At(recording, recording.Metadata.Gaps[0].Start).FullGap);
        foreach (var envelope in envelopes) await new DurableReplayProcessor(blobs, catalog).ProcessAsync(envelope, DateTimeOffset.UtcNow, default);
        Assert.Equal(first, await catalog.ReadAsync(DatasetIdentity.Australian, default));
        output.WriteLine($"Actual Australian passage: {envelopes.Count} envelopes; maximum {envelopes.Max(e => ReplayBatchCodec.Encode(e).Length)} bytes; total normalized transport JSON {envelopes.Sum(e => ReplayBatchCodec.Encode(e).Length)} bytes; snapshot JSON {first.Value.UncompressedBytes} bytes, gzip {first.Value.CompressedBytes} bytes. This is not a full-match or RU benchmark.");
    }
}
