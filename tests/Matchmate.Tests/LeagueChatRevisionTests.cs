using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatRevisionTests
{
    [Theory]
    [InlineData("named_ground_context", "arsenal")] [InlineData("continue_topic", "arsenal")]
    [InlineData("ground_context", null)] [InlineData("league_table", "arsenal")]
    [InlineData("coverage_limits", "arsenal")] [InlineData("date_changes", "invalid subject")]
    public async Task ContinuationRequiresCanonicalTopicAndValidSubjectShape(string workflow, string? subject)
    {
        var topic = new LeagueAcceptedTopic(workflow, subject); Assert.False(LeagueWorkflowRegistry.ValidTopic(topic)); var reads = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(LeagueChatContext.From(LeagueChatFixtures.Snapshot()), "Tell me more.", topic, default)); Assert.Equal(0, reads);
    }
    [Fact]
    public async Task ConflictingKnownNameCannotHijackAContinuationSubject()
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)), "Tell me more about Everton.", new("ground_context", "arsenal"), default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
    }
    [Fact]
    public async Task StructurallyValidButAbsentContinuationSubjectNeverFallsBackToUiClub()
    {
        var reads = 0; var topic = new LeagueAcceptedTopic("ground_context", "barcelona"); Assert.True(LeagueWorkflowRegistry.ValidTopic(topic));
        await Assert.ThrowsAsync<InvalidDataException>(() => LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(LeagueChatContext.From(LeagueChatFixtures.Snapshot()), "Tell me more.", topic, default));
        Assert.Equal(1, reads);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task PinnedBrentford18SeptemberSourceGroundCanContinueWithoutChangingSubject()
    {
        var model = new LeagueModel(LeagueReferenceCodec.Decode(File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"))));
        var snapshot = model.Snapshot("2026-27", 15, "brentford"); Assert.Equal("2026-09-18", snapshot.Date); var reads = 0;
        var executor = new LeagueWorkflowExecutor((season, index, club, _) => { reads++; return Task.FromResult(model.Snapshot(season, index, club)); });
        var router = new LeagueChatFixtures.Router { Workflow = "continue_topic" }; var narrator = new LeagueChatFixtures.Model { Completing = (_, _) => Task.FromResult(new MatchModelReply("{\"answer\":\"This reference only lists the ground and town; it has no further stadium detail. We can look at the club's results or table position.\",\"evidenceIds\":[\"ground_reference\"]}", [])) };
        using var session = LeagueChatFixtures.Session(router: router, model: narrator, executor: executor); var context = LeagueChatContext.From(snapshot); session.SetContext(context);
        await session.AskSourceAsync(context, "ground"); await session.AskAsync(context, "Tell me more.");
        Assert.Equal(2, reads); Assert.Equal("brentford", session.Turns[^1].ResolvedClubId); Assert.Equal(new LeagueAcceptedTopic("ground_context", "brentford"), router.Inputs[^1].AcceptedTopic);
        Assert.Contains("no further stadium detail", session.Turns[^1].Answer); Assert.Equal(MatchChatOutcome.Answered, session.Turns[^1].Outcome);
    }
    [Theory]
    [InlineData("What changed on 5 January?", false)] [InlineData("What changed on January 5?", false)] [InlineData("What changed on 5 January 2016?", false)]
    [InlineData("What changed on 6 January 2016?", true)] [InlineData("What changed on 5 January 2015?", true)] [InlineData("What changed on 5 January 2017?", true)]
    public async Task CalendarDateScopeUsesSelectedYearAcrossSeasonBoundary(string question, bool clarify)
    {
        var original = LeagueChatFixtures.Season(true); var dates = original.Dates.Select((date, i) => (date, shifted: new DateOnly(2015, 12, 1).AddDays(i).ToString("yyyy-MM-dd"))).ToDictionary(p => p.date, p => p.shifted);
        var model = new LeagueSeasonModel(new("2015-16", "2015/16", LeagueChatFixtures.Snapshot(historical: true).Clubs, original.Matches.Select(m => m with { Date = dates[m.Date] }).ToArray()), version: Matchmate.Core.LeagueReferenceCodec.CurrentVersion);
        var snapshot = model.Snapshot(36, "everton"); Assert.Equal("2016-01-05", snapshot.Date);
        var result = await new LeagueWorkflowExecutor((_, index, club, _) => Task.FromResult(model.Snapshot(index, club))).ExecuteAsync("date_changes", LeagueChatContext.From(snapshot), question, null, default);
        Assert.Equal(clarify ? MatchChatOutcome.Clarify : MatchChatOutcome.Answered, result.Outcome);
    }
    [Theory]
    [InlineData("What points did Barcelona add on this date?")]
    [InlineData("What happened to Barcelona on this date?")]
    [InlineData("What changed for Barcelona on this date?")]
    [InlineData("What points did our Barcelona team add on this date?")]
    [InlineData("What points did their Barcelona team add on this date?")]
    [InlineData("What changed for Manchester on this date?")]
    [InlineData("What changed for Arsenal and Everton on this date?")]
    [InlineData("What did we add on this date?")]
    public async Task NamedDateRequiresExactlyOneTrustedNameRegardlessOfGrammarOrContext(string question)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("named_date_changes", LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
    }
    [Fact]
    public async Task DateWorkflowKeepsNamedReferenceSourceIntentAndClarificationWithinThreeReads()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "named_date_changes" }; var model = new LeagueChatFixtures.Model();
        var reads = new List<string?>(); using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, club) => reads.Add(club)));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "What points did Arsenal add on this date?")).Accepted);
        Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId); Assert.Equal(new string?[] { "everton", "arsenal", "arsenal" }, reads); reads.Clear();
        router.Workflow = "date_changes"; await session.AskAsync(context, "What points did they add on this date?"); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId); Assert.Equal(3, reads.Count); reads.Clear();
        await session.AskSourceAsync(context, "changes"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId); Assert.Equal(2, reads.Count); Assert.True(session.Turns[^1].SourceOnly); reads.Clear();
        router.Workflow = "named_date_changes"; var narrations = model.Requests.Count;
        await session.AskAsync(context, "What points did their Barcelona team add on this date?");
        Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Empty(session.Turns[^1].Evidence); Assert.Equal(narrations, model.Requests.Count); Assert.Single(reads); reads.Clear();
        await session.AskAsync(context, "Man City"); Assert.Equal("manchester-city", session.Turns[^1].ResolvedClubId); Assert.NotNull(router.Inputs[^1].Clarification); Assert.Equal(3, reads.Count); reads.Clear();
        session.ResetSubjectReference(); router.Workflow = "date_changes"; await session.AskAsync(context, "What points did they add on this date?");
        Assert.Equal("everton", session.Turns[^1].ResolvedClubId); Assert.False(router.Inputs[^1].HasNamedClubReference); Assert.Equal(2, reads.Count);
    }
    [Fact]
    public async Task WholeLeagueDateProjectionRemainsBoundedWithoutAClub()
    {
        var reads = 0; var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, null));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("date_changes", context, "What happened here?", null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal(2, reads);
        using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text);
        Assert.Equal(20, data.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
        Assert.All(data.RootElement.GetProperty("facts").GetProperty("resultsOnSelectedDate").EnumerateArray(), m => Assert.Equal(context.Date, m.GetProperty("Date").GetString()));
    }
    [Fact]
    public async Task NegativeQuestionPremiseDoesNotBecomePreparedEvidence()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "club_form" }; var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        await session.AskAsync(context, "Why have our recent results been poor?");
        Assert.Equal("club_form", router.Workflow); Assert.Single(router.Inputs);
        var prepared = Assert.Single(model.Requests).Messages.Single(m => m.Content.StartsWith("Prepared evidence", StringComparison.Ordinal)).Content;
        Assert.DoesNotContain("poor", prepared); Assert.Contains("club_results", prepared);
        using var standing = JsonDocument.Parse(session.Turns[^1].Evidence.Single(e => e.Id == "club_standing").Text);
        Assert.Equal(LeagueChatFixtures.Snapshot(2).SelectedClub!.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
    }
    [Theory]
    [InlineData("Tell me about our stadium reference.")]
    [InlineData("Tell me our home town and ground.")]
    [InlineData("Where is our town?")]
    [InlineData("Tell me about our new ground.")]
    public async Task OrdinaryPlaceWordsBindSelectedClubAndNeverTownOrInitialAliases(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", context, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId);
        Assert.Contains("Test ground Everton", LeagueWorkflowExecutor.SourceAnswer(result.Evidence));
        var followup = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", context, question.Replace("our", "their", StringComparison.Ordinal), "arsenal", default);
        Assert.Equal("arsenal", followup.ResolvedClubId);
    }
    [Theory]
    [InlineData("named_club_standing", "How are Barcelona doing for our club?")]
    [InlineData("named_club_form", "Barcelona: what are their recent results?")]
    [InlineData("named_ground_context", "Tell me about their Barcelona ground.")]
    [InlineData("named_ground_context", "Where is Manchester's ground?")]
    [InlineData("named_ground_context", "Where is NEW's stadium?")]
    [InlineData("named_club_standing", "How are City doing?")]
    [InlineData("named_date_changes", "What changed for Barcelona on this date?")]
    public async Task NamedOrIncompleteUnknownIdentityCannotFallBackToSelectionOrReference(string workflow, string question)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync(workflow, LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence);
    }
    [Theory]
    [InlineData("Man City", "manchester-city")]
    [InlineData("Man Utd", "manchester-united")]
    [InlineData("Palace", "crystal-palace")]
    [InlineData("Forest", "nottingham-forest")]
    [InlineData("Spurs", "tottenham")]
    [InlineData("Leeds", "leeds")]
    public async Task NamedGroundUsesOnlyCuratedTrustedAlias(string alias, string expected)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("named_ground_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), "Where is " + alias + " based?", null, default);
        Assert.Equal(expected, result.ResolvedClubId); Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
    }
    [Fact]
    public async Task DateAnswerComputesNoMatchUnchangedRankAndActualDayResultWithinTwoReads()
    {
        var model = DayScenario(); await CheckDayScenario((index, club) => model.Snapshot(index, club));
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ActualPinnedManCity14SeptemberMatchesIndependentScenario()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        await CheckDayScenario((index, club) => model.Snapshot("2026-27", index, club));
    }
    private static async Task CheckDayScenario(Func<int?, string?, LeagueSeasonSnapshot> snapshot)
    {
        var context = LeagueChatContext.From(snapshot(14, "manchester-city")); var reads = new List<int?>();
        var executor = new LeagueWorkflowExecutor((_, index, club, _) => { reads.Add(index); return Task.FromResult(snapshot(index, club)); });
        var result = await executor.ExecuteAsync("date_changes", context, "What happened here?", null, default);
        Assert.Equal(new int?[] { 14, 13 }, reads); var answer = LeagueWorkflowExecutor.SourceAnswer(result.Evidence);
        Assert.Contains("Manchester City did not play on this date.", answer); Assert.Contains("Points stayed at 12 from 4 matches.", answer);
        Assert.Contains("Stayed in position 2.", answer); Assert.Contains("Leeds United 4–1 Newcastle United.", answer);
        using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text);
        var facts = data.RootElement.GetProperty("facts"); var row = facts.GetProperty("rows")[0];
        Assert.False(row.GetProperty("playedOnDate").GetBoolean()); Assert.Equal(0, row.GetProperty("pointsChange").GetInt32());
        Assert.Equal(2, row.GetProperty("previousPosition").GetInt32()); Assert.Equal(2, row.GetProperty("position").GetInt32());
        Assert.Single(facts.GetProperty("resultsOnSelectedDate").EnumerateArray()); Assert.Equal("2026-09-14", context.Date);
    }
    private static LeagueSeasonModel DayScenario()
    {
        // Explicit synthetic scenario matching the independently verified no-match/date example.
        var clubs = LeagueChatFixtures.Snapshot().Clubs;
        string[] dates = ["2026-08-21", "2026-08-22", "2026-08-23", "2026-08-24", "2026-08-28", "2026-08-29", "2026-08-30", "2026-09-04", "2026-09-05", "2026-09-06", "2026-09-11", "2026-09-12", "2026-09-13", "2026-09-14"];
        (string, string)[] pairs = [("manchester-city", "chelsea"), ("arsenal", "everton"), ("arsenal", "fulham"), ("arsenal", "hull"), ("manchester-city", "brentford"), ("arsenal", "liverpool"), ("arsenal", "ipswich"), ("manchester-city", "bournemouth"), ("brighton", "chelsea"), ("coventry", "sunderland"), ("manchester-city", "tottenham"), ("fulham", "hull"), ("liverpool", "everton"), ("leeds", "newcastle")];
        var matches = pairs.Select((p, i) => new LeagueMatch(p.Item1 + ":" + p.Item2, dates[i], p.Item1, p.Item2, i == 13 ? 4 : 1, i == 13 ? 1 : 0, "Synthetic date scenario")).ToArray();
        return new(new("2026-27", "2026/27", clubs, matches, "ongoing", 380, "2026-09-14", "2026-10-23T12:15:59Z"), version: LeagueReferenceCodec.CurrentVersion);
    }
    [Fact]
    public async Task PreseasonAndSharedRankDateAnswersDoNotInventPriorRanking()
    {
        var zero = await LeagueChatFixtures.Executor().ExecuteAsync("date_changes", LeagueChatContext.From(LeagueChatFixtures.Snapshot(0, null)), "What happened here?", null, default);
        Assert.Contains("All clubs are unranked", LeagueWorkflowExecutor.SourceAnswer(zero.Evidence));
        var model = new LeagueSeasonModel(new("2026-27", "2026/27", LeagueChatFixtures.Snapshot().Clubs,
            [new("manchester-city:chelsea", "2026-08-21", "manchester-city", "chelsea", 1, 0, "Synthetic shared rank"),
             new("arsenal:everton", "2026-08-21", "arsenal", "everton", 1, 0, "Synthetic shared rank")], "ongoing", 380, "2026-08-21", "2026-10-23T12:15:59Z"), version: LeagueReferenceCodec.CurrentVersion);
        var shared = model.Snapshot(1, "manchester-city"); Assert.True(shared.SelectedClub!.Row.Shared);
        var executor = new LeagueWorkflowExecutor((_, index, club, _) => Task.FromResult(model.Snapshot(index, club)));
        var result = await executor.ExecuteAsync("date_changes", LeagueChatContext.From(shared), "What happened on this day?", null, default);
        Assert.Contains("there was no previous ranking", LeagueWorkflowExecutor.SourceAnswer(result.Evidence));
        Assert.Contains("shared position", LeagueWorkflowExecutor.SourceAnswer(result.Evidence));
    }
    [Fact]
    public async Task NamedThenSourceReselectAndBriefClarificationRetainCorrectSubject()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_standing" }; using var session = LeagueChatFixtures.Session(router: router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        await session.AskAsync(context, "How are Arsenal doing?"); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        router.Workflow = "ground_context"; await session.AskAsync(context, "Tell me about their new ground."); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        await session.AskSourceAsync(context, "ground"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
        router.Workflow = "named_ground_context"; await session.AskAsync(context, "Where is Barcelona's ground?"); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
        await session.AskAsync(context, "Palace"); Assert.Equal("crystal-palace", session.Turns[^1].ResolvedClubId); Assert.NotNull(router.Inputs[^1].Clarification);
        session.ResetSubjectReference(); router.Workflow = "ground_context"; await session.AskAsync(context, "Tell me about their home town and ground.");
        Assert.Equal("everton", session.Turns[^1].ResolvedClubId); Assert.False(router.Inputs[^1].HasNamedClubReference);
    }
}
