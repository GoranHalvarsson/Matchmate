using System.Text;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatContextPhraseTests
{
    [Theory]
    [InlineData("Who gained or lost places when this day's results were added?")]
    [InlineData("Who changed places after this selected day's results here?")]
    [InlineData("Show changes from the selected date's results here.")]
    [InlineData("Explain the whole league after the current day's results here.")]
    [InlineData("Show this day's results and this season's table here.")]
    [InlineData("Show the whole league after this played day's results here.")]
    public async Task QualifiedScopeOwnersRemainLeagueReferencesWithoutAClub(string question)
    {
        foreach (var selection in new string?[] { null, "coventry" })
        {
            var c = Context(selection); Assert.False(LeagueClubResolver.RequiresSubject(question));
            await Answer(c, question, "league_context", null, 2);
        }
    }

    [Theory]
    [InlineData("I'm checking 2026-27: what home ground is listed for our selected club?")]
    [InlineData("What home ground is listed for our current club here?")]
    [InlineData("Where is our selected team based here?")]
    [InlineData("Show our selected club's ground here.")]
    [InlineData("What ground is listed for our current selected club here?")]
    [InlineData("Show our home club's ground here.")]
    public async Task CompleteEmbeddedAndPossessiveClubReferencesUseSelectedAuthority(string question)
    {
        await Answer(Context("newcastle"), question, "ground_context", "newcastle", 1);
        Assert.True(LeagueClubResolver.RequiresSubject(question));
    }

    [Theory]
    [InlineData("Where does our selected club stand here?")]
    [InlineData("How has our current selected club performed here?")]
    [InlineData("What have our selected club won here?")]
    [InlineData("How has our home team performed here?")]
    public async Task GeneralSubjectsUseTheSameCompleteClubReferenceGrammar(string question)
    {
        Assert.True(LeagueClubResolver.RequiresSubject(question));
        await Answer(Context(), question, "club_context", "everton", 2);
    }

    [Theory]
    [InlineData("What home ground is listed for our selected club here?")]
    [InlineData("Show our selected club's ground here.")]
    [InlineData("Where does our current selected club stand here?")]
    [InlineData("How has our selected team performed here?")]
    public async Task MissingClubReferenceRefusesBeforeAnyReadEvenUnderWrongGlobalOrDayRoute(string question)
    {
        foreach (var workflow in new[] { "ground_context", "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("What home ground is listed for our selected club here?")]
    [InlineData("Show our selected club's ground here.")]
    [InlineData("Where does our current selected club stand here?")]
    public async Task ExistingClubReferenceCannotAcquireAGlobalBundleThroughWrongWorkflow(string question)
    {
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
    }

    [Theory]
    [InlineData("Tell me about Arsenal and their recent results here.", "named_club_context", "arsenal", 3)]
    [InlineData("Could I get Arsenal's points and win-draw-loss record here?", "named_club_context", "arsenal", 3)]
    [InlineData("Tell me about Arsenal and their form here.", "named_club_context", "arsenal", 3)]
    [InlineData("Tell me about Arsenal and their home ground here.", "named_ground_context", "arsenal", 2)]
    [InlineData("Tell me our home town and ground here.", "ground_context", "everton", 1)]
    [InlineData("Tell me about their Man Utd ground here.", "named_ground_context", "manchester-united", 2)]
    [InlineData("Show the ground for our selected club and their ground here.", "ground_context", "everton", 1)]
    public Task AnaphoricFactFacetsAreNotAdditionalClubIdentities(string question, string workflow, string subject, int reads)
        => Answer(Context(), question, workflow, subject, reads);

    [Theory]
    [InlineData("What ground is listed for our barcelona club here?")]
    [InlineData("What ground is listed for our selected barcelona club here?")]
    [InlineData("Show our barcelona selected club's ground here.")]
    [InlineData("Show our selected club's ground and barcelona's record here.")]
    [InlineData("Show this day's results and barcelona's record here.")]
    [InlineData("Show day's results here.")]
    [InlineData("Show selected's ground here.")]
    [InlineData("Show Arsenal's ground and barcelona's record here.")]
    [InlineData("Show the ground for our selected club and barcelona here.")]
    [InlineData("Show the ground for our selected club and Arsenal and Everton here.")]
    [InlineData("Tell me about Arsenal and their barcelona team here.")]
    [InlineData("Show the ground for our selected club and their barcelona team here.")]
    [InlineData("Tell me about Arsenal and their recent results and barcelona's record here.")]
    public async Task ContextPhrasesDoNotHideUnknownPossessivesInsertedNamesOrMultipleIdentities(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context", "club_context", "league_context", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var continuationReads = 0; var continuation = await LeagueChatFixtures.Executor((_, _, _) => continuationReads++).ExecuteContinuationAsync(Context(), question, new("ground_context", "arsenal"), default);
        Feedback(continuation, MatchChatOutcome.Clarify); Assert.Equal(1, continuationReads);
    }

    [Fact]
    public async Task KnownInsertedNameStillOverridesUiAndPriorReferencesWithoutChangingThePage()
    {
        const string question = "What home ground is listed for our Arsenal club here?"; var c = Context("newcastle");
        await Answer(c, question, "named_ground_context", "arsenal", 2, "bournemouth");
        Assert.Equal("newcastle", c.SelectedClubId);
    }

    [Fact]
    public async Task TheirSelectedClubUsesTrustedPriorSubjectAndContinuationKeepsAcceptedAuthority()
    {
        const string question = "Tell me the ground for their selected club here."; var c = Context();
        await Answer(c, question, "ground_context", "arsenal", 2, "arsenal");
        foreach (var intent in Intents)
        {
            var topic = new LeagueAcceptedTopic("ground_context", "arsenal"); Assert.Equal("continue_topic", Select(c, question, "continue_topic", "context", intent, topic).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal(2, reads); Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(e => e.Id));
        }
    }

    [Fact]
    public async Task ScopeOwnerContinuationStaysWholeLeagueWithNoClubSelection()
    {
        const string question = "Explain this day's results here for me."; var c = Context(null); var topic = new LeagueAcceptedTopic("date_changes", null);
        foreach (var intent in Intents)
        {
            Assert.Equal("continue_topic", Select(c, question, "continue_topic", "context", intent, topic).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal(2, reads); Assert.Equal(new[] { "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
        }
    }

    [Theory]
    [InlineData("Show this day's results in March.", MatchChatOutcome.Clarify)]
    [InlineData("Show our selected club's ground at this minute.", MatchChatOutcome.Unavailable)]
    [InlineData("Show our selected club's ground on 19 September 2026.", MatchChatOutcome.Clarify)]
    public async Task SharedContextGrammarCannotOverrideAnUnresolvedDifferentOrActualTime(string question, MatchChatOutcome outcome)
    {
        foreach (var intent in Intents)
        {
            var c = Context(); Assert.Equal(outcome == MatchChatOutcome.Unavailable ? "live_unavailable" : "other_season", Select(c, question, "ground_context", "context", intent).WorkflowId);
            foreach (var workflow in new[] { "league_context", "ground_context", "club_context", "date_changes" })
            {
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, c, question, "arsenal", default);
                Feedback(result, outcome); Assert.Equal(0, reads);
            }
        }
    }

    [Fact]
    public async Task ContextEligibilityLeavesTypedTextAndAskedAtAuthorityByteIdentical()
    {
        const string question = "I'm checking 2026-27: what home ground is listed for our selected club?";
        var router = new LeagueChatFixtures.Router { Routing = (input, _) => Task.FromResult(Select(Context(), input.Question, "ground_context", "context", "explicit_time_reference")) };
        using var session = LeagueChatFixtures.Session(router: router); var c = Context(); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question)); var turn = Assert.Single(session.Turns);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(turn.Question)); Assert.Equal(c, turn.Context); Assert.Equal(MatchChatOutcome.Answered, turn.Outcome); Assert.Equal("everton", turn.ResolvedClubId);
    }

    [Theory]
    [InlineData("What ground is listed for this club here?")]
    [InlineData("What stadium is listed for that team here?")]
    [InlineData("What town is listed for the selected club here?")]
    [InlineData("Show the ground for this current team here.")]
    [InlineData("Show that selected club's stadium here.")]
    [InlineData("What is this club's town here?")]
    [InlineData("Show this ground here.")]
    [InlineData("Show that stadium here.")]
    [InlineData("Show the selected town here.")]
    [InlineData("At the 20 September 2026 cutoff, what ground is listed for this club?")]
    public async Task V8SpecificClubReferencesNeedAuthorityAcrossFactualWorkflows(string question)
    {
        foreach (var workflow in new[] { "ground_context", "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
        Assert.True(LeagueClubResolver.RequiresSubject(question));
        await Answer(Context(), question, "ground_context", "everton", 1);
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
    }

    [Theory]
    [InlineData("Show this Unknown club here.")]
    [InlineData("What ground is listed for that unknown team here?")]
    [InlineData("Show the selected barcelona club's stadium here.")]
    [InlineData("Show this selected club and that barcelona team here.")]
    [InlineData("Show this club's ground and barcelona's record here.")]
    [InlineData("Show this selected club and Arsenal and Everton here.")]
    [InlineData("Show this season's club results and that barcelona team here.")]
    [InlineData("Show this season's barcelona club results here.")]
    [InlineData("Show the results that barcelona club has here.")]
    [InlineData("Show Everton's home reference for this season: that barcelona club's ground here.")]
    [InlineData("Show this season; the selected barcelona club's ground here.")]
    [InlineData("Show the highlighted barcelona club's ground here.")]
    [InlineData("Show the listed barcelona club's ground here.")]
    [InlineData("Show that same barcelona team's record here.")]
    [InlineData("Show the whole table here, then this barcelona club's ground.")]
    public async Task V8ValidContextNeverLicensesInsertedUnknownOrMultipleIdentity(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var continuationReads = 0; var continuation = await LeagueChatFixtures.Executor((_, _, _) => continuationReads++).ExecuteContinuationAsync(Context(), question, new("date_changes", "arsenal"), default);
        Feedback(continuation, MatchChatOutcome.Clarify); Assert.Equal(1, continuationReads);
    }

    [Theory]
    [InlineData("Show this day's results here.", false)]
    [InlineData("Show this season's club results here.", false)]
    [InlineData("Show that selected day's results here.", false)]
    [InlineData("Show the selected date's results here.", false)]
    [InlineData("Show the whole league's results here.", false)]
    [InlineData("Show this table and these results here.", false)]
    [InlineData("Show the points across all clubs here.", false)]
    [InlineData("I haven't picked a team. Give me the standings for every club on the page.", true)]
    public async Task V8GenericFactAndScopeReferencesRemainGlobal(string question, bool implicitView)
    {
        Assert.False(LeagueClubResolver.RequiresSubject(question));
        foreach (var selection in new string?[] { null, "everton" })
            if (implicitView) await ImplicitAnswer(Context(selection), question, "league_context", null, 2);
            else await Answer(Context(selection), question, "league_context", null, 2);
    }

    [Theory]
    [InlineData("What points did our selected club add here?")]
    [InlineData("What ground is listed for this club here?")]
    [InlineData("Show that selected team's changes here.")]
    public async Task V8PresentGlobalTopicCannotBorrowUiAuthorityForSpecificClubFacet(string question)
    {
        foreach (var selection in new string?[] { null, "everton" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(Context(selection), question, new("date_changes", null), default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("Talk me through the results our club has accumulated by this selected date.")]
    [InlineData("Show the results this club has here.")]
    [InlineData("Explain the points that selected team have here.")]
    public Task V8FactFacetAndFollowingClubReferenceKeepSeparateConstituents(string question)
        => Answer(Context(), question, "club_context", "everton", 2);

    [Theory]
    [InlineData("I'd like Everton's home reference for this season: stadium and town.", false)]
    [InlineData("Show Everton's home reference for this season; stadium and town here.", false)]
    [InlineData("Show Everton's home reference for this season. Stadium and town here.", false)]
    [InlineData("What's the listed home ground for Everton?", true)]
    [InlineData("What are the listed ground and town for Everton here?", false)]
    public Task V8EmbeddedReferencesRespectRequestAndSentenceBoundaries(string question, bool implicitView)
        => implicitView ? ImplicitAnswer(Context("newcastle"), question, "named_ground_context", "everton", 2)
            : Answer(Context("newcastle"), question, "named_ground_context", "everton", 2);

    [Fact]
    public Task V8NamedClubAlongsideTheHighlightedClubRetainsNamedAuthority()
        => Answer(Context("coventry"), "I'm following Leeds United rather than the highlighted club. Give me their record in this view.", "named_club_context", "leeds", 3);

    [Theory]
    [InlineData("Name the home venue and town saved for our club in this season.", false)]
    [InlineData("Which stadium should I associate with this club on the map, and in which town?", true)]
    [InlineData("For the club I have open, give me the listed town and home ground.", true)]
    public Task V8FactHeadsAndLinkersDoNotInventUnknownClubModifiers(string question, bool implicitView)
        => implicitView ? ImplicitAnswer(Context(), question, "ground_context", "everton", 1)
            : Answer(Context(), question, "ground_context", "everton", 1);

    [Fact]
    public async Task V8SameReferenceRetainsTheAcceptedSubjectAndInspectsLaterUnknowns()
    {
        foreach (var question in new[] { "There is more I'd like to hear about that same stadium reference.", "Explain that same ground here." })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(Context(), question, new("ground_context", "arsenal"), default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal(2, reads);
            Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(e => e.Id));
        }
        var unknownReads = 0; var rejected = await LeagueChatFixtures.Executor((_, _, _) => unknownReads++).ExecuteContinuationAsync(Context(), "Explain this season's ground reference, then that same barcelona club here.", new("ground_context", "arsenal"), default);
        Feedback(rejected, MatchChatOutcome.Clarify); Assert.Equal(1, unknownReads);
    }

    [Fact]
    public Task V8PreseasonScopeHeadsAreBoundariesRatherThanClubIdentities()
        => Answer(LeagueChatContext.From(LeagueChatFixtures.Snapshot(0, null)), "Show the whole table before the first played day.", "league_context", null, 1);

    [Theory]
    [InlineData("At the 20 September 2026 cutoff, are our points and recent scores encouraging?")]
    [InlineData("Show our last few league results here.")]
    [InlineData("Show our starting league record here.")]
    public Task V8FactAndCalendarScopeHeadsDoNotDeclareUnknownClubModifiers(string question)
        => Answer(Context(), question, "club_context", "everton", 2);

    [Fact]
    public async Task V8ExplicitAcceptedAndReferenceSubjectsRemainIndependentOfUi()
    {
        const string question = "What ground is listed for this club here?"; var c = Context("newcastle");
        await Answer(c, question, "ground_context", "newcastle", 1);
        await Answer(Context(null), question, "ground_context", "arsenal", 2, "arsenal");
        await Answer(c, "What ground is listed for this Arsenal club here?", "named_ground_context", "arsenal", 2);
        await Answer(c, "What ground is listed for this season's Arsenal club here?", "named_ground_context", "arsenal", 2);
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(c, question, new("ground_context", "arsenal"), default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal(2, reads);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(e => e.Id)); Assert.Equal("newcastle", c.SelectedClubId);
        reads = 0; result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(c, "Explain this day's results here.", new("date_changes", null), default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal(2, reads);
        Assert.Equal(new[] { "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
    }

    [Theory]
    [InlineData("Show this club's ground at this minute.", MatchChatOutcome.Unavailable)]
    [InlineData("Show this club's ground in March.", MatchChatOutcome.Clarify)]
    [InlineData("Show this club's ground on 19 September 2026.", MatchChatOutcome.Clarify)]
    public async Task V8TimePrecedenceStillRefusesBeforeIdentityReads(string question, MatchChatOutcome outcome)
    {
        foreach (var intent in Intents)
        {
            var c = Context(null); Assert.Equal(outcome == MatchChatOutcome.Unavailable ? "live_unavailable" : "other_season", Select(c, question, "ground_context", "context", intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("date_changes", c, question, null, default);
            Feedback(result, outcome); Assert.Equal(0, reads);
        }
    }

    [Fact]
    public async Task V8CoverageBypassesClubEligibilityAndRawQuestionRemainsUnchanged()
    {
        const string capability = "Can this club's results be live here?"; var reads = 0;
        var coverage = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("coverage_limits", Context(null), capability, null, default);
        Assert.Equal(MatchChatOutcome.Answered, coverage.Outcome); Assert.Null(coverage.ResolvedClubId); Assert.Single(coverage.Evidence); Assert.Equal("league_coverage", coverage.Evidence[0].Id); Assert.Equal(1, reads);
        const string question = "What ground is listed for this club here?";
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model); var context = Context(null); session.SetContext(context); await session.AskAsync(context, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question)); var turn = Assert.Single(session.Turns);
        Assert.Equal(question, turn.Question); Assert.Equal(context, turn.Context); Assert.Equal(MatchChatOutcome.Clarify, turn.Outcome); Assert.Empty(turn.Evidence); Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("Show our form against Barcelona club here.")]
    [InlineData("Show our form Barcelona club here.")]
    [InlineData("Show their recent form Barcelona club here.")]
    [InlineData("Show our home ground Barcelona club here.")]
    [InlineData("Show this club's form Barcelona club here.")]
    [InlineData("Show our recent results and Barcelona club here.")]
    [InlineData("Show our form against this season's Barcelona club here.")]
    [InlineData("Show our form, then that Barcelona club here.")]
    [InlineData("Show our form Arsenal and Barcelona club here.")]
    [InlineData("Show our form: Barcelona club here.")]
    [InlineData("Show our form, Barcelona club here.")]
    [InlineData("Show their Barcelona club's form here.")]
    [InlineData("Show this club: Barcelona club here.")]
    [InlineData("Show this club form Barcelona club here.")]
    [InlineData("Show our Barcelona club ground here.")]
    [InlineData("Show this Barcelona club's form here.")]
    [InlineData("Tell me Everton's ground and its Barcelona club here.")]
    [InlineData("Show its Barcelona club here.")]
    [InlineData("Show its season's Barcelona club here.")]
    [InlineData("Show our form against its Barcelona club here.")]
    public async Task V8R2CompletedFactFacetNeverHidesResidualUnknownClub(string question)
    {
        foreach (var workflow in new[] { "club_context", "club_form", "ground_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context("arsenal"), question, "newcastle", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context(), question, new("club_context", "arsenal"), default);
        Feedback(accepted, MatchChatOutcome.Clarify); Assert.Equal(1, acceptedReads);
    }

    [Theory]
    [InlineData("Show our form against Barcelona club here.")]
    [InlineData("Show our form Barcelona club here.")]
    [InlineData("Show this club's form Barcelona club here.")]
    public async Task V8R2MissingClubStillRefusesBeforeAnyRead(string question)
    {
        foreach (var workflow in new[] { "club_context", "ground_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
    }

    [Fact]
    public async Task V8R2KnownReferencesAndGlobalFactConstituentsKeepTheirAuthority()
    {
        await Answer(Context(), "Show our Arsenal club's form here.", "named_club_context", "arsenal", 3);
        await Answer(Context(), "Show their Arsenal team's ground here.", "named_ground_context", "arsenal", 2);
        await Answer(Context(null), "Show the whole league's results here.", "league_context", null, 2);
        await Answer(Context(null), "Show this season's club results here.", "league_context", null, 2);
        await Answer(Context("newcastle"), "I'd like Everton's home reference for this season: stadium and town.", "named_ground_context", "everton", 2);
        await Answer(Context(), "Show the results our club has here.", "club_context", "everton", 2);
        await Answer(Context(), "Show our form against their club here.", "club_context", "everton", 2);
        await Answer(Context(), "Show our results and their club's ground here.", "ground_context", "everton", 1);
        await Answer(Context(), "Show our club ground here.", "ground_context", "everton", 1);
        await Answer(Context(), "Show our club's ground here.", "ground_context", "everton", 1);
        await Answer(Context(), "Show our club's ground and town here.", "ground_context", "everton", 1);
        await Answer(LeagueChatContext.From(LeagueChatFixtures.Snapshot(0, null)), "Show the whole table before the first played day.", "league_context", null, 1);
    }

    [Theory]
    [InlineData("Tell me Everton's ground and its home town in 2026/27.")]
    [InlineData("Tell me Everton's ground and its town here.")]
    public Task V8R2ExistingPossessiveAnaphorStartsAnIndependentlyValidatedReference(string question)
        => Answer(Context("newcastle"), question, "named_ground_context", "everton", 2);

    [Theory]
    [InlineData("Show our form against Barcelona club here.")]
    [InlineData("Show our form Barcelona club here.")]
    public async Task V8R2TypedUnknownResidualNeverReachesNarrationOrChangesRawQuestion(string question)
    {
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" }; var model = new LeagueChatFixtures.Model(); var reads = 0;
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var c = Context("arsenal"); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question));
        var turn = Assert.Single(session.Turns); Assert.Equal(question, turn.Question); Assert.Equal(c, turn.Context);
        Assert.Equal(MatchChatOutcome.Clarify, turn.Outcome); Assert.Empty(turn.Evidence); Assert.Null(turn.ResolvedClubId); Assert.Empty(model.Requests); Assert.Equal(1, reads);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("2026-27", 12, "2026-09-12", "brighton", "How have Nottingham Forest's latest results affected their standing?", "named_club_context", "nottingham-forest", 3)]
    [InlineData("2026-27", 8, "2026-08-31", "bournemouth", "What home ground is listed for this club, and which town is it in?", "ground_context", "bournemouth", 1)]
    [InlineData("2016-17", 107, "2017-05-21", "west-ham", "What are Stoke City's recorded home ground and town here?", "named_ground_context", "stoke", 2)]
    public async Task V9ExactPinnedCasesBindTheRequestedClubAndCutoff(string season, int index, string date, string selected, string question, string workflow, string subject, int expectedReads)
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new Matchmate.Core.LeagueModel(Matchmate.Core.LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot(season, index, selected)); Assert.Equal(date, context.Date);
        var canonical = workflow.StartsWith("named_", StringComparison.Ordinal) ? workflow[6..] : workflow;
        Assert.Equal(workflow, Select(context, question, canonical, workflow.StartsWith("named_", StringComparison.Ordinal) ? "explicit_name" : "context", "selected_view").WorkflowId);
        var reads = 0; var result = await new LeagueWorkflowExecutor((key, cutoff, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(key, cutoff, club)); }).ExecuteAsync(workflow, context, question, null, default);
        V9Evidence(result, context, subject, canonical == "ground_context"); Assert.Equal(expectedReads, reads); Assert.Equal(selected, context.SelectedClubId);
        if (canonical == "ground_context")
        {
            using var evidence = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "ground_reference").Text);
            var expected = model.Snapshot(season, index, subject).SelectedClub!.Club;
            Assert.Equal(expected.Ground, evidence.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
            Assert.Equal(expected.Town, evidence.RootElement.GetProperty("facts").GetProperty("Town").GetString());
        }
    }

    [Theory]
    [InlineData("How have Nottingham Forest's latest results affected their standing?", "selected_view")]
    [InlineData("How have Nottingham Forest's latest results affected their standing here?", "selected_view")]
    [InlineData("How have Nottingham Forest's latest results affected their standing here?", "explicit_time_reference")]
    [InlineData("How has Everton's recent record affected their standing here?", "selected_view")]
    [InlineData("How have Nottingham Forest performed here?", "selected_view")]
    [InlineData("How is Nottingham Forest standing here?", "selected_view")]
    [InlineData("How have Nottingham Forest been standing here?", "selected_view")]
    [InlineData("How are Nottingham Forest standing here?", "selected_view")]
    public Task V9OwnedResultFacetsAreNotFiniteIdentityPredicates(string question, string intent)
        => Answer(Context("brighton"), question, "named_club_context", question.Contains("Everton", StringComparison.Ordinal) ? "everton" : "nottingham-forest", 3, intents: [intent]);

    [Theory]
    [InlineData("How have Barcelona's latest results affected their standing here?")]
    [InlineData("How has Barcelona's recent record affected their standing here?")]
    [InlineData("How have our Barcelona club's latest results affected their standing here?")]
    [InlineData("How have Nottingham Forest's latest results affected their standing, and have Barcelona won here?")]
    [InlineData("How have Nottingham Forest's latest results affected their standing, and have Arsenal won here?")]
    [InlineData("How have Barcelona performed here?")]
    [InlineData("How is Barcelona standing here?")]
    [InlineData("How have Barcelona been standing here?")]
    public async Task V9OwnedResultQuestionsCannotBorrowUiOrAcceptedIdentity(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context("brighton"), question, "arsenal", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context("brighton"), question, new("club_context", "nottingham-forest"), default);
        Feedback(accepted, MatchChatOutcome.Clarify); Assert.Equal(1, acceptedReads);
    }

    [Theory]
    [InlineData("What home ground is listed for this club, and which town is it in?", "selected_view")]
    [InlineData("What home ground is listed for this club, and which town is it in here?", "selected_view")]
    [InlineData("What home ground is listed for this club, and which town is it in here?", "explicit_time_reference")]
    [InlineData("What ground is listed for our selected club here, and which town is it in?", "selected_view")]
    [InlineData("What ground is listed for this club here and what town is listed?", "selected_view")]
    [InlineData("Show our form, and which ground does our club use here?", "selected_view")]
    [InlineData("Show our form, and which town is it in here?", "selected_view")]
    [InlineData("Which town is our club in here, and what ground is listed?", "selected_view")]
    [InlineData("Which ground is our club associated with in this club reference?", "selected_view")]
    [InlineData("Which town is our club based in here?", "selected_view")]
    [InlineData("Which town is our club located in here?", "selected_view")]
    [InlineData("Which home-ground name and town belong to the club I've picked?", "selected_view")]
    [InlineData("Which town is our club's listed home ground in?", "selected_view")]
    public Task V9IndependentGroundAndTownClausesKeepTheSelectedClub(string question, string intent)
        => Answer(Context("bournemouth"), question, "ground_context", "bournemouth", 1, intents: [intent]);

    [Theory]
    [InlineData("What ground is listed for this club here, and which Barcelona club is in this town?")]
    [InlineData("Which Barcelona club is in this town, and what ground is listed for this club here?")]
    [InlineData("Show our form, and which ground is Barcelona club in here?")]
    [InlineData("What ground is listed for this club here, and have Barcelona played here?")]
    [InlineData("What ground is listed for this club here, and have Arsenal and Barcelona played here?")]
    [InlineData("What ground is listed for this club here: Barcelona club.")]
    [InlineData("What ground is listed for this club here, Barcelona club.")]
    [InlineData("Show our form, and which ground does Barcelona use?")]
    [InlineData("Show our form, and which town is Barcelona in?")]
    [InlineData("Which ground does Barcelona use, and what ground is listed for our club here?")]
    [InlineData("Which town is Barcelona in, and what ground is listed for our club here?")]
    [InlineData("Which ground is Barcelona associated with in this club reference?")]
    [InlineData("Which ground are Man City and Arsenal associated with in this club reference?")]
    [InlineData("Which town is Barcelona based in here?")]
    [InlineData("Which town is Barcelona located in here?")]
    [InlineData("Which town is Barcelona's listed home ground in here?")]
    [InlineData("Which town is our club's listed Barcelona ground in here?")]
    [InlineData("Which town is Everton and Barcelona's listed home ground in here?")]
    public async Task V9IndependentQuestionsStillValidateEveryUnknownIdentity(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context", "club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context("bournemouth"), question, "arsenal", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context("brighton"), question, new("ground_context", "bournemouth"), default);
        Feedback(accepted, MatchChatOutcome.Clarify); Assert.Equal(1, acceptedReads);
    }

    [Theory]
    [InlineData("What are Everton's recorded home ground and town here?")]
    [InlineData("What is our club's recorded home ground here?")]
    [InlineData("Show this club's recorded ground and town here.")]
    [InlineData("Show our recorded home ground and town here.")]
    public Task V9RecordedSourceDescriptorsStayInsideCompleteFactPhrases(string question)
        => Answer(Context(), question, question.Contains("Everton", StringComparison.Ordinal) ? "named_ground_context" : "ground_context", "everton", 1);

    [Theory]
    [InlineData("What are Barcelona's recorded home ground and town here?")]
    [InlineData("Show our recorded Barcelona club's ground here.")]
    [InlineData("What are Everton and Barcelona's recorded home ground and town here?")]
    [InlineData("Show our recorded home ground Barcelona club here.")]
    [InlineData("Show our club's recorded ground here, and which Barcelona club is in this town?")]
    public async Task V9RecordedSourceDescriptorsDoNotLicenseUnknownOwners(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context", "date_changes", "league_context" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context("brighton"), question, new("ground_context", "everton"), default);
        Feedback(accepted, MatchChatOutcome.Clarify); Assert.Equal(1, acceptedReads);
    }

    [Fact]
    public async Task V9ReferenceAcceptedMissingGlobalAndTimeAuthoritiesRemainSeparate()
    {
        const string ground = "What ground is listed for this club here, and which town is it in?";
        await Answer(Context(null), ground, "ground_context", "bournemouth", 2, "bournemouth");
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context("brighton"), ground, new("ground_context", "bournemouth"), default);
        V9Evidence(accepted, Context("brighton"), "bournemouth", true); Assert.Equal(2, acceptedReads);
        foreach (var workflow in new[] { "ground_context", "club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), ground, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
        await Answer(Context(null), "Which club leads this table?", "league_context", null, 2);
        foreach (var question in new[] { "Show our recorded ground at this moment.", "Show our recorded ground in March.", "Show our recorded ground on 19 September 2026." })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("ground_context", Context(), question, null, default);
            Feedback(result, question.Contains("moment", StringComparison.Ordinal) ? MatchChatOutcome.Unavailable : MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
        var coverageReads = 0; var coverage = await LeagueChatFixtures.Executor((_, _, _) => coverageReads++).ExecuteAsync("coverage_limits", Context(null), "Does the recorded ground reference provide live venue information?", null, default);
        Assert.Equal(MatchChatOutcome.Answered, coverage.Outcome); Assert.Null(coverage.ResolvedClubId); Assert.Equal("league_coverage", Assert.Single(coverage.Evidence).Id); Assert.Equal(1, coverageReads);
    }

    [Fact]
    public async Task V9TypedOwnedFactsPreserveRawQuestionsAndFreshPreparedEvidence()
    {
        const string question = "How have Nottingham Forest's latest results affected their standing?";
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_context" }; var model = new LeagueChatFixtures.Model(); var reads = 0;
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var c = Context("brighton"); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question));
        var turn = Assert.Single(session.Turns); Assert.Equal(question, turn.Question); Assert.Equal(c, turn.Context); Assert.Equal(MatchChatOutcome.Answered, turn.Outcome); Assert.Equal("nottingham-forest", turn.ResolvedClubId); Assert.Equal(3, reads);
        var request = Assert.Single(model.Requests); var prepared = request.Messages.Single(m => m.Content.StartsWith("Prepared evidence for this turn only:", StringComparison.Ordinal));
        using var evidence = JsonDocument.Parse(prepared.Content[(prepared.Content.IndexOf('\n') + 1)..]);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, evidence.RootElement.EnumerateArray().Select(e => e.GetProperty("Id").GetString()));
    }

    [Theory]
    [InlineData("Which town is it in here?")]
    [InlineData("Which ground does it use here?")]
    [InlineData("Which town is it based in here?")]
    [InlineData("Which town is it located in here?")]
    [InlineData("Which ground is it associated with here?")]
    [InlineData("Which town is our club's listed home ground in here?")]
    public async Task V9PlaceAnaphorsRequireAClubRatherThanBorrowGlobalAuthority(string question)
    {
        await Answer(Context("bournemouth"), question, "ground_context", "bournemouth", 1);
        await Answer(Context(null), question, "ground_context", "bournemouth", 2, "bournemouth");
        foreach (var workflow in new[] { "ground_context", "club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
        foreach (var topic in new[] { new LeagueAcceptedTopic("league_context", null), new LeagueAcceptedTopic("date_changes", null) })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(Context("bournemouth"), question, topic, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, reads);
        }
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context("bournemouth"), question, null, default);
            Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(1, reads);
        }
        var acceptedReads = 0; var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++).ExecuteContinuationAsync(Context("brighton"), question, new("ground_context", "bournemouth"), default);
        V9Evidence(accepted, Context("brighton"), "bournemouth", true); Assert.Equal(2, acceptedReads);
    }

    [Fact]
    public Task V9PlacePassivePredicateDoesNotBecomePartOfTheTrustedName()
        => Answer(Context("fulham"), "Which ground is Man City associated with in this club reference?", "named_ground_context", "manchester-city", 2, intents: ["selected_view"]);

    [Theory]
    [InlineData("Which town is Everton based in here?")]
    [InlineData("Which town is Everton located in here?")]
    [InlineData("Which town is Everton's listed home ground in here?")]
    public Task V9CompletePlacePredicatesKeepExplicitKnownNamesAheadOfUi(string question)
        => Answer(Context("brighton"), question, "named_ground_context", "everton", 2);

    private static void V9Evidence(LeagueWorkflowResult result, LeagueChatContext context, string subject, bool ground)
    {
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId);
        Assert.Equal(ground ? new[] { "ground_reference", "league_coverage" } : ["club_standing", "club_results", "date_change", "league_coverage"], result.Evidence.Select(e => e.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text); Assert.Equal(context.Date, evidence.RootElement.GetProperty("date").GetString()); Assert.Equal(context.SeasonKey, evidence.RootElement.GetProperty("season").GetString());
        }
    }

    private static async Task ImplicitAnswer(LeagueChatContext c, string question, string workflow, string? subject, int reads)
    {
        await Answer(c, question, workflow, subject, reads, intents: ["selected_view"]);
        var canonical = workflow.StartsWith("named_", StringComparison.Ordinal) ? workflow[6..] : workflow;
        var route = Select(c, question, canonical, workflow.StartsWith("named_", StringComparison.Ordinal) ? "explicit_name" : "context", "explicit_time_reference");
        Assert.Equal("other_season", route.WorkflowId);
        var actualReads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => actualReads++).ExecuteAsync(route.WorkflowId!, c, question, null, default);
        Feedback(result, MatchChatOutcome.Clarify); Assert.Equal(0, actualReads);
    }

    private static readonly string[] Intents = ["selected_view", "explicit_time_reference"];
    private static LeagueChatContext Context(string? club = "everton") => LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: club));
    private static void Feedback(LeagueWorkflowResult result, MatchChatOutcome outcome) { Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); }
    private static MatchRoutingResult Select(LeagueChatContext c, string question, string workflow, string binding, string intent, LeagueAcceptedTopic? topic = null)
    {
        var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), [], AcceptedTopic: topic);
        return DecisionLeagueWorkflowRouter.Select(DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, binding, scope: intent))), new DecisionRoutingOptions(), input);
    }
    private static async Task Answer(LeagueChatContext c, string question, string workflow, string? subject, int expectedReads, string? reference = null, string[]? intents = null)
    {
        foreach (var intent in intents ?? Intents)
        {
            var canonical = workflow.StartsWith("named_", StringComparison.Ordinal) ? workflow[6..] : workflow;
            Assert.Equal(workflow, Select(c, question, canonical, workflow.StartsWith("named_", StringComparison.Ordinal) ? "explicit_name" : "context", intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, c, question, reference, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
            var expectedIds = canonical switch { "ground_context" => new[] { "ground_reference", "league_coverage" }, "league_context" => ["table_snapshot", "date_change", "league_coverage"], _ => ["club_standing", "club_results", "date_change", "league_coverage"] };
            Assert.Equal(expectedIds, result.Evidence.Select(e => e.Id));
            foreach (var e in result.Evidence) { using var data = JsonDocument.Parse(e.Text); Assert.Equal(c.Date, data.RootElement.GetProperty("date").GetString()); Assert.Equal(c.SeasonKey, data.RootElement.GetProperty("season").GetString()); }
            if (canonical == "ground_context")
            {
                using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "ground_reference").Text); var expected = LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Club;
                Assert.Equal(expected.Ground, data.RootElement.GetProperty("facts").GetProperty("Ground").GetString()); Assert.Equal(expected.Town, data.RootElement.GetProperty("facts").GetProperty("Town").GetString());
            }
            if (canonical == "league_context") { using var data = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "table_snapshot").Text); Assert.Equal(20, data.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength()); }
        }
    }
}
