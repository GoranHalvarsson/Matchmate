using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.League;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.LeagueChat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual component trees/lifecycle, no browser replacement host.
public sealed class LeagueChatUiTests
{
    [Fact]
    public async Task IntegratedConversationRendersClubIdentityDatedThreadAndSourceFacts()
    {
        using var session = LeagueChatFixtures.Session(); var snapshot = LeagueChatFixtures.Snapshot(1); var context = LeagueChatContext.From(snapshot); session.SetContext(context); await session.AskSourceAsync(context, "standing");
        using var component = new LeagueConversation(); Set(component, "Session", session); Set(component, "Context", context); Set(component, "Club", snapshot.SelectedClub!.Club); Set(component, "SeasonLabel", snapshot.Season.Label); Apply(component);
        await Invoke(component, "ToggleConversation");
        using var tree = Render(component); var text = Text(tree); Assert.Contains("What would you like to know?", text); Assert.Contains("Source answer", text); Assert.Contains("Asked at 2026/27", text); Assert.Contains("Supporting facts", text);
        Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(CrestBadge));
        Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "maxlength" && Equals(f.AttributeValue, "1000"));
        Assert.DoesNotContain("match-chat-paper", text); Assert.DoesNotContain("Decision-1", text);
        Assert.Contains("Ask Matchmate", text);
        var css = File.ReadAllText(Path.Combine(HostingTestPaths.Root(), "src", "Matchmate.Web", "Components", "Experience", "LeagueConversation.razor.css"));
        Assert.Contains("min-height:44px", css); Assert.Contains("min-width:0", css); Assert.Contains("@media(max-width:600px)", css);
    }
    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void InitialWelcomeHasOneComposerOneVisibleSuggestionAndAccessibleDisclosure(MatchLanguage language)
    {
        using var session = LeagueChatFixtures.Session();
        using var component = new LeagueConversation(); Set(component, "Session", session); Set(component, "Context", LeagueChatContext.From(LeagueChatFixtures.Snapshot())); Set(component, "Language", language); Apply(component);
        using var tree = Render(component);
        Assert.Contains(ConversationText.Get(language, "LeagueWelcomeEyebrow"), Text(tree)); Assert.Contains(ConversationText.Get(language, "LeagueWelcomeHeading"), Text(tree));
        Assert.Single(Elements(tree, "form")); Assert.Single(Elements(tree, "input"));
        var panel = ElementWithClass(tree, "league-conversation-panel"); Assert.True((bool)panel["hidden"]!);
        var toggle = ElementWithClass(tree, "league-conversation-toggle"); Assert.Equal("false", toggle["aria-expanded"]); Assert.Equal(panel["id"], toggle["aria-controls"]);
        var chips = ElementsWithinClass(tree, "question-chips", "button");
        Assert.Single(chips, a => !Equals(a.GetValueOrDefault("hidden"), true));
        Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task SourceSendExpandsAndCollapseReopenPreservesTurnDraftAndSingleComposer()
    {
        using var session = LeagueChatFixtures.Session(); using var component = new LeagueConversation();
        Set(component, "Session", session); Set(component, "Context", LeagueChatContext.From(LeagueChatFixtures.Snapshot(1))); Apply(component);
        await Invoke(component, "SendAsync", "changes"); var turn = Assert.Single(session.Turns); Assert.True(turn.SourceOnly); Assert.True((bool)Get(component, "_expanded")!);
        Set(component, "_question", "Tell me more about that");
        await Invoke(component, "ToggleConversation"); Assert.False((bool)Get(component, "_expanded")!);
        using (var collapsed = Render(component))
        {
            Assert.True((bool)ElementWithClass(collapsed, "league-conversation-panel")["hidden"]!);
            Assert.Single(Elements(collapsed, "form")); Assert.Equal("Tell me more about that", Assert.Single(Elements(collapsed, "input"))["value"]);
            Assert.Contains(turn.Question, Text(collapsed));
        }
        await Invoke(component, "ToggleConversation");
        Assert.Same(turn, Assert.Single(session.Turns)); Assert.Equal("Tell me more about that", Get(component, "_question"));
        using var reopened = Render(component); Assert.False(Equals(ElementWithClass(reopened, "league-conversation-panel").GetValueOrDefault("hidden"), true));
        Assert.Equal("true", ElementWithClass(reopened, "league-conversation-toggle")["aria-expanded"]);
        Assert.Equal(3, ElementsWithinClass(reopened, "question-chips", "button").Count(a => !Equals(a.GetValueOrDefault("hidden"), true)));
        Assert.Single(Elements(reopened, "form"));
        await Invoke(component, "Reset"); Assert.Empty(session.Turns); Assert.Equal("", Get(component, "_question")); Assert.False((bool)Get(component, "_expanded")!);
    }
    [Fact]
    public async Task TypedSendExpandsAndPendingReplyCanBeCancelledWhileCollapsed()
    {
        var pending = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router { Routing = (_, _) => pending.Task }; using var session = LeagueChatFixtures.Session(router: router); using var component = new LeagueConversation();
        Set(component, "Session", session); Set(component, "Context", LeagueChatContext.From(LeagueChatFixtures.Snapshot())); Apply(component); Set(component, "_question", "Where do we stand?");
        var asking = Invoke(component, "SubmitAsync"); Assert.False(asking.IsCompleted); Assert.True((bool)Get(component, "_expanded")!);
        await Invoke(component, "ToggleConversation"); Assert.True((bool)Get(component, "_pending")!); Assert.False(asking.IsCompleted);
        using (var tree = Render(component))
        {
            var pendingRow = ElementWithClass(tree, "league-pending"); Assert.False(Equals(pendingRow.GetValueOrDefault("hidden"), true));
            Assert.Contains(ConversationText.Get(MatchLanguage.English, "CancelReply"), Text(tree));
            Assert.Single(ElementsWithinClass(tree, "league-pending", "button")); Assert.Empty(ElementsWithinClass(tree, "league-conversation-panel", "button"));
        }
        await Invoke(component, "Cancel"); await asking.WaitAsync(TimeSpan.FromSeconds(2)); pending.SetResult(new(MatchRoutingStatus.Selected, "club_standing"));
        Assert.False((bool)Get(component, "_pending")!); Assert.False((bool)Get(component, "_expanded")!); Assert.Equal("Where do we stand?", Get(component, "_question")); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task AcceptedTurnRevealsOnceAfterRenderEvenAtHistoryLimitAndUnrelatedRendersDoNotJump()
    {
        using var session = LeagueChatFixtures.Session(); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1)); session.SetContext(context);
        for (var i = 0; i < MatchChatLimits.Turns; i++) Assert.True((await session.AskSourceAsync(context, "changes")).Accepted);
        var previous = session.Turns[^1].Id; var js = new ThreadJs(); await using var component = new LeagueConversation();
        Set(component, "Session", session); Set(component, "JS", js); Set(component, "Context", context); Apply(component);
        await Invoke(component, "OnAfterRenderAsync", true); Assert.Null(js.Module.Calls[^1][3]);
        await Invoke(component, "SendAsync", "changes"); var latest = session.Turns[^1].Id;
        Assert.NotEqual(previous, latest); Assert.Equal(MatchChatLimits.Turns, session.Turns.Count);
        await Invoke(component, "OnAfterRenderAsync", false); Assert.Equal(latest.ToString("N"), js.Module.Calls[^1][3]); Assert.Equal(0, js.FocusCalls);
        await Invoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]);
        await Invoke(component, "ToggleConversation"); await Invoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]);
        Set(component, "Language", MatchLanguage.Swedish); Apply(component); await Invoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]);
        Assert.Equal(1, js.Imports); Assert.All(js.Module.Calls, call => Assert.IsType<ElementReference>(call[0]));
    }
    [Fact]
    public async Task ReplyReceivedWhileCollapsedQueuesOneRevealUntilTheNextVisibleRender()
    {
        var pending = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router { Routing = (_, _) => pending.Task }; using var session = LeagueChatFixtures.Session(router: router);
        var js = new ThreadJs(); await using var component = new LeagueConversation();
        Set(component, "Session", session); Set(component, "JS", js); Set(component, "Context", LeagueChatContext.From(LeagueChatFixtures.Snapshot())); Apply(component); Set(component, "_question", "Where do we stand?");
        Set(component, "_input", new ElementReference("composer", new WebElementReferenceContext(js)));
        await Invoke(component, "OnAfterRenderAsync", true); var asking = Invoke(component, "SubmitAsync"); await Invoke(component, "OnAfterRenderAsync", false);
        Assert.True((bool)js.Module.Calls[^1][2]!); Assert.Null(js.Module.Calls[^1][3]);
        await Invoke(component, "ToggleConversation"); pending.SetResult(new(MatchRoutingStatus.Selected, "club_standing")); await asking;
        var turn = Assert.Single(session.Turns); await Invoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]); Assert.NotNull(Get(component, "_revealTurn"));
        await Invoke(component, "ToggleConversation"); await Invoke(component, "OnAfterRenderAsync", false);
        Assert.Equal(turn.Id.ToString("N"), js.Module.Calls[^1][3]); Assert.Null(Get(component, "_revealTurn"));
        Assert.Equal(1, js.FocusCalls);
        await Invoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]);
    }
    [Fact]
    public async Task TypingCallsPauseBeforeRoutingAndContextUpdateCancelsPendingReply()
    {
        var paused = false; var pending = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router { Routing = (_, _) => { Assert.True(paused); return pending.Task; } }; using var session = LeagueChatFixtures.Session(router: router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); using var component = new LeagueConversation(); Set(component, "Session", session); Set(component, "Context", context); Set(component, "OnAsking", EventCallback.Factory.Create(new object(), (Action)(() => paused = true))); Apply(component);
        Set(component, "_question", "Where do we stand?"); var asking = Invoke(component, "SubmitAsync"); Assert.False(asking.IsCompleted); Assert.True(paused);
        Set(component, "Updating", true); Apply(component); await asking.WaitAsync(TimeSpan.FromSeconds(2)); pending.SetResult(new(MatchRoutingStatus.Selected, "club_standing"));
        Assert.Empty(session.Turns); Assert.False((bool)Get(component, "_pending")!); using var tree = Render(component); Assert.Contains("updating or unavailable", Text(tree));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetainedViewDuringDelayedOrFailedUpdateCannotAnswerAgainstOldContext(bool fail)
    {
        var snapshot = LeagueChatFixtures.Snapshot(1); using var session = LeagueChatFixtures.Session(); var context = LeagueChatContext.From(snapshot); session.SetContext(context); await session.AskSourceAsync(context, "standing");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => pending.Task)) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, session, snapshot);
        var seeking = Invoke(page, "SeekAsync", 2); Assert.Same(snapshot, Get(page, "_snapshot")); Assert.False((await session.AskAsync(context, "Where do we stand?")).Accepted);
        pending.SetResult(fail ? new(HttpStatusCode.ServiceUnavailable) : Json(LeagueChatFixtures.Snapshot(2))); await seeking;
        if (fail) { Assert.Null(Property(page, "ConversationContext")); Assert.False((await session.AskSourceAsync(context, "standing")).Accepted); }
        else { var committed = Assert.IsType<LeagueChatContext>(Property(page, "ConversationContext")); Assert.Equal(2, committed.DateIndex); Assert.True((await session.AskSourceAsync(committed, "standing")).Accepted); }
        Assert.Equal(1, session.Turns[0].Context.DateIndex);
    }
    [Fact]
    public async Task AskingCancelsInFlightPlaybackRestoresStatusAndProtectsCommittedDate()
    {
        var initial = LeagueChatFixtures.Snapshot(1); using var session = LeagueChatFixtures.Session(); var context = LeagueChatContext.From(initial); session.SetContext(context);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => { entered.TrySetResult(); return pending.Task; })) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, session, initial);
        var playback = new CancellationTokenSource(); Set(page, "_playing", true); Set(page, "_speed", 4); Set(page, "_playback", playback);
        var playing = Invoke(page, "PlayAsync", (long)Get(page, "_intent")!, playback.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True((bool)Get(page, "_loading")!);
        await Invoke(page, "PauseForConversation"); Assert.False((bool)Get(page, "_playing")!); Assert.False((bool)Get(page, "_loading")!); Assert.StartsWith("Showing 2026/27", (string)Get(page, "_status")!);
        Assert.Equal(context, Property(page, "ConversationContext")); Assert.True((await session.AskSourceAsync(context, "standing")).Accepted);
        pending.SetResult(Json(LeagueChatFixtures.Snapshot(2))); await playing; Assert.Same(initial, Get(page, "_snapshot")); Assert.Equal(1, Get(page, "_index"));
    }
    [Fact]
    public async Task ExplicitClickOnAlreadySelectedClubClearsPriorNamedSubjectAndDepartureInvalidatesChat()
    {
        var snapshot = LeagueChatFixtures.Snapshot(1); var router = new LeagueChatFixtures.Router(); using var session = LeagueChatFixtures.Session(router: router); var context = LeagueChatContext.From(snapshot); session.SetContext(context); await session.AskAsync(context, "How are Arsenal doing?");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(snapshot)))) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, session, snapshot);
        await Invoke(page, "SelectClubAsync", "everton"); router.Workflow = "ground_context"; await session.AskAsync(context, "Tell me about their ground?");
        Assert.False(router.Inputs[^1].HasNamedClubReference); Assert.Equal("everton", session.Turns[^1].ResolvedClubId); Assert.Equal(2, session.Turns.Count);
        await ((Nav)Property(page, "Navigation")!).LeaveAsync(); Assert.False((await session.AskSourceAsync(context, "standing")).Accepted); Assert.Null(Property(page, "ConversationContext"));
    }
    [Fact]
    public async Task LeaguePagePassesActualSeasonAndContextRatherThanLiteralRazorAttributeText()
    {
        using var session = LeagueChatFixtures.Session(); var snapshot = LeagueChatFixtures.Snapshot(1); using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(snapshot)))) { BaseAddress = new("https://test.invalid/") };
        var page = Page(http, session, snapshot); using var tree = Render(page); var frames = Frames(tree);
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(LeagueConversation));
        Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(LeagueConversation));
        Assert.True(Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(LeagueConversation)) < Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(SeasonPlayback)));
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "SeasonLabel" && Equals(f.AttributeValue, "2026/27"));
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "ResultsThrough" && Equals(f.AttributeValue, "2026-09-20"));
        await page.DisposeAsync();
    }
    private static LeagueExplorer Page(HttpClient http, LeagueChatSession session, LeagueSeasonSnapshot snapshot)
    {
        var page = new LeagueExplorer(); Set(page, "Api", new ReplayApiClient(http)); Set(page, "ConversationSession", session); Set(page, "Navigation", new Nav());
        Set(page, "_snapshot", snapshot); Set(page, "_seasons", new[] { snapshot.Season }); Set(page, "_season", snapshot.Season.SeasonKey); Set(page, "_index", snapshot.DateIndex); Set(page, "_club", snapshot.SelectedClub?.Club.Id);
        typeof(LeagueExplorer).GetMethod("OnAfterRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [true]); return page;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Nav : NavigationManager
    {
        public Nav() => Initialize("https://localhost:7410/", "https://localhost:7410/?season=2026-27&date=1&club=everton");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        protected override void SetNavigationLockState(bool value) { }
        public async Task LeaveAsync() { const string uri = "https://localhost:7410/recordings/brentford-existing-illustration"; if (await NotifyLocationChangingAsync(uri, null, true)) Uri = uri; }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static void Set(object target, string name, object? value) { var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (p is not null) p.SetValue(target, value); else target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value); }
    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static Task Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args) as Task ?? Task.CompletedTask;
    private static void Apply(object target) => target.GetType().GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static RenderTreeBuilder Render(object target) { var tree = new RenderTreeBuilder(); target.GetType().GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [tree]); return tree; }
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree)
    {
        var frames = tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
        var nested = new List<RenderTreeFrame>(frames);
        foreach (var fragment in frames.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).Select(frame => frame.AttributeValue).OfType<RenderFragment>())
        {
            using var child = new RenderTreeBuilder(); fragment(child); nested.AddRange(Frames(child));
        }
        return nested.ToArray();
    }
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private static Dictionary<string, object?> Attributes(RenderTreeFrame[] frames, int index)
    {
        var attributes = new Dictionary<string, object?>();
        for (var i = index + 1; i < frames.Length && frames[i].FrameType == RenderTreeFrameType.Attribute; i++) attributes[frames[i].AttributeName] = frames[i].AttributeValue;
        return attributes;
    }
    private static Dictionary<string, object?>[] Elements(RenderTreeBuilder tree, string element) => Frames(tree).Select((frame, index) => (frame, index)).Where(f => f.frame.FrameType == RenderTreeFrameType.Element && f.frame.ElementName == element).Select(f => Attributes(Frames(tree), f.index)).ToArray();
    private static Dictionary<string, object?> ElementWithClass(RenderTreeBuilder tree, string className) => Frames(tree).Select((frame, index) => (frame, index)).Where(f => f.frame.FrameType == RenderTreeFrameType.Element).Select(f => Attributes(Frames(tree), f.index)).Single(a => a.GetValueOrDefault("class")?.ToString()?.Split(' ').Contains(className) == true);
    private static Dictionary<string, object?>[] ElementsWithinClass(RenderTreeBuilder tree, string className, string element)
    {
        var frames = Frames(tree); var start = Enumerable.Range(0, frames.Length).First(i => frames[i].FrameType == RenderTreeFrameType.Element && Attributes(frames, i).GetValueOrDefault("class")?.ToString()?.Split(' ').Contains(className) == true);
        Assert.True(start >= 0); var end = start + frames[start].ElementSubtreeLength;
        return Enumerable.Range(start + 1, end - start - 1).Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == element).Select(i => Attributes(frames, i)).ToArray();
    }
    private sealed class ThreadJs : IJSRuntime
    {
        public ThreadModule Module { get; } = new();
        public int Imports { get; private set; }
        public int FocusCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        { try { return Record<TValue>(identifier, args); } catch (JSException) { throw; } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        { try { return Record<TValue>(identifier, args); } catch (JSException) { throw; } }
        private ValueTask<TValue> Record<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "Blazor._internal.domWrapper.focus") { FocusCalls++; return ValueTask.FromResult(default(TValue)!); }
            Assert.Equal("import", identifier); Assert.Equal("./Components/Experience/LeagueConversation.razor.js", Assert.Single(args!)); Imports++;
            return ValueTask.FromResult((TValue)(object)Module);
        }
    }
    private sealed class ThreadModule : IJSObjectReference
    {
        public List<object?[]> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        { try { return Record<TValue>(identifier, args); } catch (JSException) { throw; } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        { try { return Record<TValue>(identifier, args); } catch (JSException) { throw; } }
        private ValueTask<TValue> Record<TValue>(string identifier, object?[]? args)
        {
            Assert.Equal("updateThread", identifier); Calls.Add(args!); return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#pragma warning restore BL0006
