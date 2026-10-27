using System.Net;
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
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect actual component trees, without a substitute browser host.
public sealed class ReplayInsightTests(ITestOutputHelper output)
{
    private static FootballView View(long total = 600000, decimal share = 50m, int homeShots = 0, int awayShots = 0,
        int homeAttempts = 20, int homeCompleted = 16, int awayAttempts = 20, int awayCompleted = 16)
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var view = ledger.Query(Query(fixture, 0));
        var home = (long)(total * share / 100m); var away = total - home;
        return view with { Context = view.Context with { CursorMs = 600000 }, Dataset = view.Dataset with { Closed = true,
            Manifest = view.Dataset.Manifest with { Capabilities = [.. view.Dataset.Manifest.Capabilities, FootballActionPolicy.GeneratedModelActionsV1],
                Sources = [.. view.Dataset.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] },
            Schedule = view.Dataset.Schedule! with { Segments = [new("period-one", 0, 1000000, 0)] } },
            IllustrationStatistics = new([new("team-home", homeAttempts, homeCompleted, homeShots, home, total == 0 ? null : 100m * home / total),
                new("team-away", awayAttempts, awayCompleted, awayShots, away, total == 0 ? null : 100m * away / total)], total) };
    }
    [Theory] [InlineData(299999, false)] [InlineData(300000, true)]
    public void FiveMinutesOfCompletedControlIsRequired(long total, bool expected) => Assert.Equal(expected, ReplayInsight.Project(View(total)) is not null);
    [Theory] [InlineData(44.999, false)] [InlineData(45, true)] [InlineData(50, true)] [InlineData(55, true)] [InlineData(55.001, false)]
    public void BalancedControlIncludesOnlyTheFortyFiveToFiftyFiveRange(double share, bool expected)
    {
        var card = ReplayInsight.Project(View(share: (decimal)share)); Assert.Equal(expected, card is not null);
        if (card is not null) { Assert.Equal(ReplayInsightKind.BalancedControl, card.Kind); Assert.Equal("Little separates them", card.Headline); Assert.Contains("on this measure", card.Body); }
    }
    [Theory] [InlineData(19, 19, 20, 14, false)] [InlineData(20, 18, 20, 16, true)] [InlineData(100, 89, 100, 80, false)]
    public void PassingRequiresTwentyAttemptsEachAndTenPercentagePoints(int attempts, int completed, int otherAttempts, int otherCompleted, bool expected)
    {
        var card = ReplayInsight.Project(View(share: 60, homeAttempts: attempts, homeCompleted: completed, awayAttempts: otherAttempts, awayCompleted: otherCompleted));
        Assert.Equal(expected, card is not null); if (card is not null) { Assert.Equal(ReplayInsightKind.PassCompletionGap, card.Kind); Assert.Null(card.ControlNote); Assert.Contains("does not measure pass difficulty", card.Body); }
    }
    [Fact]
    public void HigherPassingRateDoesNotClaimAHigherAbsoluteCompletionCount()
    {
        var card = ReplayInsight.Project(View(share: 60, homeAttempts: 20, homeCompleted: 20, awayAttempts: 100, awayCompleted: 80), MatchAudienceMode.Analyst)!;
        Assert.Equal(ReplayInsightKind.PassCompletionGap, card.Kind); Assert.Contains("20 of 20 passes (100%)", card.Body); Assert.Contains("80 of 100 (80%)", card.Body);
        Assert.Contains("larger share of attempted passes", card.Body); Assert.DoesNotContain("means more passes completed", card.Body);
    }
    [Theory] [InlineData(1, 0)] [InlineData(0, 3)]
    public void ShotContrastHasPriorityOverPassingThenBalancedControl(int homeShots, int awayShots)
    {
        var card = Assert.IsType<ReplayInsight>(ReplayInsight.Project(View(homeShots: homeShots, awayShots: awayShots, homeCompleted: 20, awayCompleted: 10)));
        Assert.Equal(ReplayInsightKind.ShotControlContrast, card.Kind); Assert.Equal("Control is shared. The shots aren’t.", card.Headline);
        Assert.Contains("control and shots measure different things", card.Body, StringComparison.OrdinalIgnoreCase); Assert.Contains("finished spells", card.ControlNote); Assert.DoesNotContain("chance", card.Body);
        Assert.Equal("Show shots for both teams in this generated replay", ReplayInsight.Question(card.Kind));
        var passing = ReplayInsight.Project(View(homeShots: 1, awayShots: 1, homeCompleted: 20, awayCompleted: 10));
        Assert.Equal(ReplayInsightKind.PassCompletionGap, passing!.Kind);
    }
    [Theory]
    [InlineData("null")] [InlineData("no-cap")] [InlineData("no-source")] [InlineData("hash")] [InlineData("duplicate-source")]
    [InlineData("no-stats")] [InlineData("team")] [InlineData("negative")] [InlineData("total")] [InlineData("share")]
    [InlineData("context")] [InlineData("stream")] [InlineData("fixture")] [InlineData("policy")] [InlineData("application")] [InlineData("future-total")] [InlineData("open-unready")]
    public void MissingOrMalformedAuthorityNeverCreatesACard(string fault)
    {
        var v = View(); var stats = v.IllustrationStatistics!; var sources = v.Dataset.Manifest.Sources;
        var view = fault switch {
            "null" => null, "no-cap" => v with { Dataset = v.Dataset with { Manifest = v.Dataset.Manifest with { Capabilities = [] } } },
            "no-source" => v with { Dataset = v.Dataset with { Manifest = v.Dataset.Manifest with { Sources = [sources[0]] } } },
            "hash" => v with { Dataset = v.Dataset with { Manifest = v.Dataset.Manifest with { Sources = [sources[0], sources[1] with { ArtifactHash = "bad" }] } } },
            "duplicate-source" => v with { Dataset = v.Dataset with { Manifest = v.Dataset.Manifest with { Sources = [.. sources, sources[1]] } } },
            "no-stats" => v with { IllustrationStatistics = null }, "team" => v with { IllustrationStatistics = stats with { Teams = [stats.Teams[0], stats.Teams[0]] } },
            "negative" => v with { IllustrationStatistics = stats with { Teams = [stats.Teams[0] with { Shots = -1 }, stats.Teams[1]] } },
            "total" => v with { IllustrationStatistics = stats with { TotalClassifiedControlMs = 600001 } },
            "share" => v with { IllustrationStatistics = stats with { Teams = [stats.Teams[0] with { ControlSharePercent = 49m }, stats.Teams[1]] } },
            "context" => v with { Context = v.Context with { DatasetVersion = "other-version" } }, "stream" => v with { Context = v.Context with { StreamId = "other-stream" } },
            "fixture" => v with { Context = v.Context with { FixtureId = "other-fixture" } }, "policy" => v with { Context = v.Context with { PresentationPolicyRevision = 2 } },
            "application" => v with { Context = v.Context with { ApplicationSequence = v.Dataset.HeadSequence + 1 } },
            "future-total" => v with { Context = v.Context with { CursorMs = 599999 } }, "open-unready" => v with { Frame = null, Dataset = v.Dataset with { Closed = false } }, _ => v };
        Assert.Null(ReplayInsight.Project(view));
    }
    [Fact]
    public void SnapshotIsImmutableAndRecomputesIdenticallyOnRewindWithoutCoordinatesOrFutureIndexes()
    {
        var v = View(homeShots: 1); var card = ReplayInsight.Project(v)!;
        Assert.True(ReplayInsight.Matches(card.Action, v)); Assert.False(ReplayInsight.Matches(card.Action, v with { Context = v.Context with { CursorMs = 599900 } }));
        Assert.False(ReplayInsight.Matches(card.Action, v with { Dataset = v.Dataset with { HeadSequence = v.Dataset.HeadSequence + 1 } }));
        Assert.False(ReplayInsight.Matches(card.Action, View(homeShots: 2)));
        Assert.Equal(card, ReplayInsight.Project(v)); Assert.Equal(card, ReplayInsight.Project(v with { Frame = null, GeneratedActionRecordIds = ["future-id"], Events = [] }));
        v.IllustrationStatistics!.Teams[0] = v.IllustrationStatistics.Teams[0] with { Shots = 2 };
        Assert.Equal(1, Assert.IsType<FootballTeamIllustrationStatistics>(card.Action.Home).Shots); Assert.False(ReplayInsight.Matches(card.Action, v));
    }
    [Fact]
    public void CardRenderIsQuietAndLoadingKeepsItsAcceptedClockWithDisabledAction()
    {
        var card = ReplayInsight.Project(View(homeShots: 1))!; var component = new Card(); Parameter(component, "Insight", card); Parameter(component, "Disabled", true);
        using var tree = new RenderTreeBuilder(); component.Render(tree); var text = Text(tree);
        Assert.Contains(card.Headline, text); Assert.Contains("Generated replay ·", text); Assert.Contains("10:00", text); Assert.Contains("Ask about this", text);
        Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "disabled" && Equals(f.AttributeValue, true));
        Assert.DoesNotContain(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-live");
    }
    [Fact]
    public async Task PageKeepsAcceptedInsightDuringFetchButHidesTemporaryEventPresentation()
    {
        var view = View(homeShots: 1); await using var page = new Page(); Set(page, "_view", view); Set(page, "_selectedStream", view.Context.StreamId);
        var insight = typeof(FootballReplay).GetProperty("CurrentInsight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var accepted = Assert.IsType<ReplayInsight>(insight.GetValue(page));
        Set(page, "_updating", true); Set(page, "_cursorMs", 900000L); Assert.Same(accepted, insight.GetValue(page));
        Set(page, "_eventPresentationContext", new MatchChatContext(view.Context, view.Dataset.HeadSequence)); Assert.Null(insight.GetValue(page));
    }
    [Theory] [InlineData("seek")] [InlineData("head")] [InlineData("dataset")] [InlineData("updating")] [InlineData("presentation")]
    public async Task ParentRejectsRetainedActionsAfterContextChange(string change)
    {
        var v = View(homeShots: 1); var card = ReplayInsight.Project(v)!; await using var page = new Page(); Set(page, "_view", v); Set(page, "_selectedStream", v.Context.StreamId);
        var next = change switch { "seek" => v with { Context = v.Context with { CursorMs = 599900 } }, "head" => v with { Dataset = v.Dataset with { HeadSequence = v.Dataset.HeadSequence + 1 } },
            "dataset" => v with { Context = v.Context with { DatasetVersion = "other" } }, _ => v };
        Set(page, "_view", next); if (change == "updating") Set(page, "_updating", true); if (change == "presentation") Set(page, "_eventPresentationContext", new MatchChatContext(v.Context, v.Dataset.HeadSequence));
        await (Task)Method(typeof(FootballReplay), "PrepareInsightQuestionAsync").Invoke(page, [card.Action])!; Assert.Null(Get(page, "_preparedInsight"));
    }
    [Fact]
    public async Task ExplicitActionPausesAndPreparesOnlyAndPreservesTheSelectedFutureGoal()
    {
        var v = View(homeShots: 1); var card = ReplayInsight.Project(v)!; await using var page = new Page();
        Set(page, "_view", v); Set(page, "_selectedStream", v.Context.StreamId); Set(page, "_playing", true); Set(page, "_selectedEvent", new MatchSelectedEvent("future-goal", 900000, "90"));
        await (Task)Method(typeof(FootballReplay), "PrepareInsightQuestionAsync").Invoke(page, [card.Action])!;
        Assert.False((bool)Get(page, "_playing")!); var prepared = Assert.IsType<ReplayInsightPreparation>(Get(page, "_preparedInsight"));
        Assert.Equal(v.Context, prepared.Action.Query); Assert.Equal("future-goal", prepared.Context.SelectedEvent!.RecordId);
        using var h = new ConversationHarness(); h.Set(prepared.Context); Parameter(h.Component, "PreparedInsight", prepared); h.Component.Apply();
        Assert.Equal(ReplayInsight.Question(card.Kind), Field(typeof(MatchConversation), "_question").GetValue(h.Component)); Assert.Empty(h.Session.Turns);
        Assert.Contains("Question ready. Send it when you’re ready.", (string)Field(typeof(MatchConversation), "_notice").GetValue(h.Component)!); Assert.Equal(0, h.NetworkCalls);
        h.Component.Apply(); Assert.Equal(0, h.NetworkCalls);
    }
    [Theory] [InlineData("draft")] [InlineData("pending")] [InlineData("updating")] [InlineData("context")] [InlineData("disposed")]
    public void ComposerPreparationNeverOverwritesDraftPendingOrChangedContext(string state)
    {
        var v = View(); var card = ReplayInsight.Project(v)!; var context = new MatchChatContext(v.Context, v.Dataset.HeadSequence);
        using var h = new ConversationHarness(); h.Set(context);
        if (state == "draft") Field(typeof(MatchConversation), "_question").SetValue(h.Component, "My own question");
        if (state == "pending") Field(typeof(MatchConversation), "_pending").SetValue(h.Component, true);
        if (state == "updating") Parameter(h.Component, "Updating", true);
        if (state == "disposed") h.Component.Dispose();
        Parameter(h.Component, "PreparedInsight", new ReplayInsightPreparation(Guid.NewGuid(), card.Action, state == "context" ? context with { HeadSequence = 99 } : context)); h.Component.Apply();
        Assert.Equal(state == "draft" ? "My own question" : "", Field(typeof(MatchConversation), "_question").GetValue(h.Component)); Assert.Equal(0, h.NetworkCalls);
        if (state is "draft" or "pending") Assert.Contains("current question is still here", (string)Field(typeof(MatchConversation), "_notice").GetValue(h.Component)!);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ActualV5ReadsUseCurrentStatsAtOpeningHalfGoalAndEndWithoutExtraRetrieval()
    {
        var store = new ChunkedLocalFootballStore(Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")); var catalog = await store.CatalogAsync(default);
        var dataset = catalog.Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal));
        foreach (var (cursor, kind) in new (long, ReplayInsightKind?)[] { (0, null), (299000, null), (2820000, ReplayInsightKind.BalancedControl), (3780000, ReplayInsightKind.ShotControlContrast), (5880000, ReplayInsightKind.ShotControlContrast), (2820000, ReplayInsightKind.BalancedControl) })
        {
            var view = await store.QueryAsync(FullFootballSourceTests.Query(dataset, cursor), default); var card = ReplayInsight.Project(view);
            Assert.Equal(kind, card?.Kind); if (card is not null) { Assert.Equal(cursor, card.Action.Query.CursorMs); Assert.Equal(dataset.HeadSequence, card.Action.HeadSequence); }
            output.WriteLine($"cursor={cursor};control={view.IllustrationStatistics?.TotalClassifiedControlMs};card={card?.Kind.ToString() ?? "none"}");
        }
        foreach (var old in catalog.Where(d => d.Manifest.DatasetId == "australian-recorded" || d.Manifest.DatasetVersion.StartsWith("brent-full-v4-", StringComparison.Ordinal)))
            Assert.Null(ReplayInsight.Project(await store.QueryAsync(FullFootballSourceTests.Query(old, old.Schedule!.Segments[0].CursorStartMs), default)));
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static object? Get(FootballReplay page, string name) => Field(typeof(FootballReplay), name).GetValue(page);
    private static void Set(FootballReplay page, string name, object value) => Field(typeof(FootballReplay), name).SetValue(page, value);
    private static void Parameter(object component, string name, object value) => component.GetType().GetProperty(name)!.SetValue(component, value);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private sealed class Card : ReplayInsightCard { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Page : FootballReplay { }
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); }
    private sealed class ConversationHarness : IDisposable
    {
        public int NetworkCalls; public MatchChatSession Session { get; } public Conversation Component { get; } = new(); private readonly HttpClient _http;
        public ConversationHarness()
        {
            _http = new(new Handler(() => NetworkCalls++)) { BaseAddress = new("https://unit.invalid/") };
            var model = new NoModel(); Session = new(new Auth(), new ReplayApiClient(_http), model, model, Options.Create(new MatchChatOptions()));
            typeof(MatchConversation).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Component, Session);
        }
        public void Set(MatchChatContext context) { Parameter(Component, "Context", context); Component.Apply(); }
        public void Dispose() { Component.Dispose(); Session.Dispose(); _http.Dispose(); }
    }
    private sealed class Handler(Action called) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { called(); throw new InvalidOperationException("No retrieval is allowed while preparing a draft."); } }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal())); }
    private sealed class NoModel : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured => true;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) => throw new InvalidOperationException("No Decision call is allowed while preparing a draft.");
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) => throw new InvalidOperationException("No narrator call is allowed while preparing a draft.");
    }
}
#pragma warning restore BL0006
