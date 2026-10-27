using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect the real component tree without a substitute browser host.
public sealed class ReplayRecapTests(ITestOutputHelper output)
{
    private const long Half = 2820000, End = 5880000;
    private static FootballView View(long cursor, string version = "version-one", string stream = "stream-one")
    {
        var fixture = Fixture(version, stream); var view = Ledger(fixture).Query(Query(fixture, 0));
        var periods = new PeriodDefinition[] { new("period-one", 1, "First half", 0, null), new("period-two", 2, "Second half", 2700000, null) };
        var goal = Goal(fixture) with { FootballTime = new("period-one", 60000, 60000, "source_exact", true, "1") };
        var schedule = view.Dataset.Schedule! with { Segments = [new("period-one", 0, Half, 0), new("period-two", Half, End, 0)], Releases = [new(goal.RecordId, 60000)] };
        return view with { Context = view.Context with { CursorMs = cursor }, PeriodId = cursor < Half ? "period-one" : "period-two", LocalMs = cursor < Half ? cursor : cursor - Half,
            Dataset = view.Dataset with { Closed = true, PendingRecords = 0, Fixture = view.Dataset.Fixture! with { Periods = periods }, Schedule = schedule,
                Manifest = view.Dataset.Manifest with { Capabilities = ["match_event", "complete-existing-generated-100ms-model", FootballActionPolicy.GeneratedModelActionsV1],
                    Sources = [.. view.Dataset.Manifest.Sources, new("generated-model-actions", "actions.json", new string('a', 64))] } },
            Events = cursor >= 60000 ? [goal] : [], HomeGoals = 0, AwayGoals = 0,
            IllustrationStatistics = new([new("team-home", 100, 80, 3, 300000, 50m), new("team-away", 120, 90, 1, 300000, 50m)], 600000) };
    }
    private static FootballView Evidence(FootballView view)
    {
        var boundary = ReplayRecap.BoundaryFor(view)!; var cursor = boundary.EvidenceCursorMs;
        return view with { Context = view.Context with { CursorMs = cursor }, PeriodId = cursor < Half ? "period-one" : "period-two", LocalMs = cursor < Half ? cursor : cursor - Half };
    }
    [Theory]
    [InlineData(Half - 1, null)] [InlineData(Half, ReplayRecapKind.HalfTime)] [InlineData(Half + 1, ReplayRecapKind.HalfTime)]
    [InlineData(End - 1, ReplayRecapKind.HalfTime)] [InlineData(End, ReplayRecapKind.FullTime)] [InlineData(End + 1, null)]
    public void ReachedBoundariesComeFromTheSchedule(long cursor, ReplayRecapKind? expected)
    {
        var boundary = ReplayRecap.BoundaryFor(View(cursor)); Assert.Equal(expected, boundary?.Kind);
        if (boundary?.Kind == ReplayRecapKind.FullTime) Assert.Equal(5760000, boundary.MatchClockMs);
    }
    [Fact]
    public void NonstandardPeriodDurationsAndDisplayClockDoNotUseNominalFortyFiveMinutes()
    {
        var view = View(Half); var schedule = view.Dataset.Schedule!;
        view = view with { Context = view.Context with { CursorMs = 2900000 }, Dataset = view.Dataset with { Schedule = schedule with { Segments = [new("period-one", 0, 2900000, 0), new("period-two", 2900000, 6100000, 0)] } } };
        var boundary = ReplayRecap.BoundaryFor(view)!; Assert.Equal(2900000, boundary.CursorMs); Assert.Equal(2900000, boundary.MatchClockMs);
    }
    [Fact]
    public void ValidatedFirstPeriodActionsFinishingExactlyAtBoundaryAreIncludedAndSecondPeriodActionsAreNot()
    {
        var fixture = GeneratedIllustrationStatisticsTests.GeneratedFixture();
        fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = [.. fixture.Manifest.Capabilities, "complete-existing-generated-100ms-model"],
            Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "actions.json", new string('a', 64))] } };
        var firstPass = GeneratedIllustrationStatisticsTests.Action(fixture, "first-pass", "pass", 99900, 100000);
        var firstControl = GeneratedIllustrationStatisticsTests.Action(fixture, "first-control", "possession", 0, 100000, outcome: "controlled");
        var secondPass = GeneratedIllustrationStatisticsTests.Action(fixture, "second-pass", "pass", 0, 1, period: "period-two");
        var records = GeneratedIllustrationStatisticsTests.GeneratedContext(fixture).Concat(new[] { firstPass, firstControl, secondPass }).ToArray();
        var ledger = new FootballLedger(); ledger.Accept(new(Batch(fixture, 1, records, close: true), DateTimeOffset.UtcNow));
        var before = ledger.Query(Query(fixture, 99999)); var exact = ledger.Query(Query(fixture, 100000)); var after = ledger.Query(Query(fixture, 100001));
        Assert.Equal(0, before.IllustrationStatistics!.Teams[0].PassAttempts); Assert.Equal(1, exact.IllustrationStatistics!.Teams[0].PassAttempts);
        Assert.Equal(2, after.IllustrationStatistics!.Teams[0].PassAttempts); Assert.Equal(100000, exact.IllustrationStatistics.TotalClassifiedControlMs);
        var card = ReplayRecap.Project(exact, exact, MatchAudienceMode.Analyst)!;
        Assert.NotNull(card); Assert.Contains("1/1", card.GeneratedSummary); Assert.Equal(100000, card.Action.Boundary.EvidenceCursorMs);
        // Zero-duration or negative-start second-half actions cannot pass the same ledger validator.
        var invalidLedger = new FootballLedger();
        var invalid = secondPass with { FootballTime = secondPass.FootballTime! with { StartMs = 0, EndMs = 0 } };
        Assert.Throws<RecordingValidationException>(() => invalidLedger.Accept(new(Batch(fixture, 1,
            GeneratedIllustrationStatisticsTests.GeneratedContext(fixture).Append(invalid).ToArray(), close: true), DateTimeOffset.UtcNow)));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SecondPeriodOpeningReportsMakeThisBoundedHalfTimeRecapUnavailable(bool possession)
    {
        var v = View(Half); var record = v.Events[0] with { RecordId = "second-opening", FootballTime = new("period-two", 0, 0, "source_exact", true, "45") };
        v = v with { Dataset = v.Dataset with { Schedule = v.Dataset.Schedule! with { Releases = [.. v.Dataset.Schedule!.Releases, new(record.RecordId, Half)] } } };
        v = possession ? v with { Possessions = [record with { Payload = new PossessionPayload("person-one", "team-home", "a", "b", true, true, 0, "dependency") }] }
            : v with { Events = [.. v.Events, record] };
        Assert.Null(ReplayRecap.Project(v, v));
    }
    [Theory]
    [InlineData("open")] [InlineData("pending")] [InlineData("partial")] [InlineData("no-complete")] [InlineData("local-offset")]
    [InlineData("overlap")] [InlineData("version")] [InlineData("head")] [InlineData("policy")] [InlineData("fixture")] [InlineData("live")]
    public void PartialOpenAndMismatchedAuthorityCannotBeCalledFullTime(string fault)
    {
        var v = View(End); var d = v.Dataset; var s = d.Schedule!;
        v = fault switch {
            "open" => v with { Dataset = d with { Closed = false } }, "pending" => v with { Dataset = d with { PendingRecords = 1 } },
            "partial" => v with { Dataset = d with { Schedule = s with { Segments = [s.Segments[0]] } } },
            "no-complete" => v with { Dataset = d with { Manifest = d.Manifest with { Capabilities = ["match_event", FootballActionPolicy.GeneratedModelActionsV1] } } },
            "local-offset" => v with { Dataset = d with { Schedule = s with { Segments = [s.Segments[0] with { LocalStartMs = 100 }, s.Segments[1]] } } },
            "overlap" => v with { Dataset = d with { Schedule = s with { Segments = [s.Segments[0], s.Segments[1] with { CursorStartMs = Half - 1 }] } } },
            "version" => v with { Context = v.Context with { DatasetVersion = "other-version" } },
            "head" => v with { Context = v.Context with { ApplicationSequence = d.HeadSequence + 1 } },
            "policy" => v with { Context = v.Context with { PresentationPolicyRevision = 99 } },
            "fixture" => v with { Context = v.Context with { FixtureId = "other-fixture" } },
            "live" => v with { Dataset = d with { Stream = d.Stream with { DeliveryMode = "live" } } }, _ => v };
        Assert.Null(ReplayRecap.BoundaryFor(v)); Assert.Null(ReplayRecap.Project(v, v));
    }
    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void ModeChangesUseIdenticalFactsAndLocalizedGeneratedQualifications(MatchLanguage language)
    {
        var v = View(End); var fan = ReplayRecap.Project(v, v, MatchAudienceMode.Fan, language)!;
        var analyst = ReplayRecap.Project(v, v, MatchAudienceMode.Analyst, language)!;
        Assert.Equal(fan.Action, analyst.Action); Assert.Equal(fan.ReportedSummary, analyst.ReportedSummary);
        Assert.Contains("Home 1–0 Away", fan.ReportedSummary); Assert.Contains("80/100", analyst.GeneratedSummary); Assert.Contains("90/120", analyst.GeneratedSummary);
        Assert.Null(fan.ReportedDetail); Assert.NotNull(analyst.ReportedDetail); Assert.NotEqual(fan.GeneratedSummary, analyst.GeneratedSummary);
        Assert.Equal(ReplayText.Get(language, "RecapFullTime"), fan.Heading); Assert.Contains("10:00", fan.ControlNote);
        Assert.DoesNotContain("Recap", fan.ControlNote); Assert.NotEmpty(ReplayRecap.Question(fan.Action, language));
    }
    [Fact]
    public void GoalsAreReportedCountsRatherThanTheConfirmedOnlyTransportScoreAndDisallowedGoalsAreExcluded()
    {
        var v = View(End); var goal = v.Events[0]; var disallowed = goal with { RecordId = "disallowed-goal", Payload = ((MatchEventPayload)goal.Payload) with { Decision = "disallowed" } };
        v = v with { Events = [goal, disallowed], Dataset = v.Dataset with { Schedule = v.Dataset.Schedule! with { Releases = [.. v.Dataset.Schedule!.Releases, new(disallowed.RecordId, 70000)] } } };
        var card = ReplayRecap.Project(v, v)!; Assert.Contains("Home 1–0 Away", card.ReportedSummary); Assert.DoesNotContain("Home 2", card.ReportedSummary);
    }
    [Theory] [InlineData("stats")] [InlineData("source")] [InlineData("denominator")] [InlineData("event-future")] [InlineData("evidence-cursor")] [InlineData("evidence-head")]
    public void InvalidEvidenceNeverBecomesAPlausibleRecap(string fault)
    {
        var v = View(Half); var e = Evidence(v);
        e = fault switch {
            "stats" => e with { IllustrationStatistics = null },
            "source" => e with { Dataset = e.Dataset with { Manifest = e.Dataset.Manifest with { Sources = [] } } },
            "denominator" => e with { IllustrationStatistics = e.IllustrationStatistics! with { TotalClassifiedControlMs = 600001 } },
            "event-future" => e with { Dataset = e.Dataset with { Schedule = e.Dataset.Schedule! with { Releases = [new(e.Events[0].RecordId, Half + 1)] } } },
            "evidence-cursor" => e with { Context = e.Context with { CursorMs = Half + 5000 } },
            "evidence-head" => e with { Dataset = e.Dataset with { HeadSequence = e.Dataset.HeadSequence + 1 } }, _ => e };
        Assert.Null(ReplayRecap.Project(v, e));
    }
    [Fact]
    public async Task ForwardSeekRewindAndDatasetSwitchReplaceOrHideTheSingleCard()
    {
        var v = View(End); await using var page = new FootballReplay(); Bind(page, v); Set(page, "_recapEvidence", v);
        Assert.Equal(ReplayRecapKind.FullTime, Current(page)!.Action.Boundary.Kind);
        var half = View(Half + 20000); Bind(page, half); Assert.Null(Current(page)); Set(page, "_recapEvidence", Evidence(half));
        Assert.Equal(ReplayRecapKind.HalfTime, Current(page)!.Action.Boundary.Kind);
        Bind(page, View(Half - 1)); Assert.Null(Current(page)); Bind(page, half); Assert.NotNull(Current(page));
        Bind(page, View(End, "other-version", "other-stream")); Assert.Null(Current(page));
    }
    [Theory] [InlineData("updating")] [InlineData("unavailable")] [InlineData("presentation")] [InlineData("selection")]
    public async Task TemporaryOrUnacceptedScenesNeverDisplayTheRecap(string state)
    {
        var v = View(End); await using var page = new FootballReplay(); Bind(page, v); Set(page, "_recapEvidence", v);
        if (state == "updating") Set(page, "_updating", true); if (state == "unavailable") Set(page, "_contextUnavailable", true);
        if (state == "presentation") Set(page, "_eventPresentationContext", new MatchChatContext(v.Context, v.Dataset.HeadSequence));
        if (state == "selection") Set(page, "_selectedEvent", new MatchSelectedEvent("goal-1", 60000, "1"));
        Assert.Null(Current(page));
    }
    [Theory] [InlineData("rewind")] [InlineData("dataset")] [InlineData("head")] [InlineData("disposed")]
    public async Task LateBoundaryReadCannotOverwriteNewerAcceptedContext(string change)
    {
        var v = View(Half + 10000); var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => gate.Task)) { BaseAddress = new("https://unit.invalid/") };
        var page = new FootballReplay(); Bind(page, v); SetProperty(page, "Api", new ReplayApiClient(http)); Set(page, "_generation", 1L);
        var pending = Call(page, "UpdateRecapAsync", v, 1L, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        if (change == "disposed") await page.DisposeAsync();
        else { Bind(page, change == "dataset" ? View(End, "other-version", "other-stream") : change == "head" ? v with { Dataset = v.Dataset with { HeadSequence = 2 } } : View(0)); Set(page, "_generation", 2L); }
        gate.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) }); await pending;
        Assert.Null(Get(page, "_recapEvidence")); await page.DisposeAsync();
    }
    [Fact]
    public async Task AutomaticBoundaryLoadsOnceAndRepeatedAcceptedTicksDoNotAppendOrCallModels()
    {
        var v = View(Half + 1000); var reads = 0;
        using var http = new HttpClient(new Handler((_, _) => { reads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) }); })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); SetProperty(page, "Api", new ReplayApiClient(http)); Set(page, "_generation", 1L);
        await Call(page, "UpdateRecapAsync", v, 1L, CancellationToken.None); Assert.NotNull(Current(page));
        await Call(page, "UpdateRecapAsync", v, 1L, CancellationToken.None); Assert.Equal(1, reads);
    }
    [Fact]
    public async Task CanceledBoundaryReadDoesNotBlockALaterSeekToTheSameBoundary()
    {
        var v = View(Half + 1000); var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        using var http = new HttpClient(new Handler((_, _) => ++reads == 1 ? gate.Task
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) }))) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); SetProperty(page, "Api", new ReplayApiClient(http)); Set(page, "_generation", 1L);
        using var lifetime = new CancellationTokenSource(); var first = Call(page, "UpdateRecapAsync", v, 1L, lifetime.Token); lifetime.Cancel();
        await first; Assert.Null(Current(page));
        Set(page, "_generation", 2L); await Call(page, "UpdateRecapAsync", v, 2L, CancellationToken.None);
        Assert.NotNull(Current(page)); Assert.Equal(2, reads);
        gate.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) });
    }
    [Theory] [InlineData("http")] [InlineData("json")] [InlineData("binding")]
    public async Task FailedBoundaryReadCanBeRetriedByTheNextAcceptedRequest(string fault)
    {
        var v = View(Half + 1000); var reads = 0;
        using var http = new HttpClient(new Handler((_, _) => {
            if (++reads > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) });
            return Task.FromResult(fault == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = fault == "json" ? new StringContent("invalid json")
                    : JsonContent.Create(Evidence(v) with { Context = v.Context with { CursorMs = 0 } }) });
        })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); SetProperty(page, "Api", new ReplayApiClient(http)); Set(page, "_generation", 1L);
        await Call(page, "UpdateRecapAsync", v, 1L, CancellationToken.None); Assert.Null(Current(page));
        Set(page, "_generation", 2L); await Call(page, "UpdateRecapAsync", v, 2L, CancellationToken.None); Assert.NotNull(Current(page)); Assert.Equal(2, reads);
    }
    [Fact]
    public async Task OlderCompletionCannotClearTheNewerBoundaryCache()
    {
        var v = View(Half + 1000); var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        using var http = new HttpClient(new Handler((_, _) => ++reads == 1 ? gate.Task
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) }))) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); SetProperty(page, "Api", new ReplayApiClient(http)); Set(page, "_generation", 1L);
        var first = Call(page, "UpdateRecapAsync", v, 1L, CancellationToken.None);
        var newer = v with { CoverageMessage = "new accepted context" }; Bind(page, newer); Set(page, "_generation", 2L);
        await Call(page, "UpdateRecapAsync", newer, 2L, CancellationToken.None); var accepted = Current(page); Assert.NotNull(accepted);
        gate.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(Evidence(v)) }); await first;
        Set(page, "_generation", 3L); await Call(page, "UpdateRecapAsync", newer, 3L, CancellationToken.None);
        Assert.Equal(accepted, Current(page)); Assert.Equal(2, reads);
    }
    [Fact]
    public async Task AskingAboutStoredHalfTimeSeeksToItsExactEvidenceAndPreparesOnly()
    {
        var v = View(Half + 60000); var e = Evidence(v); var reads = 0;
        using var http = new HttpClient(new Handler(async (request, token) => {
            var query = await request.Content!.ReadFromJsonAsync<FootballQuery>(cancellationToken: token); Assert.Equal(Half, query!.CursorMs);
            reads++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(e) };
        })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); Set(page, "_recapEvidence", e); Set(page, "_personId", "person-one"); Set(page, "_playing", true);
        SetProperty(page, "Api", new ReplayApiClient(http)); var action = Current(page)!.Action;
        await Call(page, "PrepareRecapQuestionAsync", action);
        var preparation = Assert.IsType<ReplayRecapPreparation>(Get(page, "_preparedRecap")); Assert.Equal(action, preparation.Action);
        Assert.Equal(Half, preparation.Context.Query.CursorMs); Assert.Null(preparation.Context.Query.SubjectId); Assert.Null(preparation.Context.SelectedEvent);
        Assert.False((bool)Get(page, "_playing")!); Assert.Null(Get(page, "_personId")); Assert.Equal(1, reads);
    }
    [Theory] [InlineData("rewind")] [InlineData("head")] [InlineData("updating")]
    public async Task RetainedRecapActionCannotPrepareAfterContextChanges(string change)
    {
        var v = View(End); var action = ReplayRecap.Project(v, v)!.Action;
        await using var page = new FootballReplay(); Bind(page, v); Set(page, "_recapEvidence", v);
        if (change == "rewind") Bind(page, View(0));
        if (change == "head") Bind(page, v with { Dataset = v.Dataset with { HeadSequence = 2 } });
        if (change == "updating") Set(page, "_updating", true);
        await Call(page, "PrepareRecapQuestionAsync", action); Assert.Null(Get(page, "_preparedRecap"));
    }
    [Fact]
    public void CardIsSingleQuietSurfaceWithAnUnsentAction()
    {
        var v = View(End); var card = new Card(); SetProperty(card, "Recap", ReplayRecap.Project(v, v)!);
        using var tree = new RenderTreeBuilder(); card.Render(tree);
        var frames = tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
        Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "data-recap");
        Assert.DoesNotContain(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-live");
    }
    [Theory] [InlineData("ready")] [InlineData("draft")] [InlineData("pending")] [InlineData("mismatch")] [InlineData("updating")]
    public void TypedPreparationDoesNotSendOrOverwriteDrafts(string state)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Preparing a draft must not make an API call."))) { BaseAddress = new("https://unit.invalid/") };
        var model = new NoModel(); using var session = new MatchChatSession(new Auth(), new ReplayApiClient(http), model, model, Options.Create(new MatchChatOptions()));
        using var c = new Conversation(); SetProperty(c, "Session", session); var v = View(End); var action = ReplayRecap.Project(v, v)!.Action;
        var context = new MatchChatContext(action.Query, action.HeadSequence); SetProperty(c, "Context", context); c.Apply();
        if (state == "draft") Set(typeof(MatchConversation), c, "_question", "My own draft");
        if (state == "pending") Set(typeof(MatchConversation), c, "_pending", true);
        if (state == "updating") SetProperty(c, "Updating", true);
        SetProperty(c, "PreparedRecap", new ReplayRecapPreparation(Guid.NewGuid(), action, state == "mismatch" ? context with { HeadSequence = 99 } : context)); c.Apply();
        Assert.Equal(state == "ready" ? ReplayRecap.Question(action, MatchLanguage.English) : state == "draft" ? "My own draft" : "", Get(typeof(MatchConversation), c, "_question"));
        Assert.Empty(session.Turns); c.Apply(); Assert.Empty(session.Turns);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ExistingFullRecordingAndAustralianExtractKeepTheirDifferentMeaning()
    {
        var store = new ChunkedLocalFootballStore(Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")); var catalog = await store.CatalogAsync(default);
        var dataset = catalog.Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal));
        foreach (var cursor in new[] { Half, Half + 60000, End })
        {
            var v = await store.QueryAsync(FullFootballSourceTests.Query(dataset, cursor), default); var boundary = ReplayRecap.BoundaryFor(v)!;
            var e = await store.QueryAsync(v.Context with { CursorMs = boundary.EvidenceCursorMs }, default); var recap = ReplayRecap.Project(v, e, MatchAudienceMode.Analyst);
            Assert.NotNull(recap); Assert.Contains(cursor == End ? "Brentford 3–0 Chelsea" : "Brentford 0–0 Chelsea", recap.ReportedSummary);
            output.WriteLine($"cursor={cursor};kind={boundary.Kind};evidence={boundary.EvidenceCursorMs};homePasses={e.IllustrationStatistics!.Teams[0].PassAttempts};awayPasses={e.IllustrationStatistics.Teams[1].PassAttempts}");
        }
        foreach (var d in catalog.Where(d => d.Manifest.DatasetId == "australian-recorded"))
            Assert.Null(ReplayRecap.BoundaryFor(await store.QueryAsync(FullFootballSourceTests.Query(d, d.Schedule!.Segments[^1].CursorEndMs), default)));
    }
    private static void Bind(FootballReplay page, FootballView view) { Set(page, "_view", view); Set(page, "_selectedStream", view.Context.StreamId); Set(page, "_catalog", new[] { view.Dataset }); Set(page, "_cursorMs", view.Context.CursorMs); }
    private static ReplayRecap? Current(FootballReplay p) => (ReplayRecap?)typeof(FootballReplay).GetProperty("CurrentRecap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(p);
    private static Task Call(FootballReplay p, string name, params object?[] args) => (Task)typeof(FootballReplay).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(p, args)!;
    private static void Set(FootballReplay p, string name, object value) => Set(typeof(FootballReplay), p, name, value);
    private static object? Get(FootballReplay p, string name) => Get(typeof(FootballReplay), p, name);
    private static void Set(Type type, object obj, string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static object? Get(Type type, object obj, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void SetProperty(object obj, string name, object value)
    {
        for (var type = obj.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } property)
            { property.SetValue(obj, value); return; }
        throw new InvalidOperationException("Missing component property: " + name);
    }
    private sealed class Card : ReplayRecapCard { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal())); }
    private sealed class NoModel : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured => true;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) => throw new InvalidOperationException("No automatic Decision call.");
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) => throw new InvalidOperationException("No automatic narrator call.");
    }
}
#pragma warning restore BL0006
