using System.Net;
using System.Reflection;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Experience;
using Matchmate.Web.HistoricalChat;
using Matchmate.Web.LeagueChat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual component rendering/lifecycle; no replacement app host.
public sealed class SiteConversationLocalizationTests
{
    public static IEnumerable<object[]> LanguageStages => from language in new[] { MatchLanguage.Swedish, MatchLanguage.Spanish }
        from stage in new[] { "read", "router", "narrator" } select new object[] { language, stage };
    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task LeagueUsesLocalizedMainSourceProseAndKeepsOriginalSupportingText(MatchLanguage language)
    {
        using var session = LeagueChatFixtures.Session(); var snapshot = LeagueChatFixtures.Snapshot(2); var context = LeagueChatContext.From(snapshot);
        session.SetContext(context); session.SetLanguage(language); Assert.True((await session.AskSourceAsync(context, "standing")).Accepted);
        var turn = Assert.Single(session.Turns); var answer = turn.Answer;
        using var raw = JsonDocument.Parse(turn.Evidence[0].Text); var sourceAnswer = raw.RootElement.GetProperty("sourceAnswer").GetString()!;
        var html = WebUtility.HtmlDecode(await Html<LeagueConversation>(session, language, new()
        { [nameof(LeagueConversation.Context)] = context, [nameof(LeagueConversation.Club)] = snapshot.SelectedClub!.Club, [nameof(LeagueConversation.SeasonLabel)] = snapshot.Season.Label }));
        Assert.Contains(ConversationText.Get(language, "LeagueHeading"), html); Assert.Contains(ConversationText.Get(language, "AskLeague"), html);
        Assert.Contains(LeagueChatPresentation.DisplayQuestion(LeagueWorkflowRegistry.Suggestions(true)[0], language), html);
        Assert.Contains("lang=\"en\"", html); Assert.Contains(sourceAnswer, html);
        if (language != MatchLanguage.English)
        {
            var main = html[..html.IndexOf("<details", StringComparison.Ordinal)];
            Assert.Contains(answer, main); Assert.DoesNotContain(sourceAnswer, main); Assert.NotEqual(sourceAnswer, answer);
        }
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task HistoricalSuggestionsAndAccessibilityTranslateWhileRawEvidenceStaysEnglish(MatchLanguage language)
    {
        using var session = Historical(); var context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
        session.SetContext(context); session.SetLanguage(language); Assert.True((await session.AskSuggestionAsync(context, "result")).Accepted);
        var turn = Assert.Single(session.Turns); var answer = turn.Answer;
        using var raw = JsonDocument.Parse(turn.Evidence[0].Text); var sourceAnswer = raw.RootElement.GetProperty("sourceAnswer").GetString()!;
        var html = WebUtility.HtmlDecode(await Html<HistoricalConversation>(session, language, new() { [nameof(HistoricalConversation.Context)] = context }));
        Assert.Contains(ConversationText.Get(language, "MatchHeading"), html); Assert.Contains(ConversationText.Get(language, "AskMatch"), html);
        Assert.Contains("aria-label=\"" + ConversationText.Get(language, "MatchConversation") + "\"", html);
        foreach (var suggestion in HistoricalWorkflows.Suggestions)
        {
            var title = HistoricalChatPresentation.DisplayTitle(suggestion, language); var question = HistoricalChatPresentation.DisplayQuestion(suggestion, language);
            Assert.Contains(title, html); Assert.Contains(question, html); Assert.Contains("aria-label=\"" + title + ": " + question + "\"", html);
        }
        Assert.Contains(answer, html); Assert.Contains(sourceAnswer, html); Assert.Matches(@"<span\b[^>]*\blang=""en""[^>]*>", html);
    }

    [Theory]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task LeagueFlipDuringAwaitedPausePreservesDraftAndOriginalHistoryWithoutNewIo(MatchLanguage language)
    {
        var reads = 0; var router = new LeagueChatFixtures.Router();
        using var session = LeagueChatFixtures.Session(router: router, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var snapshot = LeagueChatFixtures.Snapshot(2); var context = LeagueChatContext.From(snapshot); session.SetContext(context);
        Assert.True((await session.AskSourceAsync(context, "standing")).Accepted); var original = Assert.Single(session.Turns);
        using var component = new LeagueConversation(); Set(component, "Session", session); Set(component, "Context", context); Apply(component);
        Set(component, "_question", "My unsent words"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(component, "OnAsking", EventCallback.Factory.Create(new object(), async () => { entered.SetResult(); await gate.Task; }));
        var baseline = reads; var asking = Invoke(component, "SubmitAsync"); await entered.Task;
        Set(component, "Language", language); Apply(component); gate.SetResult(); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("My unsent words", Field(component, "_question")); Assert.False((bool)Field(component, "_pending")!);
        Assert.Same(original, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, original.Language);
        Assert.Equal(language, session.Language); Assert.Equal(baseline, reads); Assert.Empty(router.Inputs);
        using var tree = Render(component); Assert.Contains(ConversationText.Get(language, "PendingCancelledDraft"), Text(tree));
        Assert.Contains(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "lang" && Equals(frame.AttributeValue, "en"));
        var html = WebUtility.HtmlDecode(await Html<LeagueConversation>(session, language, new() { [nameof(LeagueConversation.Context)] = context }));
        Assert.Contains(original.Answer, html);
    }

    [Theory]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task HistoricalFlipCancelsPendingIntentWithoutExtraReadsOrErasingDraftAndHistory(MatchLanguage language)
    {
        var reads = 0; var fixture = HistoricalChatFixtures.Fixture(); var snapshot = HistoricalChatFixtures.Model().Snapshot(null);
        var evidence = new HistoricalEvidence((_, _, _) => { reads++; return Task.FromResult(fixture); }, (_, _, _, _) => { reads++; return Task.FromResult(snapshot); });
        var model = new HistoricalChatSessionTests.Model();
        using var session = Historical(model, evidence); var context = HistoricalChatContext.From(fixture); session.SetContext(context);
        Assert.True((await session.AskSuggestionAsync(context, "result")).Accepted); var original = Assert.Single(session.Turns);
        model.Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously); var initialModelCalls = model.Requests.Count;
        await using var component = new HistoricalConversation(); Set(component, "Session", session); Set(component, "Context", context); Apply(component); Set(component, "_question", "My unsent follow-up");
        var asking = Invoke(component, "SubmitAsync"); Assert.False(asking.IsCompleted); Assert.Equal(initialModelCalls + 1, model.Requests.Count); var baseline = reads;
        Set(component, "Language", language); Apply(component);
        model.Delayed.SetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), [])); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("My unsent follow-up", Field(component, "_question")); Assert.False((bool)Field(component, "_pending")!); Assert.Equal("", Field(component, "_pendingQuestion"));
        Assert.Same(original, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, original.Language); Assert.Equal(language, session.Language);
        Assert.Equal(baseline, reads); Assert.Equal(initialModelCalls + 1, model.Requests.Count);
        using var tree = Render(component); Assert.Contains(original.Answer, Text(tree)); Assert.Contains(ConversationText.Get(language, "PendingCancelledDraft"), Text(tree));
    }

    [Theory, MemberData(nameof(LanguageStages))]
    public async Task F_AL_001_LeagueComponentLanguageFlipPreservesAcceptedClubAtEveryPendingStage(MatchLanguage language, string stage)
    {
        var delay = false; var reads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause() { entered.TrySetResult(); await release.Task; }
        var router = new LeagueChatFixtures.Router { Routing = async (input, _) =>
        {
            if (delay && stage == "router") await Pause();
            return new(MatchRoutingStatus.Selected, input.Question.Contains("Arsenal", StringComparison.Ordinal) ? "named_club_standing" : input.Question == "Tell me more" ? "continue_topic" : "ground_context");
        } };
        var replySource = new LeagueChatFixtures.Model();
        var model = new LeagueChatFixtures.Model { Completing = async (request, token) =>
        { if (delay && stage == "narrator") await Pause(); return await replySource.CompleteAsync(request, token); } };
        var executor = new LeagueWorkflowExecutor(async (season, index, club, _) =>
        { reads++; if (delay && stage == "read") await Pause(); return LeagueChatFixtures.Snapshot(index, club, season == "2015-16"); });
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: executor);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "How are Arsenal doing?")).Accepted);
        var original = Assert.Single(session.Turns); Assert.Equal("arsenal", original.ResolvedClubId);
        using var component = new LeagueConversation(); Set(component, "Session", session); Set(component, "Context", context); Apply(component);
        Set(component, "_question", "Tell me more"); delay = true;
        var pending = Invoke(component, "SubmitAsync"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readCount = reads; var routeCount = router.Inputs.Count; var modelCount = model.Requests.Count;
        Set(component, "Language", language); Apply(component); release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Tell me more", Field(component, "_question")); Assert.False((bool)Field(component, "_pending")!);
        Assert.Same(original, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, original.Language);
        Assert.Equal(readCount, reads); Assert.Equal(routeCount, router.Inputs.Count); Assert.Equal(modelCount, model.Requests.Count);
        delay = false; Set(component, "_question", "Tell me about their ground?"); await Invoke(component, "SubmitAsync");
        Assert.Equal(2, session.Turns.Count); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        Assert.Equal(language, session.Turns[^1].Language); Assert.True(router.Inputs[^1].HasNamedClubReference);
        Assert.Equal("", Field(component, "_question"));
    }

    [Theory, MemberData(nameof(LanguageStages))]
    public async Task F_AL_002_HistoricalComponentLanguageFlipPreservesAcceptedTopicAtEveryPendingStage(MatchLanguage language, string stage)
    {
        var fixture = HistoricalChatFixtures.Fixture(); var delay = false; var reads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause() { entered.TrySetResult(); await release.Task; }
        var evidence = new HistoricalEvidence(async (_, _, _) =>
        { reads++; if (delay && stage == "read") await Pause(); return fixture; }, (_, _, _, _) =>
        { reads++; return Task.FromResult(HistoricalChatFixtures.Model().Snapshot(null)); });
        var router = new GatedHistoricalRouter(async _ =>
        { if (delay && stage == "router") await Pause(); return new(MatchRoutingStatus.Selected, "overview"); });
        var model = new GatedHistoricalModel(async _ =>
        { if (delay && stage == "narrator") await Pause(); return new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []); });
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), evidence, router, model, Options.Create(new MatchChatOptions()));
        var context = HistoricalChatContext.From(fixture); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "Explain Southampton's result")).Accepted);
        var original = Assert.Single(session.Turns); Assert.Equal("southampton", original.ClubId);
        await using var component = new HistoricalConversation(); Set(component, "Session", session); Set(component, "Context", context); Apply(component);
        Set(component, "_question", "Tell me more"); delay = true;
        var pending = Invoke(component, "SubmitAsync"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readCount = reads; var routeCount = router.Inputs.Count; var modelCount = model.Requests.Count;
        Set(component, "Language", language); Apply(component); release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Tell me more", Field(component, "_question")); Assert.False((bool)Field(component, "_pending")!);
        Assert.Same(original, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, original.Language);
        Assert.Equal(readCount, reads); Assert.Equal(routeCount, router.Inputs.Count); Assert.Equal(modelCount, model.Requests.Count);
        delay = false; await Invoke(component, "SubmitAsync");
        Assert.Equal(2, session.Turns.Count); Assert.Equal("southampton", router.Inputs[^1].Topic!.ClubId);
        Assert.Equal("overview", router.Inputs[^1].Topic!.Workflow); Assert.Equal("southampton", session.Turns[^1].ClubId);
        Assert.Equal(language, session.Turns[^1].Language); Assert.Equal("", Field(component, "_question"));
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task DateChangeFactsTranslateWrappersAndDatesWithoutChangingPreparedFields(MatchLanguage language)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, null));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("date_changes", context, "What changed on this date?", null, default);
        var fact = result.Evidence.Single(e => e.Id == "date_change"); var original = fact.Text;
        var component = new LeagueSourceFacts(); Set(component, "Evidence", fact); Set(component, "Language", language); Apply(component);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LeagueSourceFacts>(ParameterView.FromDictionary(new Dictionary<string, object?>
        { [nameof(LeagueSourceFacts.Evidence)] = fact, [nameof(LeagueSourceFacts.Language)] = language }))).ToHtmlString()));
        Assert.Contains(ConversationText.Get(language, "ResultsOnDate"), html); Assert.Contains(ConversationText.Get(language, "MovementCaption"), html);
        Assert.Contains(ExperiencePresentation.Date(context.Date, language), html); Assert.Contains("lang=\"en\"", html);
        Assert.Equal(20, RegexCount(html, "scope=\"row\"")); Assert.Equal(10, RegexCount(html, "<time ")); Assert.Equal(original, fact.Text);
        Assert.Equal("Unranked", LeagueSourcePresentation.Position(null, false));
        Assert.Equal(LeagueText.Get(language, "SharedPosition", 2), LeagueSourcePresentation.Position(2, true, language));
    }

    private static HistoricalChatSession Historical(HistoricalChatSessionTests.Model? model = null, HistoricalEvidence? evidence = null)
        => new(new HistoricalChatSessionTests.Auth(), evidence ?? HistoricalChatFixtures.Evidence(), new HistoricalChatSessionTests.Router("overview"), model ?? new HistoricalChatSessionTests.Model(), Options.Create(new MatchChatOptions()));
    private sealed class GatedHistoricalRouter(Func<HistoricalRoutingInput, Task<MatchRoutingResult>> route) : IHistoricalWorkflowRouter
    {
        public bool Configured => true;
        public List<HistoricalRoutingInput> Inputs { get; } = [];
        public Task<MatchRoutingResult> RouteAsync(HistoricalRoutingInput input, CancellationToken token) { Inputs.Add(input); return route(input); }
    }
    private sealed class GatedHistoricalModel(Func<HistoricalNarratorRequest, Task<MatchModelReply>> complete) : IHistoricalChatModel
    {
        public bool Configured => true;
        public List<HistoricalNarratorRequest> Requests { get; } = [];
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token) { Requests.Add(request); return complete(request); }
    }
    private static async Task<string> Html<T>(object session, MatchLanguage language, Dictionary<string, object?> parameters) where T : IComponent
    {
        var registrations = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()); registrations.AddSingleton(session.GetType(), session);
        using var services = registrations.BuildServiceProvider(); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(CascadingValue<MatchLanguage>.Value)] = language,
            [nameof(CascadingValue<MatchLanguage>.ChildContent)] = (RenderFragment)(builder => { builder.OpenComponent<T>(0); builder.AddMultipleAttributes(1, parameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!))); builder.CloseComponent(); })
        }))).ToHtmlString());
    }
    private static int RegexCount(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
    private static void Set(object target, string name, object? value) { var type = target.GetType(); if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } property) property.SetValue(target, value); else type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value); }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static Task Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null) as Task ?? Task.CompletedTask;
    private static void Apply(object target) => target.GetType().GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static RenderTreeBuilder Render(object target) { var tree = new RenderTreeBuilder(); target.GetType().GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [tree]); return tree; }
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent));
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
    }
}
#pragma warning restore BL0006
