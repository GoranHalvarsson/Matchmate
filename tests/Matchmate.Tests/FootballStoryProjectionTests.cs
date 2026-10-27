using System.Text.Json;
using System.Diagnostics;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class FootballStoryProjectionTests(ITestOutputHelper output)
{
    private static FootballRecord Control(TestFixture fixture, string id = "control", long start = 0, long end = 1000,
        string team = "team-home", string period = "period-one") => Action(fixture, id, "possession", start, end, team, "controlled", period);
    private static FootballRecord[] ReleasedContext(TestFixture fixture, params PresentationRelease[] releases)
    {
        var context = GeneratedContext(fixture);
        context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Releases = releases } };
        return context;
    }
    private static FootballLedger Closed(TestFixture fixture, FootballRecord[] actions, FootballRecord[]? context = null)
    {
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, context ?? GeneratedContext(fixture));
        ledger.Accept(new(Batch(fixture, 2, actions, close: true), At("13:00")));
        return ledger;
    }
    private static FootballStory Story(FootballLedger ledger, TestFixture fixture, long cursor) => Assert.IsType<FootballStory>(ledger.Query(Query(fixture, cursor)).Story);

    [Fact]
    public void WholeControlSpellWaitsForCompletionAndRewindHidesAllPassagePayload()
    {
        var fixture = GeneratedFixture();
        var control = Control(fixture); var pass = Action(fixture, "pass", "pass", 100, 500);
        var ledger = Closed(fixture, [control, pass]);
        Assert.Empty(Story(ledger, fixture, 999).Passages);
        Assert.Equal("{\"passages\":[]}", JsonSerializer.Serialize(Story(ledger, fixture, 999), FootballCodec.Json));
        var passage = Assert.Single(Story(ledger, fixture, 1000).Passages);
        Assert.Equal((0L, 1000L, 1000L, 1, 1, 0, 0, "control_end"),
            (passage.StartCursorMs, passage.EndCursorMs, passage.DurationMs, passage.PassAttempts, passage.PassCompleted, passage.Turnovers, passage.Shots, passage.Ending));
        Assert.Equal(new[] { control.RecordId, pass.RecordId }, passage.Evidence.Select(e => e.RecordId));
        Assert.Equal(FootballCodec.ContentHash(pass), passage.Evidence[1].Fingerprint);
        Assert.Equal(new[] { "source" }, passage.Evidence[1].EvidenceIds);
        Assert.Equal(64, passage.Fingerprint.Length);
        Assert.Equal(passage.Fingerprint, Assert.Single(Story(ledger, fixture, 1001).Passages).Fingerprint);
        Assert.Empty(Story(ledger, fixture, 999).Passages);
        var roundTrip = JsonSerializer.Deserialize<FootballView>(JsonSerializer.Serialize(ledger.Query(Query(fixture, 1000)), FootballCodec.Json), FootballCodec.Json)!;
        Assert.Equal(passage.Fingerprint, Assert.Single(roundTrip.Story!.Passages).Fingerprint);
    }

    [Fact]
    public void LateReleaseOfContainedPassWithholdsEntireSpellUntilItsRelease()
    {
        var fixture = GeneratedFixture();
        var ledger = Closed(fixture, [Control(fixture), Action(fixture, "late-pass", "pass", 100, 500)],
            ReleasedContext(fixture, new("control", 1000), new("late-pass", 1800)));
        Assert.Empty(Story(ledger, fixture, 1000).Passages); Assert.Empty(Story(ledger, fixture, 1799).Passages);
        var released = Assert.Single(Story(ledger, fixture, 1800).Passages);
        Assert.Equal(1, released.PassCompleted); Assert.Equal(1000, released.EndCursorMs);
        Assert.Empty(Story(ledger, fixture, 1799).Passages);
    }

    [Fact]
    public void PeriodStartsUnownedGapsAndTurnoversRemainSeparateWithoutMerging()
    {
        var fixture = GeneratedFixture(); var ledger = Closed(fixture,
        [
            Control(fixture, "first", 0, 1000), Action(fixture, "turnover", "pass", 800, 1000, outcome: "turnover"),
            Control(fixture, "after-gap", 2000, 3000), Action(fixture, "pass", "pass", 2100, 2300),
            Control(fixture, "second-period", 0, 1000, period: "period-two"), Action(fixture, "shot", "shot", 500, 1000, outcome: "goal", period: "period-two")
        ]);
        var beforeHalf = Story(ledger, fixture, 99999).Passages;
        Assert.Equal(2, beforeHalf.Length); Assert.Equal(new[] { "turnover", "control_end" }, beforeHalf.Select(p => p.Ending));
        Assert.Equal(2000, beforeHalf[1].StartCursorMs);
        Assert.Equal(2, Story(ledger, fixture, 100999).Passages.Length);
        var second = Story(ledger, fixture, 101000).Passages[2];
        Assert.Equal(("period-two", 100000L, 101000L, 1, "shot"), (second.PeriodId, second.StartCursorMs, second.EndCursorMs, second.Shots, second.Ending));
        Assert.Equal(0, second.PassAttempts);
    }

    [Fact]
    public void PeriodEndAndKnownZeroCountsAreSupportedWithoutInventingShotsOrTurnovers()
    {
        var fixture = GeneratedFixture(); var ledger = Closed(fixture, [Control(fixture, start: 99000, end: 100000)]);
        var passage = Assert.Single(Story(ledger, fixture, 100000).Passages);
        Assert.Equal((0, 0, 0, 0, "period_end"), (passage.PassAttempts, passage.PassCompleted, passage.Turnovers, passage.Shots, passage.Ending));
        Assert.Single(passage.Evidence);
        Assert.Empty(Story(Closed(fixture, []), fixture, 100000).Passages);
    }

    [Theory]
    [InlineData("open")] [InlineData("partial-head")] [InlineData("known-then")] [InlineData("knowledge-as-of")] [InlineData("effective-date")] [InlineData("pending")]
    public void IncompleteMembershipAndUnsupportedHistoryAreUnavailable(string boundary)
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var control = Control(fixture); var pass = Action(fixture, "pass", "pass", 100, 500);
        if (boundary == "pending") pass = pass with { Revision = 2, SupersedesRevision = 1 };
        ledger.Accept(new(Batch(fixture, 2, [control, pass], close: boundary != "open"), At("13:00")));
        var query = Query(fixture, 1000);
        query = boundary switch
        {
            "partial-head" => query with { ApplicationSequence = 1 },
            "known-then" => query with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") },
            "knowledge-as-of" => query with { KnowledgeAsOfUtc = At("12:03") },
            "effective-date" => query with { EffectiveDate = "2026-01-01" },
            _ => query
        };
        var view = ledger.Query(query); Assert.Null(view.Story);
        Assert.False(JsonSerializer.Serialize(view, FootballCodec.Json).Contains("\"story\":", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentHeadCanBeImplicitOrExplicitAndCompletedReferenceRemainsSupported()
    {
        var fixture = GeneratedFixture(); var ledger = Closed(fixture, [Control(fixture)]);
        var implicitStory = Assert.Single(Story(ledger, fixture, 1000).Passages);
        var explicitStory = Assert.Single(ledger.Query(Query(fixture, 1000) with { ApplicationSequence = 2 }).Story!.Passages);
        Assert.Equal(implicitStory.Fingerprint, explicitStory.Fingerprint);
        Assert.Single(ledger.Query(Query(fixture, 1000) with { KnowledgeMode = "completed_reference" }).Story!.Passages);
    }

    [Fact]
    public void LatestControlRevisionIsSelectedBeforeCompletionWithoutOldRevisionFallback()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var original = Control(fixture); Add(ledger, fixture, 2, original, Action(fixture, "pass", "pass", 100, 500));
        var correction = original with { Revision = 2, SupersedesRevision = 1, FootballTime = original.FootballTime! with { EndMs = 2000 } };
        Add(ledger, fixture, 3, correction); ledger.Accept(new(Batch(fixture, 4, [original], close: true), At("13:00")));
        Assert.Empty(Story(ledger, fixture, 1000).Passages); Assert.Empty(Story(ledger, fixture, 1999).Passages);
        var passage = Assert.Single(Story(ledger, fixture, 2000).Passages);
        Assert.Equal(2, passage.Evidence[0].Revision); Assert.Equal(FootballCodec.ContentHash(correction), passage.Evidence[0].Fingerprint);
        Assert.Null(ledger.Query(Query(fixture, 1000) with { ApplicationSequence = 2 }).Story);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void WithdrawalOfControlOrContainedActionWithholdsWholeSpell(bool controlWithdrawal)
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var control = Control(fixture); var pass = Action(fixture, "pass", "pass", 100, 500); Add(ledger, fixture, 2, control, pass);
        var original = controlWithdrawal ? control : pass;
        var withdrawal = original with { Revision = 2, SupersedesRevision = 1, RecordState = "withdrawn", WithdrawalReason = "Authored action withdrawn" };
        Add(ledger, fixture, 3, withdrawal); ledger.Accept(new(Batch(fixture, 4, [original], close: true), At("13:00")));
        Assert.Empty(Story(ledger, fixture, 2000).Passages);
        Assert.False(ledger.Accept(new(Batch(fixture, 4, [original], close: true), At("13:00"))));
        Assert.Empty(Story(ledger, fixture, 2000).Passages);
    }

    [Theory]
    [InlineData("Liverpool")] [InlineData("Australian")]
    public void SourcesWithoutGeneratedActionsHaveUnavailableStory(string label)
    {
        var fixture = Fixture(label.ToLowerInvariant(), label.ToLowerInvariant() + "-stream");
        var ledger = Closed(fixture, [], Context(fixture));
        Assert.Null(ledger.Query(Query(fixture, 1000)).Story);
    }

    [Theory]
    [InlineData("orphan")] [InlineData("wrong-team")] [InlineData("overlapping-control")] [InlineData("overlapping-actions")]
    [InlineData("turnover-before-end")] [InlineData("shot-before-end")] [InlineData("cross-period")]
    [InlineData("duration-cap")] [InlineData("action-cap")] [InlineData("outside-period")]
    public void ContradictoryContainmentRosterIntervalsOrCapsFailClosed(string fault)
    {
        var fixture = GeneratedFixture(); var actions = new List<FootballRecord> { Control(fixture), Action(fixture, "pass", "pass", 100, 500) };
        if (fault == "orphan") actions.RemoveAt(0);
        if (fault == "wrong-team") actions[1] = actions[1] with { Payload = ((FootballActionPayload)actions[1].Payload) with { PersonId = "person-two" }, EntityRefs = ["team-home", "person-three", "person-two"] };
        if (fault == "overlapping-control") actions.Add(Control(fixture, "second", 500, 1500));
        if (fault == "overlapping-actions") actions.Add(Action(fixture, "overlap", "pass", 400, 700));
        if (fault == "turnover-before-end") actions[1] = Action(fixture, "pass", "pass", 100, 500, outcome: "turnover");
        if (fault == "shot-before-end") actions[1] = Action(fixture, "pass", "shot", 100, 500, outcome: "goal");
        if (fault == "cross-period") actions[1] = actions[1] with { FootballTime = actions[1].FootballTime! with { PeriodId = "period-two" } };
        if (fault == "duration-cap") actions[0] = Control(fixture, end: 60001);
        if (fault == "action-cap") actions = [Control(fixture), .. Enumerable.Range(0, 13).Select(i => Action(fixture, "pass-" + i, "pass", i * 20, i * 20 + 10))];
        if (fault == "outside-period") actions[0] = Control(fixture, start: 99999, end: 100001);
        Assert.Null(Closed(fixture, actions.ToArray()).Query(Query(fixture, 150000)).Story);
    }

    [Theory]
    [InlineData("real")] [InlineData("reported")] [InlineData("missing-fields")] [InlineData("conflict")]
    [InlineData("wrong-fixture")] [InlineData("wrong-version")] [InlineData("wrong-stream")] [InlineData("duplicate-action")]
    [InlineData("duplicate-release")] [InlineData("early-release")]
    public void InvalidProvenanceAndCanonicalBindingsFailClosedAtProjectionBoundary(string fault)
    {
        var fixture = GeneratedFixture(); var control = Control(fixture); var pass = Action(fixture, "pass", "pass", 100, 500);
        var view = Closed(fixture, [control, pass]).Query(Query(fixture, 1000));
        var dataset = view.Dataset; var query = view.Context;
        pass = fault switch
        {
            "real" => pass with { Evidence = [pass.Evidence[0] with { Origin = "real" }] },
            "reported" => pass with { Evidence = [pass.Evidence[0] with { Method = "reported" }] },
            "missing-fields" => pass with { FieldEvidence = [] },
            "conflict" => pass with { Conflicts = [new("/payload/outcome", ["source"], "Conflicting authored outcome")] },
            "wrong-fixture" => pass with { FixtureId = "another-fixture" },
            _ => pass
        };
        if (fault == "wrong-version") query = query with { DatasetVersion = "another-version" };
        if (fault == "wrong-stream") query = query with { StreamId = "another-stream" };
        if (fault is "duplicate-release" or "early-release") dataset = dataset with { Schedule = dataset.Schedule! with
        { Releases = fault == "duplicate-release" ? [new("pass", 500), new("pass", 500)] : [new("pass", 499)] } };
        FootballRecord[] records = fault == "duplicate-action" ? [control, pass, pass] : [control, pass];
        Assert.Null(FootballStoryProjector.Project(dataset, query, records, records.Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task PreparedSparseStoreRetainsWholeMembershipAndCanonicalDependencies()
    {
        var fixture = GeneratedFixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes();
        var writer = new AzureFootballStore(blobs, indexes); var control = Control(fixture); var pass = Action(fixture, "pass", "pass", 100, 500);
        await writer.AppendAsync(Batch(fixture, 1, ReleasedContext(fixture, new PresentationRelease("pass", 1800))), default);
        await writer.AppendAsync(Batch(fixture, 2, [control, pass], close: true), default);
        var reader = new AzureFootballStore(blobs, indexes);
        Assert.Empty((await reader.QueryAsync(Query(fixture, 1799), default)).Story!.Passages);
        var passage = Assert.Single((await reader.QueryAsync(Query(fixture, 1800), default)).Story!.Passages);
        Assert.Equal(new[] { control.RecordId, pass.RecordId }, passage.Evidence.Select(e => e.RecordId));
        Assert.Equal(FootballCodec.ContentHash(pass), passage.Evidence[1].Fingerprint);
        Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.RecoveredRecordCount);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task CompleteCanonicalChelseaActionsProduce150BoundedPassagesWithExactTotals()
    {
        var path = Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", "brentford-full-v5", "manifest.json");
        var records = new List<FootballRecord>(); FootballManifest? manifest = null; FootballStream? stream = null;
        await foreach (var batch in new FullBrentfordFootballSource(path).ReadBatchesAsync(default))
        {
            manifest ??= batch.Manifest; stream ??= batch.Stream;
            records.AddRange(batch.Records.Where(r => r.Payload is not TrackingPayload));
        }
        Assert.Equal(881, records.Count(r => r.Payload is FootballActionPayload));
        var ledger = new FootballLedger();
        foreach (var batch in FootballCodec.Batches(manifest!, stream!, records)) ledger.Accept(new(batch, At("13:00")));
        var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)); var story = Assert.IsType<FootballStory>(end.Story);
        Assert.Equal(150, story.Passages.Length);
        Assert.Equal((728, 583, 145, 3, 5850000L), (story.Passages.Sum(p => p.PassAttempts), story.Passages.Sum(p => p.PassCompleted),
            story.Passages.Sum(p => p.Turnovers), story.Passages.Sum(p => p.Shots), story.Passages.Sum(p => p.DurationMs)));
        Assert.Equal(881, story.Passages.Sum(p => p.Evidence.Length));
        Assert.All(story.Passages, p => { Assert.InRange(p.DurationMs, 1, 60000); Assert.InRange(p.Evidence.Length, 1, 13); });
        Assert.Empty(ledger.Query(FullFootballSourceTests.Query(dataset, 0)).Story!.Passages);
        var first = story.Passages[0];
        Assert.Empty(ledger.Query(FullFootballSourceTests.Query(dataset, first.EndCursorMs - 1)).Story!.Passages);
        Assert.Single(ledger.Query(FullFootballSourceTests.Query(dataset, first.EndCursorMs)).Story!.Passages);
        Assert.Equal(149, ledger.Query(FullFootballSourceTests.Query(dataset, 5879999)).Story!.Passages.Length);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) ledger.Query(FullFootballSourceTests.Query(dataset, 5880000));
        watch.Stop();
        output.WriteLine(JsonSerializer.Serialize(new { StoryBytes = JsonSerializer.SerializeToUtf8Bytes(story, FootballCodec.Json).Length,
            ViewBytes = JsonSerializer.SerializeToUtf8Bytes(end, FootballCodec.Json).Length, QueryMeanMs = watch.Elapsed.TotalMilliseconds / 10,
            Passages = story.Passages.Length, Dependencies = story.Passages.Sum(p => p.Evidence.Length) }, FootballCodec.Json));
    }
}
