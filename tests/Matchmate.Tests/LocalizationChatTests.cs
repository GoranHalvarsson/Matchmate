using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Resources;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;

namespace Matchmate.Tests;

public sealed class LocalizationChatTests
{
    public static IEnumerable<object[]> Combinations() => MatchLanguages.Supported.SelectMany(language => Enum.GetValues<MatchAudienceMode>().Select(mode => new object[] { language, mode }));
    internal static (TestFixture Fixture, FootballLedger Ledger) Recording()
    {
        var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with { Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "first-pass", "pass", 1000, 7400), Action(fixture, "control", "possession", 0, 20000, outcome: "controlled"));
        return (fixture, ledger);
    }
    [Fact]
    public void LanguageCatalogAndStateAreBoundedEphemeralAndDoNotChangeGlobalCulture()
    {
        var original = CultureInfo.CurrentCulture; var ui = CultureInfo.CurrentUICulture; var state = new MatchLanguageState(); var changes = 0; state.Changed += () => changes++;
        Assert.Equal(MatchLanguage.English, state.Language); Assert.False(state.SetLanguage(MatchLanguage.English)); Assert.False(state.SetLanguage((MatchLanguage)99));
        Assert.True(state.SetLanguage(MatchLanguage.Swedish)); Assert.False(state.SetLanguage(MatchLanguage.Swedish)); Assert.True(state.SetLanguage(MatchLanguage.Spanish)); Assert.Equal(2, changes);
        Assert.Equal(new[] { "en", "sv", "es" }, MatchLanguages.Supported.Select(MatchLanguages.Code));
        Assert.Equal(new[] { "en-GB", "sv-SE", "es-ES" }, MatchLanguages.Supported.Select(language => MatchLanguages.Culture(language).Name));
        Assert.Equal(new[] { "English", "Svenska", "Español" }, MatchLanguages.Supported.Select(MatchLanguages.Name));
        Assert.Equal(original, CultureInfo.CurrentCulture); Assert.Equal(ui, CultureInfo.CurrentUICulture);
    }
    [Fact]
    public void AllThreeResourceCatalogsHaveIdenticalKeysPlaceholdersAndExplicitCultureFallback()
    {
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Localization", "ChatText" + suffix + ".resx"))
            .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var neutral = Read(""); Assert.True(neutral.Count >= 100);
        foreach (var suffix in new[] { ".sv", ".es" })
        {
            var translated = Read(suffix); Assert.Equal(neutral.Keys.Order(), translated.Keys.Order());
            foreach (var key in neutral.Keys) { Assert.False(string.IsNullOrWhiteSpace(translated[key])); Assert.Equal(Regex.Matches(neutral[key], @"\{\d+\}").Select(m => m.Value).Order(), Regex.Matches(translated[key], @"\{\d+\}").Select(m => m.Value).Order()); }
        }
        Assert.Equal("unavailable", ChatText.Get((MatchLanguage)99, "Stats.Unavailable"));
        Assert.Contains("79,8", ChatText.Get(MatchLanguage.Swedish, "Stats.Seconds", 79.8m)); Assert.Contains("79,8", ChatText.Get(MatchLanguage.Spanish, "Stats.Seconds", 79.8m));
        Assert.Throws<MissingManifestResourceException>(() => ChatText.Get(MatchLanguage.English, "Missing.SemanticKey"));
    }
    [Theory] [MemberData(nameof(Combinations))]
    public async Task ExplicitSourceActionsRetainLocalizedClickedTextWithIdenticalEvidenceAndNoModels(MatchLanguage language, MatchAudienceMode mode)
    {
        var (fixture, ledger) = Recording(); using var localized = new Harness(ledger); using var baseline = new Harness(ledger);
        var context = new MatchChatContext(Query(fixture, 20000) with { SubjectId = "person-one" }, 2); localized.Session.SetContext(context); baseline.Session.SetContext(context);
        localized.Session.SetLanguage(language); localized.Session.SetAudienceMode(mode); baseline.Session.SetAudienceMode(mode);
        foreach (var source in MatchSourceQuestions.All)
        {
            Assert.True((await localized.Session.AskSourceAsync(context, source.Id)).Accepted); Assert.True((await baseline.Session.AskSourceAsync(context, source.Id)).Accepted);
            var actual = localized.Session.Turns[^1]; var expected = baseline.Session.Turns[^1];
            Assert.Equal(language, actual.Language); Assert.Equal(mode, actual.AudienceMode); Assert.Equal(MatchSourceQuestions.DisplayQuestion(source.Id, language), actual.Question);
            Assert.Equal(source.Id, MatchSourceQuestions.Exact(actual.Question)!.Id); Assert.Equal(JsonSerializer.Serialize(expected.Evidence), JsonSerializer.Serialize(actual.Evidence));
            Assert.InRange(actual.Answer.Length, 1, 1800); Assert.Equal(expected.Context, actual.Context);
        }
        Assert.Equal(4, localized.Queries.Count); Assert.Equal(baseline.Queries, localized.Queries); Assert.Empty(localized.Router.Inputs); Assert.Empty(localized.Model.Requests);
    }
    [Theory] [MemberData(nameof(Combinations))]
    public async Task TypedOriginalQuestionAndEvidenceStayUnchangedWhileSameNarratorCallGetsTrustedLanguage(MatchLanguage language, MatchAudienceMode mode)
    {
        var (fixture, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context); h.Session.SetLanguage(language); h.Session.SetAudienceMode(mode);
        const string original = "Explain this replay moment"; Assert.True((await h.Session.AskAsync(context, original)).Accepted);
        Assert.Equal(original, Assert.Single(h.Router.Inputs).Question); var request = Assert.Single(h.Model.Requests); Assert.Equal(original, request.Messages[^1].Content);
        Assert.StartsWith("match-commentator-v12", request.Messages[0].Content); Assert.Single(request.Messages, m => m.Content.StartsWith(MatchLanguagePresentation.Version));
        Assert.Contains(ChatText.Get(language, "Language.Instruction"), request.Messages.Single(m => m.Content.StartsWith(MatchLanguagePresentation.Version)).Content);
        Assert.DoesNotContain("Language", JsonSerializer.Serialize(h.Router.Inputs[0])); Assert.Equal(7400, Assert.Single(h.Queries).CursorMs);
        var turn = Assert.Single(h.Session.Turns); Assert.Equal(language, turn.Language); Assert.Equal(original, turn.Question);
    }
    [Theory] [InlineData("read")] [InlineData("router")] [InlineData("narrator")]
    public async Task LanguageFlipPreservesOldTurnsAndRejectsLateIgnoredCancellation(string stage)
    {
        var (f, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(f, 7400), 2); h.Session.SetContext(context);
        await h.Session.AskSourceAsync(context, MatchSourceQuestionId.Positions); var previous = Assert.Single(h.Session.Turns);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait() { entered.TrySetResult(); await gate.Task; }
        if (stage == "read") h.WaitRead = Wait; if (stage == "router") h.Router.Wait = Wait; if (stage == "narrator") h.Model.Wait = Wait;
        var pending = h.Session.AskAsync(context, "Explain this replay moment"); await entered.Task; h.Session.SetLanguage(MatchLanguage.Swedish); gate.SetResult();
        Assert.False((await pending).Accepted); Assert.Same(previous, Assert.Single(h.Session.Turns)); Assert.Equal(MatchLanguage.English, previous.Language);
        h.WaitRead = null; h.Router.Wait = null; h.Model.Wait = null;
        await h.Session.AskSourceAsync(context, MatchSourceQuestionId.Positions); Assert.Equal(MatchLanguage.Swedish, h.Session.Turns[^1].Language);
    }
    [Theory]
    [InlineData("Visa hur målet gick till", "goal")] [InlineData("Muestra cómo ocurrió el gol", "goal")]
    [InlineData("Visa bytet på ett diagram", "substitution")] [InlineData("Muestra la sustitución en un diagrama", "substitution")]
    [InlineData("Rita varningen", "yellow_card")] [InlineData("Dibuja la tarjeta", "yellow_card")]
    public void LocalVisualPresentationRecognizesExistingEventKindsWithoutChoosingAWorkflow(string question, string kind)
    {
        Assert.NotNull(MatchEventVisual.Intent(question)); Assert.True(MatchEventVisual.MatchesRequestedKind(question, new("event-one", 1000, "1", kind)));
        Assert.False(MatchEventVisual.MatchesRequestedKind(question, null));
    }
    [Theory] [InlineData("Hur spelade han?")] [InlineData("Berätta mer om henne")] [InlineData("¿Cómo jugó él?")] [InlineData("Háblame de ella")]
    public void LocalPlayerPronounsUseTheExistingTrustedReferenceFence(string question)
    {
        Assert.True(MatchPlayerResolver.ReferenceQuestion(question)); var people = new[] { new SquadPerson("person-one", "team-home", "Cole Palmer", 20, "starter") };
        Assert.Equal("person-one", MatchPlayerResolver.Resolve(people, new(question, null, "person-one")).SubjectId); Assert.Null(MatchPlayerResolver.Resolve(people, new(question)).SubjectId);
    }
    [Fact]
    public async Task LocalizedExactQuestionKeepsItsOriginalTextAndBypassesModels()
    {
        var (f, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(f, 7400), 2); h.Session.SetContext(context); h.Session.SetLanguage(MatchLanguage.Spanish);
        const string original = "Visa statistik för reprisen"; Assert.True((await h.Session.AskAsync(context, original)).Accepted);
        Assert.Equal(original, Assert.Single(h.Session.Turns).Question); Assert.StartsWith("En esta repetición", h.Session.Turns[0].Answer); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Requests); Assert.Single(h.Queries);
    }
    [Fact]
    public async Task LanguageUnavailableFeedbackIsNotTranslatedIntoZeroAndDoesNotRewriteOldTurns()
    {
        var (f, ledger) = Recording(); using var h = new Harness(ledger); var context = new MatchChatContext(Query(f, 7400), 2); h.Session.SetContext(context); h.Session.SetLanguage(MatchLanguage.Swedish);
        h.Transform = v => v with { IllustrationStatistics = null }; Assert.True((await h.Session.AskSourceAsync(context, MatchSourceQuestionId.ReplayStatistics)).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.Contains("inte tillgänglig", turn.Answer); Assert.DoesNotContain("0", turn.Answer);
        Assert.False(h.Session.SetLanguage((MatchLanguage)99)); h.Session.SetLanguage(MatchLanguage.Spanish); Assert.Same(turn, Assert.Single(h.Session.Turns)); Assert.Equal(MatchLanguage.Swedish, turn.Language);
    }
    [Theory] [InlineData(MatchLanguage.Swedish, false)] [InlineData(MatchLanguage.Swedish, true)] [InlineData(MatchLanguage.Spanish, false)] [InlineData(MatchLanguage.Spanish, true)]
    public async Task ReviewedOriginalEnglishNoticeAppearsOnlyWhenEligiblePassagesAreActuallySupplied(MatchLanguage language, bool reviewed)
    {
        var (f, ledger) = Recording(); var context = new MatchChatContext(Query(f, 20000) with { SubjectId = "person-one" }, 2);
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(new("selected_player_context", MatchWorkflowRegistry.Version,
            new("owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
        var bundle = result.Bundle! with { Items = result.Bundle!.Items.Select(item => {
            if (item.Tool != "player_involvement") return item;
            var json = JsonNode.Parse(item.Text)!.AsObject(); json["reviewedBrief"] = new JsonObject { ["observations"] = reviewed
                ? new JsonArray(new JsonObject { ["text"] = "Original English reviewed passage.", ["qualification"] = "A single eligible source observation." }) : new JsonArray() };
            return item with { Text = json.ToJsonString() }; }).ToImmutableArray() };
        var formatter = typeof(MatchSourceQuestions).GetMethod("FormatForLanguage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var answer = ((string Answer, MatchChatEvidence[] Evidence))formatter.Invoke(null, [bundle, null, MatchAudienceMode.Fan, language])!;
        Assert.Equal(reviewed, answer.Answer.Contains(ChatText.Get(language, "Source.RawNotes"), StringComparison.Ordinal));
        Assert.DoesNotContain("Original English reviewed passage", answer.Answer);
        Assert.Equal(JsonSerializer.Serialize(bundle.Items.Select(item => item.Text)), JsonSerializer.Serialize(answer.Evidence.Select(item => item.Text)));
        if (reviewed) Assert.Contains("Original English reviewed passage", answer.Evidence.Single(item => item.Tool == "player_involvement").Text);
    }
    internal sealed class Harness : IDisposable
    {
        internal readonly List<FootballQuery> Queries = []; internal readonly Router Router = new(); internal readonly Model Model = new(); internal readonly MatchChatSession Session;
        internal Func<Task>? WaitRead; internal Func<FootballView, FootballView>? Transform; private readonly HttpClient _http;
        internal Harness(FootballLedger ledger)
        {
            _http = new(new Handler(async request => { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>())!; Queries.Add(query); if (WaitRead is not null) await WaitRead(); var v = ledger.Query(query); return new(HttpStatusCode.OK) { Content = JsonContent.Create(Transform?.Invoke(v) ?? v) }; })) { BaseAddress = new("https://unit.invalid/") };
            Session = new(new Auth(), new ReplayApiClient(_http), Router, Model, Options.Create(new MatchChatOptions()));
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    internal sealed class Router : IMatchWorkflowRouter
    { public bool Configured => true; internal string Workflow = "current_match_summary"; internal List<MatchRoutingInput> Inputs = []; internal Func<Task>? Wait;
        public async Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Inputs.Add(input); if (Wait is not null) await Wait(); return new(MatchRoutingStatus.Selected, Workflow); } }
    internal sealed class Model : IMatchChatModel
    { public bool Configured => true; internal List<MatchNarratorRequest> Requests = []; internal Func<Task>? Wait;
        public async Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { Requests.Add(request); if (Wait is not null) await Wait(); return new(JsonSerializer.Serialize(new { answer = "The evidence is limited.", evidenceIds = new[] { "coverage" } }), []); } }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Auth : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "language-user"), new("roles", "Tester")], "unit", "sub", "roles")))); }
}
