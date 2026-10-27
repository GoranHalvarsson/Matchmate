using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Experience;
using Xunit;

namespace Matchmate.Tests;

public sealed partial class MatchGoalVisualTests
{
    // Authored replies test the production request/validation/commit contract, not live model voice quality.
    public static TheoryData<string, bool, string> ExpertVoiceGoalReplies
    {
        get
        {
            var cases = new TheoryData<string, bool, string>();
            foreach (var visual in new[] { false, true })
            {
                cases.Add("goal-1", visual, "For the goal you picked, Damsgaard's corner was headed across goal by Schuster, and Anthony finished with a close-range header.\n\nMy reading is that the second ball made the far side the key danger. The reports do not establish an individual defensive mistake.");
                cases.Add("goal-2", visual, "For the goal you picked, Damsgaard released Schade, Martinez saved Schade's shot, and Thiago scored from the rebound.\n\nThe danger with that sequence is having to defend another chance before the play is settled. That is a football reading, not proof of which defender was responsible.");
                cases.Add("goal-3", visual, "For the goal you picked, Schade was identified at the throw-in setup and later with the ball before his reported cross. Carvalho finished at the far post; Brentford alone reports a slight Acheampong deflection.\n\nMy reading is that a cross changing direction can make the far side harder to protect. The return passer and exact contacts remain unclear.");
            }
            return cases;
        }
    }

    [Theory] [MemberData(nameof(ExpertVoiceGoalReplies))]
    public async Task ExpertVoice_NaturalGoalRepliesUseUnchangedPreparedEvidenceAndCommitWithOrWithoutDiagram(string goalId, bool showVisual, string answer)
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        var goal = end.Events.Single(r => r.RecordId == goalId);
        var release = dataset.Schedule.Releases.Single(r => r.RecordId == goalId).CursorMs;
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, release - 15000));
        var selection = new MatchSelectedEvent(goalId, release, goal.FootballTime!.ReportedClock!);
        using var h = new Harness(view); h.Api.Read = q => ledger.Query(q); h.Set(new(view.Context, dataset.HeadSequence, selection));
        h.Model.Wait = () => Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "selected_goal" } }), []));
        var question = showVisual ? "Show me how this goal happened" : "Why did Chelsea let in a goal here, what did they do wrong?";

        Assert.True((await h.Ask(question)).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.False(turn.EvidenceOnly); Assert.Equal(answer, turn.Answer);
        Assert.Equal(question, turn.Question); Assert.True(turn.SelectedEventScope); Assert.Equal(view.Context, turn.Context.Query);
        Assert.Equal(showVisual, turn.EventVisual is not null);
        if (turn.EventVisual is { } visual)
            Assert.Equal(((MatchEventPayload)goal.Payload).Sequence!.Steps.Select(s => s.Text), visual.Steps.Select(s => s.Text));
        Assert.Equal(1, h.Model.Calls); Assert.Equal(new[] { view.Context.CursorMs, release }, h.Api.Queries.Select(q => q.CursorMs));

        var request = h.Model.Request!; Assert.Equal(question, Assert.Single(request.Messages, m => m.Role == "user").Content);
        Assert.StartsWith("match-commentator-v12\n", request.Messages[0].Content);
        Assert.Contains("clearly qualified football", request.Messages[0].Content);
        Assert.Contains("never imply it already happened at the current cursor", request.Messages[0].Content);
        Assert.DoesNotContain("with its reported clock and replay release", request.Messages[0].Content);
        Assert.Equal(showVisual, request.Messages.Any(m => m.Content == CommentatorPrompt.EventVisualPresentation));
        Assert.Equal(new[] { "selected_goal" }, turn.Evidence.Select(e => e.Id));
        var bundle = ExpertVoiceBundle(request); Assert.Equal("selected_goal_event", bundle.WorkflowId);
        Assert.Equal(MatchWorkflowTemporalMode.SelectedGoalRetrospective, bundle.TemporalMode);
        Assert.Equal(new MatchEvidenceProjection(view, compactEventSources: true).SelectedGoal(goal, selection).Text, bundle.Items.Single(i => i.Id == "selected_goal").Text);
        foreach (var evidence in goal.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent))
        {
            var full = Assert.Single(bundle.EventProvenance, p => p.RecordId == goal.RecordId && p.Revision == goal.Revision
                && p.EvidenceId == evidence.EvidenceId && p.SourceId == evidence.SourceId);
            Assert.Equal(evidence.Origin, full.Origin); Assert.Equal(evidence.Method, full.Method); Assert.Equal(evidence.ClaimStatus, full.ClaimStatus);
            Assert.Equal(evidence.AvailableAtUtc, full.AvailableAtUtc); Assert.Equal(goal.FootballTime, full.FootballTime);
        }
        Assert.Equal(new MatchEvidenceProjection(view).Get("match_summary").Text, bundle.Items.Single(i => i.Id == "match_summary").Text);
        using var selected = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "selected_goal").Text);
        Assert.False(selected.RootElement.GetProperty("alreadyEligibleAtCurrentCursor").GetBoolean());
        Assert.DoesNotContain("replay release", answer); Assert.DoesNotContain("formal assist", answer);
    }

    [Fact]
    public async Task ExpertVoice_ExplicitTimeQuestionRetainsExactSuppliedReportedReleaseAndAskedAtClocks()
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        var goal = end.Events.Single(r => r.RecordId == "goal-2");
        var release = dataset.Schedule.Releases.Single(r => r.RecordId == goal.RecordId).CursorMs;
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, release - 15000));
        using var h = new Harness(view); h.Api.Read = q => ledger.Query(q);
        h.Set(new(view.Context, dataset.HeadSequence, new(goal.RecordId, release, goal.FootballTime!.ReportedClock!)));
        string? authoredAnswer = null;
        h.Model.Wait = () =>
        {
            var bundle = ExpertVoiceBundle(h.Model.Request!);
            using var selected = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "selected_goal").Text);
            var data = selected.RootElement;
            Assert.Equal(ExperiencePresentation.Clock(release), data.GetProperty("replayRelease").GetString());
            Assert.Equal(ExperiencePresentation.Clock(view.Context.CursorMs), data.GetProperty("currentReplay").GetString());
            Assert.Equal(goal.FootballTime.ReportedClock, data.GetProperty("selectedEvent").GetProperty("reportedClock").GetString());
            authoredAnswer = $"The goal you picked is reported at {goal.FootballTime.ReportedClock}. Its replay release is {data.GetProperty("replayRelease").GetString()}; you asked at {data.GetProperty("currentReplay").GetString()}, before that release.";
            return Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer = authoredAnswer, evidenceIds = new[] { "selected_goal" } }), []));
        };
        const string question = "When is the goal I picked reported, and what are its replay release and asked-at clocks?";
        Assert.True((await h.Ask(question)).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.False(turn.EvidenceOnly); Assert.Equal(authoredAnswer, turn.Answer);
        Assert.Null(turn.EventVisual); Assert.Equal(1, h.Model.Calls); Assert.Equal(2, h.Api.Queries.Count);
        Assert.Equal(question, Assert.Single(h.Model.Request!.Messages, m => m.Role == "user").Content);
    }

    [Fact]
    public async Task ExpertVoice_CurrentScoreStaysAtCursorDespiteLaterSelectedGoal()
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var goalRelease = dataset.Schedule!.Releases.Single(r => r.RecordId == "goal-3").CursorMs;
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, goalRelease - 15000));
        using var h = new Harness(view); h.Api.Read = q => ledger.Query(q); h.Router.Route = "current_match_summary";
        h.Set(new(view.Context, dataset.HeadSequence, new("goal-3", goalRelease, "90+1")));
        using var expected = JsonDocument.Parse(new MatchEvidenceProjection(view).Get("match_summary").Text);
        var score = expected.RootElement;
        var answer = $"The reported score here is {score.GetProperty("homeReportedGoals").GetInt32()}-{score.GetProperty("awayReportedGoals").GetInt32()}.";
        h.Model.Wait = () => Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "match_summary" } }), []));
        Assert.True((await h.Ask("What is the score here?")).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.False(turn.EvidenceOnly); Assert.Equal(answer, turn.Answer);
        Assert.False(turn.SelectedEventScope); Assert.Null(turn.EventVisual);
        Assert.Equal(view.Context.CursorMs, Assert.Single(h.Api.Queries).CursorMs); Assert.Equal(1, h.Model.Calls);
        var bundle = ExpertVoiceBundle(h.Model.Request!); Assert.Equal(MatchWorkflowTemporalMode.ReplayAtCursor, bundle.TemporalMode);
        var currentSummary = bundle.Items.Single(i => i.Id == "match_summary").Text;
        Assert.Equal(new MatchEvidenceProjection(view).Get("match_summary", includeReportSummary: true).Text, currentSummary);
        Assert.DoesNotContain("goal-3", currentSummary);
        Assert.DoesNotContain(bundle.Items, i => i.Id is "selected_goal" or "selected_event");
        Assert.DoesNotContain(h.Model.Request!.Messages, m => m.Content == CommentatorPrompt.EventVisualPresentation);
    }

    [Theory]
    [InlineData("My reading is that he reached 20 km/h.", "selected_goal", MatchReplyRejection.UnsupportedMeasurement)]
    [InlineData("The danger with that sequence is the 999 goals conceded.", "selected_goal", MatchReplyRejection.UnsupportedNumber)]
    [InlineData("The reports describe a corner and header.", "club-sequence", MatchReplyRejection.UnknownEvidence)]
    public async Task ExpertVoice_NaturalQualificationNeverRelaxesNumericOrCitationGuards(string answer, string citation, MatchReplyRejection expected)
    {
        var source = await GoalNarrationSource(); MatchReplyRejection? reason = null;
        Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { citation } }),
            new Dictionary<string, MatchChatEvidence> { [source.Id] = source }, rejection => reason = rejection));
        Assert.Equal(expected, reason);
    }

    private static MatchWorkflowBundle ExpertVoiceBundle(MatchNarratorRequest request)
    {
        var prepared = Assert.Single(request.Messages, m => m.Content.StartsWith("Prepared app-computed"));
        return JsonSerializer.Deserialize<MatchWorkflowBundle>(prepared.Content[(prepared.Content.IndexOf('\n') + 1)..])!;
    }
}
