using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Components.League;
using Matchmate.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect actual component render trees; no substitute browser host.
public sealed class CurrentSeasonTests
{
    private static string Root => LocalPaths.FindRoot();
    private static byte[] Bytes(string version) => File.ReadAllBytes(Path.Combine(Root, ".local", "league-reference-" + version, "league-reference.json"));
    private static LeagueReferenceBundle Bundle() => LeagueReferenceCodec.Decode(Bytes("v2"));
    private static LeagueSeasonData Current(LeagueReferenceBundle b) => b.Seasons.Single(s => s.SeasonKey == "2026-27");
    private static LeagueReferenceBundle Replace(LeagueReferenceBundle b, LeagueSeasonData current) => b with { Seasons = b.Seasons.Select(s => s.SeasonKey == current.SeasonKey ? current : s).ToArray() };

    [Fact, Trait("RequiresLocalData", "true")]
    public void PinnedRawSourceRetainsEveryScoredResultIncludingFiveAlternateZeroScores()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, "data", "current-season", "2026-27", "en.1.json"));
        Assert.Equal("bb91f0ab3e8359df163bb9ec98549bfed85e200dd4c32f3bd606b5890d932dd2", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var raw = JsonDocument.Parse(bytes);
        using var mapping = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data", "current-season", "2026-27", "club-context.json")));
        var names = mapping.RootElement.GetProperty("clubs").EnumerateArray().ToDictionary(c => c.GetProperty("sourceTeamName").GetString()!, c => c.GetProperty("club").GetProperty("id").GetString()!);
        var season = Current(Bundle()); var scored = 0; var flatZeroes = 0; var pending = 0;
        foreach (var fixture in raw.RootElement.GetProperty("matches").EnumerateArray())
        {
            var key = names[fixture.GetProperty("team1").GetString()!] + ":" + names[fixture.GetProperty("team2").GetString()!];
            if (!fixture.TryGetProperty("score", out var score)) { pending++; Assert.DoesNotContain(season.Matches, m => m.MatchKey == key); continue; }
            if (score.ValueKind == JsonValueKind.Object) score = score.GetProperty("ft");
            else { flatZeroes++; Assert.All(score.EnumerateArray(), n => Assert.Equal(0, n.GetInt32())); }
            var match = Assert.Single(season.Matches, m => m.MatchKey == key);
            Assert.Equal(score[0].GetInt32(), match.HomeGoals); Assert.Equal(score[1].GetInt32(), match.AwayGoals);
            Assert.Equal(fixture.GetProperty("date").GetString(), match.Date); scored++;
        }
        Assert.Equal(50, scored); Assert.Equal(330, pending); Assert.Equal(5, flatZeroes);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public void SnapshotHasTwentyCurrentClubsFiftyResultsAnd380FixtureDenominator()
    {
        var model = new LeagueModel(Bundle()); var snapshot = model.Snapshot("2026-27", null, "everton"); var info = snapshot.Season;
        Assert.Equal(3, model.Seasons().Length); Assert.Equal("ongoing", info.Status); Assert.Equal(50, info.MatchCount); Assert.Equal(380, info.ScheduledMatchCount);
        Assert.Equal("2026-09-20", info.ResultsThrough); Assert.StartsWith("2026-10-23T", info.RetrievedAt);
        Assert.Equal("2026-09-20", snapshot.Date); Assert.Equal(50, snapshot.Completed); Assert.Equal(20, snapshot.Clubs.Length);
        Assert.All(snapshot.Rows, r => Assert.Equal(5, r.Played)); Assert.All(snapshot.Clubs, c => Assert.Equal("assets/crests/" + c.Id + "-2026.png", c.CrestPath));
        Assert.Equal("Hill Dickinson Stadium", snapshot.SelectedClub!.Club.Ground);
        Assert.Equal("Tottenham Hotspur Stadium", snapshot.Clubs.Single(c => c.Id == "tottenham").Ground);
        Assert.Equal("MKM Stadium", snapshot.Clubs.Single(c => c.Id == "hull").Ground);
        Assert.Equal(new[] { "manchester-city", "arsenal", "brighton", "brentford", "leeds" }, snapshot.Rows.Take(5).Select(r => r.Id));
        Assert.Equal(new[] { 15, 12, 10, 9, 9 }, snapshot.Rows.Take(5).Select(r => r.Points));
        Assert.Equal(new long[] { 8, 4, 11, 6, 4 }, snapshot.Rows.Take(5).Select(r => r.GoalDifference));
        Assert.Equal(new long[] { 13, 8, 16, 10, 7 }, snapshot.Rows.Take(5).Select(r => r.GoalsFor));
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public void PendingAndFutureFixturesNeverEnterPlaybackFormOrResultLinks()
    {
        var model = new LeagueModel(Bundle()); var season = Current(Bundle());
        Assert.All(model.Seasons().Single(s => s.SeasonKey == "2026-27").PlayedDates, d => Assert.True(string.CompareOrdinal(d, "2026-09-20") <= 0));
        for (var index = 0; index <= model.Seasons().Single(s => s.SeasonKey == "2026-27").PlayedDates.Length; index++)
        {
            var snapshot = model.Snapshot("2026-27", index);
            Assert.All(snapshot.Results, m => Assert.True(string.CompareOrdinal(m.Date, snapshot.Date) <= 0));
            foreach (var club in snapshot.Clubs)
            {
                var facts = model.Snapshot("2026-27", index, club.Id).SelectedClub!;
                Assert.All(facts.Recent.Concat(facts.Today), m => Assert.Contains(m, season.Matches));
                if (facts.Latest is not null) Assert.True(string.CompareOrdinal(facts.Latest.Date, snapshot.Date) <= 0);
            }
        }
        var match = model.Match("2026-27", season.Matches[^1].MatchKey);
        Assert.Contains("3 loaded seasons", match.SourceContext); Assert.Contains("2026-09-20", match.SourceContext); Assert.Contains("2026-10-23", match.SourceContext);
        Assert.All(match.HomePreMatchForm.Concat(match.AwayPreMatchForm), m => Assert.True(string.CompareOrdinal(m.Date, match.Match.Date) < 0));
        using var raw = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data", "current-season", "2026-27", "en.1.json")));
        using var mapping = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data", "current-season", "2026-27", "club-context.json")));
        var names = mapping.RootElement.GetProperty("clubs").EnumerateArray().ToDictionary(c => c.GetProperty("sourceTeamName").GetString()!, c => c.GetProperty("club").GetProperty("id").GetString()!);
        var pending = raw.RootElement.GetProperty("matches").EnumerateArray().First(f => !f.TryGetProperty("score", out _));
        var pendingKey = names[pending.GetProperty("team1").GetString()!] + ":" + names[pending.GetProperty("team2").GetString()!];
        Assert.Throws<KeyNotFoundException>(() => model.Match("2026-27", pendingKey));
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public void V2RetainsAll208HistoricalSnapshotsAnd4160ClubFacts()
    {
        var legacy = new LeagueModel(LeagueReferenceCodec.Decode(Bytes("v1"))); var current = new LeagueModel(Bundle()); var snapshots = 0; var facts = 0;
        foreach (var season in legacy.Seasons())
        {
            Assert.Equal("complete", current.Seasons().Single(s => s.SeasonKey == season.SeasonKey).Status);
            for (var index = 0; index <= season.PlayedDates.Length; index++)
            {
                var before = legacy.Snapshot(season.SeasonKey, index); var after = current.Snapshot(season.SeasonKey, index);
                Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Results, after.Results); Assert.Equal(before.Clubs, after.Clubs); Assert.Equal(before.Completed, after.Completed); snapshots++;
                foreach (var club in before.Clubs)
                {
                    Assert.Equal(JsonSerializer.Serialize(legacy.Snapshot(season.SeasonKey, index, club.Id).SelectedClub), JsonSerializer.Serialize(current.Snapshot(season.SeasonKey, index, club.Id).SelectedClub)); facts++;
                }
            }
        }
        Assert.Equal(208, snapshots); Assert.Equal(4160, facts);
        using var old = JsonDocument.Parse(Bytes("v1")); using var added = JsonDocument.Parse(Bytes("v2"));
        for (var i = 0; i < 2; i++) Assert.Equal(old.RootElement.GetProperty("seasons")[i].GetRawText(), added.RootElement.GetProperty("seasons")[i].GetRawText());
    }

    [Theory]
    [InlineData(null, "2026-10-23T12:15:59Z")]
    [InlineData("2026-02-30", "2026-10-23T12:15:59Z")]
    [InlineData("2026-09-20", null)]
    [InlineData("2026-09-20", "2026-09-19T12:15:59Z")]
    [InlineData("2026-09-20", "not-a-time")]
    public void OngoingMetadataMustHaveValidCutoffAndRetrieval(string? cutoff, string? retrieved)
    {
        var b = SyntheticBundle(); Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(Replace(b, Current(b) with { ResultsThrough = cutoff, RetrievedAt = retrieved })));
    }

    [Fact]
    public void V2RejectsBeyondCutoffBadMembershipAndWeakenedHistoricalValidation()
    {
        var b = SyntheticBundle(); var c = Current(b);
        foreach (var bad in new[] { c with { Matches = [c.Matches[0] with { Date = "2026-09-21" }, .. c.Matches[1..]] },
            c with { Clubs = [c.Clubs[0] with { Id = "fictional-club" }, .. c.Clubs[1..]] }, c with { Status = "complete" },
            c with { ScheduledMatchCount = 50 }, c with { Matches = [.. c.Matches, c.Matches[0]] } })
            Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(Replace(b, bad)));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(b with { Seasons = [b.Seasons[0] with { Matches = b.Seasons[0].Matches[..^1] }, .. b.Seasons[1..]] }));
        Assert.Throws<LeagueReferenceValidationException>(() => new LeagueModel(b with { SchemaVersion = 1, Version = LeagueReferenceCodec.Version }));
    }

    [Theory]
    [InlineData("homeGoals")]
    [InlineData("awayGoals")]
    public void MissingFullTimeGoalFieldsAreRejectedInsteadOfBecomingZero(string field)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(SyntheticBundle(), LeagueReferenceCodec.Json))!;
        json["seasons"]![2]!["matches"]![0]!.AsObject().Remove(field);
        Assert.Throws<LeagueReferenceValidationException>(() => LeagueReferenceCodec.Decode(JsonSerializer.SerializeToUtf8Bytes(json)));
    }

    [Fact]
    public void CurrentCompetitionBadgeManifestPinsAllTwentyOriginalPngsWithSeparateRights()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data", "current-season", "2026-27", "crest-sources.json")));
        var metadata = document.RootElement; Assert.Contains("Not CC0", metadata.GetProperty("rights").GetString());
        var assets = metadata.GetProperty("assets").EnumerateArray().ToArray(); Assert.Equal(20, assets.Length);
        Assert.Equal(20, assets.Select(a => a.GetProperty("clubId").GetString()).Distinct().Count());
        foreach (var asset in assets)
        {
            var club = asset.GetProperty("clubId").GetString(); var expected = "assets/crests/" + club + "-2026.png";
            Assert.Equal(expected, asset.GetProperty("crestPath").GetString());
            Assert.Equal("src/Matchmate.Web/wwwroot/" + expected, asset.GetProperty("path").GetString());
            var bytes = File.ReadAllBytes(Path.Combine(Root, asset.GetProperty("path").GetString()!));
            Assert.Equal("89504e470d0a1a0a", Convert.ToHexStringLower(bytes.AsSpan(0, 8))); Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal(asset.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes))); Assert.Equal(asset.GetProperty("bytes").GetInt32(), bytes.Length);
            Assert.Equal(asset.GetProperty("width").GetInt32(), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
            Assert.Equal(asset.GetProperty("height").GetInt32(), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
            Assert.StartsWith("https://resources.premierleague.com/premierleague/badges/100/t", asset.GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task VersionedStoreFallsBackOnlyForAbsenceAndRetriesCorruptV2()
    {
        var reads = new List<string>();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(SyntheticBundle(), LeagueReferenceCodec.Json);
        var legacy = SyntheticBundle() with { SchemaVersion = 1, Version = LeagueReferenceCodec.Version, Seasons = SyntheticBundle().Seasons[..2] };
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy, LeagueReferenceCodec.Json);
        var absent = LeagueReferenceStore.Versioned((version, _) => { reads.Add(version); return Task.FromResult<byte[]?>(version == LeagueReferenceCodec.CurrentVersion ? null : legacyBytes); });
        Assert.Equal(2, (await absent.ReadAsync(default)).Seasons().Length); Assert.Equal(new[] { LeagueReferenceCodec.CurrentVersion, LeagueReferenceCodec.Version }, reads);
        reads.Clear(); var valid = false;
        var corrupt = LeagueReferenceStore.Versioned((version, _) => { reads.Add(version); return Task.FromResult<byte[]?>(valid ? bytes : "{}"u8.ToArray()); });
        await Assert.ThrowsAsync<LeagueReferenceValidationException>(() => corrupt.ReadAsync(default)); Assert.Equal(new[] { LeagueReferenceCodec.CurrentVersion }, reads);
        valid = true; Assert.Equal(3, (await corrupt.ReadAsync(default)).Seasons().Length); Assert.Equal(2, reads.Count);
        var unavailable = LeagueReferenceStore.Versioned((_, _) => throw new IOException("read failure"));
        await Assert.ThrowsAsync<IOException>(() => unavailable.ReadAsync(default));
        using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => corrupt.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task LocalStorePrefersV2AndDoesNotHideInvalidV2BehindValidLegacy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "matchmate-current-season-" + Guid.NewGuid().ToString("N"));
        try
        {
            var oldPath = Path.Combine(folder, ".local", "league-reference-v1", "league-reference.json");
            var newPath = Path.Combine(folder, ".local", "league-reference-v2", "league-reference.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!); Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            var b = SyntheticBundle(); var old = b with { SchemaVersion = 1, Version = LeagueReferenceCodec.Version, Seasons = b.Seasons[..2] };
            await File.WriteAllBytesAsync(oldPath, JsonSerializer.SerializeToUtf8Bytes(old, LeagueReferenceCodec.Json));
            Assert.Equal(2, (await LeagueReferenceStore.Local(folder).ReadAsync(default)).Seasons().Length);
            await File.WriteAllBytesAsync(newPath, "{}"u8.ToArray());
            await Assert.ThrowsAsync<LeagueReferenceValidationException>(() => LeagueReferenceStore.Local(folder).ReadAsync(default));
            await File.WriteAllBytesAsync(newPath, JsonSerializer.SerializeToUtf8Bytes(b, LeagueReferenceCodec.Json));
            Assert.Equal(3, (await LeagueReferenceStore.Local(folder).ReadAsync(default)).Seasons().Length);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AuthenticatedExistingApiReturnsThreeSeasonsAndCurrentSnapshot()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(SyntheticBundle(), LeagueReferenceCodec.Json);
        await using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILeagueReferenceStore>(new LeagueReferenceStore(_ => Task.FromResult<byte[]?>(bytes)))));
        using var http = api.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/league/seasons")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token()); var client = new ReplayApiClient(http);
        Assert.Equal(3, (await client.LeagueSeasonsAsync(default)).Length);
        var result = await client.LeagueSnapshotAsync("2026-27", null, "everton", default); Assert.Equal(50, result.Completed); Assert.Equal(380, result.Season.ScheduledMatchCount);
        Assert.Equal("ongoing", result.Season.Status); Assert.Equal(LeagueReferenceCodec.CurrentVersion, result.Version);
    }

    [Fact]
    public async Task ExistingUiRendersCoverageAndSwitchesWithExplicitQueryContext()
    {
        var model = new LeagueModel(SyntheticBundle()); var current = model.Snapshot("2026-27", null, "everton");
        using var playback = Render(new SeasonPlayback(), ("Snapshot", current)); Assert.Contains("50 / 380", Text(playback)); Assert.Contains("Ongoing snapshot", Text(playback));
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/seasons", StringComparison.Ordinal) ? model.Seasons() : model.Snapshot("2026-27", 1, "everton"))) { BaseAddress = new("https://test.invalid/") };
        await using var page = new LeagueExplorer(); Set(page, "Api", new ReplayApiClient(http)); var navigation = new Nav(); Set(page, "Navigation", navigation);
        Set(page, "Season", "2026-27"); Set(page, "DateIndex", 1); Set(page, "Club", "everton"); await Invoke(page, "OnParametersSetAsync");
        var snapshot = (LeagueSeasonSnapshot)Get(page, "_snapshot")!; Assert.Equal("2026-27", snapshot.Season.SeasonKey); Assert.Equal(1, snapshot.DateIndex); Assert.Equal("everton", snapshot.SelectedClub!.Club.Id);
        Assert.EndsWith("?season=2026-27&date=1&club=everton", navigation.Uri);
        using var rendered = Render(page); Assert.Contains("Results through 20 September 2026", Text(rendered)); Assert.Contains("23 October 2026", Text(rendered)); Assert.Contains("No automatic updates", Text(rendered));
        using var picker = Render(new SeasonPicker(), ("Seasons", model.Seasons()), ("Selected", "2026-27"), ("_open", true)); Assert.Contains("2026/27", Text(picker)); Assert.Contains("Ongoing snapshot", Text(picker));
        Assert.True(Text(picker).IndexOf("2026/27", StringComparison.Ordinal) < Text(picker).IndexOf("2016/17", StringComparison.Ordinal));
        Assert.True(Text(picker).IndexOf("2016/17", StringComparison.Ordinal) < Text(picker).IndexOf("2015/16", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshRootChoosesNewestAvailableSeasonWithoutAClubAndExplicitOlderQueryWins(bool legacy)
    {
        var bundle = SyntheticBundle();
        var model = new LeagueModel(legacy ? bundle with { SchemaVersion = 1, Version = LeagueReferenceCodec.Version, Seasons = bundle.Seasons[..2] } : bundle);
        var newest = legacy ? "2016-17" : "2026-27";
        Assert.Equal(legacy ? new[] { "2016-17", "2015-16" } : new[] { "2026-27", "2016-17", "2015-16" }, model.Seasons().Select(s => s.SeasonKey));
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/seasons", StringComparison.Ordinal) ? model.Seasons()
            : model.Snapshot(request.RequestUri.AbsolutePath.Split('/')[^1], request.RequestUri.Query.Contains("dateIndex=1", StringComparison.Ordinal) ? 1 : null))) { BaseAddress = new("https://test.invalid/") };
        await using var page = new LeagueExplorer(); Set(page, "Api", new ReplayApiClient(http)); var navigation = new Nav("/"); Set(page, "Navigation", navigation);
        await Invoke(page, "OnParametersSetAsync"); var snapshot = Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot"));
        Assert.Equal(newest, snapshot.Season.SeasonKey); Assert.Null(snapshot.SelectedClub); Assert.Null(Get(page, "_club")); Assert.Contains("season=" + newest, navigation.Uri);
        Set(page, "Season", "2015-16"); Set(page, "DateIndex", 1); await Invoke(page, "OnParametersSetAsync");
        snapshot = Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")); Assert.Equal("2015-16", snapshot.Season.SeasonKey); Assert.Equal(1, snapshot.DateIndex);
    }

    [Fact]
    public async Task SeasonCallbackFromWestHamClearsAbsentClubAndCommitsCurrentSeason()
    {
        var model = new LeagueModel(SyntheticBundle()); var old = model.Snapshot("2015-16", null, "west-ham"); var requests = new List<string>();
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Query.Contains("clubId=west-ham", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(model.Snapshot("2026-27", null)) };
        })) { BaseAddress = new("https://test.invalid/") };
        await using var page = new LeagueExplorer(); Set(page, "Api", new ReplayApiClient(http)); var navigation = new Nav("/?season=2015-16&date=38&club=west-ham"); Set(page, "Navigation", navigation);
        Set(page, "_snapshot", old); Set(page, "_seasons", model.Seasons()); Set(page, "_season", "2015-16"); Set(page, "_club", "west-ham");
        await Invoke(page, "ChangeSeasonAsync", "2026-27");
        var snapshot = Assert.IsType<LeagueSeasonSnapshot>(Get(page, "_snapshot")); Assert.Equal("2026-27", snapshot.Season.SeasonKey); Assert.Null(snapshot.SelectedClub);
        Assert.Equal(new[] { "/api/league/seasons/2026-27?clubId=west-ham", "/api/league/seasons/2026-27" }, requests);
        Assert.Contains("season=2026-27", navigation.Uri); Assert.DoesNotContain("club=", navigation.Uri); Assert.False((bool)Get(page, "_loadError")!);
    }

    // Explicitly synthetic contract fixtures keep ordinary CI independent of ignored local source bundles.
    private static LeagueReferenceBundle SyntheticBundle()
    {
        var ids = new[] { "arsenal", "aston-villa", "bournemouth", "brentford", "brighton", "chelsea", "coventry", "crystal-palace", "everton", "fulham", "hull", "ipswich", "leeds", "liverpool", "manchester-city", "manchester-united", "newcastle", "nottingham-forest", "sunderland", "tottenham" };
        var clubs = ids.Select((id, i) => new LeagueClub(id, "Synthetic club " + i, "TST", "Test town", "Test ground", 100 + i, 200, false, null)).ToArray();
        LeagueSeasonData Season(int year, int rounds)
        {
            var seasonClubs = year == 2026 ? clubs : clubs.Select(c => c.Id == "coventry" ? c with { Id = "west-ham" } : c).ToArray();
            var games = new List<LeagueMatch>();
            for (var leg = 0; leg < (rounds == 5 ? 1 : 2); leg++)
            {
                var ring = Enumerable.Range(0, 20).ToList();
                for (var round = 0; round < (rounds == 5 ? 5 : 19); round++)
                {
                    var date = year == 2026 ? new DateOnly(2026, 8, 21).AddDays(round == 4 ? 30 : round * 7) : new DateOnly(year, 8, 1).AddDays(leg * 19 + round);
                    for (var pair = 0; pair < 10; pair++)
                    {
                        var home = ring[pair]; var away = ring[19 - pair]; if (leg == 1) (home, away) = (away, home);
                        games.Add(new(seasonClubs[home].Id + ":" + seasonClubs[away].Id, date.ToString("yyyy-MM-dd"), seasonClubs[home].Id, seasonClubs[away].Id, (home + round) % 3, (away + round) % 3, "Synthetic round"));
                    }
                    var last = ring[^1]; ring.RemoveAt(19); ring.Insert(1, last);
                }
            }
            return new(year + "-" + ((year + 1) % 100).ToString("D2"), year + "/" + ((year + 1) % 100).ToString("D2"), seasonClubs, games.ToArray(), year == 2026 ? "ongoing" : "complete", 380,
                year == 2026 ? "2026-09-20" : null, year == 2026 ? "2026-10-23T12:15:59Z" : null);
        }
        var metadata = JsonSerializer.SerializeToElement(new { source = "Inline synthetic test-only data; never product facts" });
        return new(2, LeagueReferenceCodec.CurrentVersion, LeagueReferenceCodec.Timing, "Synthetic contract calculation", [new("inline-test", "inline-test-fixtures", new string('a', 64), "Test only")], "Test licence", metadata, metadata,
            [Season(2015, 38), Season(2016, 38), Season(2026, 5)]);
    }

    private sealed class Handler(Func<HttpRequestMessage, object> get) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(get(request)) }); }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> get) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(get(request)); }
    private sealed class Nav : NavigationManager
    {
        public Nav(string path = "/?season=2026-27&date=1&club=everton") => Initialize("https://localhost:7410/", "https://localhost:7410" + path);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
    }
    private static void Set(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null) property.SetValue(target, value); else target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    }
    private static object? Get(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static Task Invoke(object target, string name, params object[] args) => (Task)target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!;
    private static RenderTreeBuilder Render(object component, params (string Key, object? Value)[] parameters)
    {
        foreach (var (key, value) in parameters) Set(component, key, value);
        var builder = new RenderTreeBuilder(); component.GetType().GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [builder]); return builder;
    }
    private static string Text(RenderTreeBuilder b)
    {
        var frames = b.GetFrames(); var text = new List<string>();
        foreach (var frame in frames.Array.Take(frames.Count))
        {
            if (frame.FrameType == RenderTreeFrameType.Text) text.Add(frame.TextContent);
            else if (frame.FrameType == RenderTreeFrameType.Markup) text.Add(frame.MarkupContent);
            else if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeValue is RenderFragment fragment)
            { using var child = new RenderTreeBuilder(); fragment(child); text.Add(Text(child)); }
        }
        return string.Concat(text);
    }
}
#pragma warning restore BL0006
