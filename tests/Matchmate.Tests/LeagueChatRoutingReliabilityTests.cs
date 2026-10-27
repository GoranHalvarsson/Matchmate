using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Core;
using Matchmate.LeagueChatEvaluation;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatRoutingReliabilityTests
{
    [Theory]
    [InlineData("club_context", "everton", 2, "How are we doing here?", "everton")]
    [InlineData("named_club_context", "everton", 2, "What points did Arsenal add on this date?", "arsenal")]
    [InlineData("club_context", "everton", 0, "What happened here?", "everton")]
    [InlineData("league_context", "everton", 2, "Give me the whole league picture.", null)]
    [InlineData("league_context", null, 2, "What happened here?", null)]
    public async Task BroadEvidenceComputesAllRequestedFactsWithCutoffAndReadBudgets(string workflow, string? selection, int index, string question, string? subject)
    {
        var reads = 0; var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(index, selection));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, context, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.InRange(reads, 1, 3);
        Assert.Equal(subject is null ? new[] { "table_snapshot", "date_change", "league_coverage" } : new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
        using var day = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text);
        var facts = day.RootElement.GetProperty("facts"); Assert.Equal(subject is null ? 20 : 1, facts.GetProperty("rows").GetArrayLength());
        foreach (var match in facts.GetProperty("resultsOnSelectedDate").EnumerateArray()) Assert.Equal(context.Date, match.GetProperty("Date").GetString());
        if (subject is not null)
        {
            using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
            var expected = LeagueChatFixtures.Snapshot(index, subject).SelectedClub!.Row;
            Assert.Equal(expected.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
            Assert.Equal(expected.Played, standing.RootElement.GetProperty("facts").GetProperty("Played").GetInt32());
        }
    }
    [Theory]
    [InlineData("How are barcelona doing here?")]
    [InlineData("What points did barcelona add on this date?")]
    [InlineData("What points did our barcelona team add here?")]
    [InlineData("What points did their barcelona team add here?")]
    [InlineData("Tell me about their barcelona ground.")]
    [InlineData("How are Arsenal and barcelona doing?")]
    [InlineData("What changed for Manchester on this date?")]
    [InlineData("Where is barcelona\u2019s ground?")]
    [InlineData("Where is Manchester\u2019s stadium?")]
    public async Task ErroneousContextBindingCannotAcquireUiOrPriorFactsForExplicitUnknownSubject(string question)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Single(Enumerable.Range(0, reads));
    }
    [Theory]
    [InlineData("Tell me about our stadium reference.")]
    [InlineData("Tell me our home town and ground.")]
    [InlineData("Where is our town?")]
    [InlineData("Tell me about our New Ground.")]
    [InlineData("Where is our Home Town?")]
    [InlineData("Tell me about our form.")]
    [InlineData("Tell me about our recent run of results.")]
    [InlineData("Explain the changes to our position.")]
    [InlineData("Is our ground in London?")]
    [InlineData("How have we been doing?")]
    [InlineData("How are our recent results doing?")]
    public async Task OrdinaryReferenceGrammarNeverBecomesAnUnrelatedClub(string question)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId);
    }
    [Theory]
    [InlineData("How are our Man City team doing?", "manchester-city")]
    [InlineData("Tell me about their Man Utd ground.", "manchester-united")]
    [InlineData("Where is Arsenal’s ground?", "arsenal")]
    public async Task KnownNamesOverrideContextEvenBesidePronouns(string question, string subject)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, "brentford", default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId);
    }
    [Theory]
    [InlineData("Does this reference include live results?")]
    [InlineData("Can this source show last season or another date?")]
    [InlineData("Why are today's scores missing from this source?")]
    public async Task CoverageMentionsDoNotBecomeRequestsForOutOfScopeFacts(string question)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("coverage_limits", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id);
    }
    [Theory]
    [InlineData("What changed on 20 Sept 2026?", false)]
    [InlineData("What changed on 20th September?", false)]
    [InlineData("What changed on September 20th 2026?", false)]
    [InlineData("What changed on 18 Sept 2026?", true)]
    [InlineData("What changed on 18th September?", true)]
    [InlineData("What changed on September 20th 2025?", true)]
    public async Task ErroneousSelectedScopeCannotBypassCalendarIdentity(string question, bool rejected)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(rejected ? MatchChatOutcome.Clarify : MatchChatOutcome.Answered, result.Outcome);
        if (rejected) Assert.Empty(result.Evidence);
    }
    [Theory]
    [InlineData("Where do we currently stand?", null, null, MatchChatOutcome.Unavailable, 0)]
    [InlineData("How are we currently doing?", null, null, MatchChatOutcome.Unavailable, 0)]
    [InlineData("What points have we already earned?", null, "everton", MatchChatOutcome.Answered, 2)]
    [InlineData("How have they already been performing?", "arsenal", "arsenal", MatchChatOutcome.Answered, 3)]
    [InlineData("Where do they really currently stand?", "arsenal", null, MatchChatOutcome.Unavailable, 0)]
    [InlineData("How has Arsenal recently been performing?", null, "arsenal", MatchChatOutcome.Answered, 3)]
    [InlineData("What points has Arsenal already earned?", null, "arsenal", MatchChatOutcome.Answered, 3)]
    public async Task PredicateAdverbsPreserveExactSelectedNamedAndReferenceSubjects(string question, string? reference, string? subject, MatchChatOutcome outcome, int expectedReads)
    {
        var reads = 0; var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", c, question, reference, default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
        if (outcome == MatchChatOutcome.Unavailable) { Assert.Empty(result.Evidence); return; }
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
        using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
        Assert.Equal(LeagueChatFixtures.Snapshot(2, subject).SelectedClub!.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
    }
    [Theory]
    [InlineData("How is barcelona currently doing?", MatchChatOutcome.Unavailable, 0)]
    [InlineData("Where does Manchester currently stand?", MatchChatOutcome.Unavailable, 0)]
    [InlineData("What points has barcelona already earned?", MatchChatOutcome.Clarify, 1)]
    [InlineData("How has our barcelona team recently been performing?", MatchChatOutcome.Clarify, 1)]
    [InlineData("How have Arsenal and barcelona recently been doing?", MatchChatOutcome.Clarify, 1)]
    public async Task AdverbGrammarCannotGiveUnknownOrMixedNamesContextAuthority(string question, MatchChatOutcome outcome, int expectedReads)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)), question, "arsenal", default);
        Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(expectedReads, reads);
    }
    [Theory]
    [InlineData("2016-17", "2016-09-18", "Describe our club results at this older date.", false, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2016-17", "2016-09-18", "Describe our club results at this older date.", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2016-17", "2016-09-18", "Describe our results on the selected historical played day.", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2016-17", "2016-09-18", "At this older date show our results on 19 September 2016.", true, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "What changed on 20th September 2026?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", "2026-09-20", "What changed on September 20th?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2015-16", "2016-01-05", "What changed on 5 January?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2015-16", "2016-01-05", "What changed on January 5th 2016?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2015-16", "2016-01-05", "How are we doing in 2015/16?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("1999-00", "2000-01-05", "How are we doing in 1999/2000?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", "2026-09-20", "What changed on 1999-09-20?", false, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "What changed on 20 September 1999?", false, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "How were we doing in 1999?", false, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "Show next year please.", false, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "Show another day please.", false, LeagueTimeResolution.DifferentTime)]
    [InlineData("2026-27", "2026-09-20", "Show our scores right now.", false, LeagueTimeResolution.RealWorldNow)]
    [InlineData("2026-27", "2026-09-20", "Show us on Monday.", false, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "Show us on 32 September 2026.", false, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "Show us on Smarch 18.", false, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "01/01/2026 table please", false, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "1/1/2026 table please", true, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "01-01-2026 table please", false, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "01/01 table please", true, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "Show us at an unnamed time.", true, LeagueTimeResolution.Unresolved)]
    [InlineData("2026-27", "2026-09-20", "Did we score 2026 goals?", false, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", "2026-09-20", "Have we won 3 matches here?", false, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", "2026-09-20", "Have we won 3-0 here?", false, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", null, "Before the first played day, how do we stand?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", null, "What is included before any results?", true, LeagueTimeResolution.MatchesSelection)]
    [InlineData("2026-27", "2026-09-20", "Before the first played day, how do we stand?", true, LeagueTimeResolution.DifferentTime)]
    public void SharedTimeGuardSeparatesCalendarEqualityAvailabilityAndUnresolvedSyntax(string season, string? date, string question, bool explicitReference, LeagueTimeResolution expected)
        => Assert.Equal(expected, LeagueTimeScope.Resolve(season, date, question, explicitReference));
    [Theory]
    [InlineData("What changed on 1999-09-20?", MatchChatOutcome.Clarify)]
    [InlineData("What changed on 20 September 1999?", MatchChatOutcome.Clarify)]
    [InlineData("Show next year please.", MatchChatOutcome.Clarify)]
    [InlineData("Show another date please.", MatchChatOutcome.Clarify)]
    [InlineData("01/01/2026 table please", MatchChatOutcome.Clarify)]
    [InlineData("Show us on Smarch 18.", MatchChatOutcome.Clarify)]
    [InlineData("Show our scores today.", MatchChatOutcome.Unavailable)]
    public async Task WrongSelectedWorkflowNeverGetsSubstituteFactsForExplicitTime(string question, MatchChatOutcome expected)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(expected, result.Outcome); Assert.Empty(result.Evidence); Assert.Null(result.ResolvedClubId); Assert.Equal(0, reads);
    }
    [Theory]
    [InlineData("in January")] [InlineData("in February")] [InlineData("in March")]
    [InlineData("in April")] [InlineData("in May")] [InlineData("in June")]
    [InlineData("in July")] [InlineData("in August")] [InlineData("on September")]
    [InlineData("during October")] [InlineData("since November")] [InlineData("by December")]
    [InlineData("in Jan")] [InlineData("in Feb")] [InlineData("in Mar")]
    [InlineData("in Apr")] [InlineData("in Jun")] [InlineData("in Jul")]
    [InlineData("in Aug")] [InlineData("on Sep")] [InlineData("as of Sept")]
    [InlineData("in Oct")] [InlineData("in Nov")] [InlineData("in Dec")]
    [InlineData("on 20 September 2026 and in March")]
    public async Task BareKnownMonthNeverAcquiresSelectedDayFactsUnderEitherBinaryIntent(string reference)
    {
        var question = "How were we doing " + reference + "?";
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            Assert.Equal(LeagueTimeResolution.Unresolved, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question, intent == "explicit_time_reference"));
            var input = new LeagueRoutingInput(question, new(context.SeasonKey, context.DateIndex, context.Date, context.SelectedClubId, context.SelectedClubName), []);
            var choice = DecisionLeagueWorkflowRouter.ParseChoices(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(scope: intent)));
            var route = DecisionLeagueWorkflowRouter.Select(choice, new DecisionRoutingOptions(), input);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal("other_season", route.WorkflowId);
            // The executor must also refuse a wrong factual route and legacy source routes,
            // rather than rely solely on the classifier's scope or mapped feedback route.
            foreach (var workflow in new[] { route.WorkflowId!, "club_context", "club_standing", "ground_context" })
            {
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, context, question, "arsenal", default);
                Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
            }
        }
    }
    [Theory]
    [InlineData("Show March form here.")]
    [InlineData("March results at this snapshot.")]
    [InlineData("Show May form here.")]
    [InlineData("September results at this selected date.")]
    [InlineData("Show Sept results here.")]
    [InlineData("May we review March form here?")]
    [InlineData("May we review May form here?")]
    [InlineData("Show March form on 20 September 2026.")]
    [InlineData("Show March form in 2026/27 here.")]
    public async Task ExplicitResidualMonthCannotBorrowSelectionOrMatchingDateAuthority(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(LeagueTimeResolution.Unresolved, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question, explicitReference: true));
        foreach (var topic in new[] { "club_context", "ground_context", "league_context" })
        {
            var input = new LeagueRoutingInput(question, new(context.SeasonKey, context.DateIndex, context.Date, context.SelectedClubId, context.SelectedClubName), []);
            var choice = DecisionLeagueWorkflowRouter.ParseChoices(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(topic, scope: "explicit_time_reference")));
            var route = DecisionLeagueWorkflowRouter.Select(choice, new DecisionRoutingOptions(), input);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal("other_season", route.WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, context, question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Theory]
    [InlineData("May we see the table?", false)]
    [InlineData("May we review our points here?", true)]
    [InlineData("May we review our points on 20 September 2026?", false)]
    [InlineData("May we review our points on 20 September 2026?", true)]
    [InlineData("May I review our points on 2026-09-20?", true)]
    [InlineData("May they review our points on September 20th 2026?", true)]
    public async Task ObviousModalMayDoesNotCreateAResidualCalendarMonth(string question, bool explicitReference)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question, explicitReference));
        var input = new LeagueRoutingInput(question, new(context.SeasonKey, context.DateIndex, context.Date, context.SelectedClubId, context.SelectedClubName), []);
        var choice = DecisionLeagueWorkflowRouter.ParseChoices(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(scope: explicitReference ? "explicit_time_reference" : "selected_view")));
        var route = DecisionLeagueWorkflowRouter.Select(choice, new DecisionRoutingOptions(), input); Assert.Equal("club_context", route.WorkflowId);
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, context, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId); Assert.Equal(2, reads);
    }
    [Theory]
    [InlineData("What changed on 2026-09-20?")]
    [InlineData("What changed on 20 September 2026?")]
    [InlineData("What changed on September 20th 2026?")]
    [InlineData("What changed on 20th Sept?")]
    public async Task CompleteMatchingCalendarDateIsConsumedBeforeResidualMonthGuard(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question, intent == "explicit_time_reference"));
            var input = new LeagueRoutingInput(question, new(context.SeasonKey, context.DateIndex, context.Date, context.SelectedClubId, context.SelectedClubName), []);
            var choice = DecisionLeagueWorkflowRouter.ParseChoices(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(scope: intent)));
            var route = DecisionLeagueWorkflowRouter.Select(choice, new DecisionRoutingOptions(), input);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal("club_context", route.WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, context, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId); Assert.Equal(2, reads);
            using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
            Assert.Equal(LeagueChatFixtures.Snapshot().SelectedClub!.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
        }
    }
    [Theory]
    [InlineData("May we see the table?", "league_context")]
    [InlineData("Did we score 2026 goals?", "club_context")]
    [InlineData("Have we won 3 matches here?", "club_context")]
    public async Task ModalMayAndFootballQuantitiesRemainSelectedViewQuestions(string question, string workflow)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync(workflow, context, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.NotEmpty(result.Evidence);
        Assert.Equal(workflow == "league_context" ? null : "everton", result.ResolvedClubId);
    }
    [Theory]
    [InlineData("coverage_limits", MatchChatOutcome.Answered, 1)]
    [InlineData("unsupported", MatchChatOutcome.Unsupported, 0)]
    [InlineData("clarify", MatchChatOutcome.Clarify, 0)]
    public async Task BareMonthDoesNotOverrideMetadataOrExplicitNonFactualWorkflow(string workflow, MatchChatOutcome outcome, int expectedReads)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); var question = "Can this source show scores in March?";
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            var input = new LeagueRoutingInput(question, new(context.SeasonKey, context.DateIndex, context.Date, context.SelectedClubId, context.SelectedClubName), []);
            var choice = DecisionLeagueWorkflowRouter.ParseChoices(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, scope: intent)));
            var route = DecisionLeagueWorkflowRouter.Select(choice, new DecisionRoutingOptions(), input); Assert.Equal(workflow, route.WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, context, question, null, default);
            Assert.Equal(outcome, result.Outcome); Assert.Equal(expectedReads, reads); Assert.Null(result.ResolvedClubId);
            if (expectedReads == 0) Assert.Empty(result.Evidence); else Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id);
        }
    }
    [Theory]
    [InlineData("unsupported", "Predict next season's champion using live scores.", MatchChatOutcome.Unsupported)]
    [InlineData("clarify", "Compare something unspecified today.", MatchChatOutcome.Clarify)]
    public async Task ExplicitNonFactualWorkflowKeepsItsOutcomeDespiteTimeWording(string workflow, string question, MatchChatOutcome expected)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(expected, result.Outcome); Assert.Empty(result.Evidence); Assert.Null(result.ResolvedClubId); Assert.Equal(0, reads);
    }
    [Fact]
    public async Task ChangedIntentAndTemporalRepliesCannotRecoverPendingGroundQuestion()
    {
        foreach (var question in new[] { "How are Arsenal doing?", "Arsenal points", "Tomorrow", "Tell me more about that ground" })
        {
            var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(router: router, model: model);
            var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: null)); session.SetContext(c); await session.AskAsync(c, "Tell me about their ground?");
            router.Workflow = question == "Tell me more about that ground" ? "continue_topic" : question == "Tomorrow" ? "club_context" : "named_club_context";
            await session.AskAsync(c, question); Assert.Null(router.Inputs[^1].Clarification); Assert.Null(session.Turns[^1].OriginalQuestion);
            if (question is "Tomorrow" or "Tell me more about that ground") { Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Empty(session.Turns[^1].Evidence); }
            else
            {
                Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
                var prepared = model.Requests[^1].Messages.Single(m => m.Content.StartsWith("Prepared evidence for this turn only:", StringComparison.Ordinal)).Content;
                using var data = JsonDocument.Parse(prepared[(prepared.IndexOf('\n') + 1)..]); Assert.Equal(4, data.RootElement.GetArrayLength());
                Assert.DoesNotContain(data.RootElement.EnumerateArray(), e => e.GetProperty("Id").GetString() == "ground_reference");
                Assert.Equal("club_standing", Assert.Single(session.Turns[^1].Evidence).Id);
            }
        }
    }
    [Fact]
    public async Task BriefKnownReplyKeepsOriginalTimeScopeButNewFullQuestionCanChangeIntent()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: null)); session.SetContext(c);
        var original = "Tell me about their ground on 18 September 2026?"; await session.AskAsync(c, original);
        router.Workflow = "named_ground_context"; await session.AskAsync(c, "arsenal");
        Assert.Equal(original, router.Inputs[^1].Clarification?.OriginalQuestion); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Empty(session.Turns[^1].Evidence);
        await session.AskAsync(c, "Tell me about Arsenal's ground."); Assert.Null(router.Inputs[^1].Clarification);
        Assert.Equal(MatchChatOutcome.Answered, session.Turns[^1].Outcome); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
    }
    [Fact]
    public async Task ElaboratingGroundReferenceUsesAcceptedNamedSubjectAndFreshFacts()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "named_ground_context" }; var reads = 0; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(c); await session.AskAsync(c, "Tell me about Arsenal's ground.");
        reads = 0; router.Workflow = "continue_topic"; await session.AskAsync(c, "Please expand that ground reference.");
        Assert.Equal(MatchChatOutcome.Answered, session.Turns[^1].Outcome); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        Assert.Equal(new LeagueAcceptedTopic("ground_context", "arsenal"), router.Inputs[^1].AcceptedTopic); Assert.Equal(2, reads);
        Assert.Equal("ground_reference", Assert.Single(session.Turns[^1].Evidence).Id);
        var prepared = model.Requests[^1].Messages.Single(m => m.Content.StartsWith("Prepared evidence for this turn only:", StringComparison.Ordinal)).Content;
        using var data = JsonDocument.Parse(prepared[(prepared.IndexOf('\n') + 1)..]);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, data.RootElement.EnumerateArray().Select(e => e.GetProperty("Id").GetString()));
    }
    [Fact]
    public void EveryCanonicalCurrentClubNameAndCuratedAliasIsOneExactBriefIdentity()
    {
        var clubs = LeagueChatFixtures.Snapshot().Clubs;
        foreach (var club in clubs)
        {
            Assert.True(LeagueQuestionProjection.BriefIdentityReply(club.Name));
            Assert.Equal(club.Id, LeagueClubResolver.ResolveBrief(clubs, club.Name).ClubId);
        }
        Assert.Equal("tottenham", LeagueClubResolver.ResolveBrief(clubs, "spurs").ClubId);
        Assert.Equal("brighton", LeagueClubResolver.ResolveBrief(clubs, "Brighton & Hove Albion").ClubId);
    }
    [Theory]
    [InlineData("Brighton & Hove Albion", "brighton")]
    [InlineData("Spurs", "tottenham")]
    [InlineData("Arsenal & barcelona", null)]
    [InlineData("Arsenal mystery", null)]
    [InlineData("barcelona & Real Madrid", null)]
    public async Task PendingGroundReplyRequiresExactWholeTrustedIdentity(string reply, string? subject)
    {
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: null)); session.SetContext(c); await session.AskAsync(c, "Tell me about their ground?");
        router.Workflow = "named_ground_context"; await session.AskAsync(c, reply);
        Assert.NotNull(router.Inputs[^1].Clarification); Assert.Equal(subject, session.Turns[^1].ResolvedClubId);
        Assert.Equal(subject is null ? MatchChatOutcome.Clarify : MatchChatOutcome.Answered, session.Turns[^1].Outcome);
        if (subject is null) Assert.Empty(session.Turns[^1].Evidence);
    }
    [Theory]
    [InlineData("Tell me about Arsenal and barcelona.")]
    [InlineData("Tell me about Arsenal & barcelona.")]
    [InlineData("Tell me about barcelona and Arsenal.")]
    [InlineData("Tell me about barcelona & Arsenal.")]
    [InlineData("Tell me about Brighton & Hove Albion and barcelona.")]
    public async Task MaskedIdentityCoordinationCannotBorrowOnlyOneClubsFacts(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
        }
    }
    [Theory]
    [InlineData("Tell me about Brighton & Hove Albion.", "brighton")]
    [InlineData("Tell me about Arsenal and their form.", "arsenal")]
    public async Task TrustedLongNameAndStructuralPronounDoNotBecomeMixedIdentities(string question, string subject)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("named_club_context", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId);
    }
    [Fact]
    public async Task BinaryIntentDiagnosticsNeverRewriteOriginalSemanticGold()
    {
        var item = Case() with { Question = "How are we doing in 2026/27?" };
        var diagnostic = EvaluationDecisionParser.Parse(System.Text.Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(scope: "explicit_time_reference")))!;
        var result = await Runner().ExecuteAsync(item, new(MatchRoutingStatus.Selected, "club_context"), diagnostic);
        Assert.Equal("selected_snapshot", item.Expected.TimeScope); Assert.Equal("selected_snapshot", result.ResolvedTimeScope); Assert.True(result.TimeScopeCorrect);
        Assert.Equal("explicit_time_reference", diagnostic.TimeIntent.Choice); Assert.Null(result.TimeIntentCorrect); Assert.True(result.Usable);
        var optional = item with { Expected = item.Expected with { TimeIntent = "selected_view" } };
        Assert.False((await Runner().ExecuteAsync(optional, new(MatchRoutingStatus.Selected, "club_context"), diagnostic)).TimeIntentCorrect);
    }
    [Fact]
    public async Task BroadTopicContinuityKeepsFreshNamedSubjectAndLegacySourceCanReplaceIt()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_context" }; var reads = 0;
        using var session = LeagueChatFixtures.Session(router: router, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(c);
        await session.AskAsync(c, "How are Arsenal doing here?"); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        router.Workflow = "continue_topic"; reads = 0; await session.AskAsync(c, "Tell me more.");
        Assert.Equal(MatchChatOutcome.Answered, session.Turns[^1].Outcome); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId); Assert.Equal(3, reads);
        await session.AskSourceAsync(c, "ground"); await session.AskAsync(c, "Go on.");
        Assert.Equal("everton", session.Turns[^1].ResolvedClubId); Assert.Contains(session.Turns[^1].Evidence, e => e.Id == "ground_reference");
    }
    [Fact]
    public async Task UnknownNewIntentCannotReviveBroadNamedTopicOrFallBackToSelection()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(c); await session.AskAsync(c, "How are Arsenal doing?");
        router.Workflow = "club_context"; await session.AskAsync(c, "How are barcelona doing?"); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
        await session.AskAsync(c, "How are they doing?"); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
        router.Workflow = "continue_topic"; await session.AskAsync(c, "Tell me more."); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
        await session.AskSourceAsync(c, "standing"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task RealBroadCityContextAt14SeptemberIncludesNoMatchAndOnlyThatDaysResults()
    {
        var model = new LeagueModel(LeagueReferenceCodec.Decode(File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"))));
        var c = LeagueChatContext.From(model.Snapshot("2026-27", 14, "manchester-city")); Assert.Equal("2026-09-14", c.Date); var reads = 0;
        var executor = new LeagueWorkflowExecutor((season, index, club, _) => { reads++; return Task.FromResult(model.Snapshot(season, index, club)); });
        var result = await executor.ExecuteAsync("club_context", c, "What happened here?", null, default);
        Assert.Equal(2, reads); Assert.Equal(4, result.Evidence.Length);
        using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text); var facts = data.RootElement.GetProperty("facts");
        var row = Assert.Single(facts.GetProperty("rows").EnumerateArray()); Assert.False(row.GetProperty("playedOnDate").GetBoolean());
        Assert.Equal(12, row.GetProperty("points").GetInt32()); Assert.Equal(0, row.GetProperty("pointsChange").GetInt32());
        Assert.Equal(2, row.GetProperty("position").GetInt32()); Assert.Equal(2, row.GetProperty("previousPosition").GetInt32());
        var game = Assert.Single(facts.GetProperty("resultsOnSelectedDate").EnumerateArray());
        Assert.Equal("Leeds United", game.GetProperty("home").GetString()); Assert.Equal("Newcastle United", game.GetProperty("away").GetString());
        Assert.Equal(4, game.GetProperty("HomeGoals").GetInt32()); Assert.Equal(1, game.GetProperty("AwayGoals").GetInt32());
    }
    private static ReliabilityCase Case(string workflow = "club_context", string outcome = "Answered", string? subject = "everton", string[]? evidence = null)
        => new("unit", "How is our club doing?", new("2026-27", 2, LeagueChatFixtures.Snapshot(2).Date, "everton", "Everton"),
            new(workflow, LeagueWorkflowRegistry.Family(workflow), outcome, subject, evidence ?? ["club_standing", "club_results", "date_change", "league_coverage"], "context", "selected_snapshot"));
    private static ReliabilityRunner Runner() => new(LeagueReferenceCodec.CurrentVersion, (season, index, club) => LeagueChatFixtures.Snapshot(index, club, season == "2015-16"));
    [Fact]
    public async Task EndToEndScoringRequiresPlanOutcomeSubjectEvidenceNotGenericAbstention()
    {
        var runner = Runner(); var item = Case(); var good = await runner.ExecuteAsync(item, new(MatchRoutingStatus.Selected, "club_context")); Assert.True(good.Usable);
        var abstained = await runner.ExecuteAsync(item, new(MatchRoutingStatus.Clarify)); Assert.False(abstained.Usable);
        var missing = Case("continue_topic", "Clarify", null, []); var noTopic = await runner.ExecuteAsync(missing, new(MatchRoutingStatus.Selected, "continue_topic")); Assert.True(noTopic.Usable);
        Assert.False((await runner.ExecuteAsync(missing, new(MatchRoutingStatus.Clarify))).Usable);
        var wrongSubject = item with { Expected = item.Expected with { SubjectId = "arsenal" } };
        Assert.True((await runner.ExecuteAsync(wrongSubject, new(MatchRoutingStatus.Selected, "club_context"))).UnsafeSubject);
        var wrongTime = item with { Expected = item.Expected with { TimeScope = "different_time" } };
        Assert.True((await runner.ExecuteAsync(wrongTime, new(MatchRoutingStatus.Selected, "club_context"))).UnsafeTime);
        var wrongEvidence = item with { Expected = item.Expected with { EvidenceIds = ["ground_reference"] } };
        Assert.False((await runner.ExecuteAsync(wrongEvidence, new(MatchRoutingStatus.Selected, "club_context"))).Usable);
    }
    [Fact]
    public async Task UnexpectedMetadataAnswerForUnknownClubIsUnsafeEvenWhenBothSubjectsAreNull()
    {
        var item = Case("named_club_context", "Clarify", null, []) with { Question = "How are barcelona doing?" };
        var result = await Runner().ExecuteAsync(item, new(MatchRoutingStatus.Selected, "coverage_limits"));
        Assert.Equal(MatchChatOutcome.Answered, result.Execution!.Outcome); Assert.Null(result.Execution.ResolvedClubId);
        Assert.False(result.UnsafeSubject); Assert.False(result.UnsafeTime); Assert.True(result.UnsafeUnexpectedAnswer); Assert.False(result.Usable);
        var score = new ReliabilityScore(1); score.Register(item, result); Assert.Equal(1, score.UnsafeAnswers); Assert.False(score.MeetsTarget);
    }
    [Fact]
    public async Task ReliabilityScoreKeepsSemanticFailureAndProviderFailureDistinctAndStopsAfterThree()
    {
        var item = Case(); var runner = Runner(); var score = new ReliabilityScore(60);
        score.Register(item, await runner.ExecuteAsync(item, new(MatchRoutingStatus.Clarify))); Assert.Equal(0, score.ProviderFailures);
        for (var i = 0; i < 3; i++) score.Register(item, await runner.ExecuteAsync(item, new(MatchRoutingStatus.Unavailable)));
        Assert.True(score.Stopped); Assert.Equal(3, score.ProviderFailures); Assert.False(score.MeetsTarget);
    }
    [Theory]
    [InlineData(1, 40, false)]
    [InlineData(39, 40, false)]
    [InlineData(59, 40, false)]
    [InlineData(60, 39, false)]
    [InlineData(60, 40, true)]
    [InlineData(60, 60, true)]
    public async Task FreshAcceptanceRequiresAllSixtyAndFortyActuallyExecutedSupportedCases(int limit, int supported, bool pass)
    {
        var options = EvaluationOptions.Parse(["--limit", limit.ToString()], HostingTestPaths.Root());
        var score = new ReliabilityScore(60, options.Limit); var runner = Runner();
        var answered = Case(); var answer = await runner.ExecuteAsync(answered, new(MatchRoutingStatus.Selected, "club_context"));
        var clarification = Case("clarify", "Clarify", null, []); var clarify = await runner.ExecuteAsync(clarification, new(MatchRoutingStatus.Selected, "clarify"));
        for (var i = 0; i < limit; i++) score.Register(i < supported ? answered : clarification, i < supported ? answer : clarify);
        Assert.True(score.ExecutedRequested); Assert.Equal(limit, score.Total); Assert.Equal(Math.Min(limit, supported), score.Supported);
        Assert.Equal(limit == 60, score.Completed); Assert.Equal(limit == 60 && supported >= 40, score.FreshGateComposition); Assert.Equal(pass, score.MeetsTarget);
        Assert.False(score.CalibrationQualityMet); Assert.Equal(limit < 60 ? "partial_diagnostic" : "full_fresh_gate_candidate", score.ResultKind);
        Assert.True(score.SampleQualityMet); // A perfect subset is still never acceptance.
    }
    [Theory]
    [InlineData(1, false)] [InlineData(48, true)]
    public async Task CompleteTrainingCanReportCalibrationQualityButNeverFreshAcceptance(int limit, bool calibration)
    {
        var item = Case(); var answer = await Runner().ExecuteAsync(item, new(MatchRoutingStatus.Selected, "club_context")); var score = new ReliabilityScore(48, limit);
        for (var i = 0; i < limit; i++) score.Register(item, answer);
        Assert.Equal(calibration, score.CalibrationQualityMet); Assert.Equal(calibration, score.Completed); Assert.False(score.FreshGateComposition); Assert.False(score.MeetsTarget);
        Assert.Equal(calibration ? "complete_calibration" : "partial_diagnostic", score.ResultKind);
    }
    [Theory]
    [InlineData(2, 0, false, true)] // 38/40supported and58/60overall meet both95%boundaries.
    [InlineData(3, 0, false, false)] // Overall95%, supported92.5%.
    [InlineData(0, 4, false, false)] // Supported100%, overallbelow95%.
    [InlineData(0, 0, true, false)]
    public async Task FullGateKeepsBothAccuracyDenominatorsAndZeroUnsafeRequirement(int supportedErrors, int otherErrors, bool unsafeAnswer, bool pass)
    {
        var score = new ReliabilityScore(60); var runner = Runner(); var item = Case(); var good = await runner.ExecuteAsync(item, new(MatchRoutingStatus.Selected, "club_context"));
        var negative = Case("clarify", "Clarify", null, []); var correctNegative = await runner.ExecuteAsync(negative, new(MatchRoutingStatus.Selected, "clarify"));
        for (var i = 0; i < 40; i++) score.Register(item, good with { Usable = i >= supportedErrors, UnsafeUnexpectedAnswer = unsafeAnswer && i == 0 });
        for (var i = 0; i < 20; i++) score.Register(negative, correctNegative with { Usable = i >= otherErrors });
        Assert.True(score.Completed); Assert.True(score.FreshGateComposition); Assert.Equal(pass, score.MeetsTarget);
        Assert.Equal(40 - supportedErrors, score.SupportedUsable); Assert.Equal(60 - supportedErrors - otherErrors, score.Usable);
    }
    [Fact]
    public void StrictFixtureRejectsUnknownPropertiesMissingPlansAndContextMismatch()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReliabilityFixture(2, [Case()]), ReliabilityFixtures.Json);
        Assert.Single(ReliabilityFixtures.Read(bytes).Cases);
        var duplicate = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ReliabilityFixtures.Read(System.Text.Encoding.UTF8.GetBytes(duplicate)));
        foreach (var mutate in new Action<JsonNode>[] { n => n["cases"]![0]!["private"] = "sentinel", n => n["cases"]![0]!["expected"]!.AsObject().Remove("subjectId"), n => n["cases"]![0]!["expected"]!["family"] = "wrong" })
        { var node = JsonNode.Parse(bytes)!; mutate(node); Assert.Throws<InvalidDataException>(() => ReliabilityFixtures.Read(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()))); }
        Assert.Throws<InvalidDataException>(() => Runner().Context(Case() with { Context = Case().Context with { Date = "2026-09-20" } }));
    }
    [Fact]
    public void ReferenceArgumentsAndHashFailBeforeAnyInference()
    {
        var root = Path.Combine(HostingTestPaths.Root(), ".azure", "league-routing-v2-tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--reference-bundle", ".local/league-reference-v2/league-reference.json"], root));
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--reference-bundle", "other.json", "--reference-sha256", new string('a', 64)], root));
        var path = Path.Combine(root, ".local", "league-reference-v2", "league-reference.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "not a football bundle");
        var parsed = EvaluationOptions.Parse(["--reference-bundle", path, "--reference-sha256", new string('a', 64)], root);
        Assert.Throws<InvalidDataException>(() => ReliabilityRunner.ReadReference(parsed.ReferenceBundle!, parsed.ReferenceHash!));
    }
    [Fact]
    public void FrozenTrainingUsesNewStrictPlansAndNoExactNormalizedOldQuestions()
    {
        var root = HostingTestPaths.Root(); var options = EvaluationOptions.Parse([], root); var fixture = ReliabilityFixtures.Read(options.ReadVerifiedBytes()); Assert.Equal(48, fixture.Cases.Length);
        var old = Directory.GetFiles(Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures"), "league-chat-*.json")
            .SelectMany(path => { using var d = JsonDocument.Parse(File.ReadAllBytes(path)); return d.RootElement.GetProperty("cases").EnumerateArray().Select(c => LeagueClubResolver.Normalize(c.GetProperty("question").GetString()!)).ToArray(); }).ToHashSet();
        Assert.All(fixture.Cases, c => Assert.DoesNotContain(LeagueClubResolver.Normalize(c.Question), old));
    }
}
