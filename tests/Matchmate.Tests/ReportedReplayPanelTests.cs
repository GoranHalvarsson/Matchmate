using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class ReportedReplayPanelTests
{
    private const long Half = 3360000, Second = 3420000, End = 6540000;
    private static readonly ConcurrentDictionary<long, Lazy<Task<FootballView>>> Views = new();
    private static Task<FootballView> View(long cursor) => Views.GetOrAdd(cursor, time => new(async () =>
    {
        var store = GeneratedPlayerDistanceTests.ExistingStore;
        var dataset = (await store.CatalogAsync(default)).Single(d => d.Manifest.DatasetVersion == "liverpool-generated-v2-cards-v1-f068a719fb27-45d0952330c2");
        return await store.QueryAsync(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            time, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision), default);
    })).Value;

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0, 0)]
    [InlineData(Half, 1, 0, 0, 1, 1, 0, 2, 2)]
    [InlineData(End, 1, 2, 3, 3, 2, 0, 5, 5)]
    public async Task RealLiverpoolCountsMatchGoldensAndExistingCompareExactly(long cursor, int hg, int ag, int hy, int ay, int hr, int ar, int hs, int ass)
    {
        var view = await View(cursor); var reports = Assert.IsType<ReportedReplayStatistics>(ReportedReplayProjection.Project(view));
        Assert.Equal(new ReportedTeamCounts(view.Dataset.Fixture!.Home.Id, hg, hy, hr, hs), reports.Home);
        Assert.Equal(new ReportedTeamCounts(view.Dataset.Fixture.Away.Id, ag, ay, ar, ass), reports.Away);
        Assert.Null(view.IllustrationStatistics);
        var summaryType = typeof(MatchChatSession).Assembly.GetType("Matchmate.Web.Chat.MatchReportedEventSummary")!;
        var summary = Assert.IsType<MatchChatEvidence>(summaryType.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [view]));
        using var document = JsonDocument.Parse(summary.Text); var teams = document.RootElement.GetProperty("teams").EnumerateArray().ToArray();
        foreach (var (actual, team) in new[] { reports.Home, reports.Away }.Zip(teams))
        {
            Assert.Equal(actual.TeamId, team.GetProperty("teamId").GetString()); Assert.Equal(actual.Goals, team.GetProperty("goals").GetInt32());
            Assert.Equal(actual.YellowCards, team.GetProperty("yellowCards").GetInt32()); Assert.Equal(actual.RedCards, team.GetProperty("redCards").GetInt32());
            Assert.Equal(actual.Substitutions, team.GetProperty("substitutions").GetInt32());
        }
    }

    [Theory]
    [InlineData(Half - 1, null)] [InlineData(Half, ReplayRecapKind.HalfTime)] [InlineData(Half + 1, ReplayRecapKind.HalfTime)]
    [InlineData(Second - 1, ReplayRecapKind.HalfTime)] [InlineData(Second, ReplayRecapKind.HalfTime)]
    [InlineData(End - 1, ReplayRecapKind.HalfTime)] [InlineData(End, ReplayRecapKind.FullTime)]
    public async Task RealAuthoredTransitionKeepsHalfTimeUntilFullTimeAndRewindRemovesIt(long cursor, ReplayRecapKind? expected)
    {
        var view = await View(cursor); var boundary = ReplayRecap.BoundaryFor(view); Assert.Equal(expected, boundary?.Kind);
        if (boundary is null) { Assert.Null(ReplayRecap.Project(view, await View(Half))); return; }
        Assert.Equal(expected == ReplayRecapKind.FullTime ? End : Half, boundary.EvidenceCursorMs);
        var evidence = await View(boundary.EvidenceCursorMs); var recap = Assert.IsType<ReplayRecap>(ReplayRecap.Project(view, evidence));
        Assert.True(recap.IsReported); Assert.Null(recap.GeneratedSummary); Assert.Null(recap.Action.ArtifactHash);
        Assert.NotNull(recap.Action.ReportedAuthorityHash); Assert.True(ReplayRecap.MatchesEvidence(recap.Action, evidence));
        Assert.Equal(expected == ReplayRecapKind.FullTime ? 5820000 : Half, boundary.MatchClockMs);
        Assert.Contains(expected == ReplayRecapKind.FullTime ? "Brentford 1–2 Liverpool" : "Brentford 1–0 Liverpool", recap.ReportedSummary);
        if (expected == ReplayRecapKind.HalfTime)
        {
            Assert.Contains("including reported half-time changes", recap.ControlNote);
            Assert.DoesNotContain("Gakpo", recap.ReportedSummary); Assert.DoesNotContain("Damsgaard", recap.ReportedSummary);
            Assert.Contains(ReplayText.Get(MatchLanguage.English, "ReportedRecapTeamFan", "Brentford", 0, 1, 2), recap.ReportedDetail);
        }
    }

    [Theory]
    [InlineData("overlap")] [InlineData("gap")] [InlineData("period")] [InlineData("local")] [InlineData("pause")]
    [InlineData("phase")] [InlineData("first-phase")] [InlineData("last-phase")] [InlineData("duration")]
    [InlineData("no-transition-cap")] [InlineData("open")] [InlineData("pending")]
    public async Task RecapRejectsMalformedOrIncompleteTransitionTopology(string fault)
    {
        var view = await View(Second); var d = view.Dataset; var segments = d.Schedule!.Segments.ToArray();
        switch (fault)
        {
            case "overlap": segments[1] = segments[1] with { CursorStartMs = Half - 1 }; break;
            case "gap": segments[1] = segments[1] with { CursorEndMs = Second - 1 }; break;
            case "period": segments[1] = segments[1] with { PeriodId = segments[2].PeriodId }; break;
            case "local": segments[1] = segments[1] with { LocalStartMs = 0 }; break;
            case "pause": segments[1] = segments[1] with { PausedDisplayMs = Second }; break;
            case "phase": segments[1] = segments[1] with { Phase = "play" }; break;
            case "first-phase": segments[0] = segments[0] with { Phase = "halftime_transition" }; break;
            case "last-phase": segments[2] = segments[2] with { Phase = "halftime_transition" }; break;
            case "duration": d = d with { Fixture = d.Fixture! with { Periods = [d.Fixture!.Periods[0] with { ActualDurationMs = Half + 1 }, d.Fixture.Periods[1]] } }; break;
            case "no-transition-cap": d = d with { Manifest = d.Manifest with { Capabilities = d.Manifest.Capabilities.Where(c => c != "paused-halftime-transition").ToArray() } }; break;
            case "open": d = d with { Closed = false }; break;
            case "pending": d = d with { PendingRecords = 1 }; break;
        }
        view = view with { Dataset = d with { Schedule = d.Schedule! with { Segments = segments } } };
        Assert.Null(ReplayRecap.BoundaryFor(view)); Assert.Null(ReplayRecap.Project(view, await View(Half)));
    }

    [Theory]
    [InlineData("version")] [InlineData("stream")] [InlineData("fixture")] [InlineData("policy")] [InlineData("head")]
    [InlineData("known-then")] [InlineData("live")] [InlineData("source-hash")] [InlineData("source-duplicate")]
    [InlineData("missing-source")] [InlineData("missing-report")] [InlineData("duplicate-report")] [InlineData("future-release")]
    [InlineData("report-version")] [InlineData("report-team")] [InlineData("report-period")] [InlineData("generated-evidence")]
    [InlineData("unbound-field")] [InlineData("no-report-cap")] [InlineData("no-complete-cap")] [InlineData("generated-cap-invalid")]
    public async Task InvalidReportAuthorityNeverProducesPlausiblePanels(string fault)
    {
        var v = await View(Half); var d = v.Dataset; var record = v.Events[0];
        v = fault switch
        {
            "version" => v with { Context = v.Context with { DatasetVersion = "other" } },
            "stream" => v with { Context = v.Context with { StreamId = "other" } },
            "fixture" => v with { Context = v.Context with { FixtureId = "other" } },
            "policy" => v with { Context = v.Context with { PresentationPolicyRevision = 99 } },
            "head" => v with { Context = v.Context with { ApplicationSequence = d.HeadSequence + 1 } },
            "known-then" => v with { Context = v.Context with { KnowledgeMode = "known_then" } },
            "live" => v with { Dataset = d with { Stream = d.Stream with { DeliveryMode = "live" } } },
            "source-hash" => v with { Dataset = d with { Manifest = d.Manifest with { Sources = [d.Manifest.Sources[0] with { ArtifactHash = "bad" }, .. d.Manifest.Sources.Skip(1)] } } },
            "source-duplicate" => v with { Dataset = d with { Manifest = d.Manifest with { Sources = [.. d.Manifest.Sources, d.Manifest.Sources[0]] } } },
            "missing-source" => v with { Dataset = d with { Manifest = d.Manifest with { Sources = [] } } },
            "missing-report" => v with { Events = v.Events.Skip(1).ToArray() },
            "duplicate-report" => v with { Events = [.. v.Events, record] },
            "future-release" => v with { Dataset = d with { Schedule = d.Schedule! with { Releases = d.Schedule!.Releases.Select(r => r.RecordId == record.RecordId ? r with { CursorMs = Half + 1 } : r).ToArray() } } },
            "no-report-cap" => v with { Dataset = d with { Manifest = d.Manifest with { Capabilities = d.Manifest.Capabilities.Where(c => c != "match_event").ToArray() } } },
            "no-complete-cap" => v with { Dataset = d with { Manifest = d.Manifest with { Capabilities = d.Manifest.Capabilities.Where(c => c != "complete-generated-200ms-illustration").ToArray() } } },
            "generated-cap-invalid" => v with { Dataset = d with { Manifest = d.Manifest with { Capabilities = [.. d.Manifest.Capabilities, FootballActionPolicy.GeneratedModelActionsV1] } } },
            _ => v
        };
        var altered = fault switch
        {
            "report-version" => record with { DatasetVersion = "other" },
            "report-team" => record with { Payload = ((MatchEventPayload)record.Payload) with { TeamId = "other" } },
            "report-period" => record with { FootballTime = record.FootballTime! with { PeriodId = d.Fixture!.Periods[1].Id } },
            "generated-evidence" => record with { Evidence = record.Evidence.Select(e => e with { Origin = "synthetic", Method = "generated" }).ToArray() },
            "unbound-field" => record with { FieldEvidence = new() { ["/payload"] = ["missing"], ["/footballTime"] = ["reported"] } },
            _ => record
        };
        if (!ReferenceEquals(altered, record)) v = v with { Events = [altered, .. v.Events.Skip(1)] };
        Assert.Null(ReportedReplayProjection.Project(v)); Assert.Null(ReplayInsight.Project(v)); Assert.Null(ReplayRecap.Project(v, v));
    }

    [Fact]
    public async Task SecondPeriodReportAtHalfTimeIsRejectedInsteadOfSilentlyFiltered()
    {
        var half = await View(Half); var future = (await View(End)).Events.First(e => e.FootballTime!.PeriodId == half.Dataset.Fixture!.Periods[1].Id);
        var contaminated = half with
        {
            Events = [.. half.Events, future], Dataset = half.Dataset with { Schedule = half.Dataset.Schedule! with
            { Releases = half.Dataset.Schedule!.Releases.Select(r => r.RecordId == future.RecordId ? r with { CursorMs = Half } : r).ToArray() } }
        };
        Assert.Null(ReplayRecap.Project(contaminated, contaminated)); Assert.Null(ReportedReplayProjection.Project(contaminated));
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task ModesKeepIdenticalFactsActionsAndLocalizedHonestPanelLabels(MatchLanguage language)
    {
        var v = await View(Second); var e = await View(Half); var before = JsonSerializer.Serialize(v);
        var fan = Assert.IsType<ReplayInsight>(ReplayInsight.Project(v, MatchAudienceMode.Fan, language));
        var analyst = Assert.IsType<ReplayInsight>(ReplayInsight.Project(v, MatchAudienceMode.Analyst, language));
        Assert.True(fan.IsReported); Assert.Equal(fan.Action, analyst.Action); Assert.NotEqual(fan.Body, analyst.Body);
        Assert.Equal(ReplayText.Get(language, "ReportedInsightLead", "Brentford"), fan.Headline); Assert.Contains("1–0", fan.Body);
        Assert.Contains(ReplayText.Get(language, "ReportedDismissals", "Brentford", 1), fan.Body);
        Assert.Null(fan.Action.Home); Assert.Null(fan.Action.Away); Assert.Null(fan.Action.ClassifiedControlMs); Assert.Null(fan.Action.ArtifactHash);
        Assert.True(ReplayInsight.Matches(fan.Action, JsonSerializer.Deserialize<FootballView>(before)!));
        var recapFan = Assert.IsType<ReplayRecap>(ReplayRecap.Project(v, e, MatchAudienceMode.Fan, language));
        var recapAnalyst = Assert.IsType<ReplayRecap>(ReplayRecap.Project(v, e, MatchAudienceMode.Analyst, language));
        Assert.Equal(recapFan.Action, recapAnalyst.Action); Assert.Equal(recapFan.ReportedSummary, recapAnalyst.ReportedSummary);
        Assert.NotEqual(recapFan.ReportedDetail, recapAnalyst.ReportedDetail); Assert.Null(recapAnalyst.GeneratedSummary);
        Assert.Equal(before, JsonSerializer.Serialize(v));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = b =>
            {
                b.OpenComponent<ReplayInsightCard>(0); b.AddAttribute(1, "Insight", fan); b.CloseComponent();
                b.OpenComponent<ReplayRecapCard>(2); b.AddAttribute(3, "Recap", recapFan); b.CloseComponent();
                b.OpenComponent<IllustrationStatistics>(4); b.AddAttribute(5, "View", v); b.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = language, ["ChildContent"] = content }));
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains(ReplayText.Get(language, "ReportedInsightScope", "57:00", ReplayText.Audience(language, MatchAudienceMode.Fan)), html);
            Assert.Contains(ReplayText.Get(language, "ReportedStats"), html); Assert.Contains("data-statistics=\"reported\"", html);
            Assert.DoesNotContain(ReplayText.Get(language, "GeneratedStats"), html); Assert.DoesNotContain("recap-generated", html);
            Assert.Contains(ReportedReplayProjection.Limitation(language), html);
            Assert.Contains(ReplayText.Get(language, "ReportedPanelNote"), html);
            Assert.DoesNotContain(ReplayText.Get(language, "ReportedPanelNote"), fan.ControlNote);
            Assert.DoesNotContain(ReplayText.Get(language, "ReportedPanelNote"), recapFan.ControlNote);
        });
    }

    [Fact]
    public async Task OpeningShowsReportZerosWithCoverageButNoInventedInsightAndEndShowsAwayLead()
    {
        var opening = await View(0); Assert.Null(ReplayInsight.Project(opening)); Assert.NotNull(ReportedReplayProjection.Project(opening));
        var end = await View(End); var insight = Assert.IsType<ReplayInsight>(ReplayInsight.Project(end));
        Assert.Equal("Liverpool lead on reported goals", insight.Headline); Assert.Contains("Brentford: dismissal reports 2", insight.Body);
        Assert.DoesNotContain("possession", insight.Body); Assert.DoesNotContain("dominant", insight.Body); Assert.DoesNotContain("shots", insight.Body);
        Assert.False(ReplayInsight.Matches(insight.Action, await View(Half)));
        var alteredSource = end with { Dataset = end.Dataset with { Manifest = end.Dataset.Manifest with { Sources = end.Dataset.Manifest.Sources.Select((s, i) => i == 0 ? s with { ArtifactHash = new string('a', 64) } : s).ToArray() } } };
        Assert.False(ReplayInsight.Matches(insight.Action, alteredSource)); Assert.False(ReplayRecap.MatchesEvidence(ReplayRecap.Project(end, end)!.Action, alteredSource));
    }

    [Fact]
    public async Task HalfTimeCtaSeeksToFreshExactCutoffAndPreparesWithoutSendingOrReplacingDraft()
    {
        var v = await View(Second); var evidence = await View(Half); var reads = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            var query = await request.Content!.ReadFromJsonAsync<FootballQuery>(); Assert.Equal(Half, query!.CursorMs);
            reads++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(evidence) };
        })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay(); Bind(page, v); Set(page, "_recapEvidence", evidence); SetProperty(page, "Api", new ReplayApiClient(http));
        var action = Assert.IsType<ReplayRecap>(ReplayRecap.Project(v, evidence)).Action;
        await Call(page, "PrepareRecapQuestionAsync", action);
        var prepared = Assert.IsType<ReplayRecapPreparation>(Get(page, "_preparedRecap"));
        Assert.Equal(Half, prepared.Context.Query.CursorMs); Assert.Equal(1, reads); Assert.Equal(action, prepared.Action);
        var model = new NoModel(); using var session = new MatchChatSession(new Auth(), new ReplayApiClient(http), model, model, Options.Create(new MatchChatOptions()));
        using var conversation = new Conversation(); SetProperty(conversation, "Session", session); SetProperty(conversation, "Context", prepared.Context);
        conversation.Apply(); SetProperty(conversation, "PreparedRecap", prepared); conversation.Apply();
        Assert.Equal(ReplayRecap.Question(action, MatchLanguage.English), Get(conversation, "_question")); Assert.Empty(session.Turns); Assert.Equal(1, reads);
        Set(conversation, "_question", "Keep my draft"); SetProperty(conversation, "PreparedRecap", prepared with { Id = Guid.NewGuid() }); conversation.Apply();
        Assert.Equal("Keep my draft", Get(conversation, "_question")); Assert.Empty(session.Turns); Assert.Equal(1, reads);
    }

    [Fact]
    public async Task CurrentReportInsightPreparesOnlyAndRetainedActionFailsAfterRewind()
    {
        var view = await View(Second); var action = ReplayInsight.Project(view)!.Action;
        await using var page = new FootballReplay(); Bind(page, view); Set(page, "_playing", true);
        await Call(page, "PrepareInsightQuestionAsync", action);
        var prepared = Assert.IsType<ReplayInsightPreparation>(Get(page, "_preparedInsight")); Assert.Equal(action, prepared.Action); Assert.False((bool)Get(page, "_playing")!);
        Bind(page, await View(0)); Set(page, "_preparedInsight", null); await Call(page, "PrepareInsightQuestionAsync", action); Assert.Null(Get(page, "_preparedInsight"));
    }

    private static void Bind(FootballReplay page, FootballView view) { Set(page, "_view", view); Set(page, "_selectedStream", view.Context.StreamId); Set(page, "_catalog", new[] { view.Dataset }); Set(page, "_cursorMs", view.Context.CursorMs); }
    private static void Set(object obj, string field, object? value) => Field(obj, field).SetValue(obj, value);
    private static object? Get(object obj, string field) => Field(obj, field).GetValue(obj);
    private static FieldInfo Field(object obj, string field) { for (var type = obj.GetType(); type is not null; type = type.BaseType) if (type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance) is { } result) return result; throw new InvalidOperationException(field); }
    private static void SetProperty(object obj, string name, object value) { for (var type = obj.GetType(); type is not null; type = type.BaseType) if (type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is { } property) { property.SetValue(obj, value); return; } throw new InvalidOperationException(name); }
    private static Task Call(object obj, string method, params object?[] args) => (Task)obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args)!;
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal())); }
    private sealed class NoModel : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured => true;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) => throw new InvalidOperationException("No automatic Decision call.");
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) => throw new InvalidOperationException("No automatic narrator call.");
    }
}
