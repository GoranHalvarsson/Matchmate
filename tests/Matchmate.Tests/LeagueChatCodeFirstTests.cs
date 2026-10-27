using System.Text;
using System.Text.Json;
using Matchmate.LeagueChatEvaluation;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatCodeFirstTests
{
    [Theory]
    [InlineData("Show the whole league at  present.")]
    [InlineData("Show the whole league at\nthe moment.")]
    [InlineData("Show this selected table at\tpresent.")]
    [InlineData("Explain our points on 20 September 2026 at\r\nthe\tmoment.")]
    public async Task ActualPresentPhraseWhitespaceCannotGrantSnapshotFacts(string question)
    {
        var c = Context(); var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", c, question, null, default);
        Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            Assert.Equal(LeagueTimeResolution.RealWorldNow, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
            Assert.Equal("live_unavailable", Select(question, intent).WorkflowId);
        }
    }
    [Fact]
    public async Task OutsideReplayNowQuestionCannotExecuteSnapshotFactsUnderForcedGlobalWorkflow()
    {
        const string question = "What's the score in the matches taking place now, outside this replay?";
        var c = Context("hull"); var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", c, question, null, default);
        Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
    }
    [Theory]
    [InlineData("What's the score in the matches taking place now, outside this replay?")]
    [InlineData("Who is playing now?")]
    [InlineData("How are we doing currently?")]
    [InlineData("What is Arsenal's ground at present?")]
    [InlineData("Show the whole league at the moment.")]
    [InlineData("Show this selected table now.")]
    [InlineData("Explain our points on 20 September 2026, currently.")]
    [InlineData("What happened on 20 September 2026 at the moment?")]
    [InlineData("Now, explain this table and who is playing now.")]
    [InlineData("Now, explain our points currently.")]
    [InlineData("Now, explain the matches outside this replay.")]
    [InlineData("Now show the table beyond the selected snapshot.")]
    [InlineData("Now, show the matches not in this view.")]
    public async Task ActualPresentTimeRefusesFactsDespiteWrongIntentWorkflowOrSubject(string question)
    {
        foreach (var selection in new string?[] { null, "hull" })
        {
            // The exact reported053 scope is valid without reading a snapshot at all.
            var c = Context(selection) with { DateIndex = 17 };
            var topic = new LeagueAcceptedTopic("club_form", "arsenal");
            foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
            {
                Assert.Equal(LeagueTimeResolution.RealWorldNow, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
                foreach (var workflow in new[] { "league_context", "club_context", "ground_context", "continue_topic" })
                {
                    var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), [], AcceptedTopic: topic);
                    var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, scope: intent)));
                    var route = DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input);
                    Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal("live_unavailable", route.WorkflowId);
                }
            }
            foreach (var workflow in new[] { "league_context", "league_table", "date_changes", "club_context", "named_club_context", "ground_context", "named_ground_context" })
            {
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, c, question, "arsenal", default);
                Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
            }
            var continuationReads = 0; var continued = await LeagueChatFixtures.Executor((_, _, _) => continuationReads++).ExecuteContinuationAsync(c, question, topic, default);
            Assert.Equal(MatchChatOutcome.Unavailable, continued.Outcome); Assert.Null(continued.ResolvedClubId); Assert.Empty(continued.Evidence); Assert.Equal(0, continuationReads);
        }
    }
    [Theory]
    [InlineData("Now, explain this selected table.", "league_context", "context", null, 2, 3)]
    [InlineData("Explain this table.", "league_context", "context", null, 2, 3)]
    [InlineData("Explain selected standings.", "league_context", "context", null, 2, 3)]
    [InlineData("Now, review the selected standings.", "league_context", "context", null, 2, 3)]
    [InlineData("Now show the whole league table here.", "league_context", "context", null, 2, 3)]
    [InlineData("Now, please describe the whole league on 20 September 2026.", "league_context", "context", null, 2, 3)]
    [InlineData("Now, can you show the whole league here?", "league_context", "context", null, 2, 3)]
    [InlineData("Explain the whole league on 20 September 2026.", "league_context", "context", null, 2, 3)]
    [InlineData("Now, explain our points here.", "club_context", "context", "everton", 2, 4)]
    [InlineData("Now, tell me about Arsenal's ground here.", "ground_context", "explicit_name", "arsenal", 2, 2)]
    public async Task LeadingTaskOrderNowAndMatchingDatesRetainActualSelectedFacts(string question, string workflow, string binding, string? subject, int expectedReads, int evidenceCount)
    {
        var c = Context();
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
            var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(workflow, binding, scope: intent)));
            var route = DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal(binding == "explicit_name" ? "named_" + workflow : workflow, route.WorkflowId);
            Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(c.SeasonKey, c.Date, question, intent == "explicit_time_reference"));
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, c, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads); Assert.Equal(evidenceCount, result.Evidence.Length);
            var expectedIds = workflow switch { "league_context" => new[] { "table_snapshot", "date_change", "league_coverage" }, "club_context" => ["club_standing", "club_results", "date_change", "league_coverage"], _ => ["ground_reference", "league_coverage"] };
            Assert.Equal(expectedIds, result.Evidence.Select(e => e.Id));
        }
    }
    [Theory]
    [InlineData("Does this source update live now?")]
    [InlineData("What are this source's limits currently?")]
    [InlineData("What does coverage mean at present?")]
    [InlineData("What can this snapshot tell me at the moment?")]
    public async Task ActualPresentCapabilityQuestionsRemainCoverageMetadataOnly(string question)
    {
        var c = Context();
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
            var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice("coverage_limits", scope: intent)));
            var route = DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input);
            Assert.Equal("coverage_limits", route.WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(route.WorkflowId!, c, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id); Assert.Equal(1, reads);
        }
    }
    [Theory]
    [InlineData("Hull City currently.")]
    [InlineData("Hull City now.")]
    [InlineData("Hull City at present.")]
    [InlineData("Hull City at the moment.")]
    public async Task PresentTimeCannotBeErasedByPendingBriefIdentityProjection(string question)
    {
        var c = Context(null); const string original = "Where is their ground here?";
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, null, null), [], Clarification: new(original));
            Assert.False(LeagueQuestionProjection.BriefIdentityReply(question)); Assert.Null(LeagueQuestionProjection.Original(input)); Assert.Equal(question, LeagueQuestionProjection.WorkflowQuestion(input));
            var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice("ground_context", "explicit_name", scope: intent)));
            Assert.Equal("live_unavailable", DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("named_ground_context", c, question, null, default, original);
            Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Fact]
    public async Task TaskOrderAssessmentKeepsRawSessionQuestionAndHistoryUnchanged()
    {
        const string question = "Now, explain this selected table.";
        var router = new LeagueChatFixtures.Router { Workflow = "league_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var c = Context(); session.SetContext(c); await session.AskAsync(c, question);
        Assert.Equal(question, Assert.Single(router.Inputs).Question); Assert.Equal(question, Assert.Single(session.Turns).Question); Assert.Equal(MatchChatOutcome.Answered, session.Turns[0].Outcome);
    }
    [Theory]
    [InlineData("Now, explain this selected table in March.", LeagueTimeResolution.Unresolved)]
    [InlineData("Now, explain this table on 19 September 2026.", LeagueTimeResolution.DifferentTime)]
    public async Task LeadingTaskOrderAndDeicticTableNeverOverrideConflictingCalendar(string question, LeagueTimeResolution expected)
    {
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            Assert.Equal(expected, LeagueTimeScope.Resolve("2026-27", "2026-09-20", question, intent == "explicit_time_reference"));
            Assert.Equal("other_season", Select(question, intent).WorkflowId);
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", Context(), question, null, default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Fact]
    public void BareTableDoesNotBecomeAnExplicitSelectedViewAnchor()
    {
        const string question = "Explain the table.";
        Assert.Equal(LeagueTimeAssessment.NoReference, LeagueTimeScope.Assess("2026-27", "2026-09-20", question));
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve("2026-27", "2026-09-20", question));
        Assert.Equal(LeagueTimeResolution.Unresolved, LeagueTimeScope.Resolve("2026-27", "2026-09-20", question, true));
    }
    [Theory]
    [InlineData("Before the first result is in, how many games and points do we have?", 0, "fulham", 1)]
    [InlineData("How many points and wins do we have at this cutoff?", 5, "fulham", 2)]
    [InlineData("How many games do we have here?", 5, "fulham", 2)]
    [InlineData("Could you show me how many matches and points we have here?", 5, "fulham", 2)]
    [InlineData("How many games and points does Arsenal have here?", 5, "arsenal", 3)]
    public async Task CoordinatedCountFacetsExtractTheActualClubSubject(string question, int index, string subject, int expectedReads)
    {
        var reads = 0; var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(index, "fulham"));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", c, question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
        using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
        var expected = LeagueChatFixtures.Snapshot(index, subject).SelectedClub!.Row;
        Assert.Equal(expected.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
        Assert.Equal(expected.Played, standing.RootElement.GetProperty("facts").GetProperty("Played").GetInt32());
    }
    [Theory]
    [InlineData("How many games and points do we have here?")]
    [InlineData("Could you show me how many points and wins they have here?")]
    public async Task CountQuestionCannotAcquireFactsWithoutItsClubEvenThroughWrongGlobalRoute(string question)
    {
        foreach (var workflow in new[] { "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Theory]
    [InlineData("How many games and points does barcelona have here?")]
    [InlineData("How many points and wins do Arsenal and barcelona have here?")]
    public async Task CountQuestionUnknownOrMixedSubjectCannotBorrowUiOrPriorIdentity(string question)
    {
        foreach (var workflow in new[] { "club_context", "league_context", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
        }
    }
    [Theory]
    [InlineData("Can we look at the entire league's results for this particular day?")]
    [InlineData("Show the whole league\u2019s record at this snapshot.")]
    [InlineData("Show the full league's results here.")]
    public async Task CanonicalLeaguePossessivePreservesEligibleGlobalEvidence(string question)
    {
        foreach (var selection in new string?[] { null, "arsenal" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", Context(selection), question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal(2, reads);
            Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
            using var table = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "table_snapshot").Text);
            Assert.Equal(20, table.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
        }
    }
    [Theory]
    [InlineData("Show the entire league's results and barcelona's record here.")]
    [InlineData("Show the whole league's record and Arsenal's and barcelona's results here.")]
    public async Task GlobalPossessiveDoesNotAuthorizeAnUnknownOrMixedClub(string question)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", Context(), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
    }
    [Fact]
    public async Task StrongCountSubjectStillRefusesWrongGlobalLeaguePossessiveProjection()
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", Context(), "How many games and points do we have across the entire league's record here?", null, default);
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
    }
    [Theory]
    [InlineData("Hull City, please.", "hull")]
    [InlineData("Please, Hull City!", "hull")]
    [InlineData("Please Hull City please.", "hull")]
    [InlineData("Brighton & Hove Albion, please.", "brighton")]
    [InlineData("barcelona, please.", null)]
    [InlineData("Hull City & Arsenal, please.", null)]
    public void PoliteBriefShapeAndExactFreshAliasUseOneIdentityNormalization(string question, string? subject)
    {
        Assert.True(LeagueQuestionProjection.BriefIdentityReply(question));
        Assert.Equal(subject, LeagueClubResolver.ResolveBrief(LeagueChatFixtures.Snapshot().Clubs, question).ClubId);
    }
    [Fact]
    public void PoliteHistoricalNameStillRequiresFreshSeasonMembershipAndLengthBound()
    {
        var clubs = LeagueChatFixtures.Snapshot().Clubs;
        var historical = clubs.Select(c => c.Id == "coventry" ? c with { Id = "middlesbrough", Name = "Middlesbrough" } : c).ToArray();
        Assert.True(LeagueQuestionProjection.BriefIdentityReply("Middlesbrough, please."));
        Assert.Null(LeagueClubResolver.ResolveBrief(clubs, "Middlesbrough, please.").ClubId);
        Assert.Equal("middlesbrough", LeagueClubResolver.ResolveBrief(historical, "Middlesbrough, please.").ClubId);
        Assert.False(LeagueQuestionProjection.BriefIdentityReply("Please, " + new string(' ', 81) + "Hull City, please."));
    }
    [Theory]
    [InlineData("Hull City, please.", "hull")]
    [InlineData("Please, Hull City.", "hull")]
    [InlineData("barcelona, please.", null)]
    [InlineData("Hull City & Arsenal, please.", null)]
    public async Task PoliteBriefSessionPreservesPendingTopicRawQuestionAndFreshAuthority(string reply, string? subject)
    {
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; var reads = 0;
        using var session = LeagueChatFixtures.Session(router: router, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var c = Context(null); var original = "Where is their listed home ground, and what is their town?";
        session.SetContext(c); await session.AskAsync(c, original); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
        router.Workflow = "named_ground_context"; reads = 0; await session.AskAsync(c, reply);
        Assert.Equal(reply, router.Inputs[^1].Question); Assert.Equal(original, router.Inputs[^1].Clarification?.OriginalQuestion);
        Assert.Equal(original, LeagueQuestionProjection.WorkflowQuestion(router.Inputs[^1]));
        Assert.Equal(reply, session.Turns[^1].Question); Assert.Equal(original, session.Turns[^1].OriginalQuestion);
        Assert.Equal(subject, session.Turns[^1].ResolvedClubId); Assert.Equal(subject is null ? 1 : 2, reads);
        Assert.Equal(subject is null ? MatchChatOutcome.Clarify : MatchChatOutcome.Answered, session.Turns[^1].Outcome);
        if (subject is null) Assert.Empty(session.Turns[^1].Evidence); else Assert.Equal("ground_reference", Assert.Single(session.Turns[^1].Evidence).Id);
    }
    [Theory]
    [InlineData("Hull City today, please.", "named_ground_context", MatchChatOutcome.Unavailable)]
    [InlineData("Tell me about Hull City's points, please.", "named_club_context", MatchChatOutcome.Answered)]
    public async Task PolitenessDoesNotRecoverPendingTopicForTimeOrNewIntent(string reply, string workflow, MatchChatOutcome outcome)
    {
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var c = Context(null); session.SetContext(c); await session.AskAsync(c, "Tell me about their ground?");
        router.Workflow = workflow; await session.AskAsync(c, reply);
        Assert.False(LeagueQuestionProjection.BriefIdentityReply(reply)); Assert.Null(router.Inputs[^1].Clarification);
        Assert.Equal(reply, router.Inputs[^1].Question); Assert.Null(session.Turns[^1].OriginalQuestion); Assert.Equal(outcome, session.Turns[^1].Outcome);
        if (outcome != MatchChatOutcome.Answered) Assert.Empty(session.Turns[^1].Evidence);
    }
    [Theory]
    [InlineData("Could you show me how we performed?", "everton", 2)]
    [InlineData("Would you please explain how we performed here?", "everton", 2)]
    [InlineData("Please can you tell us how Arsenal performed here?", "arsenal", 3)]
    public async Task RequestWrapperPreservesInnerClubSubjectAndExactFacts(string question, string subject, int expectedReads)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", Context(), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
        using var standing = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
        Assert.Equal(LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
    }
    [Theory]
    [InlineData("Can you show how all clubs performed here?")]
    [InlineData("Could you please describe how all the clubs performed here?")]
    public async Task RequestWrapperPreservesActualAllClubPerspective(string question)
    {
        foreach (var selection in new string?[] { null, "everton" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("league_context", Context(selection), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Equal(2, reads);
            Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, result.Evidence.Select(e => e.Id));
            using var table = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "table_snapshot").Text);
            Assert.Equal(20, table.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
        }
    }
    [Theory]
    [InlineData("Could you show me how we performed?")]
    [InlineData("Please would you explain whether we won here?")]
    public async Task MissingInnerClubSubjectCannotAcquireFactsThroughAWrapperOrWrongWorkflow(string question)
    {
        foreach (var workflow in new[] { "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Theory]
    [InlineData("Could you show me how barcelona performed?")]
    [InlineData("Can you show how Arsenal and barcelona performed here?")]
    [InlineData("Would you please explain whether barcelona won here?")]
    [InlineData("Has barcelona performed here?")]
    public async Task WrapperAndDirectUnknownInnerSubjectsCannotBorrowSelectedOrReferenceFacts(string question)
    {
        foreach (var workflow in new[] { "club_context", "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
        }
    }
    [Fact]
    public async Task ClubRequestWrapperCannotBeAnsweredByWrongGlobalProjectionWithSelection()
    {
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), "Could you show me how we performed here?", null, default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(1, reads);
        }
    }
    [Fact]
    public void AssessmentDistinguishesPositiveReferenceFromNoReferenceAndRefusal()
    {
        Assert.Equal(LeagueTimeAssessment.NoReference, LeagueTimeScope.Assess("2026-27", "2026-09-20", "How are we doing?"));
        Assert.Equal(LeagueTimeAssessment.MatchesSelection, LeagueTimeScope.Assess("2026-27", "2026-09-20", "How are we doing here?"));
        Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess("2026-27", "2026-09-20", "March results here?"));
        Assert.Equal(LeagueTimeAssessment.DifferentTime, LeagueTimeScope.Assess("2026-27", "2026-09-20", "What changed on 19 September 2026?"));
        Assert.Equal(LeagueTimeAssessment.RealWorldNow, LeagueTimeScope.Assess("2026-27", "2026-09-20", "Who is playing today?"));
        Assert.Equal("club_context", Select("How are we doing?", "selected_view", .6).WorkflowId);
        Assert.Equal("other_season", Select("How are we doing?", "explicit_time_reference", .6).WorkflowId);
    }
    [Theory]
    [InlineData("Show our points at this particular day.")]
    [InlineData("Tell me this season's reference for our club.")]
    [InlineData("How are we doing in this view?")]
    [InlineData("Show our record at the selected cutoff.")]
    [InlineData("How have our recent results been?")]
    [InlineData("Show our last three matches.")]
    [InlineData("What changed on 20 September 2026?")]
    public void PositiveCodeScopeIgnoresTimeChoiceAndConfidence(string question)
    {
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        foreach (var probability in new[] { .6, .95 })
        {
            var route = Select(question, intent, probability);
            Assert.Equal(MatchRoutingStatus.Selected, route.Status); Assert.Equal("club_context", route.WorkflowId);
        }
    }
    [Theory]
    [InlineData("Show March form here.")]
    [InlineData("September results at this snapshot.")]
    [InlineData("May results at this cutoff.")]
    [InlineData("Show March form on 20 September 2026.")]
    [InlineData("Monday form here.")]
    [InlineData("Show our form on 20 September 2026 and Monday.")]
    [InlineData("Christmas results at this snapshot.")]
    [InlineData("Easter form here.")]
    [InlineData("New Year's Day results at this cutoff.")]
    public async Task KnownUnresolvedMonthNeverBorrowsPositiveSelectionUnderEitherModelChoice(string question)
    {
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
        {
            Assert.Equal(LeagueTimeResolution.Unresolved, LeagueTimeScope.Resolve("2026-27", "2026-09-20", question, intent == "explicit_time_reference"));
            var route = Select(question, intent, .6); Assert.Equal("other_season", route.WorkflowId);
            foreach (var workflow in new[] { route.WorkflowId!, "club_context", "league_context", "date_changes" })
            {
                var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
                Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Empty(result.Evidence); Assert.Null(result.ResolvedClubId); Assert.Equal(0, reads);
            }
        }
    }
    [Theory]
    [InlineData("May we review our points here?")]
    [InlineData("May we review our points on 20 September 2026?")]
    [InlineData("Can we march up this table here?")]
    [InlineData("We march forward at this snapshot.")]
    [InlineData("Have we won 3 matches here?")]
    public void ModalVerbAndFootballQuantityControlsRemainCommittedScope(string question)
    {
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
            Assert.Equal("club_context", Select(question, intent).WorkflowId);
    }
    [Fact]
    public void WorkflowAndRelevantBindingConfidenceStillAbstainOnPositiveScope()
    {
        var c = Context(); var input = new LeagueRoutingInput("How are we doing here?", new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
        foreach (var probabilities in new[] { (.6, .95), (.95, .6) })
        {
            var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(topicProbability: probabilities.Item1, bindingProbability: probabilities.Item2)));
            Assert.Equal(MatchRoutingStatus.Clarify, DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input).Status);
        }
    }
    [Theory]
    [InlineData("What changed on 19 September 2026?", "other_season")]
    [InlineData("What happened on 01/01/2026?", "other_season")]
    [InlineData("How did we do last year?", "other_season")]
    [InlineData("Who is playing today?", "live_unavailable")]
    public void NegativeCodeScopeCannotBecomePermissionThroughEitherTimeChoice(string question, string expected)
    {
        foreach (var intent in new[] { "selected_view", "explicit_time_reference" })
            Assert.Equal(expected, Select(question, intent, .6).WorkflowId);
    }
    [Theory]
    [InlineData("How are we doing?")]
    [InlineData("What points have we earned?")]
    [InlineData("Show our club's form.")]
    [InlineData("Where do we stand?")]
    [InlineData("What is our position in the table?")]
    [InlineData("What is our league position?")]
    [InlineData("How have we done across the whole league?")]
    [InlineData("Have we won?")]
    [InlineData("Did we play?")]
    [InlineData("Are they ranked?")]
    [InlineData("How did we do?")]
    [InlineData("Show our performance.")]
    public async Task MissingClubPerformanceSubjectCannotAcquireLeagueOrDayFacts(string question)
    {
        foreach (var workflow in new[] { "league_context", "league_table", "date_changes", "club_context" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
        }
    }
    [Theory]
    [InlineData("How are barcelona doing?")]
    [InlineData("What points has barcelona earned?")]
    [InlineData("How are Arsenal and barcelona doing?")]
    [InlineData("Tell me about Arsenal and barcelona.")]
    [InlineData("Has barcelona won?")]
    [InlineData("Did barcelona play?")]
    [InlineData("Are Arsenal and barcelona ranked?")]
    [InlineData("How are our barcelona doing?")]
    [InlineData("Has their barcelona won?")]
    public async Task ErroneousGlobalOrDayWorkflowStillChecksUnknownAndMixedSubjects(string question)
    {
        foreach (var workflow in new[] { "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Empty(result.Evidence); Assert.Null(result.ResolvedClubId); Assert.Equal(1, reads);
        }
    }
    [Theory]
    [InlineData("How are we doing?")]
    [InlineData("How is Arsenal doing?")]
    [InlineData("What is our position in the table?")]
    public async Task ClubQuestionWithKnownSubjectCannotBeAnsweredByWholeTableProjection(string question)
    {
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Empty(result.Evidence); Assert.Null(result.ResolvedClubId); Assert.InRange(reads, 0, 1);
        }
    }
    [Theory]
    [InlineData("How have we done across the whole league?")]
    [InlineData("Show our league position.")]
    public async Task StrongClubSubjectTakesPriorityOverIncidentalLeagueScope(string question)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_context", Context(), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("everton", result.ResolvedClubId);
        var global = await LeagueChatFixtures.Executor().ExecuteAsync("league_context", Context(), question, null, default);
        Assert.Equal(MatchChatOutcome.Clarify, global.Outcome); Assert.Empty(global.Evidence);
    }
    [Theory]
    [InlineData("How are our Arsenal team doing?", "arsenal", MatchChatOutcome.Answered, 3)]
    [InlineData("How are our barcelona team doing?", null, MatchChatOutcome.Clarify, 1)]
    public async Task NamedWorkflowAllowsFreshIdentityValidationWithoutSelectionButNeverGrantsAuthority(string question, string? subject, MatchChatOutcome outcome, int expectedReads)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("named_club_context", Context(null), question, null, default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(subject, result.ResolvedClubId); Assert.Equal(expectedReads, reads);
        if (subject is null) Assert.Empty(result.Evidence); else Assert.Equal(4, result.Evidence.Length);
    }
    [Theory]
    [InlineData("in 2016", LeagueTimeResolution.MatchesSelection)]
    [InlineData("during 2016", LeagueTimeResolution.MatchesSelection)]
    [InlineData("season 2015", LeagueTimeResolution.MatchesSelection)]
    [InlineData("in 2015/16", LeagueTimeResolution.MatchesSelection)]
    [InlineData("season 2016", LeagueTimeResolution.DifferentTime)]
    [InlineData("during 2015", LeagueTimeResolution.DifferentTime)]
    public void CalendarYearAndSeasonStartLabelUseDifferentTrustedAnchors(string reference, LeagueTimeResolution expected)
    {
        foreach (var explicitIntent in new[] { false, true })
            Assert.Equal(expected, LeagueTimeScope.Resolve("2015-16", "2016-01-05", "Show our points " + reference, explicitIntent));
    }
    [Theory]
    [InlineData("Can we see the whole league table?")]
    [InlineData("Can we display the entire league table?")]
    [InlineData("Show our league table.")]
    [InlineData("What changed for all clubs here?")]
    [InlineData("Show the whole league including Arsenal.")]
    public async Task ExplicitLeaguePerspectiveStaysGlobalDespiteSelectionAndPermissionPronouns(string question)
    {
        foreach (var selection in new string?[] { null, "everton" })
        foreach (var workflow in new[] { "league_context", "league_table", "date_changes" })
        {
            var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(selection), question, "arsenal", default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.InRange(reads, 1, 2);
            using var evidence = JsonDocument.Parse(result.Evidence.First(e => e.Id is "table_snapshot" or "date_change").Text);
            Assert.Equal(20, evidence.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
        }
    }
    [Fact]
    public async Task ClubDateProjectionRetainsNamedSubjectAndExactReadBudget()
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("date_changes", Context(), "What points did Arsenal add here?", null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal(3, reads);
        using var evidence = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text);
        Assert.Single(evidence.RootElement.GetProperty("facts").GetProperty("rows").EnumerateArray());
    }
    [Fact]
    public async Task ExpectedTimeIntentIsAnOfflinePlanInputOnlyAndCannotHideLiveUnsafeAnswer()
    {
        var c = Context(); var question = "Show the table at an unnamed time.";
        var item = new ReliabilityCase("offline-only", question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName),
            new("other_season", "other_season", "Clarify", null, [], "context", "unresolved", "explicit_time_reference"));
        var runner = new ReliabilityRunner(c.Version, (season, index, club) => LeagueChatFixtures.Snapshot(index, club));
        var expectedPlan = await runner.ExecuteExpectedPlanAsync(item);
        Assert.True(expectedPlan.Usable); Assert.Equal(0, expectedPlan.Reads); Assert.Null(expectedPlan.BindingCorrect); Assert.Null(expectedPlan.TimeIntentCorrect);
        var decision = EvaluationDecisionParser.Parse(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice("league_context")))!;
        var actual = await runner.ExecuteAsync(item, new(MatchRoutingStatus.Selected, "league_context"), decision);
        Assert.Equal(MatchChatOutcome.Answered, actual.Execution!.Outcome); Assert.Equal("selected_snapshot", actual.ResolvedTimeScope);
        Assert.True(actual.UnsafeTime); Assert.True(actual.UnsafeUnexpectedAnswer); Assert.False(actual.TimeIntentCorrect); Assert.False(actual.Usable);
        var missingDiagnostic = await runner.ExecuteAsync(item, new(MatchRoutingStatus.Selected, "league_context"));
        Assert.True(missingDiagnostic.UnsafeTime); Assert.Null(missingDiagnostic.TimeIntentCorrect);
        var score = new ReliabilityScore(60); score.Register(item, actual); Assert.Equal(1, score.UnsafeAnswers); Assert.False(score.MeetsTarget);
    }
    private static LeagueChatContext Context(string? club = "everton") => LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: club));
    private static MatchRoutingResult Select(string question, string intent, double probability = .95)
    {
        var c = Context(); var input = new LeagueRoutingInput(question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), []);
        var decision = DecisionLeagueWorkflowRouter.ParseChoices(Encoding.UTF8.GetBytes(LeagueChatTransportTests.Choice(scope: intent, scopeProbability: probability)));
        return DecisionLeagueWorkflowRouter.Select(decision, new DecisionRoutingOptions(), input);
    }
}
