using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Matchmate.Web.LeagueChat;
using Matchmate.Web.Localization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class SiteChatLocalizationTests
{
    [Theory]
    [InlineData("LeagueChatText")]
    [InlineData("HistoricalChatText")]
    public void CatalogsHaveIdenticalKeysAndPlaceholders(string name)
    {
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Localization", name + suffix + ".resx"))
            .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var neutral = Read(""); Assert.True(neutral.Count >= 30);
        foreach (var suffix in new[] { ".sv", ".es" })
        {
            var translated = Read(suffix); Assert.Equal(neutral.Keys.Order(), translated.Keys.Order());
            foreach (var key in neutral.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]));
                Assert.Equal(Regex.Matches(neutral[key], @"\{\d+\}").Select(m => m.Value).Order(), Regex.Matches(translated[key], @"\{\d+\}").Select(m => m.Value).Order());
            }
        }
        Assert.Equal("Where do we stand?", LeagueChatText.Get((MatchLanguage)99, "Question.standing"));
        Assert.Equal("The result", HistoricalChatText.Get((MatchLanguage)99, "Title.result"));
    }

    [Theory]
    [InlineData(MatchLanguage.English, false)] [InlineData(MatchLanguage.Swedish, false)] [InlineData(MatchLanguage.Spanish, false)]
    [InlineData(MatchLanguage.English, true)] [InlineData(MatchLanguage.Swedish, true)] [InlineData(MatchLanguage.Spanish, true)]
    public async Task LeagueSourceActionsPreserveClickedQuestionReadsAndExactEvidence(MatchLanguage language, bool club)
    {
        var actualReads = new List<string>(); var expectedReads = new List<string>();
        var router = new LeagueChatFixtures.Router(); var model = new LeagueChatFixtures.Model();
        using var actual = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((s, d, c) => actualReads.Add($"{s}|{d}|{c}")));
        using var expected = LeagueChatFixtures.Session(executor: LeagueChatFixtures.Executor((s, d, c) => expectedReads.Add($"{s}|{d}|{c}")));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, club ? "everton" : null)); actual.SetContext(context); expected.SetContext(context);
        Assert.False(actual.SetLanguage((MatchLanguage)99)); actual.SetLanguage(language);
        foreach (var source in LeagueWorkflowRegistry.Suggestions(club))
        {
            Assert.True((await actual.AskSourceAsync(context, source.Id)).Accepted); Assert.True((await expected.AskSourceAsync(context, source.Id)).Accepted);
            var turn = actual.Turns[^1]; Assert.Equal(language, turn.Language); Assert.True(turn.SourceOnly);
            Assert.Equal(LeagueChatPresentation.DisplayQuestion(source, language), turn.Question);
            Assert.Equal(JsonSerializer.Serialize(expected.Turns[^1].Evidence), JsonSerializer.Serialize(turn.Evidence));
            Assert.Equal(expected.Turns[^1].ResolvedClubId, turn.ResolvedClubId); Assert.Equal(context, turn.Context);
            if (language == MatchLanguage.English) Assert.Equal(expected.Turns[^1].Answer, turn.Answer);
            else Assert.InRange(turn.Answer.Length, 1, MatchChatLimits.AnswerChars);
            if (source.Id == "standing") { Assert.Contains("Everton", turn.Answer); Assert.Contains(language == MatchLanguage.Spanish ? "puntos" : language == MatchLanguage.Swedish ? "poäng" : "point", turn.Answer); }
            if (source.Id == "ground") { Assert.Contains("Test ground Everton", turn.Answer); Assert.Contains(language == MatchLanguage.Spanish ? "historia" : language == MatchLanguage.Swedish ? "arenahistorik" : "history", turn.Answer); }
        }
        Assert.Equal(expectedReads, actualReads); Assert.Empty(router.Inputs); Assert.Empty(model.Requests);
    }

    [Theory] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task TypedLeagueIntentRemainsOriginalWithOneTrustedLanguageMessage(MatchLanguage language)
    {
        var router = new LeagueChatFixtures.Router(); var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context); session.SetLanguage(language);
        const string original = "Explain Everton's position"; Assert.True((await session.AskAsync(context, original)).Accepted);
        Assert.Equal(original, Assert.Single(router.Inputs).Question); var request = Assert.Single(model.Requests);
        Assert.Equal(original, request.Messages[^1].Content); Assert.Single(request.Messages, m => m.Content.StartsWith(LeagueChatPresentation.Version, StringComparison.Ordinal));
        Assert.Contains(ChatText.Get(language, "Language.Instruction"), request.Messages.Single(m => m.Content.StartsWith(LeagueChatPresentation.Version, StringComparison.Ordinal)).Content);
        Assert.Equal(language, Assert.Single(session.Turns).Language); Assert.Equal(original, session.Turns[0].Question);
    }

    [Theory] [InlineData("read")] [InlineData("router")] [InlineData("narrator")]
    public async Task LeagueLanguageFlipCancelsPendingReplyAndPreservesAcceptedReference(string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayRead = false;
        async Task Pause() { entered.TrySetResult(); await release.Task; }
        var executor = new LeagueWorkflowExecutor(async (s, d, c, _) => { if (delayRead) await Pause(); return LeagueChatFixtures.Snapshot(d, c); });
        var router = new LeagueChatFixtures.Router(); var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(router: router, model: model, executor: executor);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context); await session.AskSourceAsync(context, "standing"); var previous = Assert.Single(session.Turns);
        if (stage == "read") delayRead = true;
        if (stage == "router") router.Routing = async (_, _) => { await Pause(); return new(MatchRoutingStatus.Selected, "club_standing"); };
        if (stage == "narrator") model.Completing = async (_, _) => { await Pause(); return new(HistoricalChatFixtures.Reply("The table is recorded.", "club_standing"), []); };
        var pending = session.AskAsync(context, "Explain this position"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.SetLanguage(MatchLanguage.Swedish)); Assert.False(session.SetLanguage(MatchLanguage.Swedish)); release.TrySetResult(); Assert.False((await pending).Accepted);
        Assert.Same(previous, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, previous.Language);
        delayRead = false; router.Routing = null; model.Completing = null; router.Workflow = "continue_topic";
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(context, "Tell me more")).Outcome);
        Assert.Equal("everton", router.Inputs[^1].AcceptedTopic!.SubjectId); Assert.Equal(MatchLanguage.Swedish, session.Turns[^1].Language);
    }

    [Theory] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task HistoricalSourceRecoveryIsLocalizedAndPreservesPreparedEvidenceWithoutExtraCalls(MatchLanguage language)
    {
        var fixture = HistoricalChatFixtures.Fixture(); var context = HistoricalChatContext.From(fixture); var reads = 0;
        var evidence = new HistoricalEvidence((_, _, _) => { reads++; return Task.FromResult(fixture); }, (_, _, _, _) => { reads++; return Task.FromResult(HistoricalChatFixtures.Model().Snapshot(null)); });
        var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalModel { Complete = _ => Task.FromException<MatchModelReply>(new HttpRequestException("offline")) };
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), evidence, router, model, Options.Create(new MatchChatOptions()));
        session.SetContext(context); session.SetLanguage(language);
        foreach (var suggestion in HistoricalWorkflows.Suggestions)
        {
            var result = await session.AskSuggestionAsync(context, suggestion.Id); Assert.True(result.Accepted); Assert.Equal(MatchChatOutcome.NarratorUnavailable, result.Outcome);
            var turn = session.Turns[^1]; Assert.True(turn.SourceOnly); Assert.Equal(language, turn.Language);
            Assert.Equal(HistoricalChatPresentation.DisplayQuestion(suggestion, language), turn.Question); Assert.False(string.IsNullOrWhiteSpace(HistoricalChatPresentation.DisplayTitle(suggestion, language)));
            var prepared = HistoricalEvidence.Prepare(fixture, HistoricalChatFixtures.Clubs, suggestion.Workflow, null);
            var id = suggestion.Id switch { "result" => "fixture", "table" => "table", "form" => "form", _ => "reference" };
            Assert.Equal(JsonSerializer.Serialize(new[] { prepared.Evidence.Single(e => e.Id == id) }), JsonSerializer.Serialize(turn.Evidence));
            Assert.StartsWith(HistoricalChatText.Get(language, "Source.Recovery"), turn.Answer);
            Assert.Contains(suggestion.Id == "meetings" ? "2016-12-14" : "Southampton", turn.Answer); Assert.InRange(turn.Answer.Length, 1, MatchChatLimits.AnswerChars);
        }
        Assert.Equal(8, reads); Assert.Empty(router.Inputs); Assert.Equal(4, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Single(request.Messages, m => m.Content.StartsWith(HistoricalChatPresentation.Version, StringComparison.Ordinal)));
    }

    [Theory] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task EmptyHistoricalFormIsHonestLocalizedAndNeverCallsModels(MatchLanguage language)
    {
        var fixture = HistoricalChatFixtures.Fixture() with { HomePreMatchForm = [], AwayPreMatchForm = [] }; var context = HistoricalChatContext.From(fixture);
        var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalModel();
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), HistoricalChatFixtures.Evidence(fixture), router, model, Options.Create(new MatchChatOptions()));
        session.SetContext(context); session.SetLanguage(language); Assert.True((await session.AskSuggestionAsync(context, "form")).Accepted);
        var turn = Assert.Single(session.Turns); Assert.True(turn.SourceOnly); Assert.Contains(language == MatchLanguage.Swedish ? "aldrig" : "nunca", turn.Answer);
        Assert.DoesNotContain("0", turn.Answer.Replace(context.Date, "").Replace(context.SeasonKey, "")); Assert.Empty(router.Inputs); Assert.Empty(model.Requests);
    }

    [Theory] [InlineData("read")] [InlineData("router")] [InlineData("narrator")]
    public async Task HistoricalLanguageFlipPreservesHistoryAndTopicAndDiscardsLateReply(string stage)
    {
        var fixture = HistoricalChatFixtures.Fixture(); var context = HistoricalChatContext.From(fixture); var delay = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause() { entered.TrySetResult(); await release.Task; }
        var evidence = new HistoricalEvidence(async (_, _, _) => { if (delay && stage == "read") await Pause(); return fixture; }, (_, _, _, _) => Task.FromResult(HistoricalChatFixtures.Model().Snapshot(null)));
        var router = new HistoricalRouter { Routing = async _ => { if (delay && stage == "router") await Pause(); return new(MatchRoutingStatus.Selected, "overview"); } };
        var model = new HistoricalModel { Complete = async _ => { if (delay && stage == "narrator") await Pause(); return new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []); } };
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), evidence, router, model, Options.Create(new MatchChatOptions())); session.SetContext(context);
        await session.AskAsync(context, "Explain Southampton's result"); var previous = Assert.Single(session.Turns); delay = true;
        var pending = session.AskAsync(context, "Explain that result"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); session.SetLanguage(MatchLanguage.Spanish); release.TrySetResult();
        Assert.False((await pending).Accepted); Assert.Same(previous, Assert.Single(session.Turns)); Assert.Equal(MatchLanguage.English, previous.Language);
        delay = false; const string original = "Tell me more"; Assert.True((await session.AskAsync(context, original)).Accepted);
        Assert.Equal("southampton", router.Inputs[^1].Topic!.ClubId); Assert.Equal(original, router.Inputs[^1].Question); Assert.Equal(original, model.Requests[^1].Messages[^1].Content);
        Assert.Equal(MatchLanguage.Spanish, session.Turns[^1].Language);
    }

    [Theory]
    [InlineData(MatchLanguage.Swedish, "Southampton gjorde 3 mål i det tidigare resultaturvalet.", "form")]
    [InlineData(MatchLanguage.Swedish, "Stoke City gjorde 1 mål i den här matchen.", "fixture")]
    [InlineData(MatchLanguage.Spanish, "Southampton marcó 3 goles en la muestra de resultados anteriores.", "form")]
    [InlineData(MatchLanguage.Spanish, "Stoke City marcó 1 gol en este partido.", "fixture")]
    [InlineData(MatchLanguage.Swedish, "Inga målskyttar eller målmekanismer finns i underlaget.", "fixture")]
    [InlineData(MatchLanguage.Spanish, "No hay datos de goleadores ni de mecanismos de gol.", "fixture")]
    public void LocalizedHistoricalGoalClausesRequirePreparedTeamTotalScopeAndCitation(MatchLanguage language, string answer, string id)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        var actual = LocalizedHistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, id), prepared, language);
        Assert.Equal(answer, actual.Answer); Assert.Equal(id, Assert.Single(actual.Evidence).Id);
    }

    [Theory]
    [InlineData("Southampton gjorde 1 mål i det tidigare resultaturvalet.", "form")]
    [InlineData("Southampton gjorde 3 mål i den här matchen.", "form")]
    [InlineData("Southampton gjorde 3 mål i det tidigare resultaturvalet.", "fixture")]
    [InlineData("Peter Crouch gjorde 1 mål i den här matchen.", "fixture")]
    [InlineData("Stoke City gjorde 1 mål i den här matchen genom Peter Crouch.", "fixture")]
    [InlineData("Stoke City marcó 3 goles en la muestra de resultados anteriores.", "form")]
    [InlineData("Southampton marcó 3 goles en este partido.", "form")]
    [InlineData("Southampton marcó 3 goles en la muestra de resultados anteriores.", "fixture")]
    [InlineData("Peter Crouch marcó 1 gol en este partido.", "fixture")]
    [InlineData("Stoke City marcó 1 gol en este partido de cabeza.", "fixture")]
    [InlineData("Inga målskyttar finns i underlaget; siffran var 1.", "fixture")]
    [InlineData("No hay datos de goleadores; el valor fue 1.", "fixture")]
    [InlineData("Matchen spelades på St Mary's Stadium.", "fixture")]
    [InlineData("El partido se disputó en St Mary's Stadium.", "fixture")]
    [InlineData("Chelsea vann.", "fixture")]
    public void LocalizedHistoricalMisattributionDetailsAndVenueClaimsReject(string answer, string id)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => LocalizedHistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, id), prepared,
            answer.Contains("marcó", StringComparison.Ordinal) || answer.StartsWith("No ", StringComparison.Ordinal) || answer.StartsWith("El ", StringComparison.Ordinal) ? MatchLanguage.Spanish : MatchLanguage.Swedish));
    }

    [Fact]
    public void HistoricalEnglishValidationRemainsAnExactDelegate()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        var json = HistoricalChatFixtures.Reply("Stoke City scored 1 goal in this fixture.", "fixture");
        var original = HistoricalReplyValidation.Validate(json, prepared); var actual = LocalizedHistoricalReplyValidation.Validate(json, prepared, MatchLanguage.English);
        Assert.Equal(original.Answer, actual.Answer); Assert.Equal(original.Evidence, actual.Evidence);
    }

    private sealed class HistoricalModel : IHistoricalChatModel
    {
        public bool Configured => true;
        internal readonly List<HistoricalNarratorRequest> Requests = [];
        internal Func<HistoricalNarratorRequest, Task<MatchModelReply>> Complete = _ => Task.FromResult(new MatchModelReply(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []));
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token) { Requests.Add(request); return Complete(request); }
    }
    private sealed class HistoricalRouter : IHistoricalWorkflowRouter
    {
        public bool Configured => true;
        internal readonly List<HistoricalRoutingInput> Inputs = [];
        internal Func<HistoricalRoutingInput, Task<MatchRoutingResult>> Routing = _ => Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, "overview"));
        public Task<MatchRoutingResult> RouteAsync(HistoricalRoutingInput input, CancellationToken token) { Inputs.Add(input); return Routing(input); }
    }
}
