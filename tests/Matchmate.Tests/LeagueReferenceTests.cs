using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueReferenceTests
{
    [Fact]
    public void SamePlayedDateEntersTogetherTiesShareRankAndNonPlayingClubCanMove()
    {
        var clubs = new[] { Club("a", "Alpha"), Club("b", "Bravo"), Club("c", "Charlie"), Club("d", "Zulu") };
        var model = new LeagueSeasonModel(new("2015-16", "2015/16", clubs,
            [Game("a","b","2015-08-01",1,0), Game("c","d","2015-08-01",1,0), Game("b","d","2015-08-02",2,0)]), complete: false);
        var zero = model.Snapshot(0); Assert.False(zero.Ranked); Assert.All(zero.Rows, r => { Assert.Null(r.Rank); Assert.Equal(0, r.Played); });
        var one = model.Snapshot(1, "a"); Assert.Equal(2, one.Completed); Assert.Equal(2, one.Results.Length);
        Assert.Equal(new[] { "a", "c", "b", "d" }, one.Rows.Select(r => r.Id)); Assert.Equal(new int?[] { 1, 1, 3, 3 }, one.Rows.Select(r => r.Rank)); Assert.All(one.Rows, r => Assert.True(r.Shared));
        Assert.Equal(3, one.SelectedClub!.PointsChange); Assert.Null(one.SelectedClub.PositionChange);
        var two = model.Snapshot(null, "a"); Assert.Equal(2, two.DateIndex); Assert.Empty(two.SelectedClub!.Today);
        Assert.Equal(0, two.SelectedClub.PointsChange); Assert.Equal(-1, two.SelectedClub.PositionChange); Assert.Equal("2015-08-01", two.SelectedClub.PreviousDate);
        Assert.Equal("a:b", two.SelectedClub.Latest!.MatchKey);
        Assert.Throws<LeagueCursorException>(() => model.Snapshot(-1)); Assert.Throws<LeagueCursorException>(() => model.Snapshot(3));
        Assert.Throws<KeyNotFoundException>(() => model.Snapshot(1, "unknown"));
    }
    [Fact]
    public void ValidationRejectsInvalidDatesResultsMembershipDuplicatesAndIncompleteSeason()
    {
        var clubs = new[] { Club("a", "Alpha"), Club("b", "Bravo") }; var match = Game("a", "b", "2016-02-29", 0, 0);
        LeagueSeasonData Data(params LeagueMatch[] games) => new("2015-16", "2015/16", clubs, games);
        Assert.Single(new LeagueSeasonModel(Data(match), false).Matches);
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueSeasonModel(Data(match)));
        foreach (var bad in new[] { match with { Date = "2015-02-29" }, match with { HomeGoals = -1 }, match with { Home = "missing" }, match with { Away = "a" }, match with { MatchKey = "wrong" } })
            Assert.Throws<LeagueReferenceValidationException>(() => new LeagueSeasonModel(Data(bad), false));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueSeasonModel(Data(match, match), false));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueSeasonModel(Data(match) with { Clubs = [clubs[0], clubs[0]] }, false));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueSeasonModel(Data(match) with { Clubs = [clubs[0] with { CrestPath = "https://untrusted.example/crest.svg" }, clubs[1]] }, false));
        Assert.Throws<LeagueReferenceValidationException>(() => LeagueReferenceCodec.Decode(new byte[LeagueReferenceCodec.MaxBytes + 1]));
        Assert.Throws<LeagueReferenceValidationException>(() => LeagueReferenceCodec.Decode("null"u8.ToArray()));
        var bundle = Bundle(); Assert.Equal(2, new LeagueModel(bundle).Seasons().Length);
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(bundle with { Version = "other-version" }));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(bundle with { Seasons = [bundle.Seasons[0], bundle.Seasons[0]] }));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(bundle with { Seasons = [bundle.Seasons[0] with { Matches = bundle.Seasons[0].Matches[..^1] }, bundle.Seasons[1]] }));
        var huge = new LeagueSeasonModel(Data(match with { HomeGoals = int.MaxValue }), false).Snapshot(1);
        Assert.Equal(int.MaxValue, huge.Rows[0].GoalsFor);
    }
    [Fact]
    public async Task RetryAndCancellationDoNotPoisonImmutableReferenceCache()
    {
        var calls = 0; var bytes = JsonSerializer.SerializeToUtf8Bytes(Bundle(), LeagueReferenceCodec.Json);
        var store = new LeagueReferenceStore(_ => { calls++; return calls == 1 ? throw new IOException("temporary") : Task.FromResult<byte[]?>(bytes); });
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(default)); var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ReadAsync(default)));
        Assert.Equal(2, calls); Assert.All(results, r => Assert.Same(results[0], r));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(cancellation.Token));
        var attempts = 0; var corrupt = new LeagueReferenceStore(_ => Task.FromResult<byte[]?>(++attempts == 1 ? "{}"u8.ToArray() : bytes));
        await Assert.ThrowsAsync<LeagueReferenceValidationException>(() => corrupt.ReadAsync(default)); Assert.Equal(2, (await corrupt.ReadAsync(default)).Seasons().Length);
    }
    [Fact]
    public async Task LeagueRoutesUseExistingAuthorizationAndClientWithExplicit400404503()
    {
        var bundle = Bundle(); var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, LeagueReferenceCodec.Json);
        await using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILeagueReferenceStore>(new LeagueReferenceStore(_ => Task.FromResult<byte[]?>(bytes)))));
        using var http = api.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/league/seasons")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token("role")); Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/league/seasons")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token()); var client = new ReplayApiClient(http);
        Assert.Equal(2, (await client.LeagueSeasonsAsync(default)).Length); var season = await client.LeagueSnapshotAsync("2015-16", null, "club-00", default);
        Assert.Equal(380, season.Completed); Assert.Equal(20, season.Clubs.Length); Assert.Equal(38, season.SelectedClub!.Row.Played);
        var historical = await client.LeagueMatchAsync("2015-16", bundle.Seasons[0].Matches[0].MatchKey, default);
        Assert.Equal(bundle.Seasons[0].Matches[0], historical.Match); Assert.Contains("No scorers, lineups", historical.SourceContext);
        Assert.All(historical.HomePreMatchForm.Concat(historical.AwayPreMatchForm), m => Assert.True(string.CompareOrdinal(m.Date, historical.Match.Date) < 0));
        foreach (var suffix in new[] { "2015-16?dateIndex=-1", "2015-16?dateIndex=999", "2015-16?dateIndex=NaN", "2015-16?dateIndex=1.5" }) Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/league/seasons/" + suffix)).StatusCode);
        foreach (var suffix in new[] { "unknown", "2015-16?clubId=unknown", "2015-16/matches/unknown" }) Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/league/seasons/" + suffix)).StatusCode);
        await using var unavailable = ApiServiceAuthenticationTests.Factory(); using var missing = unavailable.CreateClient(); missing.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
        var response = await missing.GetAsync("/api/league/seasons"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.DoesNotContain(".local", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public void HistoricalMatchContextUsesStrictEarlierDatesAndSeasonMembership()
    {
        var model = new LeagueModel(Bundle()); var end = model.Snapshot("2016-17", null, "club-00");
        var candidate = end.SelectedClub!.Latest!; var context = model.Match("2016-17", candidate.MatchKey);
        Assert.Equal(candidate, context.Match); Assert.InRange(context.HomePreMatchForm.Length, 0, 5); Assert.InRange(context.AwayPreMatchForm.Length, 0, 5);
        Assert.All(context.HomePreMatchForm.Concat(context.AwayPreMatchForm), m => Assert.True(string.CompareOrdinal(m.Date, candidate.Date) < 0));
        Assert.All(context.PriorMeetings, p => Assert.True(string.CompareOrdinal(p.Match.Date, candidate.Date) < 0));
        var onDate = model.Snapshot("2016-17", Array.IndexOf(end.Season.PlayedDates, candidate.Date) + 1);
        Assert.Equal(onDate.Rows.Single(r => r.Id == candidate.Home), context.HomeTable.Row);
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task All208OriginalSnapshotsAnd4160ClubFactsMatchPinnedPocAndPeriodContext()
    {
        var root = LocalPaths.FindRoot(); var folder = Path.Combine(root, ".local", "league-reference-v1");
        var bytes = await BoundedFile.ReadAsync(Path.Combine(folder, "league-reference.json"), LeagueReferenceCodec.MaxBytes, default);
        var bundle = LeagueReferenceCodec.Decode(bytes); var model = new LeagueModel(bundle);
        using var expected = JsonDocument.Parse(await BoundedFile.ReadAsync(Path.Combine(folder, "parity.json"), 10_000_000, default));
        using var baseline = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, ".azure", "poc-season-model-baseline.json")));
        var snapshots = 0; var facts = 0; var rows = 0;
        foreach (var entry in expected.RootElement.EnumerateArray())
        {
            var key = entry.GetProperty("seasonKey").GetString()!; var index = entry.GetProperty("index").GetInt32(); var actual = model.Snapshot(key, index);
            Assert.Equal(Text(entry.GetProperty("date")), actual.Date); Assert.Equal(entry.GetProperty("completed").GetInt32(), actual.Completed);
            var expectedRows = entry.GetProperty("rows").EnumerateArray().ToArray(); Assert.Equal(20, expectedRows.Length);
            for (var i = 0; i < 20; i++) { CheckRow(expectedRows[i], actual.Rows[i], index); rows++; }
            CheckGames(entry.GetProperty("results"), actual.Results);
            foreach (var f in entry.GetProperty("facts").EnumerateArray())
            {
                var club = f.GetProperty("clubId").GetString()!; var value = model.Snapshot(key, index, club).SelectedClub!;
                CheckRow(f.GetProperty("row"), value.Row, index); Assert.Equal(Text(f.GetProperty("date")), value.Date); Assert.Equal(Text(f.GetProperty("previousDate")), value.PreviousDate);
                Assert.Equal(f.GetProperty("pointsChange").GetInt32(), value.PointsChange); Assert.Equal(f.GetProperty("positionChange").ValueKind == JsonValueKind.Null ? (int?)null : f.GetProperty("positionChange").GetInt32(), value.PositionChange);
                CheckGames(f.GetProperty("today"), value.Today); CheckGames(f.GetProperty("recent"), value.Recent); Assert.Equal(value.Recent.FirstOrDefault(), value.Latest); facts++;
            }
            snapshots++;
        }
        Assert.Equal(208, snapshots); Assert.Equal(4160, facts); Assert.Equal(4160, rows);
        foreach (var season in baseline.RootElement.GetProperty("seasons").EnumerateArray())
        {
            var key = season.GetProperty("season").GetString()!.Replace('/', '-'); var info = model.Seasons().Single(s => s.SeasonKey == key);
            Assert.Equal(season.GetProperty("dates").GetInt32(), info.PlayedDates.Length); CheckRow(season.GetProperty("leader"), model.Snapshot(key, null).Rows[0], info.PlayedDates.Length);
            Assert.Equal(season.GetProperty("hash").GetString(), bundle.Sources.Single(s => s.SourceId == "season-" + key).Sha256);
        }
        var one = model.Snapshot("2015-16", null); var two = model.Snapshot("2016-17", null);
        Assert.Contains(one.Clubs, c => c.Id == "aston-villa"); Assert.DoesNotContain(two.Clubs, c => c.Id == "aston-villa");
        Assert.Equal("Boleyn Ground", one.Clubs.Single(c => c.Id == "west-ham").Ground); Assert.Equal("London Stadium", two.Clubs.Single(c => c.Id == "west-ham").Ground);
        Assert.Equal("Britannia Stadium", one.Clubs.Single(c => c.Id == "stoke").Ground); Assert.Equal("bet365 Stadium", two.Clubs.Single(c => c.Id == "stoke").Ground);
        Assert.EndsWith("manchester-city-2015.png", one.Clubs.Single(c => c.Id == "manchester-city").CrestPath); Assert.EndsWith("manchester-city-2016.svg", two.Clubs.Single(c => c.Id == "manchester-city").CrestPath);
        foreach (var source in bundle.Sources) Assert.Equal(source.Sha256, ReplayBatchCodec.Hash(await File.ReadAllBytesAsync(Path.Combine(root, source.Path))));
        await File.WriteAllTextAsync(Path.Combine(root, ".azure", "league-reference-parity.json"), JsonSerializer.Serialize(new { snapshots, rows, facts, seasons = 2, matches = 760, bundleBytes = bytes.Length, bundleSha256 = ReplayBatchCodec.Hash(bytes), provenanceSourcesUnchanged = bundle.Sources.Length, rankNormalization = "Only index0 display Rank is null; original calculations/shared groups otherwise match.", originalInputsModified = false }));
    }
    private static void CheckRow(JsonElement expected, LeagueTableRow actual, int index)
    {
        Assert.Equal(expected.GetProperty("id").GetString(), actual.Id); Assert.Equal(expected.GetProperty("name").GetString(), actual.Name); Assert.Equal(expected.GetProperty("initials").GetString(), actual.Initials);
        Assert.Equal(expected.GetProperty("p").GetInt32(), actual.Played); Assert.Equal(expected.GetProperty("w").GetInt32(), actual.Won); Assert.Equal(expected.GetProperty("d").GetInt32(), actual.Drawn); Assert.Equal(expected.GetProperty("l").GetInt32(), actual.Lost);
        Assert.Equal(expected.GetProperty("gf").GetInt64(), actual.GoalsFor); Assert.Equal(expected.GetProperty("ga").GetInt64(), actual.GoalsAgainst); Assert.Equal(expected.GetProperty("gd").GetInt64(), actual.GoalDifference); Assert.Equal(expected.GetProperty("pts").GetInt32(), actual.Points);
        Assert.Equal(index == 0 ? null : (int?)expected.GetProperty("rank").GetInt32(), actual.Rank); Assert.Equal(expected.GetProperty("shared").GetBoolean(), actual.Shared);
    }
    private static void CheckGames(JsonElement expected, LeagueMatch[] actual)
    {
        var games = expected.EnumerateArray().ToArray(); Assert.Equal(games.Length, actual.Length);
        for (var i = 0; i < games.Length; i++) { Assert.Equal(games[i].GetProperty("id").GetString(), actual[i].MatchKey); Assert.Equal(games[i].GetProperty("date").GetString(), actual[i].Date); Assert.Equal(games[i].GetProperty("hg").GetInt32(), actual[i].HomeGoals); Assert.Equal(games[i].GetProperty("ag").GetInt32(), actual[i].AwayGoals); }
    }
    private static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetString();
    private static LeagueClub Club(string id, string name) => new(id, name, "TST", "Test town", "Test ground", 100, 200, false, null);
    private static LeagueMatch Game(string home, string away, string date, int hg, int ag) => new(home+":"+away,date,home,away,hg,ag,null);
    private static LeagueReferenceBundle Bundle()
    {
        LeagueSeasonData Season(int year)
        {
            var clubs = Enumerable.Range(0, 20).Select(i => Club("club-"+i.ToString("D2"), "Test club "+i.ToString("D2"))).ToArray(); var games = new List<LeagueMatch>();
            for (var leg = 0; leg < 2; leg++)
            {
                var ring = Enumerable.Range(0, 20).ToList();
                for (var round = 0; round < 19; round++)
                {
                    for (var pair = 0; pair < 10; pair++)
                    {
                        var home = ring[pair]; var away = ring[19-pair]; if (leg == 1) (home, away) = (away, home);
                        games.Add(Game(clubs[home].Id, clubs[away].Id, new DateOnly(year,8,1).AddDays(leg*19+round).ToString("yyyy-MM-dd"), (home+round)%3, (away+round)%3));
                    }
                    var last = ring[^1]; ring.RemoveAt(19); ring.Insert(1,last);
                }
            }
            return new(year+"-"+((year+1)%100).ToString("D2"), year+"/"+((year+1)%100).ToString("D2"), clubs, games.ToArray());
        }
        var metadata = JsonSerializer.SerializeToElement(new { source = "Inline synthetic test-only reference; never product data" });
        return new(1, LeagueReferenceCodec.Version, LeagueReferenceCodec.Timing, "Results-only synthetic test calculation", [new("inline-test", "inline-test-fixtures", new string('a',64), "Test only")], "Test licence", metadata, metadata, [Season(2015),Season(2016)]);
    }
}
