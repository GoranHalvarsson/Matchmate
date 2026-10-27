using System.Text.Json;
using Matchmate.Infrastructure;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatAggregateSubjectTests
{
    [Theory]
    [InlineData("overview", "southampton")][InlineData("form", "southampton")]
    [InlineData("overview", null)][InlineData("form", null)]
    public void ExplicitSampleSubjectSurvivesTheLeadingOpponentFixtureContext(string workflow, string? clubId)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, workflow, clubId);
        var answer = "Before the Stoke City fixture, Southampton’s sample showed 1 win, 1 draw and 0 losses, with 4 points. They scored 3 goals and conceded 2 across those earlier results.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared).Answer);
    }

    [Theory]
    [InlineData("stoke-city")][InlineData(null)]
    public void TheExplicitSampleCanBindTheAwayTeamWithoutChoosingTheLastNamedClubGenerally(string? clubId)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", clubId);
        var answer = "Before the Southampton fixture, Stoke City's sample showed 1 win, 0 draws and 1 loss, with 3 points. They scored 1 goal and conceded 2 across those earlier results.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared).Answer);
    }

    [Theory]
    [InlineData("They scored 2 goals across those earlier results.")]
    [InlineData("They scored 3 goals in this fixture.")]
    [InlineData("Stoke City scored 3 goals across those earlier results.")]
    [InlineData("They scored 3 goals from corners in their earlier results.")]
    [InlineData("They scored 3 goals via set pieces in their earlier results.")]
    [InlineData("They scored through Crouch, not Arnautovic.")]
    [InlineData("They pressed high without letting Stoke City settle.")]
    [InlineData("Southampton's striker had earlier results, scoring 3 goals.")]
    [InlineData("Southampton’s Crouch had earlier results, scoring 3 goals.")]
    public void ExplicitSampleSubjectDoesNotRelaxCountsScopeTeamOrDetailRules(string continuation)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(
            HistoricalChatFixtures.Reply(HomeSample + " " + continuation, "form"), prepared));
    }

    [Theory]
    [InlineData("fixture")][InlineData("table")]
    public void AnExplicitSampleSubjectStillRequiresItsFormCitation(string citation)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(
            HistoricalChatFixtures.Reply(HomeSample + " They scored 3 goals across those earlier results.", citation), prepared));
    }

    [Theory]
    [InlineData("Before the Barcelona fixture, Southampton’s sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, Dynamo’s sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, Southampton and Stoke City’s sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, Southampton's striker's sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, Southampton’s striker’s sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, Southampton Crouch’s sample showed 1 win, 1 draw and 0 losses, with 4 points.")]
    [InlineData("Before the Stoke City fixture, both teams had earlier results.")]
    public void ContextAloneOrAnAmbiguousUnknownOrPlayerSampleCannotBindThePronoun(string lead)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(
            HistoricalChatFixtures.Reply(lead + " They scored 3 goals across those earlier results.", "form"), prepared));
    }

    [Fact]
    public void AnExplicitSampleSubjectDoesNotCarryAcrossParagraphs()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(
            HistoricalChatFixtures.Reply(HomeSample + "\n\nThey scored 3 goals across those earlier results.", "form"), prepared));
    }

    [Theory]
    [InlineData("Before the Stoke City fixture, Southampton’s sample showed 1 win, 1 draw and 0 losses, with 4 points against Stoke City. They scored 3 goals across those earlier results.")]
    [InlineData(HomeSample + " They scored 3 goals across those earlier results, before facing Stoke City.")]
    [InlineData(HomeSample + " They scored 3 goals across those earlier results. Stoke City were the opponents.")]
    [InlineData(HomeSample + " They scored 3 goals across those earlier results.\n\nStoke City were the opponents.")]
    public void OnlyTheValidatedContextualOccurrenceIsExemptFromCitedIdentityChecks(string answer)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared));
    }

    [Fact]
    public void AnOrdinaryLaterOpponentMentionRemainsAllowedWhenActuallyCited()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        var answer = HomeSample + " They scored 3 goals across those earlier results. Stoke City were the opponents.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form", "fixture"), prepared).Answer);
    }

    private const string HomeSample = "Before the Stoke City fixture, Southampton’s sample showed 1 win, 1 draw and 0 losses, with 4 points.";

    public static IEnumerable<object?[]> NeutralSampleRelations()
    {
        foreach (var relation in new[] { "with", "earning", "yielding", "totalling", "totaling", "gaining", "collecting", null })
        foreach (var home in new[] { true, false })
        foreach (var pair in new[] { true, false })
            yield return [relation, home, pair];
    }

    [Theory, MemberData(nameof(NeutralSampleRelations))]
    public void ClosedNeutralPointsRelationsAndMissingPointsPreserveTheExplicitSampleSubject(string? relation, bool home, bool pair)
    {
        var club = home ? HistoricalChatFixtures.Home : HistoricalChatFixtures.Away;
        var opponent = home ? HistoricalChatFixtures.Away : HistoricalChatFixtures.Home;
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", pair ? null : club.Id);
        var metrics = home ? "1 win, 1 draw and 0 losses" : "1 win, 0 draws and 1 loss";
        var points = home ? 4 : 3;
        var tail = relation is null ? "" : $", {relation} {points} points";
        var answer = $"Before the {opponent.Name} fixture, {club.Name}'s sample showed {metrics}{tail}. They scored {(home ? 3 : 1)} goals and conceded 2 across those earlier results.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared).Answer);
    }

    [Theory]
    [InlineData("1 win, 1 draw and 0 losses, bribing 4 points")]
    [InlineData("1 win, 1 draw and 0 losses, inventing 4 points")]
    [InlineData("1 win, 1 draw and 0 losses, earning Southampton 4 points")]
    [InlineData("1 win, 1 draw and 0 losses, earning Stoke City's 4 points")]
    [InlineData("1 win, 1 draw and 0 losses; earning 4 points")]
    [InlineData("1 win, 1 draw and 0 losses, earning 4 points and Stoke City followed")]
    [InlineData("1 win, 1 draw and 0 losses, earning 4 points against Stoke City")]
    [InlineData("1 win, 1 draw and 0 losses, earning 4 points with Crouch")]
    public void MetricBodyCannotContainArbitraryActionsNamesPossessivesOrTrailingProse(string metrics)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        var answer = $"Before the Stoke City fixture, Southampton’s sample showed {metrics}. They scored 3 goals across those earlier results.";
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared));
    }

    [Theory]
    [InlineData("Before the Stoke City fixture, Stoke City's sample showed 1 win, 0 draws and 1 loss, earning 3 points.")]
    [InlineData("Before the Stoke City fixture, Southampton and Stoke City's sample showed 1 win, 1 draw and 0 losses, earning 4 points.")]
    [InlineData("Before the Stoke City fixture, Dynamo's sample showed 1 win, 1 draw and 0 losses, earning 4 points.")]
    [InlineData("Before the Stoke City fixture, Southampton's striker's sample showed 1 win, 1 draw and 0 losses, earning 4 points.")]
    public void NeutralRelationDoesNotRelaxTheExactDistinctFixtureSubjectPair(string lead)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(
            HistoricalChatFixtures.Reply(lead + " They scored 3 goals across those earlier results.", "form"), prepared));
    }

    [Theory]
    [InlineData("They scored 2 goals across those earlier results.", "form")]
    [InlineData("They scored 3 goals in this fixture.", "form")]
    [InlineData("Stoke City scored 3 goals across those earlier results.", "form")]
    [InlineData("They scored 3 goals across those earlier results.", "table")]
    [InlineData("They scored 3 goals across those earlier results.", "fixture")]
    [InlineData("They scored 3 goals from corners across those earlier results.", "form")]
    [InlineData("They scored 3 goals across those earlier results. Stoke City were the opponents.", "form")]
    [InlineData("\n\nThey scored 3 goals across those earlier results.", "form")]
    public void EarningPointsDoesNotRelaxTheFollowingAggregateOrIdentityGuards(string continuation, string citation)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview",
            continuation.StartsWith("\n\n", StringComparison.Ordinal) ? null : "southampton");
        var answer = HomeSample.Replace("with 4 points", "earning 4 points", StringComparison.Ordinal) + " " + continuation;
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, citation), prepared));
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("result")][InlineData("paired_form")]
    public async Task ActualCoventryBrightonSupportsBoundedResultAndBothTeamSampleShapes(string topic)
    {
        var data = await LeagueReferenceStore.Local(LocalPaths.FindRoot()).ReadAsync(default);
        var fixture = data.Match("2026-27", "coventry:brighton");
        Assert.Equal(new HistoricalChatContext("league-reference-v2", "2026-27", "coventry:brighton", "2026-09-13"), HistoricalChatContext.From(fixture));
        var clubs = data.Snapshot("2026-27", null).Clubs;
        var prepared = HistoricalEvidence.Prepare(fixture, clubs, "overview", null);
        if (topic == "result") Console.WriteLine("PinnedCoventryFacts=" + JsonSerializer.Serialize(new {
            context = HistoricalChatContext.From(fixture), homeGoals = fixture.Match.HomeGoals, awayGoals = fixture.Match.AwayGoals,
            homeForm = HistoricalEvidence.Form(fixture.HomePreMatchForm, fixture.Home, fixture.Match.Date, clubs),
            awayForm = HistoricalEvidence.Form(fixture.AwayPreMatchForm, fixture.Away, fixture.Match.Date, clubs),
            homeTable = HistoricalEvidence.Table(fixture.HomeTable), awayTable = HistoricalEvidence.Table(fixture.AwayTable)
        }));
        string Sample(bool home)
        {
            var club = home ? fixture.Home : fixture.Away;
            var opponent = home ? fixture.Away : fixture.Home;
            var games = (home ? fixture.HomePreMatchForm : fixture.AwayPreMatchForm).Where(m => string.CompareOrdinal(m.Date, fixture.Match.Date) < 0)
                .DistinctBy(m => m.MatchKey).OrderBy(m => m.Date, StringComparer.Ordinal).ThenBy(m => m.MatchKey, StringComparer.Ordinal).TakeLast(5).ToArray();
            int Goals(Matchmate.Contracts.LeagueMatch m) => m.Home == club.Id ? m.HomeGoals : m.AwayGoals;
            int Against(Matchmate.Contracts.LeagueMatch m) => m.Home == club.Id ? m.AwayGoals : m.HomeGoals;
            var wins = games.Count(m => Goals(m) > Against(m)); var draws = games.Count(m => Goals(m) == Against(m));
            var goals = games.Sum(Goals); var conceded = games.Sum(Against);
            Assert.Equal(goals, prepared.GoalFacts.Single(f => f.ClubId == club.Id && f.Scope == "earlier_results").Goals);
            return $"Before the {opponent.Name} fixture, {club.Name}’s sample showed {wins} wins, {draws} draws and {games.Length - wins - draws} losses, earning {wins * 3 + draws} points. They scored {goals} goals and conceded {conceded} across those earlier results.";
        }
        var answer = topic == "result"
            ? $"At full time, {fixture.Home.Name} scored {fixture.Match.HomeGoals} goals and {fixture.Away.Name} scored {fixture.Match.AwayGoals} goals in this fixture."
            : Sample(true) + "\n\n" + Sample(false);
        var result = HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, topic == "result" ? "fixture" : "form"), prepared);
        Assert.Equal(answer, result.Answer);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("overview", "one-rejected-public-test-reply.json")][InlineData("form", "one-rejected-public-test-reply.json")]
    [InlineData("overview", "one-rejected-public-test-reply-2.json")][InlineData("form", "one-rejected-public-test-reply-2.json")]
    public async Task ExactCapturedPublicReplyValidatesAgainstItsPinnedChelseaSample(string workflow, string captureFile)
    {
        var root = LocalPaths.FindRoot(); var data = await LeagueReferenceStore.Local(root).ReadAsync(default);
        using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".azure", "historical-chat", captureFile)));
        Assert.Equal("AggregateSubjectUnbound", captured.RootElement.GetProperty("rejectionReason").GetString());
        var fixture = data.Match("2026-27", "brentford:chelsea");
        Assert.Equal(new HistoricalChatContext("league-reference-v2", "2026-27", "brentford:chelsea", "2026-09-18"), HistoricalChatContext.From(fixture));
        var prepared = HistoricalEvidence.Prepare(fixture, data.Snapshot("2026-27", null).Clubs, workflow, "chelsea");
        var raw = captured.RootElement.GetProperty("rawModelReply").GetString()!;
        var result = HistoricalReplyValidation.Validate(raw, prepared);
        using var reply = JsonDocument.Parse(raw);
        Assert.Equal(reply.RootElement.GetProperty("answer").GetString(), result.Answer);
        Assert.Equal(new[] { "form" }, result.Evidence.Select(e => e.Id));
    }
}
