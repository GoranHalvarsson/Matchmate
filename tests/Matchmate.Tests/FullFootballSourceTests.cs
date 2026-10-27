using System.Diagnostics;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

// Full completed inputs stay local/unpublished. CI uses CanonicalCloudTests with inline records.
public sealed class FullFootballSourceTests(ITestOutputHelper output)
{
    [Fact]
    public Task CompleteSourcesStreamThroughChunkedStoreRecoverAndResolveOnlyNeededTracking() => CheckCompleteSources("brentford-full-v1");
    [Fact]
    public Task EnrichedCompleteSourcesStreamThroughChunkedStoreRecoverAndResolveOnlyNeededTracking() => CheckCompleteSources("brentford-full-v2");
    [Fact]
    public Task SequencedCompleteSourcesStreamThroughChunkedStoreRecoverAndResolveOnlyNeededTracking() => CheckCompleteSources("brentford-full-v3");
    [Fact]
    public Task ParticipantCompleteSourcesStreamThroughChunkedStoreRecoverAndResolveOnlyNeededTracking() => CheckCompleteSources("brentford-full-v4");
    private async Task CheckCompleteSources(string exportFolder)
    {
        var timer = Stopwatch.StartNew(); using var directory = new TestDirectory(); var root = LocalPaths.FindRoot();
        var store = new ChunkedLocalFootballStore(directory.Path);
        IStreamingFootballSource[] sources = [new FullAustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")),
            new FullBrentfordFootballSource(Path.Combine(root, ".local", "shared-inputs", exportFolder, "manifest.json"))];
        var counts = new List<int>(); long rawBytes = 0, wireBytes = 0; var batches = 0; var gaps = 0; var reports = 0; var possessions = 0;
        foreach (var source in sources)
        {
            var count = 0;
            await foreach (var batch in source.ReadBatchesAsync(default))
            {
                var encoded = FootballTransport.Encode(batch); rawBytes += FootballCodec.Encode(batch).Length; wireBytes += encoded.Bytes.Length; batches++;
                var decoded = FootballTransport.Decode(encoded.Bytes, encoded.ContentType);
                Assert.True(await store.AppendAsync(decoded, default));
                count += batch.Records.Count(r => r.Payload is TrackingPayload); gaps += batch.Records.Count(r => r.Payload is TrackingPayload p && p.Players.All(o => o.Availability != "present"));
                reports += batch.Records.Count(r => r.Payload is MatchEventPayload); possessions += batch.Records.Count(r => r.Payload is PossessionPayload);
                if (batch.Sequence == 1)
                {
                    var open = (await store.CatalogAsync(default)).Single(d => d.Stream == batch.Stream); Assert.False(open.Closed);
                    Assert.NotNull((await store.QueryAsync(Query(open, open.Schedule!.Segments[0].CursorStartMs), default)).Frame);
                }
            }
            counts.Add(count);
        }
        Assert.Equal(new[] { 1801, 58801 }, counts); Assert.Equal(348, gaps); Assert.Equal(14, reports); Assert.Equal(5, possessions); Assert.Equal(0, store.Journal.RetainedTrackingCount);
        var importMs = timer.ElapsedMilliseconds; timer.Restart();
        var restart = new ChunkedLocalFootballStore(directory.Path); var catalog = await restart.CatalogAsync(default);
        Assert.Equal(2, catalog.Length); Assert.All(catalog, d => Assert.True(d.Closed)); Assert.Equal(0, restart.Journal.PayloadReadCount);
        var recoverMs = timer.ElapsedMilliseconds; timer.Restart();
        var aus = catalog.Single(d => d.People.Length == 22); var brent = catalog.Single(d => d.People.Length == 40);
        var australian = await restart.QueryAsync(Query(aus, 100000), default); Assert.NotNull(australian.Frame); Assert.Equal("1010", australian.Frame.SourceFrameId);
        Assert.True((await restart.QueryAsync(Query(aus, 175900), default)).TrackingGap);
        Assert.Equal(5, (await restart.QueryAsync(Query(aus, 240000), default)).Possessions.Length);
        foreach (var cursor in new long[] { 0, 500000, 2820000, 3780000, 4260000, 5760000, 5880000 })
        {
            var before = restart.Journal.PayloadReadCount; var view = await restart.QueryAsync(Query(brent, cursor), default);
            Assert.NotNull(view.Frame); Assert.False(view.TrackingGap); Assert.Equal(22, view.Frame.ActiveSubjectIds.Length);
            Assert.InRange(restart.Journal.PayloadReadCount - before, 0, 1); Assert.All(view.Frame.Players.Append(view.Frame.Ball), o => Assert.Equal("generated", o.Method));
            await CheckExportParity(root, cursor, view.Frame);
        }
        Assert.DoesNotContain((await restart.QueryAsync(Query(brent, 5759999), default)).Events, r => r.RecordId == "goal-3");
        var end = await restart.QueryAsync(Query(brent, 5880000), default);
        Assert.Equal(14, end.Events.Length); Assert.Equal(3, end.Events.Count(r => r.Payload is MatchEventPayload p && p.EventKind == "goal"));
        Assert.Equal(9, end.Events.Count(r => r.Payload is MatchEventPayload p && p.EventKind == "substitution"));
        Assert.Equal(2, end.Events.Count(r => r.Payload is MatchEventPayload p && p.EventKind == "yellow_card"));
        Assert.Equal(2940000, end.Events.Single(r => r.RecordId == "goal-3").FootballTime!.EndMs);
        if (exportFolder is "brentford-full-v2" or "brentford-full-v3" or "brentford-full-v4")
        {
            Assert.Equal(exportFolder == "brentford-full-v4" ? 5 : exportFolder == "brentford-full-v3" ? 4 : 3, brent.Manifest.ManifestRevision);
            var executor = new MatchWorkflowExecutor((query, token) => restart.QueryAsync(query, token));
            foreach (var workflow in new[] { "current_match_events", "current_match_moment" })
            {
                var request = new MatchWorkflowRequest(workflow, MatchWorkflowRegistry.Version, new("authenticated-owner", 1, new(Query(brent, 5880000), brent.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor));
                var bundle = Assert.IsType<MatchWorkflowBundle>((await executor.ExecuteAsync(request, _ => true)).Bundle);
                Assert.InRange(bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars); Assert.InRange(JsonSerializer.Serialize(bundle).Length, 1, MatchWorkflowLimits.BundleChars);
                using var projected = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "current_events").Text); Assert.Equal(14, projected.RootElement.GetProperty("eligibleTotal").GetInt32());
                if (workflow == "current_match_moment") { Assert.Equal(4, bundle.Items.Length); using var moment = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "moment_context").Text);
                    Assert.Equal(6, moment.RootElement.GetProperty("players").GetArrayLength()); Assert.Contains("generated", moment.RootElement.GetRawText()); }
            }
            var selected = new MatchWorkflowRequest("selected_goal_event", MatchWorkflowRegistry.Version, new("authenticated-owner", 1,
                new(Query(brent, 3765000), brent.HeadSequence, new("goal-1", 3780000, "61")), MatchWorkflowTemporalMode.SelectedGoalRetrospective));
            var goalBundle = Assert.IsType<MatchWorkflowBundle>((await executor.ExecuteAsync(selected, _ => true)).Bundle);
            Assert.InRange(goalBundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars);
            Assert.DoesNotContain("goal-2", JsonSerializer.Serialize(goalBundle)); Assert.DoesNotContain("goal-3", JsonSerializer.Serialize(goalBundle));
            Assert.Contains("Damsgaard corner", goalBundle.Items.Single(i => i.Id == "selected_goal").Text);
        }
        var reads = restart.Journal.PayloadReadCount; var metaBytes = restart.Journal.MetadataReadBytes;
        for (var i = 0; i < 20; i++) await restart.QueryAsync(Query(brent, 5880000), default);
        Assert.Equal(reads, restart.Journal.PayloadReadCount); Assert.Equal(metaBytes, restart.Journal.MetadataReadBytes);
        await Assert.ThrowsAsync<FootballQueryException>(() => restart.QueryAsync(Query(brent, 0) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = DateTimeOffset.UtcNow }, default));
        var files = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories); var storedBytes = files.Sum(f => new FileInfo(f).Length);
        var receipt = new { counts, batches, rawBytes, wireBytes, importMs, recoverMs, queryMs = timer.ElapsedMilliseconds, storedBytes, files = files.Length,
            metadataReadBytes = restart.Journal.MetadataReadBytes, payloadReadBytes = restart.Journal.PayloadReadBytes, payloadReadCount = restart.Journal.PayloadReadCount,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, retainedTracking = restart.Journal.RetainedTrackingCount };
        output.WriteLine(JsonSerializer.Serialize(receipt));
        Directory.CreateDirectory(Path.Combine(root, ".azure")); await File.WriteAllTextAsync(Path.Combine(root, ".azure", "full-football-local-measurement.json"), JsonSerializer.Serialize(receipt));
    }
    internal static FootballQuery Query(FootballDataset d, long cursor) => new(d.Manifest.DatasetId, d.Manifest.DatasetVersion, d.Stream.StreamId, d.Manifest.FixtureId, cursor, "retrospective_replay", d.Schedule!.PolicyId, d.Schedule.PolicyRevision);
    private static async Task CheckExportParity(string root, long cursor, TrackingPayload actual)
    {
        var folder = Path.Combine(root, ".local", "shared-inputs", "brentford-full-v1");
        var frameIndex = cursor / 100; var chunk = frameIndex / 64; var offset = (int)(frameIndex % 64);
        using var raw = JsonDocument.Parse(FootballTransport.Expand(await File.ReadAllBytesAsync(Path.Combine(folder, $"frames-{chunk:D6}.json.gz")), 2_000_000));
        var expected = raw.RootElement[offset]; var players = expected.GetProperty("players").EnumerateArray().ToArray();
        for (var i = 0; i < players.Length; i++)
        {
            Assert.Equal("person-pl-reference-" + players[i].GetProperty("player").GetProperty("id").GetString()!.Replace(':', '-'), actual.Players[i].SubjectId);
            Assert.Equal(players[i].GetProperty("slotId").GetString(), actual.Players[i].SlotId);
            Assert.Equal(players[i].GetProperty("x").GetDouble(), actual.Players[i].XM); Assert.Equal(players[i].GetProperty("y").GetDouble(), actual.Players[i].YM);
        }
        Assert.Equal(expected.GetProperty("ball").GetProperty("x").GetDouble(), actual.Ball.XM); Assert.Equal(expected.GetProperty("ball").GetProperty("y").GetDouble(), actual.Ball.YM);
    }
}
