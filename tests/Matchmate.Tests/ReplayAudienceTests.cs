using System.Net;
using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect actual component render trees, no test-only browser host.
public sealed class ReplayAudienceTests
{
    private static (TestFixture Fixture, FootballLedger Ledger) Recording()
    {
        var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with { Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "pass-one", "pass", 1000, 7400), Action(fixture, "control-one", "possession", 0, 20000, outcome: "controlled"));
        return (fixture, ledger);
    }
    private static FootballView InsightView(ReplayInsightKind kind)
    {
        var (f, ledger) = Recording(); var v = ledger.Query(Query(f, 20000));
        return v with { Context = v.Context with { CursorMs = 600000 }, Dataset = v.Dataset with { Closed = true,
            Schedule = v.Dataset.Schedule! with { Segments = [new("period-one", 0, 1000000, 0)] } },
            IllustrationStatistics = new([new("team-home", 20, kind == ReplayInsightKind.PassCompletionGap ? 20 : 16, kind == ReplayInsightKind.ShotControlContrast ? 3 : 0, 300000, 50),
                new("team-away", 20, kind == ReplayInsightKind.PassCompletionGap ? 10 : 16, 0, 300000, 50)], 600000) };
    }
    [Theory] [InlineData(ReplayInsightKind.BalancedControl)] [InlineData(ReplayInsightKind.ShotControlContrast)] [InlineData(ReplayInsightKind.PassCompletionGap)]
    public void InsightModesChangeOnlyExplanationWithIdenticalSnapshotActionAndClock(ReplayInsightKind kind)
    {
        var view = InsightView(kind); var fan = ReplayInsight.Project(view, MatchAudienceMode.Fan)!; var analyst = ReplayInsight.Project(view, MatchAudienceMode.Analyst)!;
        Assert.Equal(kind, fan.Kind); Assert.Equal(fan.Kind, analyst.Kind); Assert.Equal(fan.Action, analyst.Action); Assert.Equal(fan.CategoryKey, analyst.CategoryKey); Assert.Equal(fan.Clock, analyst.Clock);
        Assert.Equal(fan.ControlNote, analyst.ControlNote); Assert.NotEqual(fan.Body, analyst.Body); Assert.Contains("illustrat", fan.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(ReplayInsight.Matches(analyst.Action, view)); Assert.Equal(fan, ReplayInsight.Project(view, (MatchAudienceMode)99));
        if (kind == ReplayInsightKind.BalancedControl) { Assert.Contains("almost even", fan.Body); Assert.Contains("50%", analyst.Body); }
        if (kind == ReplayInsightKind.ShotControlContrast) Assert.Contains("all 3 shots", fan.Body);
        if (kind == ReplayInsightKind.PassCompletionGap) { Assert.Contains("larger share", fan.Body); Assert.Contains("20 of 20 passes (100%)", analyst.Body); }
    }
    [Fact]
    public async Task ParentFlipRefreshesSameViewCacheWithoutSeekingFetchingOrPreparingAQuestion()
    {
        var view = InsightView(ReplayInsightKind.BalancedControl); await using var page = new FootballReplay(); Set(typeof(FootballReplay), page, "_view", view); Set(typeof(FootballReplay), page, "_selectedStream", view.Context.StreamId);
        Set(typeof(FootballReplay), page, "_cursorMs", view.Context.CursorMs); Set(typeof(FootballReplay), page, "_playing", true);
        var property = typeof(FootballReplay).GetProperty("CurrentInsight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fan = (ReplayInsight)property.GetValue(page)!;
        Method(typeof(FootballReplay), "ChangeAudience").Invoke(page, [MatchAudienceMode.Analyst]); var analyst = (ReplayInsight)property.GetValue(page)!;
        Assert.NotEqual(fan.Body, analyst.Body); Assert.Equal(fan.Action, analyst.Action); Assert.Same(view, Get(typeof(FootballReplay), page, "_view"));
        Assert.Equal(view.Context.CursorMs, Get(typeof(FootballReplay), page, "_cursorMs")); Assert.Equal(true, Get(typeof(FootballReplay), page, "_playing")); Assert.Null(Get(typeof(FootballReplay), page, "_preparedInsight"));
        Method(typeof(FootballReplay), "ChangeAudience").Invoke(page, [(MatchAudienceMode)99]); Assert.Equal(MatchAudienceMode.Analyst, Get(typeof(FootballReplay), page, "_audienceMode"));
    }
    [Theory] [InlineData(MatchSourceQuestionId.Summary)] [InlineData(MatchSourceQuestionId.Positions)] [InlineData(MatchSourceQuestionId.Player)] [InlineData(MatchSourceQuestionId.ReplayStatistics)]
    public async Task AllSourceQuestionsKeepEvidenceReadsAndNoModelsWhileAnalystAddsRelevantMethod(MatchSourceQuestionId source)
    {
        var (fixture, ledger) = Recording(); using var fan = new Harness(ledger); using var analyst = new Harness(ledger);
        var context = new MatchChatContext(Query(fixture, 20000) with { SubjectId = "person-one" }, 2);
        fan.Session.SetContext(context); analyst.Session.SetContext(context); analyst.Session.SetAudienceMode(MatchAudienceMode.Analyst);
        Assert.True((await fan.Session.AskSourceAsync(context, source)).Accepted); Assert.True((await analyst.Session.AskSourceAsync(context, source)).Accepted);
        var a = Assert.Single(fan.Session.Turns); var b = Assert.Single(analyst.Session.Turns);
        Assert.Equal(MatchAudienceMode.Fan, a.AudienceMode); Assert.Equal(MatchAudienceMode.Analyst, b.AudienceMode);
        Assert.Equal(a.Context, b.Context); Assert.Equal(a.Question, b.Question); Assert.Equal(JsonSerializer.Serialize(a.Evidence), JsonSerializer.Serialize(b.Evidence));
        Assert.NotEqual(a.Answer, b.Answer); Assert.InRange(a.Answer.Length, 1, 1800); Assert.InRange(b.Answer.Length, 1, 1800);
        Assert.Equal(Assert.Single(fan.Queries), Assert.Single(analyst.Queries)); Assert.Empty(fan.Router.Inputs); Assert.Empty(analyst.Router.Inputs); Assert.Empty(fan.Model.Requests); Assert.Empty(analyst.Model.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AnalystPlayerMethodRemainsUnknownWhenAbsentOrNull(bool absent)
    {
        var (f, ledger) = Recording(); var context = new MatchChatContext(Query(f, 7400) with { SubjectId = "person-one" }, 2);
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(new("selected_player_context", MatchWorkflowRegistry.Version,
            new("unit-owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
        var bundle = result.Bundle! with { Items = result.Bundle!.Items.Select(item => {
            if (item.Tool != "current_player") return item;
            var player = JsonNode.Parse(item.Text)!.AsObject(); if (absent) player.Remove("method"); else player["method"] = null;
            return item with { Text = player.ToJsonString() }; }).ToImmutableArray() };
        var formatted = ((string Answer, MatchChatEvidence[] Evidence))typeof(MatchSourceQuestions).GetMethod("FormatForAudience", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [bundle, null, MatchAudienceMode.Analyst])!;
        Assert.Contains("position method is unavailable", formatted.Answer); Assert.InRange(formatted.Answer.Length, 1, 1800);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ActualV5BothStylesKeepAllFourSourceEvidenceWithinExistingAnswerBudget()
    {
        var store = new ChunkedLocalFootballStore(Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")); var catalog = await store.CatalogAsync(default);
        var dataset = catalog.Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal));
        var context = new MatchChatContext(FullFootballSourceTests.Query(dataset, 5880000) with { SubjectId = "person-pl-reference-brentford-kayode" }, dataset.HeadSequence);
        var executor = new MatchWorkflowExecutor(store.QueryAsync);
        foreach (var source in MatchSourceQuestions.All)
        {
            var result = await executor.ExecuteAsync(new(source.WorkflowId, MatchWorkflowRegistry.Version, new("owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
            Assert.NotNull(result.Bundle); var formatter = typeof(MatchSourceQuestions).GetMethod("FormatForAudience", BindingFlags.NonPublic | BindingFlags.Static)!;
            var fan = ((string Answer, MatchChatEvidence[] Evidence))formatter.Invoke(null, [result.Bundle, source.Question, MatchAudienceMode.Fan])!;
            var analyst = ((string Answer, MatchChatEvidence[] Evidence))formatter.Invoke(null, [result.Bundle, source.Question, MatchAudienceMode.Analyst])!;
            Assert.Equal(JsonSerializer.Serialize(fan.Evidence), JsonSerializer.Serialize(analyst.Evidence)); Assert.InRange(analyst.Answer.Length, 1, 1800); Assert.NotEqual(fan.Answer, analyst.Answer);
        }
    }
    [Theory] [InlineData(7300L)] [InlineData(7400L)] [InlineData(20000L)]
    public async Task FocusedStatisticsRemainFocusedAndCurrentWithModeParity(long cursor)
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); h.Router.Workflow = "current_replay_statistics"; var context = new MatchChatContext(Query(fixture, cursor), 2); h.Session.SetContext(context);
        const string question = "What is Home pass completion rate?"; Assert.True((await h.Session.AskAsync(context, question)).Accepted); var fan = h.Session.Turns[0];
        h.Session.SetAudienceMode(MatchAudienceMode.Analyst); Assert.True((await h.Session.AskAsync(context, question)).Accepted); var analyst = h.Session.Turns[1];
        Assert.Equal(fan.Evidence[0].Text, analyst.Evidence[0].Text); Assert.Equal(question, h.Router.Inputs[0].Question); Assert.Equal(question, h.Router.Inputs[1].Question);
        Assert.DoesNotContain("shots", analyst.Answer); Assert.DoesNotContain("control", analyst.Answer); Assert.Contains("divided by finished pass attempts", analyst.Answer);
        Assert.Contains(cursor < 7400 ? "rate is unavailable" : "completed 1 of 1 pass (100%)", analyst.Answer); Assert.All(h.Queries, q => Assert.Equal(cursor, q.CursorMs));
        Assert.Equal(2, h.Queries.Count); Assert.Empty(h.Model.Requests); Assert.Same(fan, h.Session.Turns[0]);
    }
    [Fact]
    public async Task GenericNarratorGetsOnlyTrustedStyleDifferenceWithOriginalQuestionEvidenceAndGuards()
    {
        var (fixture, ledger) = Recording(); using var fan = new Harness(ledger); using var analyst = new Harness(ledger);
        var context = new MatchChatContext(Query(fixture, 7400), 2); const string question = "Explain this current match";
        fan.Session.SetContext(context); analyst.Session.SetContext(context); analyst.Session.SetAudienceMode(MatchAudienceMode.Analyst);
        Assert.True((await fan.Session.AskAsync(context, question)).Accepted); Assert.True((await analyst.Session.AskAsync(context, question)).Accepted);
        Assert.Equal(Assert.Single(fan.Router.Inputs), Assert.Single(analyst.Router.Inputs)); Assert.Equal(Assert.Single(fan.Queries), Assert.Single(analyst.Queries));
        var a = Assert.Single(fan.Model.Requests); var b = Assert.Single(analyst.Model.Requests); Assert.Equal(a.AllowedEvidenceIds.ToArray(), b.AllowedEvidenceIds.ToArray());
        Assert.Equal(a.Messages[0], b.Messages[0]); Assert.StartsWith("match-commentator-v12", b.Messages[0].Content);
        Assert.Equal(question, b.Messages[^1].Content); Assert.Single(a.Messages, m => m.Content.StartsWith(MatchAudiencePresentation.Version)); Assert.Single(b.Messages, m => m.Content.StartsWith(MatchAudiencePresentation.Version));
        Assert.Contains("Audience: Analyst", b.Messages.Single(m => m.Content.StartsWith(MatchAudiencePresentation.Version)).Content);
        Assert.Equal(a.Messages.Where(m => !m.Content.StartsWith(MatchAudiencePresentation.Version)), b.Messages.Where(m => !m.Content.StartsWith(MatchAudiencePresentation.Version)));
        analyst.Model.Answer = "Home completed 290 passes."; Assert.False((await analyst.Session.AskAsync(context, "Explain the match again")).Accepted); Assert.Single(analyst.Session.Turns);
    }
    [Theory] [InlineData("router")] [InlineData("read")] [InlineData("narrator")]
    public async Task ModeFlipCancelsAndRejectsIgnoredCancellationLateReplies(string stage)
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        Assert.True((await h.Session.AskSourceAsync(context, MatchSourceQuestionId.Positions)).Accepted); var retained = Assert.Single(h.Session.Turns);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait() { entered.TrySetResult(); await gate.Task; }
        if (stage == "router") h.Router.Wait = Wait; if (stage == "read") h.WaitRead = Wait; if (stage == "narrator") h.Model.Wait = Wait;
        var pending = h.Session.AskAsync(context, "Explain the current replay moment"); await entered.Task; Assert.True(h.Session.SetAudienceMode(MatchAudienceMode.Analyst)); gate.SetResult();
        Assert.False((await pending).Accepted); Assert.Same(retained, Assert.Single(h.Session.Turns)); Assert.Equal(MatchAudienceMode.Fan, retained.AudienceMode);
        Assert.Equal(MatchAudienceMode.Analyst, h.Session.AudienceMode);
    }
    [Theory] [InlineData("unsupported")] [InlineData("other_live_matches_stats")] [InlineData("clarify")]
    public async Task FeedbackTurnsRetainTheirCapturedAudience(string workflow)
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); h.Router.Workflow = workflow; var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context); h.Session.SetAudienceMode(MatchAudienceMode.Analyst);
        Assert.True((await h.Session.AskAsync(context, "A bounded request")).Accepted); var turn = Assert.Single(h.Session.Turns); Assert.Equal(MatchAudienceMode.Analyst, turn.AudienceMode);
        h.Session.SetAudienceMode(MatchAudienceMode.Fan); Assert.Same(turn, Assert.Single(h.Session.Turns)); Assert.Equal(MatchAudienceMode.Analyst, turn.AudienceMode);
    }
    [Fact]
    public async Task InvalidOrSameModeDoesNotCancelAndUnavailableNeverBecomesZero()
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Model.Wait = async () => { entered.SetResult(); await gate.Task; };
        var pending = h.Session.AskAsync(context, "Explain the match"); await entered.Task; Assert.False(h.Session.SetAudienceMode((MatchAudienceMode)99)); Assert.True(h.Session.SetAudienceMode(MatchAudienceMode.Fan)); gate.SetResult(); Assert.True((await pending).Accepted);
        h.Transform = v => v with { IllustrationStatistics = null }; h.Session.SetAudienceMode(MatchAudienceMode.Analyst);
        var result = await h.Session.AskSourceAsync(context, MatchSourceQuestionId.ReplayStatistics); Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.DoesNotContain("0 pass", h.Session.Turns[^1].Answer); Assert.Equal(MatchAudienceMode.Analyst, h.Session.Turns[^1].AudienceMode);
    }
    [Fact]
    public async Task ComponentModeFlipPreservesDraftAndStopsAwaitedOnAskingBeforeAnyIO()
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); using var c = new Conversation(); Parameter(c, "Session", h.Session); Parameter(c, "Context", new MatchChatContext(Query(fixture, 7400), 2)); c.Apply();
        Set(typeof(MatchConversation), c, "_question", "My unsent question");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Parameter(c, "OnAsking", EventCallback.Factory.Create(new object(), async () => { entered.SetResult(); await gate.Task; }));
        var pending = (Task)Method(typeof(MatchConversation), "SubmitAsync").Invoke(c, [])!; await entered.Task; Parameter(c, "AudienceMode", MatchAudienceMode.Analyst); c.Apply(); gate.SetResult(); await pending;
        Assert.Equal("My unsent question", Get(typeof(MatchConversation), c, "_question")); Assert.Equal(false, Get(typeof(MatchConversation), c, "_pending"));
        Assert.Contains("Pending reply cancelled; your draft is kept", (string)Get(typeof(MatchConversation), c, "_notice")!); Assert.Empty(h.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Requests);
        Assert.Null(Get(typeof(MatchConversation), c, "_focusPreparation"));
    }
    [Fact]
    public async Task ExistingTurnLabelsAndPreparedDraftSurviveModeChangeAndRewindUsesExistingRules()
    {
        var view = InsightView(ReplayInsightKind.ShotControlContrast); using var h = new Harness(Recording().Ledger); var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence);
        using var c = new Conversation(); Parameter(c, "Session", h.Session); Parameter(c, "Context", context); c.Apply();
        var prep = new ReplayInsightPreparation(Guid.NewGuid(), ReplayInsight.Project(view)!.Action, context); Parameter(c, "PreparedInsight", prep); c.Apply(); var draft = Get(typeof(MatchConversation), c, "_question");
        Parameter(c, "AudienceMode", MatchAudienceMode.Analyst); c.Apply(); Assert.Equal(draft, Get(typeof(MatchConversation), c, "_question")); Assert.Null(Get(typeof(MatchConversation), c, "_focusPreparation")); Assert.Empty(h.Queries);
        var (f, ledger) = Recording(); using var session = new Harness(ledger); var at = new MatchChatContext(Query(f, 20000), 2); session.Session.SetContext(at);
        await session.Session.AskSourceAsync(at, MatchSourceQuestionId.Positions); session.Session.SetAudienceMode(MatchAudienceMode.Analyst); await session.Session.AskSourceAsync(at, MatchSourceQuestionId.Positions);
        using var labelled = new Conversation(); Parameter(labelled, "Session", session.Session); Parameter(labelled, "Context", at); Parameter(labelled, "AudienceMode", MatchAudienceMode.Analyst); labelled.Apply();
        using var tree = new RenderTreeBuilder(); labelled.Render(tree); Assert.Contains(" · Fan", Text(tree)); Assert.Contains(" · Analyst", Text(tree));
        session.Session.SetContext(at with { Query = Query(f, 7300) }); Assert.Empty(session.Session.Turns); Assert.Equal(MatchAudienceMode.Analyst, session.Session.AudienceMode);
    }
    [Fact]
    public void PickerHasOneNativeAccessibleTwoButtonGroupWithExplicitPressedState()
    {
        var picker = new Picker(); Parameter(picker, "Mode", MatchAudienceMode.Analyst); using var tree = new RenderTreeBuilder(); picker.Render(tree);
        Assert.Equal(2, Frames(tree).Count(f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "button"));
        var pressed = Frames(tree).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-pressed").Select(f => f.AttributeValue).ToArray(); Assert.Equal(new object[] { "false", "true" }, pressed);
        Assert.Contains("Same evidence.", Text(tree)); Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-label" && Equals(f.AttributeValue, "Explanation style"));
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static object? Get(Type type, object target, string name) => Field(type, name).GetValue(target);
    private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Parameter(object component, string name, object value) => (component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(MatchConversation).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))!.SetValue(component, value);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Picker : AudienceModePicker { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Harness : IDisposable
    {
        public List<FootballQuery> Queries = []; public Router Router = new(); public Model Model = new(); public MatchChatSession Session; private readonly HttpClient _http;
        public Func<Task>? WaitRead; public Func<FootballView, FootballView>? Transform;
        public Harness(FootballLedger ledger)
        {
            _http = new(new Handler(async request => { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>())!; Queries.Add(query); if (WaitRead is not null) await WaitRead(); var view = ledger.Query(query); return new(HttpStatusCode.OK) { Content = JsonContent.Create(Transform?.Invoke(view) ?? view) }; })) { BaseAddress = new("https://unit.invalid/") };
            Session = new(new Auth(), new ReplayApiClient(_http), Router, Model, Options.Create(new MatchChatOptions()));
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    private sealed class Router : IMatchWorkflowRouter
    {
        public bool Configured => true; public string Workflow = "current_match_summary"; public List<MatchRoutingInput> Inputs = []; public Func<Task>? Wait;
        public async Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Inputs.Add(input); if (Wait is not null) await Wait(); return new(MatchRoutingStatus.Selected, Workflow); }
    }
    private sealed class Model : IMatchChatModel
    {
        public bool Configured => true; public List<MatchNarratorRequest> Requests = []; public Func<Task>? Wait; public string Answer = "The available source evidence is limited.";
        public async Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { Requests.Add(request); if (Wait is not null) await Wait(); return new(JsonSerializer.Serialize(new { answer = Answer, evidenceIds = new[] { "coverage" } }), []); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "audience-user"), new("roles", "Tester")], "unit", "sub", "roles")))); }
}
#pragma warning restore BL0006
