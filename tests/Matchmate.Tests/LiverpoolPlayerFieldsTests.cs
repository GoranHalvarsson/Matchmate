using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Matchmate.Tests;

public sealed class LiverpoolPlayerFieldsTests
{
    private const string Version = "liverpool-generated-v2-cards-v1-f068a719fb27-45d0952330c2";
    private static string P(string name) => "person-pl-reference-" + name;
    private static readonly PreparedGeneratedDistanceStore Distance = PreparedGeneratedDistanceStore.LoadEmbedded();
    private static async Task<FootballView> View(long cursor)
    {
        var store = GeneratedPlayerDistanceTests.ExistingStore;
        var dataset = (await store.CatalogAsync(default)).Single(d => d.Manifest.DatasetVersion == Version);
        return await store.QueryAsync(new(dataset.Manifest.DatasetId, Version, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            cursor, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision), default);
    }
    private static long? Cm(FootballView view, string name) => view.GeneratedDistance!.Players.Single(p => p.PersonId == P(name)).CumulativeCentimetres;

    [Theory]
    [InlineData(60000, 4041, 2137, 1874)]
    [InlineData(3359800, 54041, 48364, 50269)]
    [InlineData(3360000, 54041, 48364, 50269)]
    [InlineData(3360200, 54041, 48364, 50269)]
    [InlineData(3419800, 54041, 48364, 50269)]
    [InlineData(3420000, 54041, 48364, 50269)]
    [InlineData(3420200, 54041, 48364, 50269)]
    [InlineData(6540000, 103200, 48364, 93171)]
    public async Task PinnedPackMatchesIndependentRawFrameGoldensIncludingBothHalftimeBoundaries(long cursor, long thiago, long yarmoliuk, long janelt)
    {
        var raw = await View(cursor); var before = JsonSerializer.Serialize(raw); var view = Distance.Attach(raw);
        Assert.Equal(GeneratedDistanceProjection.LiverpoolDatasetSha256, GeneratedDistanceProjection.Fingerprint(raw.Dataset));
        Assert.Equal(thiago, Cm(view, "brentford-thiago")); Assert.Equal(yarmoliuk, Cm(view, "brentford-yarmoliuk")); Assert.Equal(janelt, Cm(view, "brentford-janelt"));
        Assert.Equal(40, view.GeneratedDistance!.Players.Length); Assert.Equal(cursor, view.GeneratedDistance.CursorMs);
        Assert.Same(raw.Frame, view.Frame); Assert.Same(raw.Events, view.Events);
        Assert.Null(view.IllustrationStatistics); Assert.Null(view.GeneratedActionRecordIds); Assert.Null(raw.GeneratedDistance);
        Assert.DoesNotContain(FootballActionPolicy.GeneratedModelActionsV1, view.Dataset.Manifest.Capabilities);
        Assert.Equal(before, JsonSerializer.Serialize(raw));
        if (cursor is >= 3360000 and < 3420000)
        {
            Assert.Equal("halftime_transition", view.Phase); Assert.Equal(3360000, view.PausedDisplayMs);
            Assert.Equal(cursor, view.LocalMs); Assert.Equal(3360000, ExperiencePresentation.MatchClock(view));
        }
    }

    [Fact]
    public async Task SubstitutionsDismissalsNeverPresentAndNativeFloorRemainStableOnRewind()
    {
        foreach (var cursor in new long[] { 3359800, 3360000, 3360200, 3419800, 3420000, 3420200, 6540000, 3360000, 0 })
        {
            var view = Distance.Attach(await View(cursor));
            Assert.Equal(cursor < 3360000 ? null : cursor <= 3420200 ? 0L : 7439L, Cm(view, "liverpool-frimpong"));
            if (cursor == 0) { Assert.Equal(22, view.GeneratedDistance!.Players.Count(p => p.CumulativeCentimetres == 0)); Assert.Equal(18, view.GeneratedDistance.Players.Count(p => p.CumulativeCentimetres is null)); }
        }
        var beforeEntry = Distance.Attach(await View(3839999)); var entry = Distance.Attach(await View(3840000));
        Assert.Null(Cm(beforeEntry, "liverpool-ngumoha")); Assert.Equal(0, Cm(entry, "liverpool-ngumoha"));
        Assert.Equal(7439, Cm(beforeEntry, "liverpool-frimpong")); Assert.Equal(7439, Cm(entry, "liverpool-frimpong"));
        foreach (var (name, exit, expected) in new[] { ("brentford-yarmoliuk", 3180000L, 48364L), ("brentford-janelt", 6240000L, 93171L) })
        {
            var prior = Distance.Attach(await View(exit - 200)); var gone = Distance.Attach(await View(exit)); var end = Distance.Attach(await View(6540000));
            Assert.Equal(expected, Cm(prior, name)); Assert.Equal(expected, Cm(gone, name)); Assert.Equal(expected, Cm(end, name));
            Assert.Contains(P(name), prior.Frame!.ActiveSubjectIds); Assert.DoesNotContain(P(name), gone.Frame!.ActiveSubjectIds);
        }
        var full = Distance.Attach(await View(6540000));
        Assert.Equal(32, full.GeneratedDistance!.Players.Count(p => p.CumulativeCentimetres is not null)); Assert.Equal(8, full.GeneratedDistance.Players.Count(p => p.CumulativeCentimetres is null));
        var floor = Distance.Attach(await View(60000)); var offGrid = Distance.Attach(await View(60199));
        Assert.Equal(floor.GeneratedDistance!.Players, offGrid.GeneratedDistance!.Players);
        Assert.Equal(floor.GeneratedDistance.Players, Distance.Attach(await View(60000)).GeneratedDistance!.Players);
    }

    [Fact]
    public async Task EveryDatasetAndTemporalFenceFailsClosedAndClearsStaleDistance()
    {
        var view = Distance.Attach(await View(3360200)); var d = view.Dataset; var q = view.Context;
        FootballView[] invalid = [view with { Dataset = d with { Closed = false } }, view with { Dataset = d with { PendingRecords = 1 } },
            view with { Dataset = d with { HeadSequence = 518 } }, view with { Dataset = d with { Manifest = d.Manifest with { ManifestRevision = 3 } } },
            view with { Dataset = d with { Manifest = d.Manifest with { Sources = [d.Manifest.Sources[0] with { ArtifactHash = new string('a', 64) }, .. d.Manifest.Sources.Skip(1)] } } },
            view with { Dataset = d with { People = [d.People[0] with { Name = "Changed" }, .. d.People.Skip(1)] } },
            view with { Dataset = d with { Fixture = d.Fixture! with { Pitch = d.Fixture.Pitch with { LengthM = 106 } } } },
            view with { Dataset = d with { Schedule = d.Schedule! with { PolicyRevision = 2 } } },
            view with { Context = q with { DatasetVersion = "other" } }, view with { Context = q with { StreamId = "other" } },
            view with { Context = q with { FixtureId = "other" } }, view with { Context = q with { PresentationPolicyRevision = 2 } },
            view with { Context = q with { ApplicationSequence = 516 } }, view with { Context = q with { EffectiveDate = "2026-10-17" } },
            view with { Context = q with { KnowledgeAsOfUtc = DateTimeOffset.UnixEpoch } }, view with { Context = q with { KnowledgeMode = "known_then" } },
            view with { Context = q with { CursorMs = 6540001 } }, view with { Phase = null }, view with { PausedDisplayMs = null },
            view with { LocalMs = 3360000 }, view with { PeriodId = "other" }];
        foreach (var mismatch in invalid) Assert.Null(Distance.Attach(mismatch).GeneratedDistance);
        Assert.NotNull(Distance.Attach(view with { Context = q with { ApplicationSequence = 517 } }).GeneratedDistance);
        var root = LocalPaths.FindRoot();
        var chelsea = new PreparedGeneratedDistanceStore(File.ReadAllBytes(Path.Combine(root, "data/generated-replay-distance/brentford-full-v5-distance-v1.gz")));
        var liverpool = new PreparedGeneratedDistanceStore(File.ReadAllBytes(Path.Combine(root, "data/generated-replay-distance/liverpool-cards-v1-distance-v1.gz")));
        Assert.Null(chelsea.Attach(view).GeneratedDistance); Assert.Null(liverpool.Attach(await GeneratedPlayerDistanceTests.ExistingView(0)).GeneratedDistance);
        var old = (await GeneratedPlayerDistanceTests.ExistingStore.CatalogAsync(default)).Where(ds => ds.Manifest.DatasetId == d.Manifest.DatasetId && ds.Manifest.DatasetVersion != Version);
        Assert.NotEmpty(old);
        foreach (var prior in old)
        {
            var oldView = await GeneratedPlayerDistanceTests.ExistingStore.QueryAsync(new(prior.Manifest.DatasetId, prior.Manifest.DatasetVersion, prior.Stream.StreamId,
                prior.Manifest.FixtureId, 0, "retrospective_replay", prior.Schedule!.PolicyId, prior.Schedule.PolicyRevision), default);
            Assert.Null(Distance.Attach(oldView).GeneratedDistance); Assert.Empty(MatchRoleCatalog.Embedded.Project(oldView));
        }
    }

    [Fact]
    public async Task ExactlyFiveFixtureSpecificReportRolesLeaveThirtyFiveUnknownIncludingSharedChelseaPeople()
    {
        var view = await View(6540000); var before = JsonSerializer.Serialize(view); var roles = MatchRoleCatalog.Embedded.Project(view);
        Assert.Equal(5, roles.Count); Assert.Equal(35, view.Dataset.People.Count(p => !roles.ContainsKey(p.PersonId)));
        foreach (var (name, role) in new[] { ("brentford-van-den-berg", MatchRole.Defender), ("brentford-ouattara", MatchRole.Winger),
            ("brentford-thiago", MatchRole.Striker), ("brentford-yarmoliuk", MatchRole.Midfielder), ("liverpool-jacquet", MatchRole.Defender) })
        {
            Assert.Equal(role, roles[P(name)].Role); Assert.Equal(MatchRoleScope.MatchReportDescription, roles[P(name)].Scope);
            Assert.Equal("RoleSourceBrentford", roles[P(name)].SourceTitleKey);
            Assert.Equal("https://www.brentfordfc.com/en/news/match-reports-brentford-1-liverpool-2-premier-league-17-11-2026", roles[P(name)].SourceUrl);
            Assert.NotEmpty(roles[P(name)].Locator); Assert.DoesNotContain('"', roles[P(name)].Locator);
        }
        Assert.False(roles.ContainsKey(P("brentford-kayode"))); Assert.False(roles.ContainsKey(P("brentford-schade")));
        Assert.Equal(roles, MatchRoleCatalog.Embedded.Project(await View(0))); Assert.Equal(before, JsonSerializer.Serialize(view));
        var d = view.Dataset;
        foreach (var mismatch in new[] { view with { Dataset = d with { HeadSequence = 518 } }, view with { Dataset = d with { Closed = false } },
            view with { Dataset = d with { Manifest = d.Manifest with { Sources = d.Manifest.Sources.Where(s => s.SourceId != "brentford-report").ToArray() } } },
            view with { Dataset = d with { People = [d.People[0] with { Shirt = 99 }, .. d.People.Skip(1)] } },
            view with { Context = view.Context with { EffectiveDate = "2026-10-17" } }, view with { Context = view.Context with { KnowledgeMode = "known_then" } } })
            Assert.Empty(MatchRoleCatalog.Embedded.Project(mismatch));
        Assert.Equal(24, MatchRoleCatalog.Embedded.Project(await GeneratedPlayerDistanceTests.ExistingView(0)).Count);
    }

    [Fact]
    public void NativeProfilesDoNotShareCadencePrecisionOrFrameStateAndHalftimeNeverBridges()
    {
        var d = GeneratedPlayerDistanceTests.UnitDataset();
        var schedule = d.Schedule! with { Segments = [new("period-one", 0, 600, 0), new("period-one", 600, 1000, 600, "halftime_transition", 600), new("period-two", 1000, 1600, 0)] };
        GeneratedDistanceCalculator Calculator(GeneratedDistanceProfile profile) => new(d.Fixture!.Pitch, d.People, schedule, profile);
        FootballRecord Frame(int cursor, double x, string precision = "presentation_sample", string state = "generated", string player = GeneratedPlayerDistanceTests.Player,
            string slot = "slot-one", string[]? active = null)
        {
            var local = cursor < 1000 ? cursor : cursor - 1000;
            return new(FootballContract.Version, d.Manifest.DatasetId, d.Manifest.DatasetVersion, d.Manifest.FixtureId, [], "frame-" + cursor, 1, null, "active",
                new(cursor < 1000 ? "period-one" : "period-two", local, local, precision, true), null, [], [],
                new TrackingPayload("f", null, [GeneratedPlayerDistanceTests.Observation(player, x, slot: slot)], GeneratedPlayerDistanceTests.Observation("ball"), active ?? [player], state));
        }
        var liverpool = Calculator(GeneratedDistanceProfile.LiverpoolCardsV1);
        foreach (var (cursor, x) in new[] { (0, 0d), (200, 1d), (400, 2d), (600, 40d), (800, 50d), (1000, 90d), (1200, 89d), (1400, 88d), (1600, 87d) }) liverpool.Append(Frame(cursor, x));
        Assert.Equal(new[] { 0, 100, 200, 200, 200, 200, 300, 400, 500 }, liverpool.Snapshot()[GeneratedPlayerDistanceTests.Player].Select(s => s.CumulativeCentimetres));
        Assert.Equal(0, liverpool.ExcludedJumps);
        var entrant = Calculator(GeneratedDistanceProfile.LiverpoolCardsV1); entrant.Append(Frame(600, 40, player: GeneratedPlayerDistanceTests.Incoming));
        entrant.Append(Frame(800, 50, player: GeneratedPlayerDistanceTests.Incoming)); entrant.Append(Frame(1000, 90, player: GeneratedPlayerDistanceTests.Incoming));
        Assert.Equal(new[] { 0, 0, 0 }, entrant.Snapshot()[GeneratedPlayerDistanceTests.Incoming].Select(s => s.CumulativeCentimetres));
        var wrongLiverpool = Calculator(GeneratedDistanceProfile.LiverpoolCardsV1); wrongLiverpool.Append(Frame(0, 0, "source_sample", "generated-complete-model"));
        var wrongChelsea = Calculator(GeneratedDistanceProfile.ChelseaFullV5); wrongChelsea.Append(Frame(0, 0));
        Assert.Empty(wrongLiverpool.Snapshot()[GeneratedPlayerDistanceTests.Player]); Assert.Empty(wrongChelsea.Snapshot()[GeneratedPlayerDistanceTests.Player]);
        var breaks = Calculator(GeneratedDistanceProfile.LiverpoolCardsV1);
        breaks.Append(Frame(0, 0)); breaks.Append(Frame(400, 1)); breaks.Append(Frame(1000, 2)); breaks.Append(Frame(1200, 3, slot: "new-slot"));
        breaks.Append(Frame(1400, 4, slot: "new-slot", active: [])); breaks.Append(Frame(1600, 5, slot: "new-slot"));
        Assert.All(breaks.Snapshot()[GeneratedPlayerDistanceTests.Player], sample => Assert.Equal(0, sample.CumulativeCentimetres));
        var continuous = Calculator(GeneratedDistanceProfile.LiverpoolCardsV1); continuous.Append(Frame(0, 0)); continuous.Append(Frame(200, 2));
        Assert.Equal(200, continuous.Snapshot()[GeneratedPlayerDistanceTests.Player][^1].CumulativeCentimetres); // 2m is valid at 200ms, above Chelsea's per-step limit.
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task SharedRosterDisplaysLocalizedSourceRolesAndGeneratedMetresForLiverpool(MatchLanguage language)
    {
        var view = Distance.Attach(await View(6540000)); var before = JsonSerializer.Serialize(view);
        using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment fragment = builder => { builder.OpenComponent<PlayerRoster>(0); builder.AddAttribute(1, nameof(PlayerRoster.View), view);
                builder.AddAttribute(2, nameof(PlayerRoster.SelectedPerson), P("brentford-yarmoliuk")); builder.CloseComponent(); };
            var result = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CascadingValue<MatchLanguage>.Value)] = language, [nameof(CascadingValue<MatchLanguage>.ChildContent)] = fragment }));
            return WebUtility.HtmlDecode(result.ToHtmlString());
        });
        foreach (var key in new[] { "RoleDefender", "RoleWinger", "RoleStriker", "RoleMidfielder", "RoleUnverified", "RoleReportDescription", "RoleSourceBrentford", "GeneratedDistanceExplanation" })
            Assert.Contains(ReplayText.Get(language, key), html);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceMetres", 484m.ToString("N0", MatchLanguages.Culture(language))), html);
        Assert.Contains(ReplayText.Get(language, "CurrentOnPitch", "20"), html); Assert.Contains(ReplayText.Get(language, "RosterOffPitch"), html);
        Assert.Equal(72, Regex.Matches(html, "class=\"generated-distance-value\"").Count); Assert.Equal(before, JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task ApiUsesBothPreparedPacksWithoutAdditionalReadsOrActionStatistics()
    {
        var raw = await View(6540000); var counting = new CountingStore(raw);
        using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IFootballStore>(counting)));
        using var client = api.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        for (var i = 1; i <= 2; i++)
        {
            var response = await client.PostAsJsonAsync("/api/football/view", raw.Context); response.EnsureSuccessStatusCode();
            var view = (await response.Content.ReadFromJsonAsync<FootballView>())!; Assert.Equal(103200, Cm(view, "brentford-thiago"));
            Assert.Null(view.IllustrationStatistics); Assert.Null(view.GeneratedActionRecordIds); Assert.Equal(i, counting.Reads);
        }
    }
    private sealed class CountingStore(FootballView view) : IFootballStore
    {
        public int Reads { get; private set; }
        public Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token) { Reads++; return Task.FromResult(view with { Context = query }); }
        public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => throw new InvalidOperationException("No imports.");
        public Task<FootballLedger> ReadAsync(CancellationToken token) => throw new InvalidOperationException("No archive scans.");
        public Task<FootballDataset[]> CatalogAsync(CancellationToken token) => Task.FromResult(new[] { view.Dataset });
    }
}
