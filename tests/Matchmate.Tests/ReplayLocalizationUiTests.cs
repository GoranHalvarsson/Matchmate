using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Unit inspection of real component trees; no substitute browser host.
public sealed class ReplayLocalizationUiTests
{
    public static IEnumerable<object[]> LanguagesAndAudiences => from language in MatchLanguages.Supported
        from audience in new[] { MatchAudienceMode.Fan, MatchAudienceMode.Analyst }
        select new object[] { language, audience };

    [Fact]
    public void EveryResourceExistsInEachLanguageWithMatchingPlaceholdersAndExplicitCulture()
    {
        var root = Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Localization");
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(root, "ReplayText" + suffix + ".resx"))
            .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var english = Read(""); Assert.True(english.Count >= 300);
        var before = CultureInfo.CurrentCulture; var uiBefore = CultureInfo.CurrentUICulture;
        foreach (var (language, suffix) in new[] { (MatchLanguage.English, ""), (MatchLanguage.Swedish, ".sv"), (MatchLanguage.Spanish, ".es") })
        {
            var resources = Read(suffix); Assert.Equal(english.Keys.Order(), resources.Keys.Order());
            foreach (var (key, value) in resources)
            {
                Assert.False(string.IsNullOrWhiteSpace(value)); Assert.Equal(value, ReplayText.Get(language, key));
                Assert.Equal(Regex.Matches(english[key], @"\{\d+[^}]*\}").Select(m => m.Value).Order(), Regex.Matches(value, @"\{\d+[^}]*\}").Select(m => m.Value).Order());
            }
        }
        Assert.Same(before, CultureInfo.CurrentCulture); Assert.Same(uiBefore, CultureInfo.CurrentUICulture);
        Assert.Equal(ReplayText.Get(MatchLanguage.English, "LanguageLabel"), ReplayText.Get((MatchLanguage)99, "LanguageLabel"));
        var resourcesManager = new ResourceManager("Matchmate.Web.Localization.ReplayText", typeof(ReplayText).Assembly);
        Assert.Equal(english["LanguageLabel"], resourcesManager.GetString("LanguageLabel", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Throws<MissingManifestResourceException>(() => ReplayText.Get(MatchLanguage.Spanish, "Missing.Resource"));
    }

    [Theory]
    [InlineData(MatchLanguage.English, "Source notes")]
    [InlineData(MatchLanguage.Swedish, "Källanteckningar")]
    [InlineData(MatchLanguage.Spanish, "Notas de la fuente")]
    public async Task UnknownSourceNotesHaveLocalizedHeadingsExplicitEnglishFallbackAndUnchangedEvidence(MatchLanguage language, string label)
    {
        var (view, context) = MatchGoalVisualTests.Unit();
        view = view with { Context = view.Context with { CursorMs = 52000 } };
        context = context with { Query = view.Context };
        var record = Assert.Single(view.Events);
        var visual = Assert.IsType<MatchEventVisual>(MatchEventVisual.Create(view.Dataset, record, context));
        var scene = MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs, true)!;
        Assert.NotNull(scene.Sequence.Steps[scene.ActiveStep].Uncertainty);
        var before = JsonSerializer.Serialize(new { view, visual, scene });
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<EventSequenceDiagram>(0); builder.AddAttribute(1, nameof(EventSequenceDiagram.Visual), visual); builder.CloseComponent();
                builder.OpenComponent<Matchmate.Web.Components.Football.FootballPitch>(2); builder.AddAttribute(3, nameof(Matchmate.Web.Components.Football.FootballPitch.View), view);
                builder.AddAttribute(4, nameof(Matchmate.Web.Components.Football.FootballPitch.Sequences), new[] { scene }); builder.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CascadingValue<MatchLanguage>.Value)] = language,
                [nameof(CascadingValue<MatchLanguage>.ChildContent)] = content
            }));
            var html = WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Equal(label, ReplayText.Get(language, "SourceNotes"));
            Assert.Equal(3, Regex.Matches(html, Regex.Escape(label)).Count);
            Assert.Equal(language != MatchLanguage.English, html.Contains(ReplayText.Get(language, "SourceNoteOriginalEnglish"), StringComparison.Ordinal));
            Assert.DoesNotContain(ReplayText.Get(language, "ShowOriginalSourceNotes"), html); // Every unit note is unreviewed and already original.
            var english = Regex.Matches(html, "<(?:span|small)\\b[^>]*\\blang=\"en\"[^>]*>([^<]*)</(?:span|small)>")
                .Select(match => match.Groups[1].Value.Trim()).ToArray();
            foreach (var step in scene.Sequence.Steps)
            {
                Assert.Contains(step.Text, english);
                if (step.Uncertainty is { } uncertainty) Assert.Contains(uncertainty, english);
            }
            Assert.Equal(before, JsonSerializer.Serialize(new { view, visual, scene }));
        });
    }

    [Theory, MemberData(nameof(LanguagesAndAudiences))]
    public void AllGeneratedCardKindsTranslateOnlyPresentationWithIdenticalActionsAndEvidence(MatchLanguage language, MatchAudienceMode audience)
    {
        // ReportedMatchState uses the real Liverpool report fixture in ReportedReplayPanelTests.
        foreach (var kind in Enum.GetValues<ReplayInsightKind>().Where(kind => kind != ReplayInsightKind.ReportedMatchState))
        {
            var view = InsightView(kind); var before = JsonSerializer.Serialize(view);
            var english = ReplayInsight.Project(view, audience)!; var card = ReplayInsight.Project(view, audience, language)!;
            Assert.Equal(kind, card.Kind); Assert.Equal(english.Action, card.Action); Assert.Equal(english.Clock, card.Clock);
            Assert.Equal(english.CategoryKey, card.CategoryKey); Assert.True(ReplayInsight.Matches(card.Action, view));
            Assert.Equal(before, JsonSerializer.Serialize(view));
            Assert.Equal(ReplayText.Get(language, kind switch { ReplayInsightKind.ShotControlContrast => "InsightShotHeadline", ReplayInsightKind.PassCompletionGap => "InsightPassHeadline", _ => "InsightBalancedHeadline" }), card.Headline);
            if (language != MatchLanguage.English) { Assert.NotEqual(english.Body, card.Body); Assert.NotEqual(ReplayInsight.Question(kind), ReplayInsight.Question(kind, language)); }
            var component = new InsightCard(); Set(component, "Insight", card); Set(component, "Language", language); Set(component, "AudienceMode", audience);
            using var tree = new RenderTreeBuilder(); component.Render(tree);
            Assert.Contains(ReplayText.Get(language, "AskAboutThis"), Text(tree)); Assert.Contains(card.Body, Text(tree));
            Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "data-insight" && Equals(f.AttributeValue, kind.ToString()));
        }
    }

    [Theory, MemberData(nameof(LanguagesAndAudiences))]
    public void LocaleUsesDisplayNumbersAndKeepsInvariantGeometryAndClocks(MatchLanguage language, MatchAudienceMode audience)
    {
        var view = InsightView(ReplayInsightKind.BalancedControl); var stats = view.IllustrationStatistics!;
        view = view with { IllustrationStatistics = stats with { Teams = [stats.Teams[0] with { ControlledMs = 302400, ControlSharePercent = 50.4m }, stats.Teams[1] with { ControlledMs = 297600, ControlSharePercent = 49.6m }] } };
        var card = ReplayInsight.Project(view, audience, language)!;
        if (audience == MatchAudienceMode.Analyst) Assert.Contains(50.4m.ToString("0.#", MatchLanguages.Culture(language)) + "%", card.Body);
        Assert.Equal("10:00", card.Clock); Assert.Equal("12.75", ExperiencePresentation.Number(12.75));
        var pitch = MatchPitchPresenter.Project(view, [], true, false, language);
        var english = MatchPitchPresenter.Project(view, [], true, false);
        Assert.Equal(english.Frame, pitch.Frame); Assert.Equal(english.Path, pitch.Path); Assert.Equal(english.Contact, pitch.Contact);
    }

    [Theory, MemberData(nameof(LanguagesAndAudiences))]
    public async Task LanguageFlipPreservesDraftHistoryAndStopsAwaitedAskingBeforeAnyIo(MatchLanguage language, MatchAudienceMode audience)
    {
        using var h = new Harness(); using var component = new Conversation();
        var view = InsightView(ReplayInsightKind.BalancedControl); var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence);
        Set(component, "Session", h.Session); Set(component, "Context", context); Set(component, "AudienceMode", audience); component.Apply();
        var retained = new MatchChatTurn("Original question", "Original answer", context, null, [new("raw", "coverage", "ORIGINAL_SOURCE_JSON", [])], true);
        ((List<MatchChatTurn>)Field(typeof(MatchChatSession), "_turns").GetValue(h.Session)!).Add(retained);
        Field(typeof(MatchConversation), "_question").SetValue(component, "My unsent draft");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(component, "OnAsking", EventCallback.Factory.Create(new object(), async () => { entered.SetResult(); await gate.Task; }));
        var pending = (Task)Method(typeof(MatchConversation), "SubmitAsync").Invoke(component, [])!; await entered.Task;
        var next = language == MatchLanguage.English ? MatchLanguage.Spanish : language;
        Set(component, "Language", next); component.Apply(); gate.SetResult(); await pending;
        Assert.Equal("My unsent draft", Field(typeof(MatchConversation), "_question").GetValue(component));
        Assert.Equal(false, Field(typeof(MatchConversation), "_pending").GetValue(component)); Assert.Null(Field(typeof(MatchConversation), "_focusPreparation").GetValue(component));
        Assert.Same(retained, Assert.Single(h.Session.Turns)); Assert.Equal(MatchLanguage.English, retained.Language); Assert.Equal(next, h.Session.Language); Assert.Equal(0, h.IoCalls);
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Contains("Original answer", Text(tree)); Assert.Contains("English", Text(tree));
        Assert.Contains(Frames(tree), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "lang" && Equals(f.AttributeValue, "en"));
    }

    [Theory, MemberData(nameof(LanguagesAndAudiences))]
    public void PreparedQuestionsTranslateByStableKindAndNeverOverwriteAnExistingDraft(MatchLanguage language, MatchAudienceMode audience)
    {
        using var h = new Harness(); using var component = new Conversation(); var view = InsightView(ReplayInsightKind.ShotControlContrast);
        var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence); var card = ReplayInsight.Project(view, audience, language)!;
        Set(component, "Session", h.Session); Set(component, "Context", context); Set(component, "Language", language); Set(component, "AudienceMode", audience); component.Apply();
        Set(component, "PreparedInsight", new ReplayInsightPreparation(Guid.NewGuid(), card.Action, context)); component.Apply();
        Assert.Equal(ReplayInsight.Question(card.Kind, language), Field(typeof(MatchConversation), "_question").GetValue(component));
        Field(typeof(MatchConversation), "_question").SetValue(component, "My own words");
        Set(component, "PreparedInsight", new ReplayInsightPreparation(Guid.NewGuid(), card.Action, context)); component.Apply();
        Assert.Equal("My own words", Field(typeof(MatchConversation), "_question").GetValue(component)); Assert.Equal(0, h.IoCalls);
    }

    [Fact]
    public async Task HeaderIslandAndReplayShareOneScopedStateWithoutLosingReplaySelectionOrPlayingState()
    {
        using var h = new Harness();
        using var services = new ServiceCollection().AddLogging().AddScoped<MatchLanguageState>().AddSingleton(h.Session)
            .AddSingleton(h.Api).AddSingleton<NavigationManager>(new UnitNavigation()).AddSingleton<IJSRuntime>(new NoJs())
            .AddSingleton(MatchChatDiagnosticsTests.Create(new TimingLog())).BuildServiceProvider();
        using var circuit = services.CreateScope(); using var otherCircuit = services.CreateScope();
        var state = circuit.ServiceProvider.GetRequiredService<MatchLanguageState>();
        Assert.NotSame(state, otherCircuit.ServiceProvider.GetRequiredService<MatchLanguageState>());
        await using var renderer = new ComponentTestHtmlRenderer(circuit.ServiceProvider, services.GetRequiredService<ILoggerFactory>());
        ReplayPage? page = null; var view = InsightView(ReplayInsightKind.BalancedControl);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var selector = await renderer.RenderComponentAsync<ReplayLanguageSelector>();
            var replay = await renderer.RenderComponentAsync<ReplayPage>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ReplayPage.Capture)] = (Action<ReplayPage>)(value =>
            {
                page = value; Field(typeof(FootballReplay), "_view").SetValue(value, view); Field(typeof(FootballReplay), "_selectedStream").SetValue(value, view.Context.StreamId);
                Field(typeof(FootballReplay), "_cursorMs").SetValue(value, view.Context.CursorMs); Field(typeof(FootballReplay), "_playing").SetValue(value, true);
                Field(typeof(FootballReplay), "_personId").SetValue(value, "person-one"); Field(typeof(FootballReplay), "_audienceMode").SetValue(value, MatchAudienceMode.Analyst);
            }) }));
            var initialCard = typeof(FootballReplay).GetProperty("CurrentInsight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
            Assert.Contains("Language", WebUtility.HtmlDecode(selector.ToHtmlString()));
            Assert.True(state.SetLanguage(MatchLanguage.Spanish));
            Assert.Contains("Idioma", WebUtility.HtmlDecode(selector.ToHtmlString()));
            var html = WebUtility.HtmlDecode(replay.ToHtmlString()); Assert.Contains("lang=\"es\"", html); Assert.Contains("Momentos destacados", html);
            Assert.Contains("lang=\"en\"", html); // Manifest normalization remains original English evidence.
            Assert.NotEqual(initialCard, typeof(FootballReplay).GetProperty("CurrentInsight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
            Assert.Same(view, Field(typeof(FootballReplay), "_view").GetValue(page)); Assert.Equal(view.Context.CursorMs, Field(typeof(FootballReplay), "_cursorMs").GetValue(page));
            Assert.Equal(true, Field(typeof(FootballReplay), "_playing").GetValue(page)); Assert.Equal("person-one", Field(typeof(FootballReplay), "_personId").GetValue(page));
            Assert.Equal(MatchAudienceMode.Analyst, Field(typeof(FootballReplay), "_audienceMode").GetValue(page)); Assert.Equal(0, h.IoCalls);
        });
    }

    [Theory, InlineData(MatchLanguage.English), InlineData(MatchLanguage.Swedish), InlineData(MatchLanguage.Spanish)]
    public void CoverageMapsKnownAppGeneratedMessagesAndMakesUnknownCoverageExplicit(MatchLanguage language)
    {
        var view = InsightView(ReplayInsightKind.BalancedControl);
        foreach (var (message, key) in new[] { ("No committed tracking sample at this cursor; positions are not carried across gaps.", "CoverageNoSample"),
            ("Explicit empty player frame; independent ball evidence is preserved.", "CoverageEmptyFrame"), ("Source sample; no interpolation or measured movement statistics.", "CoverageSourceSample") })
            Assert.Equal(ReplayText.Get(language, key), ReplayText.Coverage(language, view with { CoverageMessage = message }));
        Assert.Equal(ReplayText.Get(language, "CoverageUnknown"), ReplayText.Coverage(language, view with { CoverageMessage = "Unrecognized source status" }));
        Assert.Equal(ReplayText.Get(language, "StatusUpdatingAccepted", "10:00"), ReplayText.Status(language, "Updating… Showing accepted replay 10:00 until the new moment is ready."));
    }

    private static FootballView InsightView(ReplayInsightKind kind)
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var view = ledger.Query(Query(fixture, 0));
        return view with { Context = view.Context with { CursorMs = 600000 }, Dataset = view.Dataset with { Closed = true,
            Manifest = view.Dataset.Manifest with { Capabilities = [.. view.Dataset.Manifest.Capabilities, FootballActionPolicy.GeneratedModelActionsV1],
                Sources = [.. view.Dataset.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] },
            Schedule = view.Dataset.Schedule! with { Segments = [new("period-one", 0, 1000000, 0)] } },
            IllustrationStatistics = new([new("team-home", 20, kind == ReplayInsightKind.PassCompletionGap ? 20 : 16, kind == ReplayInsightKind.ShotControlContrast ? 3 : 0, 300000, 50),
                new("team-away", 20, kind == ReplayInsightKind.PassCompletionGap ? 10 : 16, 0, 300000, 50)], 600000) };
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Set(object component, string name, object? value) => (component.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(MatchConversation).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))!.SetValue(component, value);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(Frames(tree).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private sealed class InsightCard : ReplayInsightCard { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Conversation : MatchConversation { public void Apply() => OnParametersSet(); public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    public sealed class ReplayPage : FootballReplay
    {
        [Parameter] public Action<ReplayPage>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
        protected override Task OnParametersSetAsync() => Task.CompletedTask; // Unit lifecycle fixture, no API or app host.
    }
    private sealed class UnitNavigation : NavigationManager
    {
        public UnitNavigation() => Initialize("https://unit.invalid/", "https://unit.invalid/recordings");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Language changes must not navigate.");
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not use JS.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Static rendering must not use JS.");
    }
    private sealed class Harness : IDisposable
    {
        public int IoCalls; public MatchChatSession Session { get; } public ReplayApiClient Api { get; } private readonly HttpClient _http;
        public Harness()
        {
            _http = new(new NoHttp(() => IoCalls++)) { BaseAddress = new("https://unit.invalid/") }; Api = new(_http); var noModel = new NoModel(() => IoCalls++);
            Session = new(new Auth(), Api, noModel, noModel, Options.Create(new MatchChatOptions()));
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    private sealed class NoHttp(Action called) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { called(); throw new InvalidOperationException("No IO expected."); } }
    private sealed class NoModel(Action called) : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured => true;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { called(); throw new InvalidOperationException("No routing expected."); }
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { called(); throw new InvalidOperationException("No narration expected."); }
    }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal())); }
}
#pragma warning restore BL0006
