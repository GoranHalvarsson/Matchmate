using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmate.Tests;

public sealed class GeneratedPlayerDistanceTests
{
    internal const string Player = "person-one", Incoming = "person-two", Bench = "person-three";
    internal static FootballDataset UnitDataset()
    {
        var manifest = new FootballManifest(FootballContract.Version, "brentford-existing-illustration", "brent-full-v5-unit", "fixture-one", 6, "open",
            [new("generated", "unit-generated-source", new string('a', 64))], [], ["complete-existing-generated-100ms-model"], ["retrospective_replay"], "unit", []);
        var fixture = new FixturePayload("Unit", "Unit", new("team-one", "Home"), new("team-two", "Away"), new(105, 68),
            [new("period-one", 1, "First", 0, null), new("period-two", 2, "Second", 500, null)]);
        var schedule = new SchedulePayload("unit-policy", 1, [new("period-one", 0, 500, 0), new("period-two", 500, 1000, 0)], [], [], 100, "generated");
        return new(manifest, new("stream-one", manifest.DatasetId, manifest.DatasetVersion, manifest.FixtureId, "replay"), 979, true, fixture,
            [new(Player, "team-one", "Starter", 1, "starter"), new(Incoming, "team-one", "Substitute", 2, "bench"), new(Bench, "team-two", "Unused bench", 3, "bench")], schedule, 0);
    }
    internal static FootballView UnitView(FootballDataset dataset, long cursor)
    {
        var segment = dataset.Schedule!.Segments.FirstOrDefault(s => cursor < s.CursorEndMs) ?? dataset.Schedule.Segments[^1];
        var query = new FootballQuery(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            cursor, "retrospective_replay", dataset.Schedule.PolicyId, dataset.Schedule.PolicyRevision);
        var local = cursor - segment.CursorStartMs;
        return new(dataset, query, segment.PeriodId, local, null, [], [], 0, 0, false, "unit", []);
    }
    internal static FootballObservation Observation(string id = Player, double x = 10, double y = 10, string slot = "slot-one") =>
        new(id, slot, "present", x, y, null, "generated", "unknown", ["generated"]);
    private static FootballRecord Frame(int cursor, FootballObservation[]? players = null, string[]? active = null, string state = "generated-complete-model")
    {
        var dataset = UnitDataset(); var period = cursor < 500 ? "period-one" : "period-two"; var local = cursor < 500 ? cursor : cursor - 500;
        players ??= [Observation()];
        return new(FootballContract.Version, dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Manifest.FixtureId, [], "frame-" + cursor,
            1, null, "active", new(period, local, local, "source_sample", true), null, [new("generated", "generated", "unit", "synthetic", "generated", "unconfirmed")], [],
            new TrackingPayload("frame-" + cursor, null, players, Observation("ball"), active ?? players.Select(p => p.SubjectId).ToArray(), state));
    }
    private static GeneratedDistanceCalculator Calculator(FootballDataset? dataset = null)
    { dataset ??= UnitDataset(); return new(dataset.Fixture!.Pitch, dataset.People, dataset.Schedule!); }
    internal static GeneratedDistancePackMetadata Metadata(FootballDataset dataset) => new(1, "unit-distance-pack", dataset,
        new string('a', 64), new string('b', 64), new string('c', 64), 100, 15, 11, 0);
    internal static IReadOnlyDictionary<string, GeneratedDistanceSample[]> UnitPrefixes() => new Dictionary<string, GeneratedDistanceSample[]>
    { [Player] = [new(0, 0), new(100, 50), new(200, 100)], [Incoming] = [new(500, 0), new(600, 100)], [Bench] = [] };
    internal static PreparedGeneratedDistanceStore UnitStore() => new(PreparedGeneratedDistanceStore.Encode(Metadata(UnitDataset()), UnitPrefixes()));

    [Fact]
    public void EuclideanThreeFourFivePathUsesNativeDeltasAndCumulativeRounding()
    {
        var dataset = UnitDataset(); dataset = dataset with { Schedule = dataset.Schedule! with { Segments = [new("period-one", 0, 2000, 0)] } };
        var calculator = Calculator(dataset);
        // A 3m by 4m path in ten 100ms steps: exactly 5m, below the continuity filter.
        for (var i = 0; i <= 10; i++)
        {
            var record = Frame(i * 100, [Observation(x: 3d * i / 10, y: 4d * i / 10)]);
            calculator.Append(record with { FootballTime = new("period-one", i * 100, i * 100, "source_sample", true) });
        }
        Assert.Equal(500, calculator.Snapshot()[Player][^1].CumulativeCentimetres); Assert.Equal(0, calculator.ExcludedJumps);
        var tiny = Calculator();
        for (var i = 0; i <= 4; i++) tiny.Append(Frame(i * 100, [Observation(x: i * .004, y: 0)]));
        Assert.Equal(2, tiny.Snapshot()[Player][^1].CumulativeCentimetres); // Not four rounded-to-zero deltas.
    }

    [Fact]
    public void LookupFloorsAtNativeSampleAndSeekRewindRepeatHaveNoRunningState()
    {
        var store = UnitStore(); var dataset = UnitDataset();
        foreach (var (cursor, expected) in new[] { (0, 0), (99, 0), (100, 50), (199, 50), (200, 100), (999, 100), (100, 50), (0, 0), (200, 100), (200, 100) })
        {
            var result = store.Attach(UnitView(dataset, cursor)).GeneratedDistance!;
            Assert.Equal(cursor, result.CursorMs); Assert.Equal(expected, result.Players.Single(p => p.PersonId == Player).CumulativeCentimetres);
        }
        Assert.Null(GeneratedDistanceProjection.Prefix(UnitPrefixes()[Player], -1));
    }

    [Fact]
    public void SubstitutionsNeverInheritSlotDistanceAndDeparturesRetainTheirTotal()
    {
        var calculator = Calculator();
        calculator.Append(Frame(0)); calculator.Append(Frame(100, [Observation(x: 11)]));
        calculator.Append(Frame(200, [Observation(Incoming, x: 40)])); calculator.Append(Frame(300, [Observation(Incoming, x: 41)]));
        var prefixes = calculator.Snapshot();
        Assert.Equal(new[] { 0, 100 }, prefixes[Player].Select(s => s.CumulativeCentimetres));
        Assert.Equal(new[] { 0, 100 }, prefixes[Incoming].Select(s => s.CumulativeCentimetres)); Assert.Empty(prefixes[Bench]);
        var store = new PreparedGeneratedDistanceStore(PreparedGeneratedDistanceStore.Encode(Metadata(UnitDataset()), prefixes));
        Assert.Null(store.Attach(UnitView(UnitDataset(), 199)).GeneratedDistance!.Players.Single(p => p.PersonId == Incoming).CumulativeCentimetres);
        var end = store.Attach(UnitView(UnitDataset(), 1000)).GeneratedDistance!;
        Assert.Equal(100, end.Players.Single(p => p.PersonId == Player).CumulativeCentimetres); Assert.Null(end.Players.Single(p => p.PersonId == Bench).CumulativeCentimetres);
    }

    [Fact]
    public void PeriodResetKeepsTotalWithoutCountingDirectionReversal()
    {
        var calculator = Calculator(); calculator.Append(Frame(300)); calculator.Append(Frame(400, [Observation(x: 11)]));
        calculator.Append(Frame(500, [Observation(x: 94)])); calculator.Append(Frame(600, [Observation(x: 93)]));
        Assert.Equal(new[] { 0, 100, 100, 200 }, calculator.Snapshot()[Player].Select(s => s.CumulativeCentimetres));
        Assert.Equal(0, calculator.ExcludedJumps);
    }

    public static IEnumerable<object[]> BrokenSamples()
    {
        yield return ["absent"]; yield return ["inactive"]; yield return ["missing"]; yield return ["observed"]; yield return ["estimated"];
        yield return ["out-x"]; yield return ["out-y"]; yield return ["nan"]; yield return ["infinity"]; yield return ["no-slot"];
        yield return ["duplicate-person"]; yield return ["duplicate-slot"]; yield return ["frame-method"];
    }
    [Theory, MemberData(nameof(BrokenSamples))]
    public void InvalidOrMissingOrNonGeneratedSamplesBreakContinuity(string reason)
    {
        var calculator = Calculator(); calculator.Append(Frame(0));
        var observation = Observation(x: 10.5);
        observation = reason switch
        {
            "missing" => observation with { Availability = "missing" }, "observed" => observation with { Method = "observed" },
            "estimated" => observation with { Method = "estimated" }, "out-x" => observation with { XM = 106 }, "out-y" => observation with { YM = -1 },
            "nan" => observation with { XM = double.NaN }, "infinity" => observation with { YM = double.PositiveInfinity },
            "no-slot" => observation with { SlotId = null }, _ => observation
        };
        FootballObservation[] players = reason == "absent" ? [] : reason == "duplicate-person" ? [observation, observation]
            : reason == "duplicate-slot" ? [observation, observation with { SubjectId = Incoming }] : [observation];
        calculator.Append(Frame(100, players, reason == "inactive" ? [] : null, reason == "frame-method" ? "observed" : "generated-complete-model"));
        calculator.Append(Frame(200, [Observation(x: 11)])); calculator.Append(Frame(300, [Observation(x: 12)]));
        Assert.Equal(100, calculator.Snapshot()[Player][^1].CumulativeCentimetres);
    }

    [Fact]
    public void CadenceGapsSlotChangesAndLargeAuthoredJumpsDoNotBridge()
    {
        var gaps = Calculator(); gaps.Append(Frame(0)); gaps.Append(Frame(200, [Observation(x: 11)])); gaps.Append(Frame(300, [Observation(x: 12)]));
        Assert.Equal(100, gaps.Snapshot()[Player][^1].CumulativeCentimetres);
        var slot = Calculator(); slot.Append(Frame(0)); slot.Append(Frame(100, [Observation(x: 11, slot: "other-slot")])); slot.Append(Frame(200, [Observation(x: 12, slot: "other-slot")]));
        Assert.Equal(100, slot.Snapshot()[Player][^1].CumulativeCentimetres);
        var jump = Calculator(); jump.Append(Frame(0)); jump.Append(Frame(100, [Observation(x: 12)])); jump.Append(Frame(200, [Observation(x: 13)]));
        Assert.Equal(100, jump.Snapshot()[Player][^1].CumulativeCentimetres); Assert.Equal(1, jump.ExcludedJumps);
    }

    [Fact]
    public void QueryFencesAndEveryBindingChangeSuppressDistanceAndClearStaleAttachment()
    {
        var dataset = UnitDataset(); var view = UnitStore().Attach(UnitView(dataset, 600)); var store = UnitStore();
        var d = dataset; var m = d.Manifest; var f = d.Fixture!; var s = d.Schedule!; var q = view.Context;
        FootballView[] mismatches = [view with { Context = q with { KnowledgeMode = "completed_reference" } }, view with { Context = q with { KnowledgeAsOfUtc = DateTimeOffset.UnixEpoch } },
            view with { Context = q with { ApplicationSequence = 978 } }, view with { Context = q with { EffectiveDate = "2026-09-18" } },
            view with { Context = q with { DatasetVersion = "other-version" } }, view with { Context = q with { StreamId = "other-stream" } },
            view with { Context = q with { FixtureId = "other-fixture" } }, view with { Context = q with { PresentationPolicyRevision = 2 } },
            view with { Dataset = d with { Closed = false } }, view with { Dataset = d with { PendingRecords = 1 } }, view with { Dataset = d with { HeadSequence = 980 } },
            view with { Dataset = d with { Manifest = m with { ManifestRevision = 7 } } }, view with { Dataset = d with { Manifest = m with { Sources = [m.Sources[0] with { ArtifactHash = new string('d', 64) }] } } },
            view with { Dataset = d with { Fixture = f with { Pitch = f.Pitch with { LengthM = 106 } } } },
            view with { Dataset = d with { People = [d.People[0] with { Name = "Changed" }, .. d.People.Skip(1)] } },
            view with { Dataset = d with { Schedule = s with { Segments = [s.Segments[0], s.Segments[1] with { LocalStartMs = 1 }] } } },
            view with { Dataset = d with { Schedule = s with { Segments = [null!] } } },
            view with { PeriodId = "period-one" }, view with { LocalMs = 0 }];
        foreach (var mismatch in mismatches) Assert.Null(store.Attach(mismatch).GeneratedDistance);
        Assert.NotNull(store.Attach(view with { Context = q with { ApplicationSequence = 979 } }).GeneratedDistance);
        Assert.Equal(100, store.Attach(view with { Context = q with { SubjectId = Player } }).GeneratedDistance!.Players.Single(p => p.PersonId == Player).CumulativeCentimetres);
    }

    [Fact]
    public void StartupRejectsCorruptionSizesMalformedHeadersInvalidPrefixesAndRetainsImmutableCopies()
    {
        var metadata = Metadata(UnitDataset()); var prefixes = UnitPrefixes(); var bytes = PreparedGeneratedDistanceStore.Encode(metadata, prefixes);
        Assert.Throws<InvalidDataException>(() => new PreparedGeneratedDistanceStore([]));
        Assert.Throws<InvalidDataException>(() => new PreparedGeneratedDistanceStore(new byte[GeneratedDistanceLimits.CompressedBytes + 1]));
        Assert.Throws<InvalidDataException>(() => new PreparedGeneratedDistanceStore(bytes.AsSpan(0, bytes.Length / 2)));
        Assert.Throws<InvalidDataException>(() => PreparedGeneratedDistanceStore.Encode(metadata with { SourceChunksSha256 = "wrong" }, prefixes));
        Assert.Throws<InvalidDataException>(() => PreparedGeneratedDistanceStore.Encode(metadata with { Version = 2 }, prefixes));
        Assert.Throws<InvalidDataException>(() => PreparedGeneratedDistanceStore.Encode(metadata with { FrameCount = GeneratedDistanceLimits.Frames + 1 }, prefixes));
        foreach (var samples in new GeneratedDistanceSample[][] { [new(0, 1)], [new(100, 0), new(0, 1)], [new(0, 0), new(101, 100)], [new(0, 0), new(100, -1)] })
        {
            var invalid = prefixes.ToDictionary(); invalid[Player] = samples;
            Assert.Throws<InvalidDataException>(() => PreparedGeneratedDistanceStore.Encode(metadata, invalid));
        }
        static byte[] Gzip(byte[] raw) { using var target = new MemoryStream(); using (var zip = new GZipStream(target, CompressionLevel.SmallestSize, true)) zip.Write(raw); return target.ToArray(); }
        Assert.Throws<InvalidDataException>(() => new PreparedGeneratedDistanceStore(Gzip(new byte[GeneratedDistanceLimits.ExpandedBytes + 1])));
        var badHeader = new byte[12]; "MMGD0001"u8.CopyTo(badHeader); BitConverter.GetBytes(GeneratedDistanceLimits.HeaderBytes + 1).CopyTo(badHeader, 8);
        Assert.Throws<InvalidDataException>(() => new PreparedGeneratedDistanceStore(Gzip(badHeader)));
        var store = new PreparedGeneratedDistanceStore(bytes); Array.Fill(bytes, (byte)0); prefixes[Player][1] = new(100, 999);
        Assert.Equal(50, store.Attach(UnitView(UnitDataset(), 100)).GeneratedDistance!.Players.Single(p => p.PersonId == Player).CumulativeCentimetres);
    }

    internal static readonly IFootballStore ExistingStore = new ChunkedLocalFootballStore(Path.Combine(LocalPaths.FindRoot(), ".local/football-full-v1"));
    internal static async Task<FootballView> ExistingView(long cursor)
    {
        var dataset = (await ExistingStore.CatalogAsync(default)).Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal));
        return await ExistingStore.QueryAsync(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            cursor, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision), default);
    }
    [Theory]
    [InlineData(60000, 4974, 6015, 2948, 554)]
    [InlineData(2820000, 245755, 262866, 265603, 28378)]
    [InlineData(5880000, 462713, 541291, 409585, 60071)]
    public async Task PinnedPackMatchesIndependentRawExportGoldens(long cursor, int damsgaard, int thiago, int welbeck, int kelleher)
    {
        var store = PreparedGeneratedDistanceStore.LoadEmbedded(); var raw = await ExistingView(cursor); var distance = store.Attach(raw).GeneratedDistance!;
        Assert.NotNull(distance); Assert.Equal(cursor, distance.CursorMs); Assert.Equal(40, distance.Players.Length);
        foreach (var (name, cm) in new[] { ("brentford-damsgaard", damsgaard), ("brentford-thiago", thiago), ("chelsea-welbeck", welbeck), ("brentford-kelleher", kelleher) })
            Assert.Equal(cm, distance.Players.Single(p => p.PersonId == "person-pl-reference-" + name).CumulativeCentimetres);
        Assert.Same(raw.Frame, store.Attach(raw).Frame); Assert.Same(raw.IllustrationStatistics, store.Attach(raw).IllustrationStatistics);
        if (cursor == 5880000) { Assert.Equal(31, distance.Players.Count(p => p.CumulativeCentimetres is not null)); Assert.Equal(9, distance.Players.Count(p => p.CumulativeCentimetres is null)); }
    }

    [Fact]
    public async Task ApiAttachesPreparedDistanceOnceWithoutAnyExtraStoreReadsAndAustralianStaysSeparate()
    {
        var raw = await ExistingView(60000); var counting = new CountingStore(raw);
        using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IFootballStore>(counting)));
        using var client = api.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        var prepared = api.Services.GetRequiredService<PreparedGeneratedDistanceStore>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/football/view", raw.Context); response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<FootballView>())!;
            Assert.Equal(6015, result.GeneratedDistance!.Players.Single(p => p.PersonId.EndsWith("brentford-thiago", StringComparison.Ordinal)).CumulativeCentimetres);
            Assert.Equal(i + 1, counting.Reads); Assert.Same(prepared, api.Services.GetRequiredService<PreparedGeneratedDistanceStore>());
        }
        var australian = (await ExistingStore.CatalogAsync(default)).Single(d => d.Manifest.DatasetVersion.StartsWith("aus-full-v1-", StringComparison.Ordinal));
        var ausView = await ExistingStore.QueryAsync(new(australian.Manifest.DatasetId, australian.Manifest.DatasetVersion, australian.Stream.StreamId, australian.Manifest.FixtureId,
            australian.Schedule!.Segments[0].CursorStartMs, "retrospective_replay", australian.Schedule.PolicyId, australian.Schedule.PolicyRevision), default);
        Assert.Null(prepared.Attach(ausView).GeneratedDistance);
        Assert.DoesNotContain("generatedDistance", JsonSerializer.Serialize(prepared.Attach(ausView), FootballCodec.Json));
    }
    private sealed class CountingStore(FootballView view) : IFootballStore
    {
        public int Reads { get; private set; }
        public Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token) { Reads++; return Task.FromResult(view with { Context = query }); }
        public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => throw new InvalidOperationException("No imports.");
        public Task<FootballLedger> ReadAsync(CancellationToken token) => throw new InvalidOperationException("No archive scans.");
    }
}
