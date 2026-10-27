using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.League;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Matchmate.Web.HistoricalChat;
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

#pragma warning disable BL0006 // Inspect actual components; no replacement app or browser host.
public sealed class SitePageLocalizationTests
{
    [Theory]
    [InlineData("LeagueText")]
    [InlineData("HistoricalText")]
    [InlineData("ConversationText")]
    public void PageResourcesHaveCompleteKeysAndMatchingFormatsWithoutChangingGlobalCulture(string family)
    {
        var root = Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Localization");
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(root, family + suffix + ".resx"))
            .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var english = Read(""); Assert.True(english.Count >= 70);
        var manager = new ResourceManager("Matchmate.Web.Localization." + family, typeof(LeagueText).Assembly);
        var before = CultureInfo.CurrentCulture; var uiBefore = CultureInfo.CurrentUICulture;
        foreach (var (language, suffix) in new[] { (MatchLanguage.English, ""), (MatchLanguage.Swedish, ".sv"), (MatchLanguage.Spanish, ".es") })
        {
            var translated = Read(suffix); Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(value)); Assert.Equal(value, manager.GetString(key, MatchLanguages.Culture(language)));
                Assert.Equal(Regex.Matches(english[key], @"\{\d+[^}]*\}").Select(m => m.Value).Order(), Regex.Matches(value, @"\{\d+[^}]*\}").Select(m => m.Value).Order());
            }
        }
        Assert.Same(before, CultureInfo.CurrentCulture); Assert.Same(uiBefore, CultureInfo.CurrentUICulture);
        var firstKey = english.Keys.First();
        Assert.Equal(english[firstKey], manager.GetString(firstKey, CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public void LeagueControlsAndAccessibleMapUseTheSelectedLanguageOverUnchangedFacts(MatchLanguage language)
    {
        var snapshot = LeagueChatFixtures.Snapshot(2); var before = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var playback = new SeasonPlayback(); Set(playback, "Snapshot", snapshot); Set(playback, "Language", language);
        using (var tree = Render(playback))
        {
            Assert.Contains(LeagueText.Get(language, "PlaybackNote"), Text(tree));
            Assert.Contains(ExperiencePresentation.Date(snapshot.Date, language), Text(tree));
            Assert.Contains(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && Equals(frame.AttributeValue, LeagueText.Get(language, "SnapshotRange")));
        }
        var map = new LeagueMap(); Set(map, "Snapshot", snapshot); Set(map, "Language", language);
        using (var tree = Render(map)) { Assert.Contains(LeagueText.Get(language, "MapTitle"), Text(tree)); Assert.Contains(LeagueText.Get(language, "MapDescription"), Text(tree)); }
        var club = new ClubDetail(); Set(club, "Snapshot", snapshot); Set(club, "Language", language);
        using (var tree = Render(club)) { Assert.Contains(LeagueText.Get(language, "GroundMapNote"), Text(tree)); Assert.Contains(snapshot.SelectedClub!.Club.Name, Text(tree)); }
        var picker = new SeasonPicker(); Set(picker, "Seasons", new[] { snapshot.Season }); Set(picker, "Language", language); Set(picker, "_open", true);
        using (var tree = Render(picker)) { Assert.Contains(LeagueText.Get(language, "SearchSeasons"), Text(tree)); Assert.Contains(LeagueText.Get(language, "OngoingSnapshot"), Text(tree)); }
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.Equal("12.75", ExperiencePresentation.Number(12.75));
        Assert.Equal(DateOnly.ParseExact(snapshot.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMM yyyy", MatchLanguages.Culture(language)), LeagueSourcePresentation.ResultDate(snapshot.Date!, language));
    }

    [Fact]
    public async Task SharedLanguageRepaintsRealLeaguePageWithoutFetchingNavigatingOrResettingSelection()
    {
        var snapshot = LeagueChatFixtures.Snapshot(2, null); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++; return Json(request.RequestUri!.AbsolutePath == "/api/league/seasons" ? (object)new[] { snapshot.Season } : snapshot);
        })) { BaseAddress = new("https://unit.invalid/") };
        using var session = LeagueChatFixtures.Session(); var state = new MatchLanguageState(); var navigation = new Navigation();
        using var services = new ServiceCollection().AddLogging().AddSingleton(state).AddSingleton(new ReplayApiClient(http)).AddSingleton(session)
            .AddSingleton<NavigationManager>(navigation).AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new ComponentTestHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            LeaguePage? page = null;
            var output = await renderer.RenderComponentAsync<LeaguePage>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LeaguePage.Capture)] = (Action<LeaguePage>)(value => page = value) }));
            Assert.NotNull(page); var committed = Field(page!, "_snapshot"); var initialCalls = calls; var initialNavigation = navigation.Updates;
            Assert.Equal(2, initialCalls); Set(page!, "_playing", true);
            foreach (var language in new[] { MatchLanguage.Swedish, MatchLanguage.Spanish, MatchLanguage.English })
            {
                state.SetLanguage(language);
                var html = WebUtility.HtmlDecode(output.ToHtmlString());
                Assert.Contains(LeagueText.Get(language, "LeagueInPlace"), html); Assert.Contains(ConversationText.Get(language, "LeagueWelcomeHeading"), html);
                Assert.Contains("lang=\"" + MatchLanguages.Code(language) + "\"", html);
                Assert.Same(committed, Field(page!, "_snapshot")); Assert.Equal(snapshot.DateIndex, Field(page!, "_index"));
                Assert.True((bool)Field(page!, "_playing")!); Assert.Equal(initialCalls, calls); Assert.Equal(initialNavigation, navigation.Updates);
                Assert.Equal(language, session.Language);
            }
        });
    }

    [Fact]
    public async Task SharedLanguageRepaintsRealHistoricalPageWithoutReloadingFixtureOrClearingHistory()
    {
        var fixture = HistoricalChatFixtures.Fixture(); var snapshot = HistoricalChatFixtures.Model().Snapshot(null); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        { calls++; return Json(request.RequestUri!.AbsolutePath.Contains("/matches/", StringComparison.Ordinal) ? (object)fixture : snapshot); })) { BaseAddress = new("https://unit.invalid/") };
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), HistoricalChatFixtures.Evidence(), new HistoricalChatSessionTests.Router("overview"), new HistoricalChatSessionTests.Model(), Options.Create(new MatchChatOptions()));
        var state = new MatchLanguageState();
        using var services = new ServiceCollection().AddLogging().AddSingleton(state).AddSingleton(new ReplayApiClient(http)).AddSingleton(session)
            .AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new ComponentTestHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HistoricalPage? page = null;
            var output = await renderer.RenderComponentAsync<HistoricalPage>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(HistoricalPage.Capture)] = (Action<HistoricalPage>)(value => page = value), [nameof(HistoricalMatch.SeasonKey)] = fixture.Season.SeasonKey, [nameof(HistoricalMatch.MatchKey)] = fixture.Match.MatchKey
            }));
            Assert.NotNull(page); var committed = Field(page!, "_match"); Assert.Equal(2, calls);
            Assert.True((await session.AskSuggestionAsync(HistoricalChatContext.From(fixture), "result")).Accepted); var turn = Assert.Single(session.Turns);
            foreach (var language in new[] { MatchLanguage.Swedish, MatchLanguage.Spanish, MatchLanguage.English })
            {
                state.SetLanguage(language); await page!.ApplyAgain();
                var html = WebUtility.HtmlDecode(output.ToHtmlString());
                Assert.Contains(HistoricalText.Get(language, "ExploreFacts"), html); Assert.Contains(ConversationText.Get(language, "MatchHeading"), html);
                Assert.Contains(turn.Answer, html); Assert.Same(committed, Field(page!, "_match")); Assert.Same(turn, Assert.Single(session.Turns));
                Assert.Equal(MatchLanguage.English, turn.Language); Assert.Equal(language, session.Language); Assert.Equal(2, calls);
            }
        });
    }

    [Fact]
    public void DormantLegacyPageHasNoRoute()
        => Assert.Empty(typeof(Home).GetCustomAttributes<RouteAttribute>());

    public sealed class LeaguePage : LeagueExplorer
    {
        [Parameter] public Action<LeaguePage>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
    }
    public sealed class HistoricalPage : HistoricalMatch
    {
        [Parameter] public Action<HistoricalPage>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
        public Task ApplyAgain() => base.OnParametersSetAsync();
    }
    private sealed class Navigation : NavigationManager
    {
        public int Updates { get; private set; }
        public Navigation() => Initialize("https://unit.invalid/", "https://unit.invalid/");
        protected override void NavigateToCore(string uri, bool forceLoad) { Updates++; Uri = ToAbsoluteUri(uri).AbsoluteUri; }
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        protected override void SetNavigationLockState(bool value) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("A language repaint must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("A language repaint must not invoke JavaScript.");
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, value.GetType()) };
    private static object? Field(object target, string name)
    { for (var type = target.GetType(); type is not null; type = type.BaseType) if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) return field.GetValue(target); throw new MissingFieldException(name); }
    private static void Set(object target, string name, object? value)
    { for (var type = target.GetType(); type is not null; type = type.BaseType) { if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } property) { property.SetValue(target, value); return; } if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) { field.SetValue(target, value); return; } } throw new MissingMemberException(name); }
    private static RenderTreeBuilder Render(object target) { var tree = new RenderTreeBuilder(); target.GetType().GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [tree]); return tree; }
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent));
}
#pragma warning restore BL0006
