using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

public sealed partial class MatchGoalVisualTests
{
    [Theory] [InlineData("six-yard area")] [InlineData("six-yard box")]
    public async Task GoalNarration_StandardGoalAreaLandmarkPassesWithActualSelectedEvidence(string landmark)
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        var record = end.Events.Single(r => r.RecordId == "goal-1");
        Assert.Contains("six-yard area", string.Join(' ', ((MatchEventPayload)record.Payload).Details!.Select(d => d.Text)));
        var release = dataset.Schedule.Releases.Single(r => r.RecordId == record.RecordId).CursorMs;
        var selection = new MatchSelectedEvent(record.RecordId, release, record.FootballTime!.ReportedClock!);
        var source = new MatchEvidenceProjection(end).SelectedGoal(record, selection);
        var answer = "The reports describe Damsgaard's corner, Schuster's header across goal and Anthony's close-range header at the far post. "
            + $"The reviewed samples describe action across the {landmark}. Football interpretation: a second ball near goal can test the far side. "
            + "The samples do not independently establish every contact.";
        MatchReplyRejection? rejection = null;
        var failure = Record.Exception(() => MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "selected_goal" } }),
            new Dictionary<string, MatchChatEvidence> { [source.Id] = source }, reason => rejection = reason));
        Assert.True(failure is null, $"Source-supported standard field-region narration was rejected: {rejection}. Source: event-context-v2.json goal-1 reviewed_observation, six-yard area; regression wording is developer-authored, not the missing live reply.");
    }

    [Theory]
    [InlineData("goal-1", "For this selected goal, the reports describe Damsgaard's corner, Schuster heading across goal and Anthony finishing with a close-range header at the far post. Reviewed samples show action crossing the six-yard area. Football interpretation: the second ball can test far-post cover. The samples do not independently identify every contact.")]
    [InlineData("goal-2", "For this selected goal, the reports describe Damsgaard releasing Schade, Schade shooting, Martinez saving and Thiago finishing the rebound. Football interpretation: a rebound gives defenders another ball to protect near goal. The wide samples do not independently establish every contact or a formal assist.")]
    [InlineData("goal-3", "For this selected goal, the sequence places Schade at the throw-in and action into the area, followed by his cross and Carvalho's far-post finish. Brentford alone reports the slight Acheampong deflection. Football interpretation: a changed cross can make far-post cover harder. The return passer, every contact and any formal assist remain unestablished.")]
    public async Task GoalNarration_AllThreeGroundedSequenceRepliesCommitAsAiWithSameSharedVisual(string goalId, string answer)
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        var goal = end.Events.Single(r => r.RecordId == goalId); var release = dataset.Schedule.Releases.Single(r => r.RecordId == goalId).CursorMs;
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, release - 15000));
        using var h = new Harness(view); h.Api.Read = q => ledger.Query(q);
        h.Set(new(view.Context, dataset.HeadSequence, new(goalId, release, goal.FootballTime!.ReportedClock!)));
        h.Model.Wait = () => Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "selected_goal" } }), []));
        const string question = "Show me how this goal happened";
        var result = await h.Ask(question); Assert.True(result.Accepted); Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        var turn = Assert.Single(h.Session.Turns); Assert.False(turn.EvidenceOnly); Assert.Equal(answer, turn.Answer); Assert.Equal(question, turn.Question); Assert.True(turn.SelectedEventScope);
        var visual = Assert.IsType<MatchEventVisual>(turn.EventVisual); Assert.Equal(goalId, visual.EventId);
        Assert.Equal(((MatchEventPayload)goal.Payload).Sequence!.Steps.Select(s => s.Text), visual.Steps.Select(s => s.Text));
        Assert.Equal(1, h.Model.Calls); Assert.Equal(2, h.Api.Queries.Count); Assert.Equal(release, h.Api.Queries[1].CursorMs);
        Assert.Single(turn.Evidence, e => e.Id == "selected_goal"); Assert.Contains("match-commentator-v12", h.Model.Request!.Messages[0].Content);
        Assert.Contains(h.Model.Request.Messages, m => m.Content == CommentatorPrompt.EventVisualPresentation);
        var prepared = h.Model.Request.Messages.Single(m => m.Content.StartsWith("Prepared app-computed"));
        var bundle = JsonSerializer.Deserialize<MatchWorkflowBundle>(prepared.Content[(prepared.Content.IndexOf('\n') + 1)..])!;
        using var score = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "match_summary").Text);
        Assert.Equal(view.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "goal", TeamId: var team } && team == dataset.Fixture!.Home.Id), score.RootElement.GetProperty("homeReportedGoals").GetInt32());
    }

    public static TheoryData<string, MatchReplyRejection> GoalNarrationUnsafeClaims => new() {
        { "He reached 20 km/h.", MatchReplyRejection.UnsupportedMeasurement },
        { "He covered six yards.", MatchReplyRejection.UnsupportedMeasurement },
        { "He made a six-yard pass.", MatchReplyRejection.UnsupportedMeasurement },
        { "He finished with a six-yard header.", MatchReplyRejection.UnsupportedMeasurement },
        { "The header was six yards.", MatchReplyRejection.UnsupportedMeasurement },
        { "The box measured six yards.", MatchReplyRejection.UnsupportedMeasurement },
        { "His xG was 0.2.", MatchReplyRejection.UnsupportedMeasurement },
        { "They completed six passes.", MatchReplyRejection.UnsupportedMeasurement },
        { "Possession was 60%.", MatchReplyRejection.UnsupportedMeasurement },
        { "Their formation was 4-4-2.", MatchReplyRejection.UnsupportedMeasurement },
        { "There were 999 goals.", MatchReplyRejection.UnsupportedNumber },
        { "<b>Reported header</b>", MatchReplyRejection.AnswerContent }
    };

    [Theory] [MemberData(nameof(GoalNarrationUnsafeClaims))]
    public async Task GoalNarration_LandmarkAndCaveatNeverExcuseOtherUnsafeClaims(string claim, MatchReplyRejection expected)
    {
        var source = await GoalNarrationSource();
        foreach (var caveat in new[] { "", "Speed is unavailable. " })
        {
            var answer = "The reviewed sequence crosses the six-yard area. " + caveat + claim;
            MatchReplyRejection? rejection = null;
            Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "selected_goal" } }),
                new Dictionary<string, MatchChatEvidence> { [source.Id] = source }, reason => rejection = reason));
            Assert.Equal(expected, rejection);
        }
    }

    [Theory]
    [InlineData("twenty-six-yard area")] [InlineData("sixty-six-yard box")] [InlineData("hundred six-yard box")]
    [InlineData("-six-yard area")] [InlineData("+six-yard box")] [InlineData("minus six-yard area")]
    [InlineData("6.5-yard area")] [InlineData("-6.5-yard box")] [InlineData("106-yard box")] [InlineData("6-yard box")]
    [InlineData("0.six-yard area")] [InlineData("six-yard boxes")] [InlineData("six-yard area2")]
    [InlineData("six-yard box-long pass")] [InlineData("six-yard-box header")]
    public async Task GoalNarration_LandmarkExceptionHasStrictWordQuantityAndCompoundBoundaries(string phrase)
    {
        var source = await GoalNarrationSource(); MatchReplyRejection? rejection = null;
        Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer = "The report describes a " + phrase + ".", evidenceIds = new[] { "selected_goal" } }),
            new Dictionary<string, MatchChatEvidence> { [source.Id] = source }, reason => rejection = reason));
        Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, rejection);
    }

    [Theory] [InlineData("club-sequence")] [InlineData("invented-citation")]
    public async Task GoalNarration_LandmarkStillRequiresCurrentTopLevelCitation(string citation)
    {
        var source = await GoalNarrationSource(); MatchReplyRejection? rejection = null;
        Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer = "The reviewed action crosses the six-yard area.", evidenceIds = new[] { citation } }),
            new Dictionary<string, MatchChatEvidence> { [source.Id] = source }, reason => rejection = reason));
        Assert.Equal(MatchReplyRejection.UnknownEvidence, rejection);
    }

    [Fact]
    public async Task GoalNarration_TrueMeasurementRejectionKeepsSourceFallbackAndNeverRetries()
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, 3765000)); using var h = new Harness(view); h.Api.Read = q => ledger.Query(q);
        h.Set(new(view.Context, dataset.HeadSequence, new("goal-1", 3780000, "61")));
        const string unsafeAnswer = "The reports describe action in the six-yard box and a six-yard header.";
        h.Model.Wait = () => Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer = unsafeAnswer, evidenceIds = new[] { "selected_goal" } }), []));
        Assert.True((await h.Ask("Show me how this goal happened")).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.True(turn.EvidenceOnly); Assert.NotNull(turn.EventVisual); Assert.NotEqual(unsafeAnswer, turn.Answer);
        Assert.Contains(turn.Evidence, e => e.Id == "selected_goal"); Assert.Equal(1, h.Model.Calls); Assert.Equal(2, h.Api.Queries.Count);
    }

    private static async Task<MatchChatEvidence> GoalNarrationSource()
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        return new MatchEvidenceProjection(end).SelectedGoal(end.Events.Single(r => r.RecordId == "goal-1"), new("goal-1", 3780000, "61"));
    }
}
