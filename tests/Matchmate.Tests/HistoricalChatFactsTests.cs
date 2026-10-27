using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

internal static class HistoricalChatFixtures
{
    internal static readonly LeagueClub Home = Club("southampton", "Southampton", "SOU", "St Mary's Stadium");
    internal static readonly LeagueClub Away = Club("stoke-city", "Stoke City", "STK", "Bet365 Stadium");
    internal static readonly LeagueClub[] Clubs = [Home, Away, Club("chelsea", "Chelsea", "CHE", "Stamford Bridge"), Club("arsenal", "Arsenal", "ARS", "Emirates Stadium")];
    internal static LeagueClub Club(string id, string name, string initials, string ground) => new(id, name, initials, name, ground, 50, 50, false, null);
    internal static LeagueMatch Game(string home, string away, string date, int homeGoals, int awayGoals) => new(home + ":" + away, date, home, away, homeGoals, awayGoals, null);
    internal static LeagueSeasonModel Model() => new(new("2016-17", "2016/17", Clubs,
        [Game(Home.Id,"chelsea","2017-05-01",1,0), Game("arsenal",Away.Id,"2017-05-01",2,0),
         Game("chelsea",Home.Id,"2017-05-10",2,2), Game(Away.Id,"arsenal","2017-05-10",1,0),
         Game(Home.Id,Away.Id,"2017-05-21",0,1), Game("chelsea","arsenal","2017-05-21",0,3)]), false, version: "test-reference-v1");
    internal static LeagueHistoricalMatch Fixture()
    {
        var model = Model(); var home = model.Facts(3, Home.Id); var away = model.Facts(3, Away.Id);
        return new("test-reference-v1", model.Info(), model.Matches.Single(m => m.Home == Home.Id && m.Away == Away.Id), Home, Away, home, away,
            model.Matches.Where(m => m.Date != "2017-05-21" && (m.Home == Home.Id || m.Away == Home.Id)).ToArray(),
            model.Matches.Where(m => m.Date != "2017-05-21" && (m.Home == Away.Id || m.Away == Away.Id)).ToArray(),
            [new("2016-17", Game(Away.Id, Home.Id, "2016-12-14", 0, 0), Away.Name, Home.Name)], "Synthetic test fixture only.");
    }
    internal static HistoricalEvidence Evidence(LeagueHistoricalMatch? fixture = null, LeagueSeasonSnapshot? snapshot = null) => new(
        (_, _, _) => Task.FromResult(fixture ?? Fixture()), (_, _, _, _) => Task.FromResult(snapshot ?? Model().Snapshot(null)));
    internal static string Reply(string answer, params string[] ids) => JsonSerializer.Serialize(new { answer, evidenceIds = ids });
}

public sealed class HistoricalChatFactsTests
{
    [Fact]
    public async Task RealColonFixtureKeyAndSnapshotBindingAreAccepted()
    {
        var fixture = HistoricalChatFixtures.Fixture(); var context = HistoricalChatContext.From(fixture);
        Assert.True(HistoricalChatContext.Valid(context));
        Assert.Equal(fixture, (await HistoricalChatFixtures.Evidence(fixture).LoadAsync(context, default)).Match);
        foreach (var changed in new[] { context with { Version = "changed" }, context with { Date = "2017-05-20" }, context with { MatchKey = "stoke-city:southampton" }, context with { SeasonKey = "2015-16" } })
            await Assert.ThrowsAsync<InvalidDataException>(() => HistoricalChatFixtures.Evidence(fixture).LoadAsync(changed, default));
    }
    [Fact]
    public void FormIsCalculatedFromStrictEarlierDatesAndFiltersSameDayFutureDuplicates()
    {
        var f = HistoricalChatFixtures.Fixture();
        var games = f.HomePreMatchForm.Concat([f.Match, HistoricalChatFixtures.Game(f.Home.Id,f.Away.Id,"2017-05-22",9,0),f.HomePreMatchForm[0]]).ToArray();
        var text = HistoricalEvidence.Form(games, f.Home, f.Match.Date, HistoricalChatFixtures.Clubs);
        Assert.Contains("2 earlier results", text); Assert.Contains("1 wins, 1 draws, 0 losses", text); Assert.Contains("4 points, 3 goals for, 2 against", text);
        Assert.DoesNotContain("9–0", text); Assert.DoesNotContain("0–1", text);
    }
    [Fact]
    public void FormOnlyUsesLastFiveAndDoesNotTreatMissingAsAllTimeZero()
    {
        var f = HistoricalChatFixtures.Fixture();
        var games = Enumerable.Range(1, 7).Select(day => HistoricalChatFixtures.Game(f.Home.Id,f.Away.Id,$"2017-04-{day:00}",1,0) with { MatchKey = "fixture-" + day }).Reverse().ToArray();
        var text = HistoricalEvidence.Form(games, f.Home, f.Match.Date, HistoricalChatFixtures.Clubs);
        Assert.Contains("5 earlier results", text); Assert.Contains("15 points", text); Assert.DoesNotContain("2017-04-01", text);
        Assert.Contains("no earlier results supplied", HistoricalEvidence.Form([], f.Home, f.Match.Date, HistoricalChatFixtures.Clubs));
    }
    [Fact]
    public void TableKeepsAllSameDayMatchesAndPriorMeetingsKeepSameSeason()
    {
        var f = HistoricalChatFixtures.Fixture(); var bundle = HistoricalEvidence.Prepare(f, HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Equal(4, bundle.Evidence.Length); var table = bundle.Evidence.Single(e => e.Id == "table").Text;
        Assert.Contains(HistoricalEvidence.Table(f.HomeTable), JsonDocument.Parse(table).RootElement.GetProperty("sourceAnswer").GetString());
        Assert.Contains("all results on that date", table);
        Assert.Contains("2016-12-14", bundle.Evidence.Single(e => e.Id == "reference").Text);
        var polluted = f with { PriorMeetings = [..f.PriorMeetings, new("2016-17", f.Match, f.Home.Name, f.Away.Name)] };
        var previous = HistoricalEvidence.Prepare(polluted, HistoricalChatFixtures.Clubs, "meetings", null).Evidence.Single(e => e.Id == "reference").Text;
        Assert.Contains("1 earlier meetings", previous); Assert.DoesNotContain("2017-05-21", previous);
    }
    [Theory]
    [InlineData("How were Southampton doing before this game?", null, "southampton")]
    [InlineData("What kind of form did Stoke bring into it?", null, "stoke-city")]
    [InlineData("And Stoke?", "southampton", "stoke-city")]
    [InlineData("Tell me more about them", "southampton", "southampton")]
    [InlineData("Compare their recent form", null, null)]
    [InlineData("What did this result mean for their league positions?", null, null)]
    [InlineData("How was the home team doing?", null, "southampton")]
    [InlineData("Southampton and Stoke", null, null)]
    public void ClubBindingHandlesNaturalQuestionsAndFollowups(string question, string? prior, string? expected)
    {
        var actual = HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, prior, "form");
        Assert.Null(actual.Clarification); Assert.Equal(expected, actual.ClubId);
    }
    [Theory]
    [InlineData("How were they doing?")]
    [InlineData("Chelsea form please")]
    [InlineData("And Barcelona?")]
    [InlineData("Ajax form")]
    public void UnboundOrOutsideFixtureClubClarifies(string question) => Assert.NotNull(HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, null, "form").Clarification);
    [Theory]
    [InlineData("Southampton had 999 points.", "table")]
    [InlineData("Stoke scored through Crouch.", "fixture")]
    [InlineData("The stadium opened in 2017.", "reference")]
    [InlineData("Southampton pressed high.", "fixture")]
    [InlineData("<b>Stoke won.</b>", "fixture")]
    public void UnsupportedNumbersDetailsAndMarkupAreRejected(string answer, string id)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, id), prepared));
    }
    [Fact]
    public void ValidReplyRequiresCitedFactsAndGroundKeepsVenueUnverified()
    {
        var f = HistoricalChatFixtures.Fixture(); var prepared = HistoricalEvidence.Prepare(f, HistoricalChatFixtures.Clubs, "ground", f.Home.Id);
        Assert.Contains("not confirmation of this fixture", prepared.Evidence.Single(e => e.Id == "reference").Text);
        var answer = "Southampton’s season ground reference is St Mary's Stadium. The fixture venue is unverified.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "reference"), prepared).Answer);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply("Stoke won.", "made_up"), prepared));
    }
    [Theory]
    [InlineData("What happened on 2017-05-21?", true)]
    [InlineData("What happened on 21 May 2017?", true)]
    [InlineData("How does this fit in 2016/17?", true)]
    [InlineData("What happened on 2017-05-20?", false)]
    [InlineData("What happened on 20 May 2017?", false)]
    [InlineData("What about 2026/27?", false)]
    [InlineData("What happened live today?", false)]
    [InlineData("What was known at the time?", false)]
    public void ModelCannotAuthorizeOtherDatesLiveOrKnownThen(string question, bool selected) => Assert.Equal(selected, HistoricalTimeScope.SelectedFixture(question, HistoricalChatContext.From(HistoricalChatFixtures.Fixture())));
    [Fact]
    public void NaturalDateNumbersAreAllowedButCertainVenueIsRejected()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null);
        Assert.Equal("Stoke won on 21 May 2017.", HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply("Stoke won on 21 May 2017.", "fixture"), prepared).Answer);
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply("Southampton lost at St Mary's Stadium.", "reference"), prepared));
    }
}
