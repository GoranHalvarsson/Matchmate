using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

// F-SOL-01: fixture context and a selected club's earlier results have separate citation authority.
// These synthetic fixtures run in CI without any ignored public-model capture dependency.
public sealed class HistoricalChatCitationScopeTests
{
    private const string Prefix = "Before the Stoke City fixture on 2017-05-21, ";
    private const string Sample = "Southampton had 1 win and 1 draw. Southampton scored 3 goals in the earlier result sample.";
    private static HistoricalPrepared Prepare()
        => HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(),
            [.. HistoricalChatFixtures.Clubs, HistoricalChatFixtures.Club("everton", "Everton", "EVE", "Unrelated ground")], "overview", "southampton");

    [Fact]
    public void SelectedOpponentContextNeedsItsFixtureCitationEvenWhenTheFormClaimsAreCorrect()
    {
        var prepared = Prepare(); var answer = Prefix + Sample;
        var raw = HistoricalChatFixtures.Reply(answer, "form");
        Assert.Equal(answer, MatchReplyValidation.Validate(raw, prepared.Evidence.ToDictionary(e => e.Id)).Answer);
        var error = Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(raw, prepared));
        Assert.Equal("Uncited club identity.", error.Message);
        var accepted = HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form", "fixture"), prepared);
        Assert.Equal(answer, accepted.Answer);
        Assert.Equal(new[] { "form", "fixture" }, accepted.Evidence.Select(e => e.Id));
    }

    [Fact]
    public void RemovingOnlyUnusedFixtureContextAllowsTheAnswerToCiteJustForm()
    {
        var accepted = HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(Sample, "form"), Prepare());
        Assert.Equal(Sample, accepted.Answer); Assert.Equal("form", Assert.Single(accepted.Evidence).Id);
    }

    [Fact]
    public void AddingTheFixtureCitationCannotAuthorizeAnUnrelatedClub()
    {
        var answer = Prefix.Replace("Stoke City", "Everton", StringComparison.Ordinal) + Sample;
        var prepared = Prepare(); var raw = HistoricalChatFixtures.Reply(answer, "form", "fixture");
        Assert.Equal(answer, MatchReplyValidation.Validate(raw, prepared.Evidence.ToDictionary(e => e.Id)).Answer);
        Assert.Equal("Uncited club identity.", Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(raw, prepared)).Message);
    }

    [Theory]
    [InlineData("Southampton scored 1 goal in the earlier result sample.")]
    [InlineData("Southampton scored 3 goals in this fixture.")]
    [InlineData("Crouch scored 1 goal.")]
    [InlineData("Southampton scored 3 goals from corners in the earlier result sample.")]
    public void CompleteCitationsStillCannotAuthorizeWrongCountsScopePlayersOrMechanisms(string claim)
    {
        var prepared = Prepare(); var raw = HistoricalChatFixtures.Reply(Prefix + claim, "form", "fixture");
        MatchReplyValidation.Validate(raw, prepared.Evidence.ToDictionary(e => e.Id));
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(raw, prepared));
    }
}
