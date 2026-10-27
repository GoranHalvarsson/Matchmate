using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

public sealed class RecordingFixture : IAsyncLifetime
{
    public SourceRecording Source { get; private set; } = default!;
    public ReplaySnapshot Snapshot { get; private set; } = default!;
    public async Task InitializeAsync()
    {
        var source = new SkillCornerFileSource(LocalPaths.Source);
        Source = await source.ReadAsync(default);
        var stream = await source.OpenAsync(default);
        var processor = new RecordingAccumulator(stream.Header);
        await foreach (var frame in stream.Frames) processor.Accept(frame);
        Snapshot = processor.Complete();
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class ReplayTests(RecordingFixture fixture, ITestOutputHelper output) : IClassFixture<RecordingFixture>
{
    [Fact]
    public void PinnedRecordingHasExpectedCoverageAndQuality()
    {
        var m = fixture.Snapshot.Metadata;
        Assert.Equal(1801, m.FrameCount); Assert.Equal(348, m.EmptyFrames); Assert.Equal(22, m.Players.Length);
        Assert.Equal(2_865_871, m.SourceBytes); Assert.Equal(SkillCornerFileSource.SourceHash, m.SourceHash);
        Assert.Equal("real", m.Origin);
        Assert.Equal(15_974, fixture.Snapshot.Frames.Sum(f => f.Players.Count(p => p.IsDetected == true)));
        Assert.Equal(15_992, fixture.Snapshot.Frames.Sum(f => f.Players.Count(p => p.IsDetected == false)));
        Assert.Equal(348, m.Gaps.Sum(g => g.Slots));
        Assert.Contains("not wall-clock live", m.ImportMode);
        output.WriteLine($"Pinned extracted JSON: {m.SourceBytes} bytes; .NET GZipStream CompressionLevel.Optimal: {m.GzipBytes} bytes. 1801 frames / 180-second span; this is not a transport or Cosmos write-rate benchmark.");
    }
    [Theory]
    [InlineData(60, 60)] [InlineData(60.099999999, 60)] [InlineData(60.1, 60.1)]
    [InlineData(239.999, 239.9)] [InlineData(240, 240)]
    public void FrameSelectionFloorsNeverLooksAhead(double cursor, double expected)
    {
        var view = ReplayQuery.At(fixture.Snapshot, cursor);
        Assert.Equal(expected, view.SourceClock); Assert.True(view.SourceClock <= cursor);
    }
    [Theory]
    [InlineData(59.99)] [InlineData(240.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidCursorRejected(double time) => Assert.Throws<ArgumentOutOfRangeException>(() => ReplayQuery.At(fixture.Snapshot, time));
    [Fact]
    public void GapClearsPlayersWithoutCarryingPriorFrame()
    {
        var gap = fixture.Snapshot.Metadata.Gaps.First();
        var view = ReplayQuery.At(fixture.Snapshot, gap.Start);
        Assert.True(view.FullGap); Assert.Equal(22, view.Players.Length);
        Assert.All(view.Players, p => { Assert.False(p.Position.Available); Assert.Equal("missing", p.Position.Quality); Assert.Null(p.Position.X); });
        Assert.Equal("empty-positions", view.State);
    }
    [Fact]
    public void AbsentFrameDoesNotHoldPreviousPositions()
    {
        var frames = fixture.Snapshot.Frames.Where(f => f.Clock != 60.1).ToArray();
        var snapshot = fixture.Snapshot with { Frames = frames };
        var view = ReplayQuery.At(snapshot, 60.1);
        Assert.Equal("missing-frame", view.State); Assert.Null(view.SourceClock);
        Assert.All(view.Players, p => Assert.False(p.Position.Available)); Assert.False(view.Ball.Available);
    }
    [Fact]
    public void QualityUnknownEstimatedAndMissingRemainDistinctAndBallIndependent()
    {
        var original = fixture.Source.Document.Frames.First();
        var a = original.Players[0]; var b = original.Players[1]; var c = original.Players[2];
        var frame = original with { Players = [a with { X = 1, Y = 2, IsDetected = true }, b with { X = 3, Y = 4, IsDetected = false }, c with { X = 60, Y = 1, IsDetected = null }], Ball = new(null, null, null, "source-unknown") };
        var snapshot = Process([frame]);
        var view = ReplayQuery.At(snapshot, 60);
        Assert.Equal("detected", view.Players.Single(p => p.Player.Id == a.PlayerId).Position.Quality);
        Assert.Equal("estimated", view.Players.Single(p => p.Player.Id == b.PlayerId).Position.Quality);
        var unknown = view.Players.Single(p => p.Player.Id == c.PlayerId).Position;
        Assert.Equal("unknown", unknown.Quality); Assert.True(unknown.Available); Assert.True(unknown.OutOfBounds);
        Assert.Equal(19, view.Players.Count(p => p.Position.Quality == "missing")); Assert.False(view.Ball.Available);
        var emptyWithBall = frame with { Players = [], FrameState = "empty-positions", Ball = new(1, 2, true, null) };
        var ballView = ReplayQuery.At(Process([emptyWithBall]), 60);
        Assert.True(ballView.FullGap); Assert.True(ballView.Ball.Available);
    }
    [Fact]
    public void NullUnknownDuplicateAndMalformedObservationsFailValidation()
    {
        var doc = fixture.Source.Document; var frame = doc.Frames[0];
        foreach (var invalid in new[] { doc with { Frames = [null!] }, doc with { Players = [null!] },
            doc with { Frames = [frame with { Players = [null!] }] },
            doc with { Frames = [frame with { Ball = null! }] },
            doc with { Frames = [frame with { Players = [frame.Players[0], frame.Players[0]] }] },
            doc with { Frames = [frame with { Players = [frame.Players[0] with { PlayerId = -1 }] }] },
            doc with { Frames = [frame with { Players = [frame.Players[0] with { X = null, Y = 1 }] }] },
            doc with { Frames = [frame, frame] }, doc with { Cadence = 0 } })
            Assert.Throws<RecordingValidationException>(() => new ReplayProcessor().Process(fixture.Source with { Document = invalid }));
        var accumulator = new RecordingAccumulator(fixture.Source);
        accumulator.Accept(frame); Assert.Throws<RecordingValidationException>(() => accumulator.Accept(frame));
    }
    [Fact]
    public void ViewerCursorsAreIndependentAndPauseAtFirstCrossedGap()
    {
        var m = fixture.Snapshot.Metadata; var gap = m.Gaps[0];
        var one = new ViewerCursor(m); var two = new ViewerCursor(m);
        one.Play(); one.Advance(gap.Start - m.Start + 1);
        Assert.Equal(gap.Start, one.Time); Assert.False(one.Playing); Assert.Equal(m.Start, two.Time);
        one.Play(); Assert.False(one.Playing);
        one.ContinueGap(); one.Advance(.01); Assert.True(one.Playing);
        two.Seek(150); Assert.NotEqual(two.Time, one.Time);
        var after = one.Adjacent(true); Assert.NotNull(after); one.Seek(after.Value); Assert.Null(one.Gap);
    }
    [Fact]
    public async Task SourceAdapterFailsClearlyOnMissingOrChangedDataset()
    {
        using var directory = new TestDirectory();
        var adapter = new SkillCornerFileSource(directory.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.ReadAsync(default));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "1874553-60-240.json"), "{}");
        await Assert.ThrowsAsync<RecordingValidationException>(() => adapter.ReadAsync(default));
    }
    [Fact]
    public async Task AtomicStoreIsIdempotentCompleteAndKeepsPriorGoodOnFailedImport()
    {
        using var directory = new TestDirectory(); var writer = new AtomicReplayStore(directory.Path); var reader = new AtomicReplayStore(directory.Path);
        Assert.Null(await reader.ReadAsync(default));
        await writer.PublishAsync(fixture.Snapshot, default);
        var pointer = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "latest.json"));
        await writer.PublishAsync(fixture.Snapshot, default);
        Assert.Equal(pointer, await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "latest.json")));
        var read = await reader.ReadAsync(default); Assert.Equal(1801, read!.Frames.Length);
        Assert.Same(read, await reader.ReadAsync(default));
        await Assert.ThrowsAsync<RecordingValidationException>(() => writer.PublishAsync(fixture.Snapshot with { Metadata = fixture.Snapshot.Metadata with { FrameCount = 1 } }, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PublishAsync(fixture.Snapshot, cancellation.Token));
        Assert.Equal(pointer, await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "latest.json")));
        await Task.WhenAll(writer.PublishAsync(fixture.Snapshot, default), ReadRepeatedly());
        async Task ReadRepeatedly() { for (var i = 0; i < 10; i++) Assert.Equal(1801, (await reader.ReadAsync(default))!.Frames.Length); }
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Equal(1801, (await new AtomicReplayStore(directory.Path).ReadAsync(default))!.Frames.Length);
    }
    [Fact]
    public async Task IdempotentSnapshotKeepsFilesAndCancellationWhileChangedPublicationStillAdvances()
    {
        using var directory = new TestDirectory(); var writer = new AtomicReplayStore(directory.Path);
        await writer.PublishAsync(fixture.Snapshot, default);
        var paths = Directory.GetFiles(directory.Path, "*.json"); var earlier = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var path in paths) File.SetLastWriteTimeUtc(path, earlier);
        await writer.PublishAsync(fixture.Snapshot, default);
        Assert.All(paths, path => Assert.Equal(earlier, File.GetLastWriteTimeUtc(path)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PublishAsync(fixture.Snapshot, cancelled.Token));
        var nextHash = new string('a', 64);
        var next = fixture.Snapshot with { Metadata = fixture.Snapshot.Metadata with { SourceHash = nextHash, Version = "local-replay-v1-" + nextHash } };
        await writer.PublishAsync(next, default);
        var read = await new AtomicReplayStore(directory.Path).ReadAsync(default);
        Assert.Equal(next.Metadata.Version, read!.Metadata.Version); Assert.Equal(1801, read.Frames.Length);
        Assert.All(paths.Where(path => Path.GetFileName(path) != "latest.json"), path => Assert.True(File.Exists(path)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public async Task APIReadsProcessedStoreWithBoundedFramesAndExpectedStatusCodes()
    {
        using var directory = new TestDirectory();
        await using var factory = Factory(directory.Path); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/matches/1874553/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/matches/999/metadata")).StatusCode);
        foreach (var cursor in new[] { "", "NaN", "Infinity", "garbage" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/matches/1874553/frame?cursor=" + cursor)).StatusCode);
        // Same common adapter/encoded batch/store path as the Worker; no API source-file access or old DTO fallback.
        var batches = await new AustralianFootballSource(LocalPaths.Source, System.IO.Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var store = new ChunkedLocalFootballStore(directory.Path);
        foreach (var batch in batches) await store.AppendAsync(FootballCodec.Decode(FootballCodec.Encode(batch)), default);
        var dataset = Assert.Single((await client.GetFromJsonAsync<FootballDataset[]>("/api/football/datasets"))!);
        var context = new FootballQuery(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            60099, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision);
        var response = await client.PostAsJsonAsync("/api/football/view", context);
        var body = await response.Content.ReadAsStringAsync(); Assert.True(body.Length < 64_000);
        var view = JsonSerializer.Deserialize<FootballView>(body, FootballCodec.Json)!;
        Assert.Equal("610", view.Frame!.SourceFrameId); Assert.Equal(22, view.Frame.ActiveSubjectIds.Length);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/football/view", context with { CursorMs = 241000 })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/matches/1874553/metadata")).StatusCode);
    }
    [Theory]
    [InlineData("null")] [InlineData("{\"contractVersion\":1,\"metadata\":null,\"frames\":[]}")]
    [InlineData("{\"contractVersion\":1,\"metadata\":{\"version\":\"local-replay-v1-\",\"sourceHash\":null},\"frames\":[]}")]
    public async Task CorruptedOrNullPublicationReturnsUnavailable(string json)
    {
        using var directory = new TestDirectory(); var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, hash + ".json"), bytes);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "latest.json"), JsonSerializer.Serialize(new { hash }));
        await using var factory = Factory(directory.Path); using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/matches/1874553/metadata");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(directory.Path, await response.Content.ReadAsStringAsync());
    }
    private ReplaySnapshot Process(RecordedFrame[] frames) => new ReplayProcessor().Process(fixture.Source with { Document = fixture.Source.Document with { Frames = frames } });
    private static WebApplicationFactory<Matchmate.Api.Program> Factory(string store) => new WebApplicationFactory<Matchmate.Api.Program>()
        .WithWebHostBuilder(builder => builder.UseContentRoot(System.IO.Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Api"))
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["LocalReplay:StoreDirectory"] = store, ["SharedFootball:StoreDirectory"] = store })));
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(HostingTestPaths.Root(), ".local", "tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
