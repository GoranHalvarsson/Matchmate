using System.Text;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

// Synthetic, internally consistent snapshots exercise code eligibility independently
// of model choices. The three seen questions are regressions, not imported golds.
public sealed class LeagueChatTemporalScopeTests
{
    [Theory]
    [InlineData("Ignore the replay date: who's leading the Premier League at this moment?")]
    [InlineData("Show this selected table at the moment.")]
    [InlineData("Show the whole league at current moment.")]
    [InlineData("Explain our points at present moment.")]
    [InlineData("Where is Arsenal's ground at this very moment?")]
    [InlineData("Show the standings at the present moment.")]
    [InlineData("Show the standings at the current moment.")]
    [InlineData("Show this table at\tthis\nvery moment.")]
    [InlineData("Who leads the whole league as we speak?")]
    [InlineData("Show this table as we're speaking.")]
    [InlineData("Explain our points on 20 September 2026 as we are speaking.")]
    [InlineData("Now, walk us through this selected table as we speak.")]
    public Task PresentMomentFamilyCannotReleaseFactsUnderWrongPlans(string question)
        => AssertRefusal(question, LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable);

    [Theory]
    [InlineData("Who leads the whole league at this minute?")]
    [InlineData("Show the Premier League table at this very second.")]
    [InlineData("Who leads the league as of this instant?")]
    [InlineData("Explain our points at the current instant.")]
    [InlineData("Where is Arsenal's ground as of the present second?")]
    [InlineData("Show this snapshot right this minute.")]
    [InlineData("Show this snapshot at\tthis\nvery second.")]
    [InlineData("Now, walk us through this selected table as of this instant.")]
    [InlineData("Show the whole league right at this moment.")]
    [InlineData("Show this selected table as of right now.")]
    public Task CurrentInstantUnitsOverrideWrongFactualPlansAndSelectedAnchors(string question)
        => AssertRefusal(question, LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable);

    [Theory]
    [InlineData("Explain our points this week.")]
    [InlineData("Show the whole league this morning.")]
    [InlineData("Show this snapshot during the current month.")]
    [InlineData("Show this selected table in the present year.")]
    [InlineData("Where is Arsenal's ground that afternoon?")]
    [InlineData("Show this snapshot this hour.")]
    [InlineData("Show this table this minute.")]
    [InlineData("Explain our points this second.")]
    [InlineData("Show this table this instant.")]
    [InlineData("Now, walk us through our points this week.")]
    [InlineData("Show this table on 20 September 2026 this evening.")]
    [InlineData("Explain our points in this month here.")]
    public Task UnconsumedDeicticPeriodsNeverBecomeImplicitSelection(string question)
        => AssertRefusal(question, LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify);

    [Theory]
    [InlineData("Show the table for the season immediately before the one selected.")]
    [InlineData("Show the whole league in the preceding season.")]
    [InlineData("Explain our points in the following year.")]
    [InlineData("Show this table on the preceding day.")]
    [InlineData("Show the standings in the following week.")]
    [InlineData("What changed on the date directly after this selected date?")]
    [InlineData("Show the whole league on the day immediately before the selected day.")]
    [InlineData("Where is Arsenal's ground in the season directly after the selected season?")]
    [InlineData("Show the whole league in the season immediately following the one selected.")]
    public Task RelativeTimeFamiliesCannotBorrowTheSelectedDate(string question)
        => AssertRefusal(question, LeagueTimeResolution.DifferentTime, MatchChatOutcome.Clarify);

    [Theory]
    [InlineData("Show this selected table outside the replay.")]
    [InlineData("Explain our points beyond this snapshot.")]
    [InlineData("Where is Arsenal's ground not in this view?")]
    [InlineData("Show the whole league on 20 September 2026 outside of the selected view.")]
    [InlineData("Arsenal outside replay.")]
    public Task OutsideViewWithoutLiveCueRemainsUnresolvedAndFactFree(string question)
        => AssertRefusal(question, LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify);

    [Theory]
    [InlineData("Now, talk me through this selected table outside the replay.")]
    [InlineData("Now, take us through this table beyond the selected snapshot.")]
    [InlineData("Now, walk me through this selected table not in this view.")]
    [InlineData("Show this table now, and talk me through it.")]
    [InlineData("Can you now run me through this selected table?")]
    [InlineData("Now, talk me through this table and the live standings.")]
    public Task DiscourseExceptionNeverConsumesNonleadingOrOutsideLiveScope(string question)
        => AssertRefusal(question, LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable);

    [Theory]
    [InlineData("Now, talk me through the selected standings from top to bottom.", "league_context", "context", null, 2)]
    [InlineData("Now, could you please walk us through this selected table?", "league_context", "context", null, 2)]
    [InlineData("Now run me through the whole league here.", "league_context", "context", null, 2)]
    [InlineData("Now, take us through our points on 20 September 2026.", "club_context", "context", "everton", 2)]
    [InlineData("Now, talk me through our points.", "club_context", "context", "everton", 2)]
    [InlineData("Now, could you walk us through the table?", "league_context", "context", null, 2)]
    [InlineData("Now, take me through Arsenal's form.", "club_context", "explicit_name", "arsenal", 3)]
    [InlineData("Now, please talk me through Arsenal's ground here.", "ground_context", "explicit_name", "arsenal", 2)]
    [InlineData("Now, would you take me through Arsenal's form here?", "club_context", "explicit_name", "arsenal", 3)]
    [InlineData("Show the whole league on 20 September 2026.", "league_context", "context", null, 2)]
    [InlineData("How are we doing here?", "club_context", "context", "everton", 2)]
    [InlineData("Did we score 2026 goals here?", "club_context", "context", "everton", 2)]
    [InlineData("Explain our points at this point here.", "club_context", "context", "everton", 2)]
    [InlineData("Explain our points at this stage here.", "club_context", "context", "everton", 2)]
    [InlineData("Did we play 90 minutes and score in 30 seconds here?", "club_context", "context", "everton", 2)]
    [InlineData("Show this snapshot on 20 September 2026.", "league_context", "context", null, 2)]
    [InlineData("May we see this selected table?", "league_context", "context", null, 2)]
    [InlineData("Can we march up this selected table?", "league_context", "context", null, 2)]
    public async Task TaskOrderAndMatchingScopeStillProduceExactFreshFacts(string question, string workflow, string binding, string? subject, int expectedReads)
    {
        var c = Context();
        foreach (var intent in Intents)
        {
            var route = Select(c, question, workflow, binding, intent);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status);
            Assert.Equal(binding == "explicit_name" ? "named_" + workflow : workflow, route.WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, c, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
            var ids = workflow switch { "league_context" => new[] { "table_snapshot", "date_change", "league_coverage" }, "club_context" => ["club_standing", "club_results", "date_change", "league_coverage"], _ => ["ground_reference", "league_coverage"] };
            Assert.Equal(ids, result.Evidence.Select(e => e.Id));
            if (workflow == "club_context")
            {
                using var evidence = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
                Assert.Equal(LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Row.Points, evidence.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
            }
            if (workflow == "league_context")
            {
                using var evidence = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "table_snapshot").Text);
                Assert.Equal(20, evidence.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
            }
        }
    }

    [Theory]
    [InlineData("Arsenal at this moment.", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    [InlineData("Arsenal as we speak.", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    [InlineData("Arsenal at this minute.", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    [InlineData("Arsenal as of this instant.", LeagueTimeResolution.RealWorldNow, MatchChatOutcome.Unavailable)]
    [InlineData("Arsenal this morning.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Arsenal current hour.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Arsenal outside replay.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Arsenal beyond view.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Outside snapshot.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Beyond replay.", LeagueTimeResolution.Unresolved, MatchChatOutcome.Clarify)]
    [InlineData("Arsenal in the preceding season.", LeagueTimeResolution.DifferentTime, MatchChatOutcome.Clarify)]
    public async Task PendingIdentityCannotEraseTemporalAuthority(string question, LeagueTimeResolution scope, MatchChatOutcome outcome)
    {
        var c = Context(null); const string original = "Where is their ground here?";
        foreach (var intent in Intents)
        {
            var input = Input(c, question) with { Clarification = new(original) };
            Assert.False(LeagueQuestionProjection.BriefIdentityReply(question)); Assert.Null(LeagueQuestionProjection.Original(input)); Assert.Equal(question, LeagueQuestionProjection.WorkflowQuestion(input));
            Assert.Equal(scope, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
            Assert.Equal(scope == LeagueTimeResolution.RealWorldNow ? "live_unavailable" : "other_season", Select(input, "ground_context", "explicit_name", intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("named_ground_context", c, question, null, default, original);
            Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("Does this source include live facts at this moment?")]
    [InlineData("What does coverage mean outside the replay?")]
    [InlineData("What are this snapshot's limits as we speak?")]
    [InlineData("What does coverage mean for the preceding season?")]
    [InlineData("Does this source include live facts at this minute?")]
    [InlineData("What are this snapshot's limits this morning?")]
    public async Task MetadataScopeDoesNotBecomeAFootballAnswerOrAnIncorrectLiveRefusal(string question)
    {
        var c = Context();
        foreach (var intent in Intents)
        {
            Assert.Equal("coverage_limits", Select(c, question, "coverage_limits", "context", intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("coverage_limits", c, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id); Assert.Equal(1, reads);
        }
    }

    [Fact]
    public async Task TaskOrderContinuationKeepsCanonicalNamedSubjectAndReadsFreshGround()
    {
        const string question = "Now, walk me through that selected ground here."; var c = Context(); var topic = new LeagueAcceptedTopic("ground_context", "arsenal");
        foreach (var intent in Intents)
        {
            Assert.Equal("continue_topic", Select(Input(c, question) with { AcceptedTopic = topic }, "continue_topic", "context", intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(e => e.Id)); Assert.Equal(2, reads);
        }
    }

    [Fact]
    public async Task TaskOrderPreseasonAndHistoricalReferencesRetainTheirCommittedFacts()
    {
        foreach (var snapshot in new[] { LeagueChatFixtures.Snapshot(0), LeagueChatFixtures.Snapshot(historical: true) })
        {
            var c = LeagueChatContext.From(snapshot);
            var question = c.Date is null ? "Now, take me through our points before the first played day." : "Now, run me through our points on " + c.Date + ".";
            foreach (var intent in Intents)
            {
                var route = Select(c, question, "club_context", "context", intent); Assert.Equal("club_context", route.WorkflowId);
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, c, question, null, default);
                Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId); Assert.Equal(c.Date is null ? 1 : 2, reads);
                using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
                Assert.Equal(snapshot.SelectedClub!.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
            }
        }
    }

    [Fact]
    public async Task AssessmentOnlyTaskOrderNormalizationPreservesTypedQuestionAndHistory()
    {
        const string question = "Now, talk me through the selected standings from top to bottom.";
        var router = new LeagueChatFixtures.Router { Routing = (input, _) => Task.FromResult(Select(input, "league_context", "context", "selected_view")) };
        using var session = LeagueChatFixtures.Session(router: router); var c = Context(); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(question, Assert.Single(router.Inputs).Question); Assert.Equal(question, Assert.Single(session.Turns).Question); Assert.Equal(MatchChatOutcome.Answered, session.Turns[0].Outcome);
    }

    private static readonly string[] Intents = ["selected_view", "explicit_time_reference"];
    private static LeagueChatContext Context(string? club = "everton") => LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: club));
    private static LeagueRoutingInput Input(LeagueChatContext c, string question) => new(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
    private static MatchRoutingResult Select(LeagueChatContext c, string question, string workflow, string binding, string intent) => Select(Input(c, question), workflow, binding, intent);
    private static MatchRoutingResult Select(LeagueRoutingInput input, string workflow, string binding, string intent)
    {
        var response = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, binding, scope: intent)));
        return DecisionLeagueWorkflowRouter.Select(response, new DecisionRoutingOptions(), input);
    }
    private static async Task AssertRefusal(string question, LeagueTimeResolution scope, MatchChatOutcome outcome)
    {
        foreach (var selection in new string?[] { null, "everton" })
        {
            var c = Context(selection); var topic = new LeagueAcceptedTopic("club_context", "arsenal");
            // Domain eligibility comes first so RED captures a real wrong factual answer,
            // not merely a difference in the parser's diagnostic classification.
            foreach (var workflow in new[] { "league_context", "club_context", "named_club_context", "ground_context", "named_ground_context", "date_changes" })
            {
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, c, question, "arsenal", default);
                Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
            }
            var continuationReads = 0; var continuation = await LeagueChatFixtures.Executor((_, _, _) => continuationReads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(outcome, continuation.Outcome); Assert.Empty(continuation.Evidence); Assert.Null(continuation.ResolvedClubId); Assert.Equal(0, continuationReads);
            foreach (var intent in Intents)
            {
                Assert.Equal(scope, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
                foreach (var workflow in new[] { "league_context", "club_context", "ground_context", "continue_topic" })
                {
                    var route = Select(Input(c, question) with { AcceptedTopic = topic }, workflow, "context", intent);
                    Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal(scope == LeagueTimeResolution.RealWorldNow ? "live_unavailable" : "other_season", route.WorkflowId);
                }
            }
        }
    }
}
