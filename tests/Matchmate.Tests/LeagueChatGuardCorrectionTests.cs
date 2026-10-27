using System.Text;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

// Synthetic snapshots keep these guard regressions independent of ignored local data.
// The eight quoted v5 failures are now-seen regressions, never new evaluation golds.
public sealed class LeagueChatGuardCorrectionTests
{
    [Theory]
    [InlineData("Before any league games in this view, what does our points and played record look like?", "club_context", "coventry", "coventry", 0, false, 1, true)]
    [InlineData("How has our record shaped up by the day I've selected?", "club_context", "brentford", "brentford", 5, false, 2, true)]
    [InlineData("Which clubs changed position on the day shown?", "league_context", "fulham", null, 3, false, 2, true)]
    [InlineData("Could I get Fulham's points and win-draw-loss record from this table?", "named_club_context", "leeds", "fulham", 5, false, 3, true)]
    [InlineData("For 20 September 2026, how many points do we have and where are we placed?", "club_context", "newcastle", "newcastle", 5, false, 2, true)]
    [InlineData("Put West Ham United's league record and position into words for me.", "named_club_context", "liverpool", "west-ham", 5, true, 3, false)]
    [InlineData("Can you unpack that run of results for me?", "continue_topic", "arsenal", "bournemouth", 5, true, 2, false)]
    public Task SeenPositivePlansRetainTheirSubjectAndExactPreparedFacts(string question, string workflow, string? selection, string? subject, int index, bool historical, int reads, bool codeScope)
        => AssertAnswer(question, workflow, selection, subject, index, historical, reads, codeScope);

    [Theory]
    [InlineData("What does our points and played record look like here?", "club_context", "everton", 2)]
    [InlineData("Could I get Arsenal's points and win-draw-loss record from this table?", "named_club_context", "arsenal", 3)]
    [InlineData("Can I please get our points here?", "club_context", "everton", 2)]
    [InlineData("Could you show how Arsenal performed and where are we placed here?", "named_club_context", "arsenal", 3)]
    [InlineData("At this selected date, how have we performed and how are we ranked?", "club_context", "everton", 2)]
    [InlineData("Can you show how all clubs performed here?", "league_context", null, 2)]
    [InlineData("Could I get the whole league table here?", "league_context", null, 2)]
    [InlineData("Could you show how Arsenal performed here?", "named_club_context", "arsenal", 3)]
    [InlineData("For 20 September 2026, how has Arsenal won here?", "named_club_context", "arsenal", 3)]
    [InlineData("Show Arsenal's record here for me.", "named_club_context", "arsenal", 3)]
    [InlineData("Show Arsenal's record here for you.", "named_club_context", "arsenal", 3)]
    [InlineData("Explain our points here for me.", "club_context", "everton", 2)]
    [InlineData("Now, tell me how we performed here for me.", "club_context", "everton", 2)]
    [InlineData("Could you show how we performed and have we won here?", "club_context", "everton", 2)]
    [InlineData("Could you show how Arsenal performed and have Arsenal won here?", "named_club_context", "arsenal", 3)]
    [InlineData("Could you show how all clubs performed and have all clubs played here?", "league_context", null, 2)]
    [InlineData("Could you show how Arsenal performed or have Arsenal won here?", "named_club_context", "arsenal", 3)]
    public Task RequestAndIndependentQuestionClausesPreserveFactualEligibility(string question, string workflow, string? subject, int reads)
        => AssertAnswer(question, workflow, "everton", subject, 5, false, reads, true);

    [Theory]
    [InlineData("Could you show how we performed and have barcelona won here?")]
    [InlineData("Could you show how Arsenal performed and have barcelona won here?")]
    [InlineData("Could you show how we performed and is barcelona ranked here?")]
    [InlineData("Could you show how we performed and does barcelona perform here?")]
    [InlineData("Could you show how we performed and should barcelona play here?")]
    [InlineData("Could you show how Arsenal performed or have barcelona won here?")]
    public async Task IndependentAuxiliaryClauseCannotLoseAnUnknownSubject(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context", "league_context", "date_changes" })
        {
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
    }

    [Theory]
    [InlineData("What have barcelona won here?")]
    [InlineData("Where have barcelona played here?")]
    [InlineData("What did barcelona win here?")]
    [InlineData("Where does barcelona play here?")]
    [InlineData("What are barcelona doing here?")]
    [InlineData("Where is barcelona playing here?")]
    [InlineData("What have Arsenal and barcelona won here?")]
    [InlineData("Where have Arsenal and Everton played here?")]
    [InlineData("For 20 September 2026, where have barcelona played?")]
    public async Task ImmediateWhAuxiliaryStillValidatesUnknownAndMultipleSubjects(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context", "league_context", "date_changes" })
        {
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var continuationReads = 0; var continuation = await Executor(false, (_, _, _) => continuationReads++).ExecuteContinuationAsync(Context(), question, new("club_form", "bournemouth"), default);
        AssertFeedback(continuation, MatchChatOutcome.Clarify); Assert.Equal(1, continuationReads);
    }

    [Theory]
    [InlineData("What have we won here?", "club_context", "everton", 2)]
    [InlineData("Where have Arsenal played here?", "named_club_context", "arsenal", 3)]
    [InlineData("What did we earn here?", "club_context", "everton", 2)]
    [InlineData("What are we doing here?", "club_context", "everton", 2)]
    [InlineData("Where are Arsenal ranked here?", "named_club_context", "arsenal", 3)]
    [InlineData("For 20 September 2026, where have we played?", "club_context", "everton", 2)]
    public Task ImmediateWhAuxiliaryKnownSubjectsKeepFreshBoundedFacts(string question, string workflow, string? subject, int reads)
        => AssertAnswer(question, workflow, "everton", subject, 5, false, reads, true);

    [Theory]
    [InlineData("At this selected date, how has barcelona performed?")]
    [InlineData("On 20 September 2026 how has Arsenal and barcelona performed?")]
    [InlineData("At this selected date have barcelona won?")]
    [InlineData("For 20 September 2026 how has barcelona won here?")]
    [InlineData("What points have you earned here?")]
    [InlineData("How have you performed here for me?")]
    [InlineData("On 20 September 2026, have Arsenal and barcelona won?")]
    [InlineData("Could I get barcelona's points here for me?")]
    [InlineData("Could I get Arsenal's and barcelona's record here for me?")]
    [InlineData("Could you show how barcelona performed here for me?")]
    [InlineData("Could you show how Arsenal and Everton performed here?")]
    [InlineData("Could you show how Arsenal and/or barcelona performed here?")]
    [InlineData("Could you show how Arsenal performed and where does barcelona stand here?")]
    public async Task UnknownAndMultipleSubjectsStillRefuseAllForcedFactualProjections(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var c = Context();
            var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, c, question, "arsenal", default);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
    }

    [Theory]
    [InlineData("What does our points and played record look like here?")]
    [InlineData("Could I get our points here for me?")]
    [InlineData("For 20 September 2026, how many points do we have and where are we placed?")]
    [InlineData("At this selected date have we won?")]
    [InlineData("Could you show me how we performed here for you?")]
    [InlineData("Could you show how we performed and have we won here?")]
    [InlineData("What have we won here?")]
    [InlineData("Where have we played here?")]
    [InlineData("What are we doing here?")]
    public async Task MissingSubjectCannotAcquireLeagueOrDayFactsThroughWrongWorkflow(string question)
    {
        foreach (var workflow in new[] { "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("Could you show how we performed here?")]
    [InlineData("On 20 September 2026, how many points do we have and where do we stand?")]
    [InlineData("Could I get Arsenal's points here for me?")]
    public async Task SpecificClubQuestionCannotSilentlyRerouteAnErroneousGlobalWorkflow(string question)
    {
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, null, default);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
    }

    [Theory]
    [InlineData("Explain our points for the date we have chosen.", "club_context", "everton")]
    [InlineData("Explain the whole league on the chosen day.", "league_context", null)]
    [InlineData("Explain our points on the date selected here.", "club_context", "everton")]
    [InlineData("Show the league table on this day that I selected.", "league_context", null)]
    [InlineData("Explain our points by the day I\u2019ve selected.", "club_context", "everton")]
    [InlineData("Explain our points on the date we chose.", "club_context", "everton")]
    [InlineData("May we see this selected table?", "league_context", null)]
    [InlineData("Did we score 2026 goals here?", "club_context", "everton")]
    [InlineData("Now, show our points on the day chosen.", "club_context", "everton")]
    public Task CompleteSelectedDayAndExistingQuantityGrammarStillReadTheCommittedFacts(string question, string workflow, string? subject)
        => AssertAnswer(question, workflow, "everton", subject, 5, false, 2, true);

    [Theory]
    [InlineData("Move the answer back to the previous match date and give me our record.")]
    [InlineData("Show our record on the previous played day.")]
    [InlineData("Explain the whole league on the next match date.")]
    [InlineData("Show the whole league on the match day immediately before this selected date.")]
    [InlineData("Show our points on the date directly after the selected played day.")]
    [InlineData("Arsenal previous match date.")]
    public Task MatchQualifiedRelativeDatesCannotBorrowTheSnapshotDespiteWrongIntentOrWorkflow(string question)
        => AssertTimeFeedback(question, LeagueTimeResolution.DifferentTime, MatchChatOutcome.Clarify);

    [Theory]
    [InlineData("Show the whole league on the day shown in March.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Explain our points by the day I selected on Monday.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Show the whole league on the day shown at this minute.", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    [InlineData("Show our points on the day I've chosen and on 19 September 2026.", LeagueTimeResolution.DifferentTime, MatchChatOutcome.Clarify)]
    [InlineData("Show our points on the day I've selected this week.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Could you show how we performed and have we won at this minute?", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    public Task CompleteSelectedReferenceNeverErasesAnotherTemporalConstraint(string question, LeagueTimeResolution scope, MatchChatOutcome outcome)
        => AssertTimeFeedback(question, scope, outcome);

    [Fact]
    public async Task PendingIdentityAndAcceptedTopicCannotEraseAMatchQualifiedRelativeDate()
    {
        const string question = "Arsenal previous match date."; const string original = "Where is their ground here?"; var c = Context(null);
        foreach (var intent in Intents)
        {
            var input = Input(c, question) with { Clarification = new(original) };
            Assert.False(LeagueQuestionProjection.BriefIdentityReply(question)); Assert.Null(LeagueQuestionProjection.Original(input)); Assert.Equal(question, LeagueQuestionProjection.WorkflowQuestion(input));
            Assert.Equal("other_season", Select(input, "ground_context", "explicit_name", intent).WorkflowId);
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync("named_ground_context", c, question, null, default, original);
            AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("Could you show how we performed and have barcelona won here?")]
    [InlineData("Could you show how Arsenal performed or have barcelona won here?")]
    public async Task AcceptedTopicCannotHideAnUnknownIndependentAuxiliaryClause(string question)
    {
        var reads = 0; var topic = new LeagueAcceptedTopic("club_form", "bournemouth");
        var result = await Executor(false, (_, _, _) => reads++).ExecuteContinuationAsync(Context(), question, topic, default);
        AssertFeedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
    }

    [Fact]
    public async Task BeneficiaryContinuationRetainsOnlyTheAcceptedSubjectAndFreshFacts()
    {
        const string question = "Can you unpack that run of results for me here?"; var c = Context("arsenal"); var topic = new LeagueAcceptedTopic("club_form", "bournemouth");
        foreach (var intent in Intents)
        {
            Assert.Equal("continue_topic", Select(Input(c, question) with { AcceptedTopic = topic }, "continue_topic", "context", intent).WorkflowId);
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("bournemouth", result.ResolvedClubId); Assert.Equal(2, reads);
            Assert.Equal(new[] { "club_standing", "club_results", "league_coverage" }, result.Evidence.Select(e => e.Id));
            AssertStanding(result, Model().Snapshot(5, "bournemouth"));
        }
    }

    [Theory]
    [InlineData("Does this coverage include the previous match date?")]
    [InlineData("Can this source report live games on the day shown?")]
    public async Task CoverageBypassRemainsMetadataOnly(string question)
    {
        foreach (var intent in Intents)
        {
            var c = Context(); Assert.Equal("coverage_limits", Select(Input(c, question), "coverage_limits", "context", intent).WorkflowId);
            var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync("coverage_limits", c, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id); Assert.Equal(1, reads);
        }
    }

    [Fact]
    public async Task ClauseEligibilityNeverRewritesTheTypedQuestionOrHistory()
    {
        const string question = "Could I get Arsenal's points and record here for me?";
        var router = new LeagueChatFixtures.Router { Routing = (input, _) => Task.FromResult(Select(input, "club_context", "explicit_name", "selected_view")) };
        using var session = LeagueChatFixtures.Session(router: router, executor: Executor()); var c = Context(); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(question, Assert.Single(router.Inputs).Question); var turn = Assert.Single(session.Turns); Assert.Equal(question, turn.Question); Assert.Equal(MatchChatOutcome.Answered, turn.Outcome); Assert.Equal("arsenal", turn.ResolvedClubId);
    }

    private static readonly string[] Intents = ["selected_view", "explicit_time_reference"];
    private static LeagueSeasonModel Model(bool historical = false)
    {
        var source = LeagueChatFixtures.Season(historical); if (!historical) return source;
        string Remap(string id) => id == "hull" ? "west-ham" : id;
        var clubs = source.Snapshot(null).Clubs.Select(c => c.Id == "hull" ? c with { Id = "west-ham", Name = "West Ham United" } : c).ToArray();
        var matches = source.Matches.Select(m => m with { Home = Remap(m.Home), Away = Remap(m.Away), MatchKey = Remap(m.Home) + ":" + Remap(m.Away) }).ToArray();
        return new(new("2015-16", "Synthetic historical guard tests", clubs, matches), version: LeagueReferenceCodec.CurrentVersion);
    }
    private static LeagueWorkflowExecutor Executor(bool historical = false, Action<string, int?, string?>? reading = null)
    {
        var model = Model(historical); return new((season, index, club, _) => { Assert.Equal(model.Key, season); reading?.Invoke(season, index, club); return Task.FromResult(model.Snapshot(index, club)); });
    }
    private static LeagueChatContext Context(string? club = "everton", int index = 5, bool historical = false) => LeagueChatContext.From(Model(historical).Snapshot(index, club));
    private static LeagueRoutingInput Input(LeagueChatContext c, string question) => new(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
    private static MatchRoutingResult Select(LeagueRoutingInput input, string workflow, string binding, string intent)
        => DecisionLeagueWorkflowRouter.Select(DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, binding, scope: intent))), new DecisionRoutingOptions(), input);
    private static void AssertFeedback(LeagueWorkflowResult result, MatchChatOutcome outcome)
    { Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); }
    private static void AssertStanding(LeagueWorkflowResult result, LeagueSeasonSnapshot snapshot)
    {
        using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text); var row = snapshot.SelectedClub!.Row; var facts = data.RootElement.GetProperty("facts");
        Assert.Equal(row.Points, facts.GetProperty("Points").GetInt32()); Assert.Equal(row.Played, facts.GetProperty("Played").GetInt32()); Assert.Equal(row.Won, facts.GetProperty("Won").GetInt32()); Assert.Equal(row.Drawn, facts.GetProperty("Drawn").GetInt32()); Assert.Equal(row.Lost, facts.GetProperty("Lost").GetInt32());
    }
    private static async Task AssertAnswer(string question, string workflow, string? selection, string? subject, int index, bool historical, int expectedReads, bool codeScope)
    {
        var c = Context(selection, index, historical); var topic = workflow == "continue_topic" ? new LeagueAcceptedTopic("club_form", subject) : null;
        foreach (var intent in codeScope ? Intents : ["selected_view"])
        {
            var canonical = workflow.StartsWith("named_", StringComparison.Ordinal) ? workflow[6..] : workflow;
            var route = Select(Input(c, question) with { AcceptedTopic = topic }, canonical, workflow.StartsWith("named_", StringComparison.Ordinal) ? "explicit_name" : "context", intent);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal(workflow, route.WorkflowId);
            var reads = 0; var executor = Executor(historical, (_, _, _) => reads++);
            var result = topic is null ? await executor.ExecuteAsync(workflow, c, question, null, default) : await executor.ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
            Assert.Equal(workflow == "league_context" ? new[] { "table_snapshot", "date_change", "league_coverage" } : workflow == "continue_topic" ? ["club_standing", "club_results", "league_coverage"] : ["club_standing", "club_results", "date_change", "league_coverage"], result.Evidence.Select(e => e.Id));
            if (subject is not null) AssertStanding(result, Model(historical).Snapshot(index, subject));
        }
    }
    private static async Task AssertTimeFeedback(string question, LeagueTimeResolution scope, MatchChatOutcome outcome)
    {
        foreach (var selection in new string?[] { null, "everton" })
        {
            var c = Context(selection); var topic = new LeagueAcceptedTopic("club_context", "arsenal");
            foreach (var workflow in new[] { "league_context", "club_context", "named_club_context", "date_changes", "ground_context" })
            {
                var reads = 0; var result = await Executor(false, (_, _, _) => reads++).ExecuteAsync(workflow, c, question, "arsenal", default);
                AssertFeedback(result, outcome); Assert.Equal(0, reads);
            }
            var continuationReads = 0; var continuation = await Executor(false, (_, _, _) => continuationReads++).ExecuteContinuationAsync(c, question, topic, default);
            AssertFeedback(continuation, outcome); Assert.Equal(0, continuationReads);
            foreach (var intent in Intents)
            {
                Assert.Equal(scope, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
                foreach (var workflow in new[] { "league_context", "club_context", "ground_context", "continue_topic" })
                    Assert.Equal(scope == LeagueTimeResolution.RealWorldNow ? "live_unavailable" : "other_season", Select(Input(c, question) with { AcceptedTopic = topic }, workflow, "context", intent).WorkflowId);
            }
        }
    }
}
