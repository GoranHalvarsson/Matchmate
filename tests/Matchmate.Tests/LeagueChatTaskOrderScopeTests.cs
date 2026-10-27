using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatTaskOrderScopeTests
{
    [Theory]
    [InlineData("Now, break down our record here.")]
    [InlineData("Now please break down our points on 20 September 2026.")]
    [InlineData("Now, could you please break down our results in this selected view?")]
    [InlineData("Now, break down the wins, draws and losses behind our selected team's record here.")]
    public async Task LeadingTaskOrderNowKeepsPhrasalRequestsInsideTheSelectedSnapshot(string question)
    {
        var snapshot = LeagueChatFixtures.Snapshot(); var context = LeagueChatContext.From(snapshot); var reads = 0;
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        Assert.Equal(2, reads); // Existing current/previous played-date factual preparation.
    }

    [Theory]
    [InlineData("Now, break down our record right now.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, break down the live table here.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, break down our record outside this snapshot.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Can you now break down our record here?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now break our record into pieces here.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, break down our points on the next day.", LeagueTimeResolution.DifferentTime)]
    [InlineData("Now, break down our record this week.", LeagueTimeResolution.Unresolved)]
    [InlineData("Now, break down our record on 19 September 2026.", LeagueTimeResolution.DifferentTime)]
    public async Task TaskOrderViewRetainsEveryLaterOrNonleadingTimeCue(string question, LeagueTimeResolution expected)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); var reads = 0;
        Assert.Equal(expected, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        Assert.Equal(expected == LeagueTimeResolution.RealWorldNow ? MatchChatOutcome.Unavailable : MatchChatOutcome.Clarify, result.Outcome);
        Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
    }

    [Fact]
    public async Task LeadingTaskOrderAssessmentNeverReplacesTheTypedModelQuestion()
    {
        const string question = "Now, break down our record here.";
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        await session.AskAsync(context, question);
        Assert.Equal(question, Assert.Single(router.Inputs).Question); Assert.Equal(question, Assert.Single(session.Turns).Question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome); Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData("Before the first results arrive, what points total and league placing does our club have?")]
    [InlineData("Before the first result arrives, what points total does our club have?")]
    [InlineData("Before the first played days arrive, what points total does our club have?")]
    [InlineData("Before the first dates arrive, what points total does our club have?")]
    [InlineData("Before the first matches arrive, what points total does our club have?")]
    [InlineData("Before any matches arrive, what points total does our club have?")]
    public async Task PreseasonNumberFormsCannotBorrowFactsFromAPositivePlayedDate(string question)
    {
        var initial = LeagueChatContext.From(LeagueChatFixtures.Snapshot(0, "coventry"));
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(initial.SeasonKey, initial.Date, question));
        var answer = await LeagueChatFixtures.Executor().ExecuteAsync("club_context", initial, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, answer.Outcome); Assert.Equal("coventry", answer.ResolvedClubId);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, answer.Evidence.Select(item => item.Id));
        var played = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: "coventry")); var reads = 0;
        Assert.Equal(LeagueTimeResolution.DifferentTime, LeagueTimeScope.Resolve(played.SeasonKey, played.Date, question));
        var refusal = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", played, question, null, default);
        Assert.Equal(MatchChatOutcome.Clarify, refusal.Outcome); Assert.Null(refusal.ResolvedClubId); Assert.Empty(refusal.Evidence); Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("What are our points on the date currently chosen?")]
    [InlineData("Explain our record for the currently selected day.")]
    [InlineData("Show our points on the day we have currently selected.")]
    [InlineData("What are our points as of the date currently shown?")]
    public async Task R2CurrentlyInsideACompleteUiDateReferencePreservesSelectedFactsAndPayload(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(LeagueTimeAssessment.MatchesSelection, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model); session.SetContext(context);
        await session.AskAsync(context, question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome); Assert.Equal("everton", Assert.Single(session.Turns).ResolvedClubId);
        Assert.Equal(question, Assert.Single(router.Inputs).Question); Assert.Equal(question, Assert.Single(session.Turns).Question);
        Assert.Equal(question, Assert.Single(Assert.Single(model.Requests).Messages.Where(message => message.Role == "user")).Content);
    }

    [Theory]
    [InlineData("The snapshot is open, but who leads the Premier League at this real-world moment?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Show our points as of this real-world instant.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Show our points at the real world moment.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("What are our points on the date currently chosen right now?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("What are our points on the date currently chosen outside the replay?", LeagueTimeResolution.Unresolved)]
    [InlineData("What are our points on the currently selected date next week?", LeagueTimeResolution.DifferentTime)]
    [InlineData("What are our points on the day we have currently selected in March?", LeagueTimeResolution.Unresolved)]
    [InlineData("What are our points on the date currently?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("What are our points on the date currently Barcelona chosen?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, explain our points for the date currently chosen outside this view.", LeagueTimeResolution.RealWorldNow)]
    [InlineData("What are our points on the date currently chosen and currently live?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("What are our points on 19 September 2026 instead of the date currently chosen?", LeagueTimeResolution.DifferentTime)]
    public async Task R2UiSelectionProjectionPreservesEveryIndependentTimeCue(string question, LeagueTimeResolution expected)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); var reads = 0;
        Assert.Equal(expected, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        Assert.Equal(expected == LeagueTimeResolution.RealWorldNow ? MatchChatOutcome.Unavailable : MatchChatOutcome.Clarify, result.Outcome);
        Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
    }

    [Fact]
    public void R2TheExactDifferentDateCaseRetainsItsCalendarAndPlainCurrentDateIsNotProjected()
    {
        Assert.Equal(LeagueTimeResolution.DifferentTime, LeagueTimeScope.Resolve("2016-17", "2016-12-31", "What was the league table on 14 August 2016 instead of the date currently chosen?", true));
        Assert.Equal(LeagueTimeAssessment.NoReference, LeagueTimeScope.Assess("2026-27", "2026-09-20", "What are our points on the current date?"));
    }

    [Theory]
    [InlineData("Before any fixtures have been counted, what points and match record does our club start with?")]
    [InlineData("Before any fixture is counted, what points total does our club have?")]
    [InlineData("Before the first fixtures arrive, what points total does our club have?")]
    public Task R2FixtureNumberFormsCannotBorrowPositiveDateFacts(string question)
        => PreseasonNumberFormsCannotBorrowFactsFromAPositivePlayedDate(question);

    [Theory]
    [InlineData("Now, show me, for Everton in this season, what points have accumulated right now?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, show me, for Everton outside this view, what points have accumulated?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Could you now show me, for Everton, what points have accumulated?", LeagueTimeResolution.RealWorldNow)]
    [InlineData("Now, show me, for 19 September 2026, what points does our club have?", LeagueTimeResolution.DifferentTime)]
    [InlineData("Now, show me, for 2026-13-40, what points does our club have?", LeagueTimeResolution.Unresolved)]
    [InlineData("Now, show me, for March, what points does our club have?", LeagueTimeResolution.Unresolved)]
    public Task R3SharedPreludeProjectionCannotRemoveIndependentTimeCues(string question, LeagueTimeResolution expected)
        => R2UiSelectionProjectionPreservesEveryIndependentTimeCue(question, expected);

    [Fact]
    public void R3OrdinaryRequestWrappersDoNotCreateAnExplicitTimeReference()
    {
        const string question = "Please show me our points.";
        Assert.Equal(LeagueTimeAssessment.NoReference, LeagueTimeScope.Assess("2026-27", "2026-09-20", question));
        Assert.Equal(LeagueTimeResolution.Unresolved, LeagueTimeScope.Resolve("2026-27", "2026-09-20", question, true));
    }
}
