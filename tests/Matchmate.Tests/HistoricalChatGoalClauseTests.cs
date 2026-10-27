using Matchmate.Contracts;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatGoalClauseTests
{
    private static LeagueHistoricalMatch Fixture(bool homeZero = true, bool equal = false)
    {
        var fixture = HistoricalChatFixtures.Fixture();
        LeagueMatch[] Form(string club, bool zero) => [
            HistoricalChatFixtures.Game(club, "chelsea", "2017-05-01", zero ? 0 : 4, zero ? 1 : 0),
            HistoricalChatFixtures.Game("arsenal", club, "2017-05-08", zero ? 2 : 4, zero ? 0 : 3),
            HistoricalChatFixtures.Game(club, "arsenal", "2017-05-15", zero ? 0 : 1, zero ? 2 : 1)];
        return fixture with { HomePreMatchForm = Form(fixture.Home.Id, homeZero || equal), AwayPreMatchForm = Form(fixture.Away.Id, !homeZero || equal) };
    }
    private static HistoricalPrepared Prepare(bool homeZero = true, bool equal = false, string? club = null)
        => HistoricalEvidence.Prepare(Fixture(homeZero, equal), HistoricalChatFixtures.Clubs, "overview", club);
    private static void Accept(string answer, HistoricalPrepared? prepared = null, params string[] evidence)
        => Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, evidence.Length == 0 ? ["form"] : evidence), prepared ?? Prepare()).Answer);
    private static void Reject(string answer, HistoricalPrepared? prepared = null, params string[] evidence)
        => Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, evidence.Length == 0 ? ["form"] : evidence), prepared ?? Prepare()));
    private static string Words(string text) => text.Replace("HOME", HistoricalChatFixtures.Home.Name, StringComparison.Ordinal).Replace("AWAY", HistoricalChatFixtures.Away.Name, StringComparison.Ordinal);

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void EachFixtureTeamCanBindZeroFiniteGerundAndComparativeGoalClauses(bool homeZero)
    {
        var zero = homeZero ? HistoricalChatFixtures.Home : HistoricalChatFixtures.Away;
        var eight = homeZero ? HistoricalChatFixtures.Away : HistoricalChatFixtures.Home;
        var prepared = Prepare(homeZero);
        foreach (var prefix in new[] { "", "Before the match, ", "The earlier sample: " })
        {
            Accept($"{prefix}{zero.Name} had lost all 3 earlier results in the sample without scoring.", prepared);
            Accept($"{prefix}{zero.Name} had lost all 3 results in the sample, earning 0 points and scoring 0 goals.", prepared);
            Accept($"{prefix}{eight.Name} had taken 4 points and scored 8 goals across the earlier results.", prepared);
            Accept($"{prefix}{eight.Name} had 1 win, 1 draw and 1 loss, earning 4 points and scoring 8 goals in its sample.", prepared);
            Accept($"{prefix}{eight.Name} therefore came in with the stronger recent results and more goals scored in that sample.", prepared);
        }
    }

    [Theory]
    [InlineData("; ")][InlineData(", while ")][InlineData(" and ")][InlineData(": ")]
    public void MultipleExplicitTeamPredicatesBindTheirOwnTotalsAtDeclaredBoundaries(string boundary)
    {
        Accept(Words("HOME had lost all 3 earlier results in the sample without scoring" + boundary + "AWAY had taken 4 points and scored 8 goals across theirs."));
        Accept(Words("AWAY had taken 4 points and scored 8 goals across the earlier results"
            + boundary + "HOME had lost all 3 results in the sample, earning 0 points and scoring 0 goals."));
    }

    [Theory]
    [InlineData("HOME had taken 0 points and scored 8 goals across the earlier results.")]
    [InlineData("AWAY had taken 4 points and scored 0 goals across the earlier results.")]
    [InlineData("AWAY had 1 win, 1 draw and 1 loss, earning 4 points and scoring 5 goals in its sample.")]
    [InlineData("AWAY had lost all 3 earlier results in the sample without scoring.")]
    [InlineData("Compared with earlier results, AWAY had taken 4 points and scored 8 goals in this fixture.")]
    [InlineData("AWAY had taken 4 points and scored 8 goals in the earlier sample during this fixture.")]
    public void ClauseGoalsRequireTheirOwnExactTeamCountAndExistingScope(string text) => Reject(Words(text));

    [Theory]
    [InlineData("fixture")][InlineData("table")]
    public void AGoalSampleCannotBorrowAnotherEvidenceGroupsNumbers(string evidence)
        => Reject(Words("HOME had taken 0 points and scored 0 goals in the earlier sample."), null, evidence);

    [Theory]
    [InlineData("HOME had taken 0 points and scored 0 goals across the earlier results by Crouch.")]
    [InlineData("HOME had lost all 3 results in the sample, earning 0 points and scoring 0 goals through Crouch, not Arnautovic.")]
    [InlineData("HOME had lost all 3 results in the sample, earning 0 points and scoring 0 goals from corners.")]
    [InlineData("HOME had lost all 3 results in the sample, scoring 0 goals by Crouch.")]
    [InlineData("HOME scored 0 goals by Crouch in the earlier sample.")]
    [InlineData("HOME had lost all 3 earlier results in the sample without scoring via set pieces.")]
    [InlineData("HOME had lost all 3 earlier results in the sample without scoring, according to Crouch.")]
    [InlineData("AWAY had the stronger recent results and more goals scored by Crouch in that sample.")]
    public void AVerifiedPrefixCannotAuthorizeAPlayerMechanismOrUnrelatedTail(string text) => Reject(Words(text));

    [Theory]
    [InlineData("HOME’s striker had lost all 3 results in the sample, earning 0 points and scoring 0 goals.")]
    [InlineData("HOME's Crouch had lost all 3 earlier results in the sample without scoring.")]
    [InlineData("HOME Crouch had taken 0 points and scored 0 goals in the earlier sample.")]
    [InlineData("HOME and AWAY had taken 4 points and scored 8 goals in the earlier sample.")]
    [InlineData("Chelsea had taken 0 points and scored 0 goals in the earlier sample.")]
    [InlineData("Scoring 0 goals in the earlier sample, HOME had 0 points.")]
    [InlineData("They had taken 4 points and scored 8 goals in the earlier sample.")]
    [InlineData("HOME had lost all 3 results in the sample, earning 0 points and their striker scoring 0 goals.")]
    public void OnlyADirectUnambiguousSelectedTeamPredicateCanUseTheNewAllowance(string text) => Reject(Words(text));

    [Theory]
    [InlineData("Southampton's striker")][InlineData("Southampton Crouch")][InlineData("Crouch")][InlineData("Southampton's striker,")]
    public void ALeadingNounConjunctionCannotEstablishAnIndependentTeamPredicate(string subject)
        => Reject($"{subject} and Stoke City had lost all 3 results in the sample, scoring 0 goals.", Prepare(homeZero: false));

    [Fact]
    public void TheLegacyFiniteMaskCannotTreatAPlayerTeamConjunctionAsOneTeam()
        => Reject("Southampton's striker and Stoke City scored 0 goals in the earlier sample.", Prepare(homeZero: false));

    [Theory]
    [InlineData("Southampton's striker or ")][InlineData("Crouch or ")]
    [InlineData("Southampton's striker, ")][InlineData("Crouch, ")]
    [InlineData("Southampton's striker nor ")][InlineData("Crouch as well as ")]
    [InlineData("A player called ")][InlineData("According to Crouch, ")]
    [InlineData("Crouch (")][InlineData("In the earlier sample, Crouch or ")]
    public void LegacyFiniteNeedsAProvedClosedLeftSubjectBoundary(string unprovedPrefix)
        => Reject(unprovedPrefix + "Stoke City scored 0 goals in the earlier sample.", Prepare(homeZero: false));

    [Theory]
    [InlineData("")][InlineData("Before the match, ")][InlineData("At full time, ")]
    [InlineData("In the earlier sample, ")][InlineData("In that sample, ")]
    [InlineData("Compared with their earlier results, ")][InlineData("Earlier results: ")]
    public void ExistingClosedTemporalStartsRetainBoundDirectFiniteSubjects(string prefix)
    {
        // Full-time keeps fixture authority; the other prefixes keep the cited earlier-results authority.
        var fixture = prefix == "At full time, "; var goals = fixture ? 1 : 0;
        Accept(prefix + $"Stoke City scored {goals} goals " + (fixture ? "in this fixture." : "in the earlier sample."), Prepare(homeZero: false), fixture ? "fixture" : "form");
    }

    [Theory]
    [InlineData(" and ")][InlineData(", and ")]
    public void AProvedDirectFiniteTeamClauseCanPrecedeAnIndependentSecondTeamClause(string boundary)
    {
        Accept(Words("HOME scored 0 goals" + boundary + "AWAY scored 8 goals in the earlier sample."));
        Accept(Words("AWAY scored 8 goals" + boundary + "HOME scored 0 goals in the earlier sample."));
    }

    [Fact]
    public void TheExistingFullTimeTemporalAdjunctRetainsItsFixtureScopeForBothTeams()
        => Accept(Words("At full time, HOME scored 0 goals and AWAY scored 1 goal in this fixture."), null, "fixture");

    [Theory]
    [InlineData("After Crouch spoke, HOME had lost all 3 results in the sample, earning 0 points and scoring 0 goals.")]
    [InlineData("HOME had lost all 3 results in the sample, bribing 0 points and scoring 0 goals.")]
    [InlineData("HOME had lost all 3 results in the sample, earning Chelsea 0 points and scoring 0 goals.")]
    [InlineData("HOME had taken 0 points by pressing and scored 0 goals in the earlier sample.")]
    [InlineData("HOME had taken 0 points and celebrated before scoring 0 goals in the earlier sample.")]
    [InlineData("HOME had lost all 3 results in the sample; earning 0 points and scoring 0 goals.")]
    public void ArbitraryContextBridgesActionsOrNamesCannotBindAGoalExpression(string text) => Reject(Words(text));

    [Theory]
    [InlineData("HOME had the stronger recent results and more goals scored in that sample.")]
    [InlineData("AWAY had the stronger recent results and more goals scored in this fixture.")]
    [InlineData("AWAY's striker had the stronger recent results and more goals scored in that sample.")]
    public void ComparisonsRequireAnExplicitTeamAndCorrectSameSampleDirection(string text) => Reject(Words(text));

    [Fact]
    public void EqualMissingOpponentAndUncitedComparisonFactsAreInsufficient()
    {
        var text = Words("AWAY had the stronger recent results and more goals scored in that sample.");
        Reject(text, Prepare(equal: true));
        Reject(text, Prepare(club: HistoricalChatFixtures.Away.Id));
        Reject(text, null, "fixture");
    }

    [Theory]
    [InlineData("HOME had lost all 3 earlier results in the sample without scoring; HOME pressed high without letting AWAY settle.")]
    [InlineData("HOME had lost all 3 earlier results in the sample without scoring; Crouch scored through Arnautovic.")]
    [InlineData("AWAY had taken 4 points and scored 8 goals across the earlier results; lineups were 4 and 3.")]
    public void MaskingOneProvedGoalSpanPreservesAllResidualForbiddenClaims(string text) => Reject(Words(text));

    [Fact]
    public void NewExplicitClauseRecognitionDoesNotCreateParagraphPronounAuthorityOrIdentityExemptions()
    {
        Reject(Words("AWAY had taken 4 points and scored 8 goals across the earlier results.\n\nThey had taken 4 points and scored 8 goals across the earlier results."));
        var prepared = Prepare(club: HistoricalChatFixtures.Home.Id);
        Reject(Words("HOME had lost all 3 earlier results in the sample without scoring. AWAY were the opponents."), prepared);
        Accept(Words("HOME had lost all 3 earlier results in the sample without scoring. No scorers or lineups are available."));
    }
}
