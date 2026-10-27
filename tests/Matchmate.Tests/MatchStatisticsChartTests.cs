using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect actual component render trees; no browser substitute host.
public sealed class MatchStatisticsChartTests
{
    [Theory]
    [InlineData("Show passes as a chart", MatchLanguage.English, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Show a bar chart of pass attempts", MatchLanguage.English, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Show pass accuracy as a chart", MatchLanguage.English, MatchStatisticsChartMetric.PassAccuracy)]
    [InlineData("Show a graph of shots", MatchLanguage.English, MatchStatisticsChartMetric.Shots)]
    [InlineData("Visa passningar som ett diagram", MatchLanguage.Swedish, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Visa ett stapeldiagram över passningsförsök", MatchLanguage.Swedish, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Visa passningssäkerhet som ett diagram", MatchLanguage.Swedish, MatchStatisticsChartMetric.PassAccuracy)]
    [InlineData("Visa ett diagram över skott", MatchLanguage.Swedish, MatchStatisticsChartMetric.Shots)]
    [InlineData("Mostrar pases como un gráfico", MatchLanguage.Spanish, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Mostrar un gráfico de barras de intentos de pase", MatchLanguage.Spanish, MatchStatisticsChartMetric.PassAttempts)]
    [InlineData("Mostrar precisión de pase como un gráfico", MatchLanguage.Spanish, MatchStatisticsChartMetric.PassAccuracy)]
    [InlineData("Mostrar un gráfico de tiros", MatchLanguage.Spanish, MatchStatisticsChartMetric.Shots)]
    public async Task ExplicitSupportedRequestsPreserveOriginalQuestionLanguageAndRouting(string question, MatchLanguage language, MatchStatisticsChartMetric metric)
    {
        using var h = new Harness(); h.Session.SetLanguage(language);
        Assert.True((await h.Session.AskAsync(h.Context, question)).Accepted);
        var turn = Assert.Single(h.Session.Turns); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart);
        Assert.Equal(question, turn.Question); Assert.Equal(question, h.Model.LastQuestion); Assert.Equal(language, turn.Language);
        Assert.True(chart.InitiallyExpanded); Assert.Equal(metric, chart.InitialMetric); Assert.False(chart.CanChooseMetric);
        Assert.Equal(h.Context.Query with { ApplicationSequence = h.Context.HeadSequence }, chart.Context.Query); Assert.Equal("00:20", chart.ReplayClock);
        Assert.True(turn.EvidenceOnly); Assert.Equal(2, turn.Evidence.Length); Assert.Equal(1, h.Model.Routes); Assert.Equal(0, h.Model.Narrations);
        Assert.Equal(20000, Assert.Single(h.Queries).CursorMs);
    }

    [Theory]
    [InlineData("Show a chart of Arsenal shots", MatchLanguage.English)]
    [InlineData("Show actual shots as a chart", MatchLanguage.English)]
    [InlineData("Show shots on target as a chart", MatchLanguage.English)]
    [InlineData("Show this player's passes as a chart", MatchLanguage.English)]
    [InlineData("Show passes to One as a chart", MatchLanguage.English)]
    [InlineData("Show shots in the first half as a chart", MatchLanguage.English)]
    [InlineData("Show shots and xG as a chart", MatchLanguage.English)]
    [InlineData("Show shots not passes as a chart", MatchLanguage.English)]
    [InlineData("Show their shots as a chart", MatchLanguage.English)]
    [InlineData("Show possession as a chart", MatchLanguage.English)]
    [InlineData("Show completed passes as a chart", MatchLanguage.English)]
    [InlineData("Show distance as a chart", MatchLanguage.English)]
    [InlineData("Visa faktiska skott som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Visa skott i första halvlek som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Visa passningar till One som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar tiros reales como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar tiros en la primera parte como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar pases a One como un gráfico", MatchLanguage.Spanish)]
    public async Task UnsupportedQualificationsNeverProduceAnUnqualifiedChartEvenIfRouterSelectsStatistics(string question, MatchLanguage language)
    {
        using var h = new Harness(); h.Session.SetLanguage(language);
        Assert.True((await h.Session.AskAsync(h.Context, question)).Accepted);
        Assert.Null(Assert.Single(h.Session.Turns).StatisticsChart); Assert.Equal(0, h.Model.Narrations);
    }

    [Theory]
    [InlineData("Show passes by Son as a chart", MatchLanguage.English)]
    [InlineData("Show passes by Can as a chart", MatchLanguage.English)]
    [InlineData("Show shots from Son as a chart", MatchLanguage.English)]
    [InlineData("Show pass accuracy of Can as a chart", MatchLanguage.English)]
    [InlineData("Visa passningar av Son som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Visa skott av Can som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar pases por Son como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar tiros por Can como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar pases de Son como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Show passes for Son as a chart", MatchLanguage.English)]
    [InlineData("Show pass attempts for Can as a chart", MatchLanguage.English)]
    [InlineData("How many passes has Son made as a chart?", MatchLanguage.English)]
    [InlineData("How many passes have Can made as a chart?", MatchLanguage.English)]
    [InlineData("Hur många passningar har Son gjort som ett diagram?", MatchLanguage.Swedish)]
    [InlineData("Visa passningsförsök för Can som ett diagram", MatchLanguage.Swedish)]
    [InlineData("¿Cuántos pases ha Son realizado como un gráfico?", MatchLanguage.Spanish)]
    [InlineData("¿Cuántos pases han Can realizado como un gráfico?", MatchLanguage.Spanish)]
    [InlineData("Mostrar pases para Son como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Show passes by Home and Can as a chart", MatchLanguage.English)]
    [InlineData("Visa passningar av Home och Son som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar pases por Home y Can como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Show passes by Home, Can as a chart", MatchLanguage.English)]
    [InlineData("Show Son shots as a chart", MatchLanguage.English)]
    [InlineData("Show Can shots as a chart", MatchLanguage.English)]
    [InlineData("Visa Son skott som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar Son tiros como un gráfico", MatchLanguage.Spanish)]
    public async Task F_CH_001_RawPersonQualificationCannotBecomeAnAllowedWord(string question, MatchLanguage language)
    {
        using var h = new Harness(); h.Session.SetLanguage(language); await h.Session.AskAsync(h.Context, question);
        var turn = Assert.Single(h.Session.Turns); Assert.Null(turn.StatisticsChart);
        Assert.Contains(ChatText.Get(language, "Stats.PlayerUnavailable"), turn.Answer);
        Assert.DoesNotContain("66.7", turn.Answer); Assert.Equal(question, h.Model.LastQuestion);
    }

    [Theory]
    [InlineData("Show passes by Home as a chart", MatchLanguage.English)]
    [InlineData("Show passes by both teams as a chart", MatchLanguage.English)]
    [InlineData("Can you show passes as a chart?", MatchLanguage.English)]
    [InlineData("Visa passningar av Home som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Visa passningar av båda lagen som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar pases por Home como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar pases por ambos equipos como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Mostrar pases de Home como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Can you show a chart for shots?", MatchLanguage.English, MatchStatisticsChartMetric.Shots)]
    [InlineData("Which team has taken more shots as a chart?", MatchLanguage.English, MatchStatisticsChartMetric.Shots)]
    [InlineData("How many passes has Home made as a chart?", MatchLanguage.English)]
    [InlineData("How many passes have both teams made as a chart?", MatchLanguage.English)]
    [InlineData("Hur många passningar har Home gjort som ett diagram?", MatchLanguage.Swedish)]
    [InlineData("¿Cuántos pases ha Home realizado como un gráfico?", MatchLanguage.Spanish)]
    [InlineData("¿Cuántos pases han realizado ambos equipos como un gráfico?", MatchLanguage.Spanish)]
    [InlineData("Show passes by Home and Away as a chart", MatchLanguage.English)]
    [InlineData("Visa passningar av Home och Away som ett diagram", MatchLanguage.Swedish)]
    [InlineData("Mostrar pases por Home y Away como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Por favor mostrar pases como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Por favor mostrar pases por Home como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Show passes by Home, Away as a chart", MatchLanguage.English)]
    [InlineData("Show passes for this replay as a chart", MatchLanguage.English)]
    [InlineData("Show passes from this generated replay as a chart", MatchLanguage.English)]
    [InlineData("Mostrar pases de esta repetición generada como un gráfico", MatchLanguage.Spanish)]
    [InlineData("Show Home shots as a chart", MatchLanguage.English, MatchStatisticsChartMetric.Shots)]
    [InlineData("Show Home shots compared with the opponent as a chart", MatchLanguage.English, MatchStatisticsChartMetric.Shots)]
    public async Task F_CH_001_002_TeamQualificationOrdinaryModalAndPolitenessRemainSupported(string question, MatchLanguage language, MatchStatisticsChartMetric metric = MatchStatisticsChartMetric.PassAttempts)
    {
        using var h = new Harness(); h.Session.SetLanguage(language); await h.Session.AskAsync(h.Context, question);
        var chart = Assert.IsType<MatchStatisticsChartSnapshot>(Assert.Single(h.Session.Turns).StatisticsChart);
        Assert.True(chart.InitiallyExpanded); Assert.Equal(metric, chart.InitialMetric);
    }

    [Theory]
    [InlineData(7300L, 0, 0, 0)] [InlineData(7400L, 1, 1, 0)]
    [InlineData(9000L, 1, 1, 1)] [InlineData(20000L, 3, 2, 1)]
    public async Task ChartAndTextUseExactlyTheSameFinishedActionsAtCutoff(long cursor, int attempts, int completed, int shots)
    {
        using var h = new Harness(cursor); Assert.True((await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.ReplayStatistics)).Accepted);
        var turn = Assert.Single(h.Session.Turns); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart);
        Assert.False(chart.InitiallyExpanded); Assert.True(chart.CanChooseMetric); var home = chart.Teams[0]; var away = chart.Teams[1];
        Assert.Equal(attempts, home.PassAttempts); Assert.Equal(completed, home.PassCompleted); Assert.Equal(shots, home.Shots);
        Assert.Equal(attempts == 0 ? (decimal?)null : 100m * completed / attempts, home.PassAccuracy);
        Assert.Equal(0, away.PassAttempts); Assert.Null(away.PassAccuracy);
        Assert.Equal("0%", chart.BarWidth(away, MatchStatisticsChartMetric.Shots));
        Assert.Equal(100m, chart.Maximum(MatchStatisticsChartMetric.PassAccuracy));
        Assert.Equal(home.PassAccuracy is { } accuracy ? accuracy.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%" : "0%",
            chart.BarWidth(home, MatchStatisticsChartMetric.PassAccuracy));
        Assert.Contains($"{completed} of {attempts}", turn.Answer); Assert.Contains(chart.ReplayClock, turn.Answer);
        Assert.Contains($"\"passAttempts\":{attempts}", turn.Evidence.Single(item => item.Tool == "illustration_statistics").Text);
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task DirectCompareActionNeedsNoAiConnectionAndPreservesExistingSourceRegistry(MatchLanguage language)
    {
        using var h = new Harness(); h.Model.Configured = false; h.Session.SetLanguage(language);
        Assert.True((await h.Session.AskStatisticsChartAsync(h.Context)).Accepted);
        var turn = Assert.Single(h.Session.Turns); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart);
        Assert.Equal(ChatText.Get(language, "Chart.CompareTeams"), turn.Question); Assert.Equal(language, turn.Language);
        Assert.True(chart.InitiallyExpanded); Assert.True(chart.CanChooseMetric); Assert.Equal(MatchStatisticsChartMetric.PassAttempts, chart.InitialMetric);
        Assert.Equal(MatchStatisticsChartEvidenceKind.GeneratedReplay, chart.EvidenceKind);
        Assert.Equal(new[] { MatchStatisticsChartMetric.PassAttempts, MatchStatisticsChartMetric.PassAccuracy, MatchStatisticsChartMetric.Shots }, chart.AvailableMetrics);
        Assert.Throws<ArgumentOutOfRangeException>(() => chart.Value(chart.Teams[0], MatchStatisticsChartMetric.Goals));
        Assert.Equal(0, h.Model.Routes); Assert.Equal(0, h.Model.Narrations); Assert.Single(h.Queries);
        Assert.Equal(4, MatchSourceQuestions.All.Length); Assert.Null(MatchSourceQuestions.Exact(turn.Question));
    }

    [Theory]
    [InlineData("Show replay statistics as a chart")]
    [InlineData("Show passes and shots as a chart")]
    public async Task GenericAndMultipleSupportedMetricsOfferOneMetricAtATime(string question)
    {
        using var h = new Harness(); await h.Session.AskAsync(h.Context, question);
        var chart = Assert.IsType<MatchStatisticsChartSnapshot>(Assert.Single(h.Session.Turns).StatisticsChart);
        Assert.True(chart.CanChooseMetric); Assert.True(chart.InitiallyExpanded); Assert.Equal(MatchStatisticsChartMetric.PassAttempts, chart.InitialMetric);
    }

    [Fact]
    public async Task FocusedAnswersOfferTheirMetricAndSavedSnapshotNeverMovesWithReplayOrLanguage()
    {
        using var h = new Harness(); await h.Session.AskAsync(h.Context, "How many shots have both teams taken?");
        var turn = Assert.Single(h.Session.Turns); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart);
        Assert.Equal(MatchStatisticsChartMetric.Shots, chart.InitialMetric); Assert.False(chart.InitiallyExpanded); Assert.False(chart.CanChooseMetric);
        h.Session.SetLanguage(MatchLanguage.Spanish); h.Session.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 25000 } });
        Assert.Same(chart, Assert.Single(h.Session.Turns).StatisticsChart); Assert.Equal(MatchLanguage.English, turn.Language); Assert.Equal(20000, chart.Context.Query.CursorMs);
        Assert.Equal("00:20", chart.ReplayClock); Assert.Equal(1, chart.Teams[0].Shots);
        h.Session.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 7300 } }); Assert.Empty(h.Session.Turns);
        Assert.Equal(1, chart.Teams[0].Shots); // The discarded snapshot itself remains immutable.
    }

    [Theory] [InlineData("no-capability")] [InlineData("no-statistics")] [InlineData("no-auth")]
    public async Task MissingOrUnauthenticatedRecordingHasNoChart(string missing)
    {
        using var h = new Harness();
        h.Transform = view => missing switch
        { "no-capability" => view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = [] } } },
            "no-statistics" => view with { IllustrationStatistics = null }, _ => view };
        if (missing == "no-auth") h.Auth.SignedIn = false;
        var result = await h.Session.AskStatisticsChartAsync(h.Context);
        Assert.Equal(missing == "no-auth" ? MatchChatOutcome.Disconnected : missing == "no-statistics" ? MatchChatOutcome.Answered : MatchChatOutcome.Unavailable, result.Outcome);
        if (missing != "no-statistics") Assert.All(h.Session.Turns, turn => Assert.Null(turn.StatisticsChart));
        Assert.Equal(0, h.Model.Narrations);
        if (missing == "no-statistics")
        {
            var turn = Assert.Single(h.Session.Turns);
            Assert.Contains("reported_comparison", turn.Evidence.Select(item => item.Tool));
            Assert.Equal(MatchStatisticsChartEvidenceKind.ReportedEvents, Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart).EvidenceKind);
            using var strict = new Harness(); strict.Transform = view => view with { IllustrationStatistics = null };
            Assert.Equal(MatchChatOutcome.Unavailable, (await strict.Session.AskSourceAsync(strict.Context, MatchSourceQuestionId.ReplayStatistics)).Outcome);
        }
        if (missing == "no-auth") Assert.Empty(h.Queries);
    }

    [Theory] [InlineData("cancel")] [InlineData("rewind")] [InlineData("auth")]
    public async Task InFlightChartCannotCommitAfterCancellationRewindOrSignOut(string change)
    {
        using var h = new Harness(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeRead = async () => { entered.TrySetResult(); await gate.Task; };
        var pending = h.Session.AskStatisticsChartAsync(h.Context); await entered.Task;
        if (change == "cancel") h.Session.Cancel();
        else if (change == "rewind") h.Session.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 7300 } });
        else h.Auth.SignOut();
        gate.SetResult(); Assert.False((await pending).Accepted); Assert.Empty(h.Session.Turns);
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task RealChartMarkupHasLocalizedExactValuesClockAndUnavailableDenominator(MatchLanguage language)
    {
        using var h = new Harness(); await h.Session.AskAsync(h.Context, "Show pass accuracy as a chart");
        var turn = Assert.Single(h.Session.Turns); var chart = turn.StatisticsChart!;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<MatchStatisticsChart>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(MatchStatisticsChart.Chart)] = chart, [nameof(MatchStatisticsChart.TurnId)] = turn.Id, [nameof(MatchStatisticsChart.Language)] = language }));
            var html = WebUtility.HtmlDecode(root.ToHtmlString());
            Assert.Contains(ChatText.Get(language, "Chart.Generated"), html); Assert.Contains(ChatText.Get(language, "Chart.PassAccuracy"), html);
            Assert.Contains("Home", html); Assert.Contains("Away", html); Assert.Contains("00:20", html);
            Assert.Contains(66.666m.ToString("0.#", MatchLanguages.Culture(language)) + "%", html);
            Assert.Contains(ChatText.Get(language, "Chart.NoAttempts"), html); Assert.Contains(ChatText.Get(language, "Chart.Completed", 0, 0), html);
            Assert.Contains("width:66.667%", html); Assert.Contains("width:0%", html); Assert.Contains(">100%</span>", html);
            Assert.Contains("aria-expanded=\"true\"", html); Assert.Contains("aria-controls=\"statistics-chart-" + turn.Id.ToString("N") + "\"", html);
            Assert.Contains("<dl", html); Assert.DoesNotContain("<script", html); Assert.DoesNotContain("<svg", html);
        });
    }

    [Fact]
    public async Task ShowHideAndMetricChoicesOnlyChangeLocalPresentation()
    {
        using var h = new Harness(); await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.ReplayStatistics);
        var turn = Assert.Single(h.Session.Turns); var component = new ChartComponent();
        Set(component, nameof(MatchStatisticsChart.Chart), turn.StatisticsChart); Set(component, nameof(MatchStatisticsChart.TurnId), turn.Id); component.Apply();
        using (var tree = new RenderTreeBuilder()) { component.Render(tree); Assert.DoesNotContain(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "figure"); }
        Invoke(component, typeof(MatchStatisticsChart), "Toggle"); Invoke(component, typeof(MatchStatisticsChart), "Choose", MatchStatisticsChartMetric.PassAccuracy);
        using (var tree = new RenderTreeBuilder())
        {
            component.Render(tree); var text = string.Concat(Frames(tree).Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent));
            Assert.Contains("Pass accuracy", text); Assert.Contains("66.7%", text); Assert.Contains("Unavailable", text);
            Assert.Equal(3, Frames(tree).Count(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-pressed"));
        }
        component.Apply(); // A parent render must retain the user's selected metric and open state.
        Invoke(component, typeof(MatchStatisticsChart), "Toggle");
        using (var tree = new RenderTreeBuilder()) { component.Render(tree); Assert.DoesNotContain(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "figure"); }
        Assert.Single(h.Queries); Assert.Equal(0, h.Model.Routes); Assert.Equal(0, h.Model.Narrations); Assert.Same(turn, Assert.Single(h.Session.Turns));
    }

    [Fact]
    public async Task DirectButtonPausesThenSendsInTheSameChatWithoutAi()
    {
        using var h = new Harness(); h.Model.Configured = false; using var component = new Conversation();
        Set(component, "Session", h.Session); Set(component, nameof(MatchConversation.Context), h.Context);
        var paused = false; Set(component, nameof(MatchConversation.OnAsking), EventCallback.Factory.Create(new object(), () => paused = true)); component.Apply();
        h.BeforeRead = () => { Assert.True(paused); return Task.CompletedTask; };
        using (var tree = new RenderTreeBuilder()) { component.Render(tree); Assert.Contains(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Text && frame.TextContent == "Compare teams"); }
        await (Task)Invoke(component, typeof(MatchConversation), "SendChartAsync")!;
        Assert.True(Assert.Single(h.Session.Turns).StatisticsChart!.InitiallyExpanded); Assert.Single(h.Queries); Assert.Equal(0, h.Model.Routes);
    }

    private static void Set(object component, string property, object? value) => (component.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(MatchConversation).GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))!.SetValue(component, value);
    private static object? Invoke(object component, Type type, string method, params object?[] args) => type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private sealed class ChartComponent : MatchStatisticsChart { public void Apply() => OnParametersSet(); public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }

    private sealed class Harness : IDisposable
    {
        public ChartAuth Auth { get; } = new(); public Models Model { get; } = new(); public List<FootballQuery> Queries { get; } = [];
        public MatchChatContext Context { get; } public MatchChatSession Session { get; } private readonly HttpClient _http;
        public Func<FootballView, FootballView> Transform { get; set; } = view => view;
        public Func<Task> BeforeRead { get; set; } = () => Task.CompletedTask;
        public Harness(long cursor = 20000)
        {
            var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with { Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] } };
            var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
            Add(ledger, fixture, 2, Action(fixture, "first-pass", "pass", 1000, 7400), Action(fixture, "shot", "shot", 8000, 9000, outcome: "goal"),
                Action(fixture, "second-pass", "pass", 12000, 18000), Action(fixture, "turnover", "pass", 18500, 20000, outcome: "turnover"));
            Context = new(Query(fixture, cursor), 2);
            _http = new(new Handler(async request => { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>())!; Queries.Add(query); await BeforeRead();
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Transform(ledger.Query(query))) }; })) { BaseAddress = new("https://unit.invalid/") };
            Session = new(Auth, new ReplayApiClient(_http), Model, Model, Options.Create(new MatchChatOptions())); Session.SetContext(Context);
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    private sealed class Models : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured { get; set; } = true; public int Routes, Narrations; public string? LastQuestion;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Routes++; LastQuestion = input.Question; return Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, "current_replay_statistics")); }
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { Narrations++; throw new InvalidOperationException("A statistics chart must not call the narrator."); }
    }
    private sealed class ChartAuth : AuthenticationStateProvider
    {
        public bool SignedIn { get; set; } = true;
        private AuthenticationState State() => new(SignedIn
            ? new ClaimsPrincipal(new ClaimsIdentity([new("sub", "unit-owner"), new("roles", "Tester")], "unit", "sub", "roles")) : new ClaimsPrincipal());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State());
        public void SignOut() { SignedIn = false; NotifyAuthenticationStateChanged(Task.FromResult(State())); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> read) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => read(request); }
}
#pragma warning restore BL0006
