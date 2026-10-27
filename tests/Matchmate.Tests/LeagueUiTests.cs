using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.League;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual generated component trees, not a browser substitute host.
public sealed class LeagueUiTests
{
    [Fact]
    public async Task ExplicitOlderSeasonAndClubQueryRejectsLateResponseAfterNewSeasonSelection()
    {
        var old = Snapshot("2015-16", "west-ham"); var newer = Snapshot("2016-17", "west-ham");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/api/league/seasons") return Task.FromResult(Json(new[] { old.Season, newer.Season }));
            return request.RequestUri.AbsolutePath.EndsWith("2015-16", StringComparison.Ordinal) ? pending.Task : Task.FromResult(Json(newer));
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = Page(http);
        Set(page, "Season", "2015-16"); Set(page, "Club", "west-ham");
        var first = Invoke(page, "OnParametersSetAsync");
        Assert.Contains("/api/league/seasons/2015-16?clubId=west-ham", requests); Assert.Null(Get(page, "_snapshot"));
        Set(page, "Season", "2016-17"); Set(page, "Club", "west-ham"); await Invoke(page, "OnParametersSetAsync");
        pending.SetResult(Json(old)); await first;
        var accepted = Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot"));
        Assert.Equal("2016-17", accepted.Season.SeasonKey); Assert.Equal(accepted.Season.PlayedDates.Length, accepted.DateIndex);
        Assert.False((bool)Get(page, "_playing")!);
    }
    [Fact]
    public async Task RemovedClubClearsOnTrueSeasonChangeButFailedDateRetainsExplicitCommittedContext()
    {
        var first = Snapshot("2015-16", "west-ham"); var next = Snapshot("2016-17", "new-club") with { SelectedClub = null };
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var switchingSeason = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/seasons", StringComparison.Ordinal)) return Task.FromResult(Json(new[] { first.Season, next.Season }));
            if (request.RequestUri.Query.Contains("dateIndex=0", StringComparison.Ordinal)) return pending.Task;
            if (request.RequestUri.Query.Contains("clubId=west-ham", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return switchingSeason.Task;
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = Page(http, first); Set(page, "_seasons", new[] { first.Season, next.Season }); Set(page, "Season", "2016-17"); Set(page, "Club", "west-ham");
        var switching = Invoke(page, "OnParametersSetAsync"); Assert.Null(Get(page, "_snapshot"));
        switchingSeason.SetResult(Json(next)); await switching; Assert.Null(Get(page, "_club"));
        var committed = Get(page, "_snapshot"); var uri = Nav(page).Uri;
        Set(page, "DateIndex", 0); Set(page, "Club", null); var seeking = Invoke(page, "OnParametersSetAsync"); Assert.Same(committed, Get(page, "_snapshot"));
        pending.SetResult(new(HttpStatusCode.ServiceUnavailable)); await seeking;
        Assert.Same(committed, Get(page, "_snapshot")); Assert.Equal(next.DateIndex, Get(page, "_index")); Assert.Equal(uri, Nav(page).Uri);
        Assert.True((bool)Get(page, "_loadError")!); Assert.Contains("Still showing 2016/17", (string)Get(page, "_status")!);
        Assert.Contains(ExperiencePresentation.Date(next.Date), (string)Get(page, "_status")!);
    }
    [Fact]
    public async Task PlayingCompletedSeasonStartsAtUnrankedZeroAndMovesForwardOnly()
    {
        var complete = Snapshot("2015-16", "west-ham"); var zero = Snapshot("2015-16", "west-ham", 0);
        using var handler = new Handler((_, _) => Task.FromResult(Json(zero)));
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = Page(http, complete);
        var playing = Invoke(page, "ToggleAsync");
        Assert.Equal(0, Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).DateIndex);
        Assert.True((bool)Get(page, "_playing")!); Assert.All(zero.Rows, row => Assert.Null(row.Rank));
        await Invoke(page, "Pause"); await playing; Assert.False((bool)Get(page, "_playing")!);
    }
    [Fact]
    public async Task F_CURRENT_001_RelativeQueryNavigationKeepsRepeatedCallbacksAndPlaybackActive()
    {
        var old = Snapshot("2015-16", "west-ham"); var current = Snapshot("2026-27", "west-ham");
        using var handler = new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            var season = uri.AbsolutePath.Split('/')[^1];
            var index = uri.Query.Contains("dateIndex=0", StringComparison.Ordinal) ? 0 : (int?)null;
            var selected = uri.Query.Contains("clubId=other", StringComparison.Ordinal) ? "other" : "west-ham";
            return Task.FromResult(Json(Snapshot(season, "west-ham", index, selected)));
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = Page(http, old); Set(page, "_seasons", new[] { old.Season, current.Season });
        Nav(page).QueryChanged = async uri =>
        {
            var query = new Uri(uri).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Set(page, "Season", query["season"]); Set(page, "DateIndex", int.Parse(query["date"], CultureInfo.InvariantCulture));
            Set(page, "Club", query.GetValueOrDefault("club")); await Invoke(page, "OnParametersSetAsync");
        };
        await Invoke(page, "ChangeSeasonAsync", "2026-27"); await Nav(page).LastNavigation;
        Assert.False((bool)Get(page, "_navigatingAway")!);
        await Invoke(page, "SelectClubAsync", "other"); await Nav(page).LastNavigation;
        Assert.Equal("other", Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).SelectedClub!.Club.Id);
        await Invoke(page, "ChangeSeasonAsync", "2015-16"); await Nav(page).LastNavigation;
        Assert.Equal("2015-16", Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).Season.SeasonKey);
        var playing = Invoke(page, "ToggleAsync"); await Nav(page).LastNavigation;
        Assert.True((bool)Get(page, "_playing")!); Assert.Equal(0, Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).DateIndex);
        await Invoke(page, "Pause"); await playing;
        await Invoke(page, "SeekAsync", 2); await Nav(page).LastNavigation;
        Assert.Equal(2, Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).DateIndex);
        await page.DisposeAsync(); var updates = Nav(page).Updates;
        await Invoke(page, "ChangeSeasonAsync", "2026-27"); Assert.Equal(updates, Nav(page).Updates);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DelayedOrFailedClubSelectionKeepsWorkspaceAndCommittedContextMounted(bool fail)
    {
        var current = Snapshot("2015-16", "west-ham"); var next = Snapshot("2015-16", "west-ham", selected: "other");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => pending.Task); using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = Page(http, current); var uri = Nav(page).Uri;
        var selecting = Invoke(page, "SelectClubAsync", "other"); Assert.False(selecting.IsCompleted);
        Assert.Same(current, Get(page, "_snapshot")); Assert.Equal("west-ham", Get(page, "_club")); Assert.Equal(current.DateIndex, Get(page, "_index")); Assert.Equal(uri, Nav(page).Uri);
        Assert.True((bool)Get(page, "_loading")!); Assert.Contains("Showing 2015/16", (string)Get(page, "_status")!);
        using (var builder = Render(page))
        {
            var nodes = Frames(builder);
            Assert.Contains(nodes, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(LeagueStandings));
            Assert.Contains(nodes, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(LeagueMap));
            Assert.Contains(nodes, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Selected" && Equals(f.AttributeValue, "west-ham"));
            Assert.DoesNotContain("Finding the season", Text(builder));
        }
        pending.SetResult(fail ? new(HttpStatusCode.ServiceUnavailable) : Json(next)); await selecting;
        if (fail)
        {
            Assert.Same(current, Get(page, "_snapshot")); Assert.Equal("west-ham", Get(page, "_club")); Assert.Equal(uri, Nav(page).Uri);
            Assert.True((bool)Get(page, "_loadError")!); Assert.Contains("Still showing", (string)Get(page, "_status")!);
            using var builder = Render(page); Assert.Contains("Retry update", Text(builder));
        }
        else { Assert.Equal("other", Get(page, "_club")); Assert.Equal("other", Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")).SelectedClub!.Club.Id); Assert.EndsWith("&club=other", Nav(page).Uri); }
        Assert.False(Nav(page).ForcedDocumentLoad);
    }
    [Fact]
    public async Task LatestSeekWinsOverLateClubClickAndRouteEchoDoesNotFetchAgain()
    {
        var current = Snapshot("2015-16", "west-ham"); var clicked = Snapshot("2015-16", "west-ham", selected: "other"); var sought = Snapshot("2015-16", "west-ham", 1);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new Handler((request, _) => { calls++; return request.RequestUri!.Query.Contains("clubId=other", StringComparison.Ordinal) ? pending.Task : Task.FromResult(Json(sought)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, current);
        var clicking = Invoke(page, "SelectClubAsync", "other"); await Invoke(page, "SeekAsync", 1);
        pending.SetResult(Json(clicked)); await clicking;
        var accepted = Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")); Assert.Equal(1, accepted.DateIndex); Assert.Equal("west-ham", accepted.SelectedClub!.Club.Id);
        Assert.EndsWith("?season=2015-16&date=1&club=west-ham", Nav(page).Uri); Assert.Equal(1, Nav(page).Updates);
        Set(page, "Season", "2015-16"); Set(page, "DateIndex", 1); Set(page, "Club", "west-ham"); await Invoke(page, "OnParametersSetAsync");
        Assert.Equal(2, calls); Assert.Equal(1, Nav(page).Updates); Assert.Same(accepted, Get(page, "_snapshot")); Assert.False((bool)Get(page, "_loading")!);
    }
    [Fact]
    public async Task LatePlaybackLoadCannotCommitRememberOrStopNewerSelectionAndPlayIntent()
    {
        var zero = Snapshot("2015-16", "west-ham", 0); var tick = Snapshot("2015-16", "west-ham", 1); var selected = Snapshot("2015-16", "west-ham", 0, "other");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => { if (request.RequestUri!.Query.Contains("dateIndex=1", StringComparison.Ordinal)) { entered.TrySetResult(); return pending.Task; } return Task.FromResult(Json(selected)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, zero); Set(page, "_speed", 4);
        var playing = Invoke(page, "ToggleAsync"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Same(zero, Get(page, "_snapshot"));
        await Invoke(page, "SelectClubAsync", "other"); var latest = Get(page, "_snapshot"); var uri = Nav(page).Uri;
        var restarting = Invoke(page, "ToggleAsync"); Assert.False(restarting.IsCompleted);
        pending.SetResult(Json(tick)); await playing;
        for (var attempt = 0; attempt < 100 && !(bool)Get(page, "_playing")!; attempt++) await Task.Delay(5);
        Assert.Same(latest, Get(page, "_snapshot")); Assert.Equal("other", Get(page, "_club")); Assert.Equal(0, Get(page, "_index")); Assert.Equal(uri, Nav(page).Uri); Assert.Equal(1, Nav(page).Updates);
        Assert.True((bool)Get(page, "_playing")!); await Invoke(page, "Pause"); await restarting;
    }
    [Fact]
    public async Task OldEndResetCannotStartAfterNewSelectionCommits()
    {
        var completed = Snapshot("2015-16", "west-ham"); var zero = Snapshot("2015-16", "west-ham", 0); var selected = Snapshot("2015-16", "west-ham", selected: "other");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => request.RequestUri!.Query.Contains("dateIndex=0", StringComparison.Ordinal) ? pending.Task : Task.FromResult(Json(selected)));
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, completed);
        var starting = Invoke(page, "ToggleAsync"); Assert.Same(completed, Get(page, "_snapshot")); await Invoke(page, "SelectClubAsync", "other");
        var accepted = Get(page, "_snapshot"); pending.SetResult(Json(zero)); await starting;
        Assert.Same(accepted, Get(page, "_snapshot")); Assert.Equal(completed.DateIndex, Get(page, "_index")); Assert.False((bool)Get(page, "_playing")!); Assert.EndsWith("&club=other", Nav(page).Uri);
    }
    [Fact]
    public async Task StandingsInteropImportsOnceAndCleansUpWithoutPerUpdateCalls()
    {
        var js = new RecordingJs(); var component = new LeagueStandings(); Set(component, "JS", js);
        await Invoke(component, "OnAfterRenderAsync", true); await Invoke(component, "OnAfterRenderAsync", false);
        Assert.Equal(new[] { "import", "initialize" }, js.Calls); await component.DisposeAsync(); await component.DisposeAsync();
        Assert.Equal(new[] { "import", "initialize", "dispose", "module-dispose" }, js.Calls);
        var disconnected = new RecordingJs { DisconnectCleanup = true }; await using var motion = new StandingsMotionInterop(disconnected);
        await motion.InitializeAsync(default); await motion.DisposeAsync(); Assert.Contains("module-dispose", disconnected.Calls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task F_SMTH_002_RecordingLinkCannotBeRewrittenByOutgoingLeagueQueryReset(bool requestPending)
    {
        var current = Snapshot("2015-16", "west-ham"); var sought = Snapshot("2015-16", "west-ham", 1);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new Handler((_, _) => { calls++; return requestPending ? pending.Task : Task.FromResult(Json(current)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var page = Page(http, current);
        var loading = requestPending ? Invoke(page, "SeekAsync", 1) : Task.CompletedTask;
        var destination = "https://localhost:7410/recordings/brentford-existing-illustration";
        await Nav(page).NavigateAsLinkAsync(destination);
        Assert.Equal(destination, Nav(page).Uri); Assert.False((bool)Get(page, "_playing")!);
        // Reproduce the real outgoing query supplier clearing the previous league's query.
        Set(page, "Season", null); Set(page, "DateIndex", null); Set(page, "Club", null);
        await Invoke(page, "OnParametersSetAsync");
        if (requestPending) { pending.SetResult(Json(sought)); await loading; }
        Assert.Equal(requestPending ? 1 : 0, calls); Assert.Equal(destination, Nav(page).Uri); Assert.Equal(1, Nav(page).Updates);
        Assert.Same(current, Get(page, "_snapshot")); Assert.Equal(current.DateIndex, Get(page, "_index")); Assert.Equal("west-ham", Get(page, "_club"));
        Assert.False(Nav(page).ForcedDocumentLoad);
    }
    [Fact]
    public void ActualMapRetainsOriginalOutlineLondonInsetAndInvariantSeasonPositions()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE"); var snapshot = Snapshot("2015-16", "west-ham");
            var map = new RenderableMap(); Set(map, "Snapshot", snapshot); Set(map, "Selected", "west-ham"); using var builder = new RenderTreeBuilder(); map.Render(builder);
            var frames = builder.GetFrames().Array.Take(builder.GetFrames().Count).ToArray(); var text = Text(builder);
            Assert.Contains("LONDON & WATFORD", text); Assert.Contains("Illustrated locations · not to scale", text);
            Assert.Contains("M495 635Q568 638 576 565", text); // Razor preserves static SVG paths in a markup frame.
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "transform" && Equals(f.AttributeValue, "translate(610.25 420.75)"));
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onkeydown");
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "href" && Equals(f.AttributeValue, "/assets/crests/west-ham-2015.png"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void BeforeFirstMatchTableIsUnrankedAndReturnRoutePreservesContext()
    {
        var table = new RenderableStandings(); Set(table, "Snapshot", Snapshot("2015-16", "west-ham", 0));
        using var builder = new RenderTreeBuilder(); table.Render(builder); Assert.Contains("—", Text(builder));
        Assert.Equal("/?season=2015-16&date=44&club=west-ham", ExperiencePresentation.LeagueUrl("2015-16", 44, "west-ham"));
        Assert.Equal("/league/2015-16/matches/west-ham%3Aother?date=44&club=west-ham", ExperiencePresentation.MatchUrl("2015-16", "west-ham:other", 44, "west-ham"));
    }
    [Fact]
    public async Task HistoricalRouteRejectsOldMatchCompletionAfterNewRoute()
    {
        var snapshot = Snapshot("2015-16", "west-ham"); var first = Historical(snapshot, "west-ham:other"); var second = Historical(snapshot, "other:west-ham");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).EndsWith("west-ham:other", StringComparison.Ordinal) ? pending.Task
            : Task.FromResult(request.RequestUri.AbsolutePath.Contains("/matches/", StringComparison.Ordinal) ? Json(second) : Json(snapshot)));
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var page = new HistoricalMatch(); Set(page, "Api", new ReplayApiClient(http)); Set(page, "SeasonKey", "2015-16"); Set(page, "MatchKey", first.Match.MatchKey);
        var loading = Invoke(page, "LoadAsync"); Set(page, "MatchKey", second.Match.MatchKey); await Invoke(page, "LoadAsync");
        pending.SetResult(Json(first)); await loading; Assert.Equal(second.Match.MatchKey, Assert.IsType<LeagueHistoricalMatch>(Get(page, "_match")).Match.MatchKey);
    }
    [Fact]
    public void ConversationClearsAnswersAcrossTeamGroundDateAndMatchContext()
    {
        var component = new SourceConversation(); Set(component, "ContextKey", "west-ham/ground/date-1"); Set(component, "Prompts", new[] { new SourcePrompt("Tell me about our ground?", "Boleyn Ground · 2015/16 reference") });
        InvokeSync(component, "OnParametersSet"); Set(component, "_question", "Tell me about our ground?"); InvokeSync(component, "Ask");
        Assert.Contains("Boleyn", (string)Get(component, "_answer")!);
        Set(component, "ContextKey", "west-ham/ground/date-2"); InvokeSync(component, "OnParametersSet"); Assert.Null(Get(component, "_answer"));
        Set(component, "_question", "Who scored the next goal?"); InvokeSync(component, "Ask"); Assert.Contains("do not support", (string)Get(component, "_answer")!);
    }
    private static LeagueSeasonSnapshot Snapshot(string season, string club, int? index = null, string? selected = null)
    {
        var clubs = new[] { new LeagueClub(club, "West Ham", "WHU", "London", "Boleyn Ground", 610.25, 420.75, true, "assets/crests/west-ham-2015.png"), new LeagueClub("other", "Other", "OTH", "Town", "Ground", 400, 500, false, null) };
        var model = new LeagueSeasonModel(new(season, season.Replace('-', '/'), clubs, [new(club + ":other", "2015-08-01", club, "other", 2, 1, null), new("other:" + club, "2015-08-02", "other", club, 0, 0, null)]), false);
        return model.Snapshot(index, selected ?? club);
    }
    private static LeagueHistoricalMatch Historical(LeagueSeasonSnapshot s, string key) => new(s.Version, s.Season, s.Results.First() with { MatchKey = key }, s.Clubs[0], s.Clubs[1], s.SelectedClub!, s.SelectedClub!, [], [], [], "Real result only.");
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static void Set(object target, string name, object? value) { var type = target.GetType(); var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (property is not null) property.SetValue(target, value); else type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value); }
    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static Task Invoke(object target, string name, params object[] args)
    {
        var result = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        return result is ValueTask value ? value.AsTask() : result as Task ?? Task.CompletedTask;
    }
    private static void InvokeSync(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder b)
    {
        var frames = b.GetFrames().Array.Take(b.GetFrames().Count).ToArray();
        var nested = new List<RenderTreeFrame>(frames);
        foreach (var fragment in frames.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).Select(frame => frame.AttributeValue).OfType<RenderFragment>())
        { using var child = new RenderTreeBuilder(); fragment(child); nested.AddRange(Frames(child)); }
        return nested.ToArray();
    }
    private static string Text(RenderTreeBuilder b) => string.Concat(Frames(b).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private sealed class RenderableMap : LeagueMap { public void Render(RenderTreeBuilder b) => BuildRenderTree(b); }
    private sealed class RenderableStandings : LeagueStandings { public void Render(RenderTreeBuilder b) => BuildRenderTree(b); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private static LeagueExplorer Page(HttpClient http, LeagueSeasonSnapshot? committed = null)
    {
        var page = new LeagueExplorer(); Set(page, "Api", new ReplayApiClient(http));
        Set(page, "Navigation", new RecordingNavigation(committed is null ? "/" : ExperiencePresentation.LeagueUrl(committed.Season.SeasonKey, committed.DateIndex, committed.SelectedClub?.Club.Id)));
        if (committed is not null) { Set(page, "_snapshot", committed); Set(page, "_seasons", new[] { committed.Season }); Set(page, "_season", committed.Season.SeasonKey); Set(page, "_index", committed.DateIndex); Set(page, "_club", committed.SelectedClub?.Club.Id); }
        InvokeSync(page, "OnAfterRender", true); return page;
    }
    private static RecordingNavigation Nav(LeagueExplorer page) => (RecordingNavigation)typeof(LeagueExplorer).GetProperty("Navigation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static RenderTreeBuilder Render(LeagueExplorer page) { var builder = new RenderTreeBuilder(); typeof(LeagueExplorer).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]); return builder; }
    private sealed class RecordingNavigation : NavigationManager
    {
        public bool ForcedDocumentLoad { get; private set; }
        public int Updates { get; private set; }
        public Task LastNavigation { get; private set; } = Task.CompletedTask;
        public Func<string, Task>? QueryChanged { get; set; }
        public RecordingNavigation(string path) => Initialize("https://localhost:7410/", "https://localhost:7410" + path);
        protected override void NavigateToCore(string uri, bool forceLoad) => LastNavigation = NavigateProgrammaticallyAsync(uri, forceLoad);
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        protected override void SetNavigationLockState(bool value) { }
        private async Task NavigateProgrammaticallyAsync(string uri, bool forceLoad)
        {
            // RemoteNavigationManager passes the original relative URI to handlers before JS navigation.
            if (!await NotifyLocationChangingAsync(uri, null, false)) return;
            ForcedDocumentLoad |= forceLoad; Updates++; Uri = ToAbsoluteUri(uri).AbsoluteUri;
            if (QueryChanged is not null) await QueryChanged(Uri);
        }
        public async Task NavigateAsLinkAsync(string uri)
        {
            var target = ToAbsoluteUri(uri).AbsoluteUri;
            if (await NotifyLocationChangingAsync(target, null, true)) NavigateToCore(target, false);
        }
    }
    private sealed class RecordingJs : IJSRuntime, IJSObjectReference
    {
        public List<string> Calls { get; } = [];
        public bool DisconnectCleanup { get; init; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => RecordJs<TValue>(this, identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => RecordJs<TValue>(this, identifier, args);
        public ValueTask DisposeAsync() { Calls.Add("module-dispose"); return ValueTask.CompletedTask; }
    }
    private static ValueTask<TValue> RecordJs<TValue>(RecordingJs js, string identifier, object?[]? args)
    {
        js.Calls.Add(identifier);
        if (identifier == "import") { Assert.Equal("./Components/League/LeagueStandings.razor.js", args![0]); return ValueTask.FromResult((TValue)(object)js); }
        if (identifier == "dispose" && js.DisconnectCleanup) throw new JSDisconnectedException("test circuit ended");
        return ValueTask.FromResult(default(TValue)!);
    }
}
#pragma warning restore BL0006
