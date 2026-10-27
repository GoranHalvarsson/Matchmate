using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

// REV-1 regressions for F-HIST-02, F-HIST-03 and F-HIST-04; routing quality is separately measured with the real provider.
public sealed class HistoricalChatRevisionTests
{
    [Theory]
    [InlineData("Home form?", "southampton")]
    [InlineData("Away form?", "stoke-city")]
    [InlineData("Both form?", null)]
    [InlineData("Compare form and table.", null)]
    public void RoleAndComparisonWordsAreNotUnknownClubIdentities(string question, string? expected)
    {
        var result = HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, null, "form");
        Assert.Null(result.Clarification); Assert.Equal(expected, result.ClubId);
    }
    [Theory]
    [InlineData("Home and Barcelona?")]
    [InlineData("Compare Southampton and Chelsea form")]
    [InlineData("Ajax form?")]
    public void RoleHandlingRetainsOutsideFixtureAndUnknownClubSafeguards(string question) => Assert.NotNull(
        HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, null, "form").Clarification);
    [Theory]
    [InlineData("Southampton scored 3 goals in their earlier results.", "form", "southampton")]
    [InlineData("Southampton had scored only 3 goals in the previous sample.", "form", "southampton")]
    [InlineData("They scored 3 goals before this fixture.", "form", "southampton")]
    [InlineData("Stoke City scored 1 goal in this match.", "fixture", null)]
    [InlineData("Stoke scored 1 goal.", "fixture", null)]
    public void ExplicitTeamGoalsRequireExactCalculatedCountScopeAndCitation(string answer, string id, string? club)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", club);
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, id), prepared).Answer);
    }
    [Theory]
    [InlineData("Southampton scored 3 goals in this match.", "fixture")]
    [InlineData("Southampton scored 1 goal in their earlier results.", "form")]
    [InlineData("Southampton scored 3 goals in their earlier results.", "fixture")]
    [InlineData("Crouch scored 1 goal.", "fixture")]
    [InlineData("Stoke scored through Crouch, not Arnautovic.", "fixture")]
    [InlineData("Southampton pressed high without letting Stoke settle.", "fixture")]
    [InlineData("No scorers are supplied, but Crouch scored 1 goal.", "fixture")]
    [InlineData("Southampton scored 3 goals through Crouch in their earlier results.", "form")]
    [InlineData("Southampton lost at St Mary's Stadium, not Stamford Bridge.", "reference")]
    public void NegationCannotExcuseAnUnsupportedScorerActionVenueOrWrongTeamTotal(string answer, string id)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, id), prepared));
    }
    [Theory]
    [InlineData("No scorers or lineups are supplied.")]
    [InlineData("Tactical evidence is not supplied.")]
    [InlineData("I cannot tell who scored.")]
    [InlineData("This record does not show how the goals were scored.")]
    [InlineData("This source does not include stadium history.")]
    [InlineData("No corners or headers are supplied.")]
    [InlineData("This record does not provide corner information.")]
    public void NarrowCoveragePredicatesCanExplainMissingDetail(string answer)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "fixture"), prepared).Answer);
    }
    [Fact]
    public void GroupedContextRetainsExplicitParagraphSampleScopeForSourcedPronounTotals()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        var answer = "In the earlier results in this sample, Southampton had 1 win and 1 draw. They scored 3 goals and conceded 2.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared).Answer);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply("Southampton had earlier results. They scored 3 goals in this match.", "form"), prepared));
        var pair = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), pair).Answer);
    }
    [Fact]
    public void GroupedContextDoesNotCarryAnEarlierSampleScopeAcrossParagraphsOrFabricateScorers()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        foreach (var answer in new[] { "Southampton had earlier results.\n\nThey scored 3 goals.", "Southampton had earlier results. They scored 3 goals through Crouch, not Arnautovic." })
            Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared));
    }
    [Fact]
    public async Task GroupedProductionRouteKeepsQuestionIntentAndTrustedClubAcrossFollowup()
    {
        var auth = new HistoricalChatSessionTests.Auth(); var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalChatSessionTests.Model();
        using var session = new HistoricalChatSession(auth, HistoricalChatFixtures.Evidence(), router, model, Microsoft.Extensions.Options.Options.Create(new Matchmate.Web.Chat.MatchChatOptions()));
        var context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture()); session.SetContext(context);
        await session.AskAsync(context, "How were Southampton doing before the game?");
        await session.AskAsync(context, "And Stoke?");
        Assert.Equal("stoke-city", session.Turns[1].ClubId); Assert.Equal("overview", router.Inputs[1].Topic!.Workflow);
        Assert.Equal("How were Southampton doing before the game?", router.Inputs[1].PreviousQuestions[0].Question);
        Assert.Contains("form", model.Requests[1].AllowedEvidenceIds); Assert.Contains("table", model.Requests[1].AllowedEvidenceIds);
        Assert.Contains("Bet365 Stadium", model.Requests[1].Messages.Single(m => m.Content.StartsWith("Server-prepared evidence", StringComparison.Ordinal)).Content);
    }
    // REV-3: F-HIST05 scope precedence and F-HIST06 unsupported mechanism qualifiers.
    [Theory]
    [InlineData("Compared with their earlier results, Southampton scored 3 goals in this fixture.")]
    [InlineData("Southampton scored 3 goals in this match, compared with their earlier results.")]
    [InlineData("In contrast to the previous sample, Southampton scored 3 goals during this game.")]
    [InlineData("Southampton had earlier results; during this fixture they scored 3 goals.")]
    [InlineData("Southampton scored 3 goals from corners in their earlier results.")]
    [InlineData("Southampton scored 3 goals from a corner in their earlier results.")]
    [InlineData("Southampton scored 3 goals via headers in their earlier results.")]
    [InlineData("Southampton scored 3 goals via set pieces in their earlier results.")]
    [InlineData("Southampton scored 3 goals from open play in their earlier results.")]
    [InlineData("Southampton scored 3 goals off crosses in their earlier results.")]
    public void FixtureScopeOverridesComparisonAndAggregateCountsDoNotAuthorizeMechanisms(string answer)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form", "fixture"), prepared));
    }
    [Fact]
    public void ExplicitFixtureScopeStillAcceptsItsActualGoalCountBesideEarlierComparison()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        var answer = "Compared with their earlier results, Southampton scored 0 goals in this fixture.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form", "fixture"), prepared).Answer);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task AllFiveSavedActualPositiveRepliesStillValidateAgainstTheirPinnedFixtureAndClub()
    {
        var root = Matchmate.Infrastructure.LocalPaths.FindRoot();
        var model = await Matchmate.Infrastructure.LeagueReferenceStore.Local(root).ReadAsync(default);
        using var capture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".azure", "historical-chat", "evaluation-rev2-final.json")));
        var replies = capture.RootElement.GetProperty("outcomes").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "session"
            && item.GetProperty("outcome").GetString() == "Answered").ToArray();
        Assert.Equal(5, replies.Length);
        foreach (var item in replies)
        {
            var context = System.Text.Json.JsonSerializer.Deserialize<HistoricalChatContext>(item.GetProperty("context").GetRawText())!;
            var fixture = model.Match(context.SeasonKey, context.MatchKey);
            Assert.Equal(context, HistoricalChatContext.From(fixture));
            var prepared = HistoricalEvidence.Prepare(fixture, model.Snapshot(context.SeasonKey, null).Clubs, "overview", item.GetProperty("clubId").GetString());
            var validated = HistoricalReplyValidation.Validate(item.GetProperty("rawNarratorReply").GetString()!, prepared);
            Assert.Equal(item.GetProperty("answer").GetString(), validated.Answer);
            Assert.Equal(item.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()), validated.Evidence.Select(e => e.Id));
        }
    }
}
