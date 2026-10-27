using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class GeneratedIllustrationStatisticsTests
{
    internal static TestFixture GeneratedFixture()
    {
        var fixture = Fixture();
        return fixture with { Manifest = fixture.Manifest with
        {
            Capabilities = fixture.Manifest.Capabilities.Append(FootballActionPolicy.GeneratedModelActionsV1).ToArray(),
            IdentityMappings = fixture.Manifest.IdentityMappings.Concat(new[] { "person-three", "period-two" }.Select(id => new IdentityMapping(id, "unit", JsonSerializer.SerializeToElement(id)))).ToArray()
        } };
    }
    internal static FootballRecord[] GeneratedContext(TestFixture fixture)
    {
        var records = Context(fixture);
        records[0] = records[0] with { Payload = ((FixturePayload)records[0].Payload) with { Periods = [new("period-one", 1, "First", 0, 100000), new("period-two", 2, "Second", 100000, 100000)] } };
        records[1] = records[1] with { Payload = new SquadPayload([new("person-one", "team-home", "One", 1, "starter"), new("person-three", "team-home", "Three", 3, "starter"), new("person-two", "team-away", "Two", 2, "starter")]) };
        records[2] = records[2] with { Payload = ((SchedulePayload)records[2].Payload) with { Segments = [new("period-one", 0, 100000, 0), new("period-two", 100000, 200000, 0)] } };
        return records;
    }
    internal static FootballRecord Action(TestFixture fixture, string id, string kind, long start, long end, string team = "team-home", string outcome = "completed", string period = "period-one")
    {
        var payload = new FootballActionPayload(kind, team, kind == "possession" ? null : team == "team-home" ? "person-one" : "person-two",
            kind == "pass" ? outcome == "completed" ? "person-three" : "person-two" : null, kind == "pass" ? outcome == "completed" ? team : "team-away" : null, outcome);
        return Record(fixture, id, payload, new(period, start, end, "source_interval", false)) with
        {
            Evidence = [new("source", "test-source", "authored generated action", "synthetic", "generated", "authored", At("12:00"))],
            EntityRefs = new[] { payload.TeamId, payload.PersonId, payload.RecipientId, payload.RecipientTeamId }.OfType<string>().Distinct().ToArray(),
            FieldEvidence = new[] { "/footballTime", "/payload/actionKind", "/payload/teamId", "/payload/personId", "/payload/recipientId", "/payload/recipientTeamId", "/payload/outcome", "/payload/fromSlotId", "/payload/toSlotId" }.ToDictionary(path => path, _ => new[] { "source" })
        };
    }
    private static FootballTeamIllustrationStatistics Home(FootballView view) => view.IllustrationStatistics!.Teams.Single(t => t.TeamId == "team-home");

    internal static bool SequenceAvailable(FootballView view, FootballRecord[] index, long? head = null)
    {
        var page = new FootballReplay();
        foreach (var (name, value) in new (string, object)[] { ("_view", view), ("_sequenceIndex", index), ("_highlightHead", head ?? view.Dataset.HeadSequence) })
            typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
        return (bool)typeof(FootballReplay).GetProperty("SequenceIndexAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    }
    private static (FootballView View, FootballRecord[] Index) MixedReleaseView()
    {
        var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = fixture.Manifest.Capabilities.Append("shared-event-sequences-v1").ToArray() } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture).Append(Frame(fixture, 0)).ToArray());
        var view = ledger.Query(Query(fixture, 0));
        var reports = Enumerable.Range(0, 14).Select(i => Goal(fixture) with { RecordId = "reported-" + i }).ToArray();
        var actionIds = Enumerable.Range(0, 881).Select(i => "typed-action-" + i).ToArray();
        var releases = reports.Select(r => new PresentationRelease(r.RecordId, 60000)).Concat(actionIds.Select(id => new PresentationRelease(id, 500))).ToArray();
        return (view with { GeneratedActionRecordIds = actionIds, Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with { Releases = releases } } }, reports);
    }

    [Fact]
    public void F2_MixedTypedActionReleasesAllowTheReportedSequenceIndexAndOriginalPitch()
    {
        var (view, index) = MixedReleaseView(); Assert.Equal(895, view.Dataset.Schedule!.Releases.Length); Assert.Equal(14, index.Length);
        Assert.True(SequenceAvailable(view, index));
        var presentation = MatchPitchPresenter.Project(view, [], SequenceAvailable(view, index), false);
        Assert.Equal(view.Frame, presentation.Frame); Assert.False(presentation.GeneratedLayout); Assert.DoesNotContain("Event evidence is loading", presentation.Caption);
    }

    [Theory]
    [InlineData("missing-report")] [InlineData("unknown-release")] [InlineData("duplicate-typed")] [InlineData("overlap")] [InlineData("over-cap")]
    [InlineData("missing-typed")] [InlineData("unknown-typed")] [InlineData("wrong-scope")] [InlineData("stale-head")]
    public void F2_UnknownMissingAmbiguousOrUnboundReleasesStillBlockSequences(string fault)
    {
        var (view, index) = MixedReleaseView();
        if (fault == "missing-report") index = index.Skip(1).ToArray();
        if (fault == "unknown-release") view = view with { Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with { Releases = view.Dataset.Schedule!.Releases.Append(new("unknown", 500)).ToArray() } } };
        if (fault == "duplicate-typed") view = view with { GeneratedActionRecordIds = [.. view.GeneratedActionRecordIds!, view.GeneratedActionRecordIds![0]] };
        if (fault == "overlap") view = view with { GeneratedActionRecordIds = [.. view.GeneratedActionRecordIds!, index[0].RecordId] };
        if (fault == "over-cap") view = view with { GeneratedActionRecordIds = Enumerable.Range(0, 2001).Select(i => "typed-action-" + i).ToArray() };
        if (fault == "missing-typed") view = view with { GeneratedActionRecordIds = null };
        if (fault == "unknown-typed") view = view with { GeneratedActionRecordIds = view.GeneratedActionRecordIds!.Skip(1).ToArray() };
        if (fault == "wrong-scope") index[0] = index[0] with { DatasetVersion = "unbound-version" };
        Assert.False(SequenceAvailable(view, index, fault == "stale-head" ? view.Dataset.HeadSequence - 1 : null));
    }

    [Fact]
    public void F2_OldReportOnlySequenceIndexRetainsItsGuard()
    {
        var (view, index) = MixedReleaseView(); view = view with { GeneratedActionRecordIds = null, Dataset = view.Dataset with
        {
            Manifest = view.Dataset.Manifest with { Capabilities = ["shared-event-sequences-v1"] },
            Schedule = view.Dataset.Schedule! with { Releases = view.Dataset.Schedule!.Releases.Where(r => index.Any(e => e.RecordId == r.RecordId)).ToArray() }
        } };
        Assert.True(SequenceAvailable(view, index)); Assert.False(SequenceAvailable(view, index.Skip(1).ToArray()));
    }

    [Fact]
    public void F2_TypeMembershipPrecedesCompletionButNeverIncludesUncommittedUnresolvedOrUnknownKnowledge()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var future = Action(fixture, "future-pass", "pass", 50000, 60000);
        var unresolved = Action(fixture, "unresolved", "pass", 50000, 60000) with { Revision = 2, SupersedesRevision = 1 };
        var unknown = Action(fixture, "unknown-availability", "pass", 50000, 60000) with { Evidence = [future.Evidence[0] with { AvailableAtUtc = null }] };
        Add(ledger, fixture, 2, future, unresolved, unknown);
        var opening = ledger.Query(Query(fixture, 0));
        Assert.Equal(new[] { "future-pass", "unknown-availability" }, opening.GeneratedActionRecordIds);
        Assert.Equal(0, Home(opening).PassAttempts); Assert.Empty(opening.Events); Assert.Empty(opening.Possessions);
        Assert.Empty(ledger.Query(Query(fixture, 0) with { ApplicationSequence = 1 }).GeneratedActionRecordIds!);
        Assert.Equal(new[] { "future-pass" }, ledger.Query(Query(fixture, 0) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") }).GeneratedActionRecordIds);
        Add(ledger, fixture, 3, future with { Revision = 2, SupersedesRevision = 1, RecordState = "withdrawn", WithdrawalReason = "Withdrawn generated action" });
        Assert.Contains("future-pass", ledger.Query(Query(fixture, 0)).GeneratedActionRecordIds!);
        Assert.Null(Ledger(Fixture()).Query(Query(Fixture(), 0)).GeneratedActionRecordIds);
    }

    [Fact]
    public void F3_WithdrawnGeneratedActionRetainsClassificationWithZeroCountAndFourteenReportPitchReady()
    {
        var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = fixture.Manifest.Capabilities.Append("shared-event-sequences-v1").ToArray() } };
        var reports = Enumerable.Range(0, 14).Select(i => Goal(fixture) with { RecordId = "reported-" + i }).ToArray();
        var action = Action(fixture, "withdrawn-action", "pass", 100, 500); var context = GeneratedContext(fixture);
        context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with
        { Releases = reports.Select(r => new PresentationRelease(r.RecordId, 60000)).Append(new(action.RecordId, 500)).ToArray() } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, context.Append(Frame(fixture, 0)).ToArray());
        Add(ledger, fixture, 2, reports.Append(action).ToArray());
        Add(ledger, fixture, 3, action with { Revision = 2, SupersedesRevision = 1, RecordState = "withdrawn", WithdrawalReason = "Generated action withdrawn" });
        var current = ledger.Query(Query(fixture, 1000)); var end = ledger.Query(Query(fixture, 100000));
        Assert.Equal(0, Home(current).PassAttempts); Assert.Equal(0, Home(end).PassAttempts);
        Assert.Equal(new[] { action.RecordId }, current.GeneratedActionRecordIds); Assert.Equal(14, end.Events.Length);
        Assert.True(SequenceAvailable(current, end.Events)); Assert.True(SequenceAvailable(end, end.Events));
    }

    [Fact]
    public void CompletionBoundaryCountsOutcomeOnlyAtEndAndKeepsReportedFactsSeparate()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "pass", "pass", 100, 500, outcome: "turnover"), Action(fixture, "control", "possession", 0, 1000, outcome: "controlled"), Action(fixture, "shot", "shot", 1500, 2000, outcome: "goal"));
        var before = ledger.Query(Query(fixture, 499)); Assert.Equal(0, Home(before).PassAttempts); Assert.Null(Home(before).PassCompletionPercent); Assert.Null(Home(before).ControlSharePercent);
        var exact = ledger.Query(Query(fixture, 500)); Assert.Equal(1, Home(exact).PassAttempts); Assert.Equal(0m, Home(exact).PassCompletionPercent); Assert.Equal(0, Home(exact).ControlledMs);
        Assert.Equal(1, Home(ledger.Query(Query(fixture, 501))).PassAttempts);
        Assert.Equal(1000, Home(ledger.Query(Query(fixture, 1000))).ControlledMs); Assert.Equal(100m, Home(ledger.Query(Query(fixture, 1000))).ControlSharePercent);
        Assert.Equal(0m, ledger.Query(Query(fixture, 1000)).IllustrationStatistics!.Teams.Single(t => t.TeamId == "team-away").ControlSharePercent);
        Assert.Equal(0, Home(ledger.Query(Query(fixture, 1999))).Shots); var finish = ledger.Query(Query(fixture, 2000)); Assert.Equal(1, Home(finish).Shots);
        Assert.Empty(finish.Events); Assert.Empty(finish.Possessions); Assert.Equal(0, finish.HomeGoals); Assert.Equal(0, finish.AwayGoals);
    }

    [Fact]
    public void BothPeriodsRemainCumulativeAndUnfinishedControlDoesNotEnterDenominator()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "first-control", "possession", 0, 1000, outcome: "controlled"), Action(fixture, "first-pass", "pass", 200, 400),
            Action(fixture, "second-control", "possession", 0, 3000, "team-away", "controlled", "period-two"), Action(fixture, "second-pass", "pass", 200, 400, period: "period-two"));
        var half = ledger.Query(Query(fixture, 100000)); Assert.Equal(1, Home(half).PassCompleted); Assert.Equal(1000, half.IllustrationStatistics!.TotalClassifiedControlMs);
        var running = ledger.Query(Query(fixture, 102999)); Assert.Equal(2, Home(running).PassCompleted); Assert.Equal(1000, running.IllustrationStatistics!.TotalClassifiedControlMs);
        var finished = ledger.Query(Query(fixture, 103000)); Assert.Equal(4000, finished.IllustrationStatistics!.TotalClassifiedControlMs); Assert.Equal(25m, Home(finished).ControlSharePercent);
        Assert.Equal(1, Home(ledger.Query(Query(fixture, 99999))).PassAttempts); // Seeking backwards is a fresh deterministic projection.
        Assert.Equal(JsonSerializer.Serialize(finished, FootballCodec.Json), JsonSerializer.Serialize(ledger.Query(Query(fixture, 103000)), FootballCodec.Json));
    }

    [Fact]
    public void RevisionSelectionPrecedesCompletionAndHonorsKnowledgeApplicationWithdrawalAndDuplicates()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var original = Action(fixture, "pass", "pass", 100, 500); Add(ledger, fixture, 2, original);
        var corrected = original with { Revision = 2, SupersedesRevision = 1, FootballTime = original.FootballTime! with { EndMs = 1500 }, Evidence = [original.Evidence[0] with { AvailableAtUtc = At("12:05") }] };
        Add(ledger, fixture, 3, corrected); Add(ledger, fixture, 4, original);
        Assert.Equal(0, Home(ledger.Query(Query(fixture, 1000))).PassAttempts); Assert.Equal(1, Home(ledger.Query(Query(fixture, 1000) with { ApplicationSequence = 2 })).PassAttempts);
        Assert.Equal(1, Home(ledger.Query(Query(fixture, 1000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") })).PassAttempts);
        Assert.Equal(0, Home(ledger.Query(Query(fixture, 1499))).PassAttempts); Assert.Equal(1, Home(ledger.Query(Query(fixture, 1500))).PassAttempts);
        var withdrawal = corrected with { Revision = 3, SupersedesRevision = 2, RecordState = "withdrawn", WithdrawalReason = "Generated action withdrawn", Evidence = [corrected.Evidence[0] with { AvailableAtUtc = At("12:10") }] };
        Add(ledger, fixture, 5, withdrawal); Add(ledger, fixture, 6, original);
        Assert.False(ledger.Accept(new(Batch(fixture, 6, [original]), At("13:00")))); Assert.Equal(0, Home(ledger.Query(Query(fixture, 2000))).PassAttempts);
        Assert.Equal(1, Home(ledger.Query(Query(fixture, 2000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:06") })).PassAttempts);
    }

    [Fact]
    public void PendingParentUnknownAvailabilityAndPresentationReleaseRetainSharedRules()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); var context = GeneratedContext(fixture);
        context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Releases = [new("pass", 1800)] } };
        Add(ledger, fixture, 1, context);
        var original = Action(fixture, "pass", "pass", 100, 500); var child = original with { Revision = 2, SupersedesRevision = 1, Evidence = [original.Evidence[0] with { AvailableAtUtc = null }] };
        Add(ledger, fixture, 2, child); Assert.Equal(0, Home(ledger.Query(Query(fixture, 2000))).PassAttempts); Assert.Equal(1, Assert.Single(ledger.Catalog()).PendingRecords);
        Add(ledger, fixture, 3, original); Assert.Equal(0, Assert.Single(ledger.Catalog()).PendingRecords);
        Assert.Equal(0, Home(ledger.Query(Query(fixture, 1799))).PassAttempts); Assert.Equal(1, Home(ledger.Query(Query(fixture, 1800))).PassAttempts);
        Assert.Equal(1, Home(ledger.Query(Query(fixture, 2000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:06") })).PassAttempts);
    }

    [Theory]
    [InlineData("no-capability")] [InlineData("real")] [InlineData("mixed")] [InlineData("reported")] [InlineData("minute")] [InlineData("instant")]
    [InlineData("unknown-person")] [InlineData("unscoped")] [InlineData("outcome")] [InlineData("self-pass")] [InlineData("missing-fields")]
    public void MalformedOrFactualActionsAreRejectedBySharedCodec(string mutation)
    {
        var fixture = GeneratedFixture(); var record = Action(fixture, "pass", "pass", 100, 500); var payload = (FootballActionPayload)record.Payload;
        if (mutation == "no-capability") fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = [] } };
        record = mutation switch
        {
            "real" => record with { Evidence = [record.Evidence[0] with { Origin = "real" }] },
            "mixed" => record with { Evidence = [record.Evidence[0], record.Evidence[0] with { EvidenceId = "real", Origin = "real" }] },
            "reported" => record with { Evidence = [record.Evidence[0] with { Method = "reported", ClaimStatus = "reported" }] },
            "minute" => record with { FootballTime = record.FootballTime! with { Precision = "reported_minute" } },
            "instant" => record with { FootballTime = record.FootballTime! with { EndMs = 100 } },
            "unknown-person" => record with { Payload = payload with { PersonId = "unknown" } },
            "unscoped" => record with { EntityRefs = [] },
            "outcome" => record with { Payload = payload with { Outcome = "turnover" } },
            "self-pass" => record with { Payload = payload with { RecipientId = payload.PersonId } },
            "missing-fields" => record with { FieldEvidence = [] },
            _ => record
        };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 1, [record])));
    }

    [Theory] [InlineData("wrong-team")] [InlineData("overlap")] [InlineData("outside-period")]
    public void RosterAndIntervalContradictionsMakeStatisticsUnavailable(string mutation)
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        var action = Action(fixture, "pass", "pass", 100, mutation == "outside-period" ? 100001 : 500);
        if (mutation == "wrong-team") action = action with { Payload = ((FootballActionPayload)action.Payload) with { PersonId = "person-two" }, EntityRefs = ["team-home", "person-three", "person-two"] };
        Add(ledger, fixture, 2, action);
        if (mutation == "overlap") Add(ledger, fixture, 3, Action(fixture, "control-a", "possession", 0, 1000, outcome: "controlled"), Action(fixture, "control-b", "possession", 500, 1500, "team-away", "controlled"));
        Assert.Null(ledger.Query(Query(fixture, 150000)).IllustrationStatistics);
    }

    [Fact]
    public void ExistingDatasetsHaveNoIllustrationStatisticsAndPayloadRoundTrips()
    {
        var old = Fixture(); Assert.Null(Ledger(old).Query(Query(old, 1000)).IllustrationStatistics);
        var fixture = GeneratedFixture(); var batch = Batch(fixture, 1, [Action(fixture, "pass", "pass", 100, 500)]);
        var decoded = Assert.Single(FootballCodec.Decode(FootballCodec.Encode(batch)).Records);
        Assert.Equal("football_action", decoded.RecordType); Assert.IsType<FootballActionPayload>(decoded.Payload);
    }

    [Fact]
    public async Task PreparedReadsRetainActionsAndRevisionsAcrossRestartWithoutJournalRecovery()
    {
        var fixture = GeneratedFixture(); var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var writer = new AzureFootballStore(blobs, indexes);
        var original = Action(fixture, "pass", "pass", 100, 500);
        await writer.AppendAsync(Batch(fixture, 1, GeneratedContext(fixture).Append(original).ToArray()), default);
        var withdrawal = original with { Revision = 2, SupersedesRevision = 1, RecordState = "withdrawn", WithdrawalReason = "Withdrawn" };
        await writer.AppendAsync(Batch(fixture, 2, [withdrawal], true), default);
        var reader = new AzureFootballStore(blobs, indexes);
        Assert.Equal(0, Home(await reader.QueryAsync(Query(fixture, 1000), default)).PassAttempts);
        var previous = await reader.QueryAsync(Query(fixture, 1000) with { ApplicationSequence = 1 }, default);
        Assert.Equal(1, Home(previous).PassAttempts); Assert.Equal(new[] { "pass" }, previous.GeneratedActionRecordIds);
        Assert.Equal(new[] { "pass" }, (await reader.QueryAsync(Query(fixture, 1000), default)).GeneratedActionRecordIds);
        Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.RecoveredRecordCount); Assert.Equal(0, reader.PayloadReadCount);
        var context = PreparedContext(blobs, (await indexes.ReadAsync(fixture.Manifest.DatasetId, default))!.Value, fixture.Stream.StreamId);
        Assert.Equal(2, context.Page.Groups.Single(g => g.RecordId == "pass").Members.Length);
        Assert.InRange(context.RawBytes, 1, AzureFootballStore.MaxExpandedPageBytes);
    }

    [Fact]
    public async Task RetainedVersionsFitEightStreamBoundAndNinthIsRejectedWithoutRemovingHistory()
    {
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes);
        for (var i = 0; i < AzureFootballStore.MaxStreamsPerDataset; i++)
        {
            var fixture = Fixture("version-" + i, "stream-" + i); await store.AppendAsync(Batch(fixture, 1, Context(fixture), true), default);
        }
        Assert.Equal(8, (await new AzureFootballStore(blobs, indexes).CatalogAsync(default)).Length);
        var ninth = Fixture("version-9", "stream-9"); await Assert.ThrowsAsync<RecordingValidationException>(() => store.AppendAsync(Batch(ninth, 1, Context(ninth), true), default));
        Assert.Equal(8, (await new AzureFootballStore(blobs, indexes).CatalogAsync(default)).Length);
    }

    internal static (FootballReadPage Page, int RawBytes) PreparedContext(MemoryReplayBlobs blobs, FootballDatasetIndex root, string stream)
    {
        T Read<T>(FootballReadReference reference) => JsonSerializer.Deserialize<T>(FootballTransport.Expand(blobs.Content[reference.Path], AzureFootballStore.MaxExpandedPageBytes), FootballCodec.Json)!;
        var catalog = Read<FootballReadCatalog>(root.DerivedReadView!); var index = Read<FootballReadIndex>(catalog.Streams.Single(s => s.Dataset.Stream.StreamId == stream).Index);
        var bytes = FootballTransport.Expand(blobs.Content[index.Context.Path], AzureFootballStore.MaxExpandedPageBytes);
        return (JsonSerializer.Deserialize<FootballReadPage>(bytes, FootballCodec.Json)!, bytes.Length);
    }

#pragma warning disable BL0006 // Inspect the actual Razor render tree without a browser substitute host.
    private sealed class RenderableStatistics : IllustrationStatistics { public void Render(RenderTreeBuilder builder) => base.BuildRenderTree(builder); }
    [Fact]
    public void PanelLabelsGeneratedScopeAcceptedClockAndUnavailableVersusZeroPercent()
    {
        var fixture = GeneratedFixture(); var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "pass", "pass", 100, 500, outcome: "turnover"));
        var component = new RenderableStatistics(); typeof(IllustrationStatistics).GetProperty("View")!.SetValue(component, ledger.Query(Query(fixture, 1000)));
        using var tree = new RenderTreeBuilder(); component.Render(tree); var frames = tree.GetFrames();
        var text = string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.FrameType == RenderTreeFrameType.Markup ? frame.MarkupContent : ""));
        Assert.Contains("Generated illustration stats", text); Assert.Contains("At accepted replay 00:01", text); Assert.Contains("0%", text); Assert.Contains("—", text);
        Assert.Contains("completed control spells", text); Assert.Contains("not the actual match", text);
        typeof(IllustrationStatistics).GetProperty("View")!.SetValue(component, Ledger(Fixture()).Query(Query(Fixture(), 1000)));
        using var old = new RenderTreeBuilder(); component.Render(old); Assert.Equal(0, old.GetFrames().Count);
    }
#pragma warning restore BL0006
}

// Full local artifacts are excluded from CI alongside the existing FullFootballSourceTests.
public sealed class GeneratedIllustrationFullSourceTests(ITestOutputHelper output)
{
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F2_AlreadyImportedV5PreparedViewsExposeOnlyTypedIdsAndPreserveFullReplay()
    {
        var root = LocalPaths.FindRoot(); var store = new ChunkedLocalFootballStore(Path.Combine(root, ".local", "football-full-v1"));
        var dataset = (await store.CatalogAsync(default)).Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal));
        Assert.True(dataset.Closed); Assert.Equal(979, dataset.HeadSequence);
        var end = await store.QueryAsync(FullFootballSourceTests.Query(dataset, 5880000), default);
        var opening = await store.QueryAsync(FullFootballSourceTests.Query(dataset, 0), default);
        Assert.NotNull(opening.Frame); Assert.NotNull(end.Frame); Assert.Equal(22, opening.Frame.Players.Length); Assert.Equal(22, end.Frame.Players.Length);
        Assert.Equal(881, end.GeneratedActionRecordIds!.Length); Assert.Equal(end.GeneratedActionRecordIds, opening.GeneratedActionRecordIds);
        Assert.Equal(14, end.Events.Length); Assert.True(GeneratedIllustrationStatisticsTests.SequenceAvailable(opening, end.Events));
        Assert.True(GeneratedIllustrationStatisticsTests.SequenceAvailable(end, end.Events));
        Assert.Equal(728, end.IllustrationStatistics!.Teams.Sum(t => t.PassAttempts)); Assert.Equal(3, end.IllustrationStatistics.Teams.Sum(t => t.Shots));
        Assert.Equal(0, opening.IllustrationStatistics!.Teams.Sum(t => t.PassAttempts)); Assert.Equal(0, store.Journal.MetadataReadBytes);
        var receipt = JsonSerializer.Serialize(new { dataset.Manifest.DatasetVersion, dataset.HeadSequence,
            openingViewBytes = JsonSerializer.SerializeToUtf8Bytes(opening, FootballCodec.Json).Length,
            endViewBytes = JsonSerializer.SerializeToUtf8Bytes(end, FootballCodec.Json).Length,
            typedIdBytes = JsonSerializer.SerializeToUtf8Bytes(end.GeneratedActionRecordIds, FootballCodec.Json).Length,
            generatedIds = end.GeneratedActionRecordIds.Length, reportedEvents = end.Events.Length }, FootballCodec.Json);
        output.WriteLine(receipt); var directory = Path.Combine(root, "output", "generated-replay-statistics", "backend"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "rev2-existing-view.json"), receipt);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task V5FullSourceEmitsActionsOncePreservesReportsAndFitsPreparedContext()
    {
        var root = LocalPaths.FindRoot(); var path = Path.Combine(root, ".local", "shared-inputs", "brentford-full-v5", "manifest.json");
        var records = new List<FootballRecord>(); FootballManifest? manifest = null; FootballStream? stream = null; var frames = 0;
        await foreach (var batch in new FullBrentfordFootballSource(path).ReadBatchesAsync(default))
        {
            FootballCodec.Validate(batch); manifest ??= batch.Manifest; stream ??= batch.Stream;
            Assert.Equal(manifest.DatasetVersion, batch.Manifest.DatasetVersion); frames += batch.Records.Count(r => r.Payload is TrackingPayload);
            records.AddRange(batch.Records.Where(r => r.Payload is not TrackingPayload));
        }
        Assert.Equal(58801, frames); Assert.StartsWith("brent-full-v5-", manifest!.DatasetVersion); Assert.Equal(6, manifest.ManifestRevision);
        Assert.Equal(881, records.Count(r => r.Payload is FootballActionPayload)); Assert.Equal(14, records.Count(r => r.Payload is MatchEventPayload));
        Assert.Equal(records.Count, records.Select(r => r.RecordId).Distinct().Count());
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes(); var store = new AzureFootballStore(blobs, indexes);
        foreach (var batch in FootballCodec.Batches(manifest, stream!, records)) await store.AppendAsync(batch, default);
        var reader = new AzureFootballStore(blobs, indexes); var dataset = Assert.Single(await reader.CatalogAsync(default));
        var end = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, 5880000), default); var stats = Assert.IsType<FootballIllustrationStatistics>(end.IllustrationStatistics);
        Assert.Equal(881, end.GeneratedActionRecordIds!.Length); Assert.Equal(881, end.GeneratedActionRecordIds.Distinct(StringComparer.Ordinal).Count());
        Assert.True(GeneratedIllustrationStatisticsTests.SequenceAvailable(end, end.Events));
        var opening = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, 0), default);
        Assert.Equal(end.GeneratedActionRecordIds, opening.GeneratedActionRecordIds); Assert.Equal(0, opening.IllustrationStatistics!.Teams.Sum(t => t.PassAttempts));
        Assert.True(GeneratedIllustrationStatisticsTests.SequenceAvailable(opening, end.Events));
        var home = stats.Teams.Single(t => t.TeamId.EndsWith("brentford", StringComparison.Ordinal)); var away = stats.Teams.Single(t => t.TeamId.EndsWith("chelsea", StringComparison.Ordinal));
        Assert.Equal((361, 290, 3, 2905300L), (home.PassAttempts, home.PassCompleted, home.Shots, home.ControlledMs));
        Assert.Equal((367, 293, 0, 2944700L), (away.PassAttempts, away.PassCompleted, away.Shots, away.ControlledMs));
        Assert.Equal(5850000, stats.TotalClassifiedControlMs); Assert.Equal(14, end.Events.Length); Assert.Empty(end.Possessions); Assert.Equal(0, end.HomeGoals); Assert.Equal(0, end.AwayGoals);
        var half = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, 2820000), default);
        Assert.Equal((177, 142, 1412000L), (half.IllustrationStatistics!.Teams[0].PassAttempts, half.IllustrationStatistics.Teams[0].PassCompleted, half.IllustrationStatistics.Teams[0].ControlledMs));
        Assert.Equal((175, 140, 1408000L), (half.IllustrationStatistics.Teams[1].PassAttempts, half.IllustrationStatistics.Teams[1].PassCompleted, half.IllustrationStatistics.Teams[1].ControlledMs));
        var beforeFinal = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, 5879999), default);
        Assert.Equal(2914700L, beforeFinal.IllustrationStatistics!.Teams[1].ControlledMs);
        var first = records.First(r => r.Payload is FootballActionPayload { ActionKind: "pass" }); var completedAt = first.FootballTime!.EndMs!.Value + (first.FootballTime.PeriodId.EndsWith("second", StringComparison.Ordinal) ? 2820000 : 0);
        var before = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, completedAt - 1), default); var exact = await reader.QueryAsync(FullFootballSourceTests.Query(dataset, completedAt), default);
        Assert.Equal(before.IllustrationStatistics!.Teams.Sum(t => t.PassAttempts) + 1, exact.IllustrationStatistics!.Teams.Sum(t => t.PassAttempts));
        var context = GeneratedIllustrationStatisticsTests.PreparedContext(blobs, (await indexes.ReadAsync(manifest.DatasetId, default))!.Value, stream!.StreamId);
        Assert.Equal(881, context.Page.Groups.Count(g => g.Members.Any(m => m.Record.NonTracking?.Payload is FootballActionPayload)));
        Assert.InRange(context.RawBytes, 1, AzureFootballStore.MaxExpandedPageBytes); Assert.Equal(0, reader.MetadataReadBytes); Assert.Equal(0, reader.RecoveredRecordCount);
        var receipt = JsonSerializer.Serialize(new { actionCount = 881, frames, contextBytes = context.RawBytes, maxContextBytes = AzureFootballStore.MaxExpandedPageBytes, stats }, FootballCodec.Json);
        output.WriteLine(receipt);
        var receiptDirectory = Path.Combine(root, "output", "generated-replay-statistics", "backend"); Directory.CreateDirectory(receiptDirectory);
        await File.WriteAllTextAsync(Path.Combine(receiptDirectory, "verification.json"), receipt);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("hash")] [InlineData("schema")] [InlineData("membership")] [InlineData("interval")]
    public async Task SourceRejectsCorruptArtifactAndRehashedInvalidActions(string mutation)
    {
        var root = LocalPaths.FindRoot(); var original = Path.Combine(root, ".local", "shared-inputs", "brentford-full-v5"); using var directory = new TestDirectory();
        foreach (var file in Directory.GetFiles(original, "*.json")) File.Copy(file, Path.Combine(directory.Path, Path.GetFileName(file)));
        File.Copy(Path.Combine(original, "frames-000000.json.gz"), Path.Combine(directory.Path, "frames-000000.json.gz"));
        var artifactPath = Path.Combine(directory.Path, "generated-actions.json"); var artifact = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath))!.AsObject();
        if (mutation == "schema") artifact["fixtureId"] = "wrong-fixture";
        else if (mutation is "membership" or "interval")
        {
            var row = artifact["rows"]!.AsArray().First(r => r!["actionKind"]!.GetValue<string>() == "pass")!;
            if (mutation == "membership") row["team"] = row["team"]!.GetValue<string>() == "brentford" ? "chelsea" : "brentford";
            else row["endMs"] = row["startMs"]!.DeepClone();
        }
        else artifact["durationMs"] = 1;
        var bytes = System.Text.Encoding.UTF8.GetBytes(artifact.ToJsonString()); await File.WriteAllBytesAsync(artifactPath, bytes);
        if (mutation != "hash")
        {
            var manifestPath = Path.Combine(directory.Path, "manifest.json"); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            manifest["generatedActions"]!["sha256"] = ReplayBatchCodec.Hash(bytes); manifest["hashes"]!["generated-actions.json"] = ReplayBatchCodec.Hash(bytes);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        }
        await Assert.ThrowsAsync<RecordingValidationException>(async () =>
        {
            await foreach (var _ in new FullBrentfordFootballSource(Path.Combine(directory.Path, "manifest.json")).ReadBatchesAsync(default)) Assert.Fail("Invalid artifact must fail before the first published batch.");
        });
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task PriorV4SourceRemainsCompatibleWithUnavailableGeneratedStatistics()
    {
        var path = Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", "brentford-full-v4", "manifest.json");
        await foreach (var batch in new FullBrentfordFootballSource(path).ReadBatchesAsync(default))
        {
            Assert.Equal(5, batch.Manifest.ManifestRevision); Assert.StartsWith("brent-full-v4-", batch.Manifest.DatasetVersion);
            Assert.DoesNotContain(batch.Records, r => r.Payload is FootballActionPayload); var ledger = new FootballLedger(); ledger.Accept(new(batch, At("13:00")));
            var dataset = Assert.Single(ledger.Catalog()); Assert.Null(ledger.Query(FullFootballSourceTests.Query(dataset, 0)).IllustrationStatistics); break;
        }
    }
}
