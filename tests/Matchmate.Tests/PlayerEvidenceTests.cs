using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmate.Tests;

// Reads the existing closed prepared store only. No append, recovery, source import, media or provider calls.
public sealed class PlayerEvidenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Danny = "person-pl-reference-chelsea-welbeck";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Root = LocalPaths.FindRoot();
    private static readonly byte[] Bytes = File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json"));
    private static readonly PreparedPlayerEvidencePack DannyBaseline = JsonSerializer.Deserialize<PreparedPlayerEvidencePack>(
        File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v1.json")), Json)!;
    private static JsonDocument Review() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-review-v2.json")));
    private static PreparedPlayerEvidencePack Pack => JsonSerializer.Deserialize<PreparedPlayerEvidencePack>(Bytes, Json)!;
    private static readonly PreparedPlayerEvidenceStore Prepared = PreparedPlayerEvidenceStore.LoadEmbedded();
    private static readonly IFootballStore Store = new ChunkedLocalFootballStore(Path.Combine(Root, ".local/football-full-v1"));
    private static readonly Lazy<Task<FootballDataset>> Dataset = new(async () => (await Store.CatalogAsync(default))
        .Single(d => d.Manifest.DatasetVersion == Pack.Binding.DatasetVersion));
    private static async Task<FootballView> View(long cursor, string? subject = Danny)
    {
        var dataset = await Dataset.Value;
        return await Store.QueryAsync(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId,
            dataset.Manifest.FixtureId, cursor, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision,
            dataset.HeadSequence, SubjectId: subject), default);
    }
    [Theory]
    [InlineData(599999, 0)] [InlineData(600000, 1)] [InlineData(3119999, 1)] [InlineData(3120000, 2)]
    [InlineData(3659999, 2)] [InlineData(3660000, 3)]
    public async Task AcceptedFactsUseConservativePeriodLocalScheduleBoundaries(long cursor, int count)
    {
        var view = await View(cursor); var attached = Prepared.Attach(view); var evidence = Assert.IsType<PlayerEvidenceView>(attached.PlayerEvidence);
        var selected = PlayerEvidenceProjection.ForSubject(attached, Danny)!;
        Assert.Equal(count, selected.Observations.Length); Assert.All(selected.Observations, o => Assert.Equal(Danny, o.PersonId));
        Assert.All(evidence.Observations.SelectMany(o => o.Evidence), e => Assert.Null(e.AvailableAtUtc));
        Assert.Same(view.Dataset, attached.Dataset); Assert.Same(view.Events, attached.Events); Assert.Same(view.Frame, attached.Frame);
        Assert.Equal(8, view.Dataset.Manifest.Sources.Length);
        Assert.Equal(JsonSerializer.Serialize(DannyBaseline.Observations), JsonSerializer.Serialize(Pack.Observations.Where(o => o.PersonId == Danny)));
    }
    [Fact]
    public async Task NamedAndSelectedPlayersUseSameBriefWithoutSelectedGoalFutureAccessOrManifestMutation()
    {
        var raw = await View(5880000); Assert.Equal(14, raw.Events.Length); Assert.Equal(22, raw.Frame!.Players.Length);
        var ui = raw.Dataset.People.Single(p => p.Name == "Igor Thiago").PersonId; var reads = 0;
        var executor = new MatchWorkflowExecutor(async (q, token) => { reads++; return Prepared.Attach(await Store.QueryAsync(q, token)); });
        MatchWorkflowRequest Request(string workflow, FootballQuery q) => new(workflow, MatchWorkflowRegistry.Version,
            new("unit-owner", 1, new(q, 966, new("goal-3", 5760000, "90+1")), MatchWorkflowTemporalMode.ReplayAtCursor),
            workflow == "named_player_context" ? new("How did Danny Welbeck play in this match?") : null);
        var named = await executor.ExecuteAsync(Request("named_player_context", raw.Context with { SubjectId = ui }), _ => true);
        var selected = await executor.ExecuteAsync(Request("selected_player_context", raw.Context with { SubjectId = Danny }), _ => true);
        Assert.Equal(2, reads); Assert.Equal(Danny, named.ResolvedSubjectId); Assert.Null(selected.ResolvedSubjectId);
        Assert.Equal(ui, named.Bundle!.Context.Query.SubjectId); Assert.Null(named.Bundle.Context.SelectedEvent);
        Assert.Equal(selected.Bundle!.Items.Single(i => i.Id == "player_involvement").Text, named.Bundle.Items.Single(i => i.Id == "player_involvement").Text);
        Assert.All(named.Bundle.EventProvenance, p => Assert.Contains(named.Bundle.Sources, s => s.SourceId == p.SourceId));
        Assert.DoesNotContain(named.Bundle.Sources, s => s.SourceId == Pack.Id); Assert.InRange(named.Bundle.Sources.Length, 1, 8);
        using var brief = JsonDocument.Parse(named.Bundle.Items.Single(i => i.Id == "player_involvement").Text);
        Assert.Equal(Pack.Id, brief.RootElement.GetProperty("reviewedBrief").GetProperty("packId").GetString());
        Assert.Equal(raw.PlayerEvidence?.PackSha256 ?? Convert.ToHexStringLower(SHA256.HashData(Bytes)),
            brief.RootElement.GetProperty("reviewedBrief").GetProperty("packSha256").GetString());
        Assert.DoesNotContain(named.Bundle.Sources, s => s.SourceId == "visual-review"); // The existing substitution legitimately retains event-sequences provenance.
        Assert.Equal(8, raw.Dataset.Manifest.Sources.Length); Assert.Equal(14, raw.Events.Length); Assert.Equal(22, raw.Frame.Players.Length);
        Assert.InRange(named.Bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars);
        Assert.InRange(JsonSerializer.Serialize(named.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
        var text = named.Bundle.Items.Single(i => i.Id == "player_involvement").Text;
        Assert.Contains("layoff", text); Assert.Contains("ball at or immediately", text); Assert.Contains("centrally positioned", text);
        Assert.Contains("unverified", text); Assert.DoesNotContain("dw-aerial-contact", text); Assert.DoesNotContain("goal-3", text);
        const string assessment = "Welbeck's reported layoff helped set up Neto's saved chance. Football interpretation: linking play can help create an opening. Reviewed samples also show him centrally in attack; these sparse passages do not establish an overall rating.";
        var item = named.Bundle.Items.Single(i => i.Id == "player_involvement");
        Assert.Equal(assessment, MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer = assessment, evidenceIds = new[] { item.Id } }),
            new Dictionary<string, MatchChatEvidence> { [item.Id] = new(item.Id, item.Tool, item.Text, item.NumericTokens.ToArray()) }).Answer);
        var before = await executor.ExecuteAsync(Request("named_player_context", raw.Context with { CursorMs = 599999, SubjectId = ui }), _ => true);
        Assert.Equal(3, reads); Assert.DoesNotContain("dw-chance-01", before.Bundle!.Items.Single(i => i.Id == "player_involvement").Text);
    }
    [Theory]
    [InlineData("version")] [InlineData("fixture")] [InlineData("stream")] [InlineData("head")] [InlineData("open")]
    [InlineData("application")] [InlineData("source")] [InlineData("roster")] [InlineData("known-then")] [InlineData("live")]
    public async Task UnmatchedScopeReturnsOriginalViewWithoutCrossVersionOrPartialHeadEvidence(string changed)
    {
        var view = await View(5880000);
        view = changed switch {
            "version" => view with { Context = view.Context with { DatasetVersion = "another-version" } },
            "fixture" => view with { Context = view.Context with { FixtureId = "another-fixture" } },
            "stream" => view with { Context = view.Context with { StreamId = "another-stream" } },
            "head" => view with { Dataset = view.Dataset with { HeadSequence = 967 } },
            "open" => view with { Dataset = view.Dataset with { Closed = false } },
            "application" => view with { Context = view.Context with { ApplicationSequence = 965 } },
            "source" => view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Sources = view.Dataset.Manifest.Sources.Select((s, i) => i == 0 ? s with { ArtifactHash = new('a', 64) } : s).ToArray() } } },
            "roster" => view with { Dataset = view.Dataset with { People = view.Dataset.People.Select((p, i) => i == 0 ? p with { Name = "Changed name" } : p).ToArray() } },
            "known-then" => view with { Context = view.Context with { KnowledgeMode = "known_then" } },
            _ => view with { Dataset = view.Dataset with { Stream = view.Dataset.Stream with { DeliveryMode = "live" } } }
        };
        Assert.Same(view, Prepared.Attach(view)); Assert.Null(view.PlayerEvidence);
    }
    [Fact]
    public async Task SameGenericPreparedPathSupportsAnotherRosterParticipantAndUnknownIsNotZero()
    {
        var raw = await View(5880000); var other = raw.Dataset.People.Single(p => p.Name == "Pedro Neto").PersonId;
        var observation = Pack.Observations[1] with { Id = "unit-third-player", PersonId = other,
            Text = "A developer fixture observes the roster player at one instant.", RelatedPersonIds = [],
            Evidence = [Pack.Observations[1].Evidence[0] with { EvidenceId = "unit-third-frame" }] };
        var pack = Pack with { Observations = [observation] }; var store = new PreparedPlayerEvidenceStore(JsonSerializer.SerializeToUtf8Bytes(pack, Json));
        var attached = store.Attach(raw); Assert.Equal(other, Assert.Single(PlayerEvidenceProjection.ForSubject(attached, other)!.Observations).PersonId);
        Assert.Empty(PlayerEvidenceProjection.ForSubject(attached, Danny)!.Observations);
        using var empty = JsonDocument.Parse(MatchPlayerEvidence.Build(attached, Danny).Item.Text);
        Assert.Equal("no_eligible_reviewed_observations", empty.RootElement.GetProperty("reviewedBrief").GetProperty("availability").GetString());
        Assert.Equal("not_supplied", empty.RootElement.GetProperty("reviewedBrief").GetProperty("metrics").GetString());
    }
    [Fact]
    public async Task AustralianAndLegacyWireStayUnenrichedAndAcceptedArraysAreNotChanged()
    {
        var dataset = (await Store.CatalogAsync(default)).Single(d => d.Manifest.DatasetId == "australian-recorded");
        var query = new FootballQuery(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
            60000, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision);
        var view = await Store.QueryAsync(query, default); Assert.Same(view, Prepared.Attach(view)); Assert.Null(view.PlayerEvidence);
        var json = JsonSerializer.Serialize(view); Assert.DoesNotContain("PlayerEvidence", json); Assert.Null(JsonSerializer.Deserialize<FootballView>(json)!.PlayerEvidence);
    }
    [Theory]
    [InlineData("duplicate-fact")] [InlineData("unknown-person")] [InlineData("unknown-related")] [InlineData("unknown-source")]
    [InlineData("unknown-period")] [InlineData("duplicate-source")] [InlineData("source-collision")] [InlineData("duplicate-evidence")]
    [InlineData("text-size")] [InlineData("qualification-size")] [InlineData("availability")] [InlineData("generated")]
    public void CommonValidationRejectsMalformedPreparedState(string changed)
    {
        var pack = Pack; var o = pack.Observations[0];
        pack = changed switch {
            "duplicate-fact" => pack with { Observations = pack.Observations.Add(o) },
            "unknown-person" => pack with { Observations = [o with { PersonId = "unknown-person" }] },
            "unknown-related" => pack with { Observations = [o with { RelatedPersonIds = ["unknown-person"] }] },
            "unknown-source" => pack with { Observations = [o with { Evidence = [o.Evidence[0] with { SourceId = "unknown-source" }] }] },
            "unknown-period" => pack with { Observations = [o with { FootballTime = o.FootballTime with { PeriodId = "unknown-period" } }] },
            "duplicate-source" => pack with { Sources = pack.Sources.Add(pack.Sources[0]) },
            "source-collision" => pack with { Sources = [pack.Binding.SourcePins[0] with { ArtifactHash = new('a', 64) }] },
            "duplicate-evidence" => pack with { Observations = [o with { Evidence = [o.Evidence[0], o.Evidence[0]] }] },
            "text-size" => pack with { Observations = [o with { Text = new('x', PlayerEvidenceLimits.TextChars + 1) }] },
            "qualification-size" => pack with { Observations = [o with { Qualification = new('x', PlayerEvidenceLimits.QualificationChars + 1) }] },
            "availability" => pack with { Observations = [o with { Evidence = [o.Evidence[0] with { AvailableAtUtc = DateTimeOffset.UtcNow }] }] },
            _ => pack with { Observations = [o with { Evidence = [o.Evidence[0] with { Method = "generated" }] }] }
        };
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore(JsonSerializer.SerializeToUtf8Bytes(pack, Json)));
    }
    [Fact]
    public void JsonSizeUnknownMembersNonfiniteAndMissingFieldsRejectBeforeUse()
    {
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore(new byte[PlayerEvidenceLimits.PackBytes + 1]));
        var json = Encoding.UTF8.GetString(Bytes);
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore(Encoding.UTF8.GetBytes(json.Replace("\"eligibleAfterMs\": 600000", "\"eligibleAfterMs\": NaN"))));
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore(Encoding.UTF8.GetBytes(json.Replace("\"version\": 1,", "\"version\": 1, \"unexpected\": true,"))));
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore("{}"u8));
    }
    [Fact]
    public async Task UpstreamForgedBindingOrFutureFactFailsClosedInPlayerWorkflow()
    {
        var view = Prepared.Attach(await View(5880000)); var evidence = view.PlayerEvidence!;
        Assert.Throws<InvalidDataException>(() => MatchPlayerEvidence.Build(view with { Dataset = view.Dataset with { Fixture = view.Dataset.Fixture! with { Periods = null! } } }, Danny));
        Assert.Throws<InvalidDataException>(() => MatchPlayerEvidence.Build(view with { Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with { Segments = [null!] } } }, Danny));
        foreach (var forged in new[] { evidence with { Binding = evidence.Binding with { HeadSequence = 965 } },
            evidence with { PackSha256 = new('a', 64) },
            evidence with { Sources = evidence.Sources.Remove(evidence.Sources.Single(s => s.SourceId == evidence.PackId)) },
            evidence with { Observations = evidence.Observations.SetItem(0, evidence.Observations[0] with { EligibleAfterMs = 9000000 }) },
            evidence with { Sources = evidence.Sources.RemoveAt(0) } })
        {
            var bad = view with { PlayerEvidence = forged };
            Assert.Throws<InvalidDataException>(() => MatchPlayerEvidence.Build(bad, Danny));
            var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(bad with { Context = q })).ExecuteAsync(new("selected_player_context",
                MatchWorkflowRegistry.Version, new("unit", 1, new(view.Context, 966), MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
            Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle);
        }
    }
    [Fact]
    public async Task EmbeddedSnapshotIsImmutableConcurrentAndApiUsesExactlyOneStoreReadPerView()
    {
        var raw = await View(5880000); var mutable = Bytes.ToArray(); var prepared = new PreparedPlayerEvidenceStore(mutable); Array.Fill(mutable, (byte)0);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => prepared.Attach(raw))));
        Assert.All(results, v => Assert.Equal(Pack.Observations.Length, v.PlayerEvidence!.Observations.Length));
        Assert.Throws<NotSupportedException>(() => ((IList<PlayerEvidenceObservation>)results[0].PlayerEvidence!.Observations).Clear());
        var counting = new CountingStore(Store);
        await using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IFootballStore>(counting)));
        using var client = api.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        var cache = api.Services.GetRequiredService<PreparedPlayerEvidenceStore>();
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync("/api/football/view", raw.Context); response.EnsureSuccessStatusCode();
            var view = (await response.Content.ReadFromJsonAsync<FootballView>())!;
            Assert.Equal(Pack.Observations.Select(o => o.Id), view.PlayerEvidence!.Observations.Select(o => o.Id));
            Assert.Same(cache, api.Services.GetRequiredService<PreparedPlayerEvidenceStore>()); Assert.Equal(i + 1, counting.Reads);
        }
    }

    [Fact]
    public void V2MatchesOfficialPlayedCensusFrozenDispositionsAndOriginalDannyEvidence()
    {
        using var review = Review(); var approved = review.RootElement;
        using var official = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data/official-match-records/brentford-chelsea-2026-09-18.json")));
        static string Canonical(string sourceId) => "person-pl-reference-" + sourceId.Replace(':', '-');
        var played = official.RootElement.GetProperty("players").EnumerateArray().Where(p => p.GetProperty("status").GetString() == "starter")
            .Select(p => Canonical(p.GetProperty("id").GetString()!)).Concat(official.RootElement.GetProperty("events").EnumerateArray()
                .Where(e => e.GetProperty("kind").GetString() == "substitution").Select(e => Canonical(e.GetProperty("on").GetString()!)))
            .ToHashSet(StringComparer.Ordinal);
        var participants = approved.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(31, played.Count); Assert.Equal(31, participants.Length);
        Assert.True(played.SetEquals(participants.Select(p => p.GetProperty("personId").GetString()!)));
        var unused = Pack.Roster.Select(p => p.PersonId).Where(p => !played.Contains(p)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(9, unused.Count); Assert.True(unused.SetEquals(approved.GetProperty("unusedBench").EnumerateArray().Select(p => p.GetString()!)));
        Assert.Equal(57, Pack.Observations.Length); Assert.Equal("prepared-player-briefs-v2", Pack.Id);
        Assert.Equal(JsonSerializer.Serialize(DannyBaseline.Binding), JsonSerializer.Serialize(Pack.Binding));
        Assert.Equal(JsonSerializer.Serialize(DannyBaseline.Roster), JsonSerializer.Serialize(Pack.Roster));
        Assert.Equal(JsonSerializer.Serialize(DannyBaseline.Sources), JsonSerializer.Serialize(Pack.Sources.Take(4)));
        Assert.Equal(JsonSerializer.Serialize(DannyBaseline.Observations), JsonSerializer.Serialize(Pack.Observations.Where(o => o.PersonId == Danny)));
        Assert.All(Pack.Observations, o => Assert.Contains(o.PersonId, played));
        Assert.All(Pack.Observations.GroupBy(o => o.PersonId), group => Assert.InRange(group.Count(), 1, PlayerEvidenceLimits.PerPlayer));
        Assert.Equal(26, participants.Count(p => p.GetProperty("newObservationCount").GetInt32() > 0));
        foreach (var participant in participants)
        {
            var id = participant.GetProperty("personId").GetString()!;
            var observations = Pack.Observations.Where(o => o.PersonId == id && !o.Id.StartsWith("dw-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(participant.GetProperty("newObservationCount").GetInt32(), observations.Length);
            if (id == Danny) continue;
            var expectedReports = participant.GetProperty("reportedPerformanceClaimIds").EnumerateArray().Select(c => c.GetString()!).Order().ToArray();
            Assert.Equal(expectedReports, observations.Where(o => o.Evidence[0].Method == "reported").Select(o => o.Id[..5]).Order());
            Assert.Equal(participant.GetProperty("nativeObservationIds").EnumerateArray().Select(c => c.GetString()!).Order(),
                observations.Where(o => o.Evidence[0].Method == "observed").Select(o => o.Id).Order());
        }
        Assert.Equal(46, Pack.Observations.Count(o => o.Id.StartsWith("rp-", StringComparison.Ordinal)));
        Assert.Equal(8, Pack.Observations.Count(o => o.Id.StartsWith("vp-", StringComparison.Ordinal)));
        Assert.DoesNotContain(Pack.Observations, o => o.Id.StartsWith("rp-14-", StringComparison.Ordinal)
            || o.Id.StartsWith("rp-19-", StringComparison.Ordinal) || o.Id.StartsWith("rp-20-", StringComparison.Ordinal));
        Assert.All(Pack.Observations.SelectMany(o => o.Evidence), e => Assert.Null(e.AvailableAtUtc));
    }

    [Fact]
    public void ReviewSourcePinsAndTransitiveReceiptsResolveWithoutClaimingAnIndependentPrimarySource()
    {
        using var review = Review(); var source = Assert.Single(Pack.Sources, s => s.SourceId == "player-brief-review-v2");
        Assert.Equal("e7a39e8e87698440c41e781cdd1235ccdf9d7129a0616a900cdba97812870983", source.ArtifactHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, source.Locator)))), source.ArtifactHash);
        foreach (var input in review.RootElement.GetProperty("inputs").EnumerateArray())
        {
            var path = input.GetProperty("path").GetString()!;
            Assert.Equal(input.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, path)))));
        }
        foreach (var pin in Pack.Binding.SourcePins)
            Assert.Equal(pin.ArtifactHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, pin.Locator)))));
        using var reports = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "output/player-performance-review/remaining-v2/report-review.json")));
        foreach (var row in review.RootElement.GetProperty("reportRows").EnumerateArray().Where(r => r.TryGetProperty("qualification", out _)))
        {
            var id = row.GetProperty("id").GetString()!;
            var observations = Pack.Observations.Where(o => o.Id.StartsWith(id + "-", StringComparison.Ordinal)).ToArray();
            foreach (var observation in observations)
            {
                Assert.Equal(row.GetProperty("text").GetString(), observation.Text);
                Assert.Equal(row.GetProperty("qualification").GetString(), observation.Qualification);
                var evidence = Assert.Single(observation.Evidence); Assert.Equal(source.SourceId, evidence.SourceId);
                Assert.Equal("reported", evidence.Method); Assert.Equal("reported", evidence.ClaimStatus);
                Assert.Contains(id, evidence.Locator); Assert.Contains(row.GetProperty("locator").GetString()!, evidence.Locator);
                foreach (var report in row.GetProperty("sources").EnumerateArray())
                    Assert.Contains(reports.RootElement.GetProperty("sources").GetProperty(report.GetString()!).GetString()!, evidence.Locator);
                Assert.Contains("normalized review", evidence.OriginalFlags);
            }
        }
        foreach (var row in review.RootElement.GetProperty("visualRows").EnumerateArray())
        {
            var observation = Assert.Single(Pack.Observations, o => o.Id == row.GetProperty("id").GetString());
            var evidence = Assert.Single(observation.Evidence); Assert.Equal(source.SourceId, evidence.SourceId);
            Assert.Equal(row.GetProperty("text").GetString(), observation.Text); Assert.Equal(row.GetProperty("qualification").GetString(), observation.Qualification);
            Assert.Equal(row.GetProperty("periodLocalMs").GetInt64(), observation.FootballTime.StartMs);
            Assert.Equal(observation.FootballTime.StartMs, observation.FootballTime.EndMs);
            Assert.Equal(row.GetProperty("conservativeEligibleAfterMs").GetInt64(), observation.EligibleAfterMs);
            Assert.Equal(row.GetProperty("displayedClock").GetString(), observation.FootballTime.ReportedClock);
            Assert.Contains(row.GetProperty("frame").GetString()!, evidence.Locator);
            Assert.Contains(row.GetProperty("frameSha256").GetString()!, evidence.Locator);
            Assert.Contains(row.GetProperty("receiptSha256").GetString()!, evidence.OriginalFlags);
            Assert.Contains(review.RootElement.GetProperty("sourceVideoSha256").GetString()!, evidence.OriginalFlags);
        }
        Assert.All(Pack.Observations.Where(o => o.Id.StartsWith("rp-12-", StringComparison.Ordinal) || o.Id.StartsWith("rp-13-", StringComparison.Ordinal)),
            o => Assert.Contains("same", o.Qualification, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(2819999, false, false)] [InlineData(2820000, true, false)]
    [InlineData(5879999, true, false)] [InlineData(5880000, true, true)]
    public async Task UntimedPhaseReportsAppearOnlyAtTheirActualPeriodEnd(long cursor, bool first, bool second)
    {
        var view = Prepared.Attach(await View(cursor)); var observations = view.PlayerEvidence!.Observations;
        foreach (var period in Pack.Binding.PeriodIds)
        {
            var expected = Pack.Observations.Where(o => o.FootballTime.Precision == "unknown" && o.FootballTime.PeriodId == period).Select(o => o.Id).Order();
            var actual = observations.Where(o => o.FootballTime.Precision == "unknown" && o.FootballTime.PeriodId == period).Select(o => o.Id).Order();
            if (period.EndsWith("-first", StringComparison.Ordinal) ? first : second) Assert.Equal(expected, actual); else Assert.Empty(actual);
        }
        Assert.All(Pack.Observations.Where(o => o.FootballTime.Precision == "unknown"), o => {
            Assert.Null(o.FootballTime.StartMs); Assert.Null(o.FootballTime.EndMs); Assert.False(string.IsNullOrWhiteSpace(o.FootballTime.ReportedClock));
        });
    }

    [Theory]
    [InlineData("start-only")] [InlineData("end-only")] [InlineData("wrong-precision")] [InlineData("missing-clock")]
    [InlineData("long-clock")] [InlineData("zero-release")] [InlineData("known-null")]
    public void UnknownClockValidationRejectsPartialInventedOrUnlabelledTimes(string fault)
    {
        var observation = Pack.Observations.First(o => o.FootballTime.Precision == "unknown"); var time = observation.FootballTime;
        observation = fault switch {
            "start-only" => observation with { FootballTime = time with { StartMs = 0 } },
            "end-only" => observation with { FootballTime = time with { EndMs = 0 } },
            "wrong-precision" => observation with { FootballTime = time with { Precision = "source_sample" } },
            "missing-clock" => observation with { FootballTime = time with { ReportedClock = " " } },
            "long-clock" => observation with { FootballTime = time with { ReportedClock = new('x', 181) } },
            "zero-release" => observation with { EligibleAfterMs = 0 },
            _ => Pack.Observations[0] with { FootballTime = Pack.Observations[0].FootballTime with { EndMs = null } }
        };
        Assert.Throws<InvalidDataException>(() => new PreparedPlayerEvidenceStore(JsonSerializer.SerializeToUtf8Bytes(Pack with { Observations = [observation] }, Json)));
    }

    [Theory]
    [InlineData("early-release")] [InlineData("late-release")] [InlineData("duplicate-endpoint")]
    [InlineData("overlapping-cursor")] [InlineData("overlapping-local")] [InlineData("overflow")] [InlineData("zero-duration")]
    public async Task ForgedReleaseOrMalformedScheduleNeverExposesUntimedFacts(string fault)
    {
        var raw = await View(5880000); var observation = Pack.Observations.First(o => o.FootballTime.Precision == "unknown");
        var pack = Pack with { Observations = [observation] }; var segments = raw.Dataset.Schedule!.Segments;
        if (fault is "early-release" or "late-release") pack = pack with { Observations = [observation with { EligibleAfterMs = observation.EligibleAfterMs + (fault == "early-release" ? -1 : 1) }] };
        else raw = raw with { Dataset = raw.Dataset with { Schedule = raw.Dataset.Schedule with { Segments = fault switch {
            "duplicate-endpoint" => [segments[0], segments[0], segments[1]],
            "overlapping-cursor" => [segments[0], segments[1] with { CursorStartMs = 1 }],
            "overlapping-local" => [segments[0], segments[1] with { PeriodId = segments[0].PeriodId, LocalStartMs = 1 }],
            "overflow" => [segments[0] with { LocalStartMs = long.MaxValue }, segments[1]],
            _ => [segments[0] with { CursorEndMs = segments[0].CursorStartMs }, segments[1]]
        } } } };
        var store = new PreparedPlayerEvidenceStore(JsonSerializer.SerializeToUtf8Bytes(pack, Json));
        Assert.Empty(store.Attach(raw).PlayerEvidence!.Observations);
        var forged = Prepared.Attach(await View(5880000));
        forged = raw with { PlayerEvidence = forged.PlayerEvidence! with { Observations = pack.Observations } };
        Assert.Throws<InvalidDataException>(() => PlayerEvidenceProjection.ForSubject(forged, observation.PersonId));
    }

    [Fact]
    public async Task ReportedMinutesAndNativePointsKeepTheirSeparateEligibilityBoundaries()
    {
        foreach (var observation in Pack.Observations.Where(o => o.Id.StartsWith("vp-", StringComparison.Ordinal)
            || o.Id.StartsWith("rp-01-", StringComparison.Ordinal) || o.Id.StartsWith("rp-16-", StringComparison.Ordinal) || o.Id.StartsWith("rp-21-", StringComparison.Ordinal)))
        {
            var segment = (await Dataset.Value).Schedule!.Segments.Single(s => s.PeriodId == observation.FootballTime.PeriodId);
            var release = segment.CursorStartMs + observation.EligibleAfterMs - segment.LocalStartMs;
            Assert.DoesNotContain(Prepared.Attach(await View(release - 1)).PlayerEvidence!.Observations, o => o.Id == observation.Id);
            Assert.Contains(Prepared.Attach(await View(release)).PlayerEvidence!.Observations, o => o.Id == observation.Id);
            Assert.True(observation.EligibleAfterMs >= observation.FootballTime.EndMs);
        }
        Assert.All(Pack.Observations.Where(o => o.Id.StartsWith("rp-01-", StringComparison.Ordinal)), o => {
            Assert.Equal(DannyBaseline.Observations[0].FootballTime, o.FootballTime); Assert.Equal(600000, o.EligibleAfterMs);
        });
        Assert.All(Pack.Observations.Where(o => o.Id.StartsWith("rp-16-", StringComparison.Ordinal)), o => {
            Assert.Equal("reported_minute", o.FootballTime.Precision); Assert.Equal(1320000, o.FootballTime.StartMs); Assert.Equal(1380000, o.EligibleAfterMs);
        });
        var debut = Assert.Single(Pack.Observations, o => o.Id.StartsWith("rp-21-", StringComparison.Ordinal));
        Assert.Equal("reported_minute", debut.FootballTime.Precision); Assert.Equal(2460000, debut.FootballTime.StartMs); Assert.Equal(2520000, debut.EligibleAfterMs);
    }

    [Fact]
    public async Task EveryPlayedPersonHasIdenticalNamedAndUiBriefWithCompleteBoundedSourcesAndNoTruncation()
    {
        var raw = Prepared.Attach(await View(5880000)); using var review = Review();
        var executor = new MatchWorkflowExecutor((q, _) => Task.FromResult(raw with { Context = q }));
        var budgets = review.RootElement.GetProperty("participants").EnumerateArray().Select(p => {
            var id = p.GetProperty("personId").GetString()!; var projection = MatchPlayerEvidence.Build(raw, id);
            using var item = JsonDocument.Parse(projection.Item.Text); var data = item.RootElement;
            var withoutBrief = MatchPlayerEvidence.Build(raw with { PlayerEvidence = raw.PlayerEvidence! with {
                Observations = raw.PlayerEvidence!.Observations.Where(o => o.PersonId != id).ToImmutableArray() } }, id);
            var candidate = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(withoutBrief.Item.Text)!;
            candidate["reviewedBrief"] = data.GetProperty("reviewedBrief").Clone();
            return new { personId = id, itemChars = projection.Item.Text.Length, sources = projection.Sources.Length,
                fullCandidateChars = JsonSerializer.Serialize(candidate).Length,
                fullSourceIds = withoutBrief.Sources.Select(s => s.SourceId).Concat(Pack.Observations.Where(o => o.PersonId == id)
                    .SelectMany(o => o.Evidence).Select(e => e.SourceId)).Distinct().ToArray(),
                legacyRecords = withoutBrief.Provenance.GroupBy(e => e.RecordId).Select(g => new { recordId = g.Key, sources = g.Select(e => e.SourceId).Distinct() }),
                eligibleEvents = data.GetProperty("eligibleTargetEvents").GetInt32(), omittedEvents = data.GetProperty("omittedEvents").GetInt32(),
                omittedObservations = data.GetProperty("reviewedBrief").GetProperty("omitted").GetInt32(),
                observations = Pack.Observations.Where(o => o.PersonId == id).Select(o => new { o.Id, textChars = o.Text.Length, qualificationChars = o.Qualification.Length }),
                missingness = projection.Missingness };
        }).ToArray();
        output.WriteLine("D098_BUNDLE_BUDGETS=" + JsonSerializer.Serialize(budgets));
        foreach (var participant in review.RootElement.GetProperty("participants").EnumerateArray())
        {
            var id = participant.GetProperty("personId").GetString()!; var person = raw.Dataset.People.Single(p => p.PersonId == id);
            MatchWorkflowRequest Request(string workflow) => new(workflow, MatchWorkflowRegistry.Version,
                new("unit-owner", 1, new(raw.Context with { SubjectId = workflow == "named_player_context" ? Danny : id }, 966,
                    new("goal-3", 5760000, "90+1")), MatchWorkflowTemporalMode.ReplayAtCursor),
                workflow == "named_player_context" ? new($"How did {person.Name} play in this match?") : null);
            var named = await executor.ExecuteAsync(Request("named_player_context"), _ => true);
            var selected = await executor.ExecuteAsync(Request("selected_player_context"), _ => true);
            Assert.Equal(MatchWorkflowStatus.Partial, named.Status); Assert.Equal(MatchWorkflowStatus.Partial, selected.Status);
            Assert.Contains("player_reviewed_observations_sparse", named.Bundle!.Missingness);
            Assert.Contains("player_reviewed_observations_sparse", selected.Bundle!.Missingness);
            Assert.Equal(id, named.ResolvedSubjectId);
            var item = named.Bundle!.Items.Single(i => i.Id == "player_involvement");
            Assert.Equal(selected.Bundle!.Items.Single(i => i.Id == item.Id).Text, item.Text);
            using var data = JsonDocument.Parse(item.Text);
            Assert.Equal(0, data.RootElement.GetProperty("omittedEvents").GetInt32());
            Assert.Equal(0, data.RootElement.GetProperty("reviewedBrief").GetProperty("omitted").GetInt32());
            Assert.Equal(Pack.Observations.Count(o => o.PersonId == id), data.RootElement.GetProperty("reviewedBrief").GetProperty("observations").GetArrayLength());
            Assert.Equal("not_supplied", data.RootElement.GetProperty("reviewedBrief").GetProperty("metrics").GetString());
            Assert.Equal(Pack.Id, data.RootElement.GetProperty("reviewedBrief").GetProperty("packId").GetString());
            Assert.Equal(raw.PlayerEvidence!.PackSha256, data.RootElement.GetProperty("reviewedBrief").GetProperty("packSha256").GetString());
            Assert.DoesNotContain(named.Bundle.Sources, s => s.SourceId == Pack.Id);
            Assert.Contains(raw.PlayerEvidence.Sources, s => s.SourceId == Pack.Id && s.ArtifactHash == raw.PlayerEvidence.PackSha256);
            Assert.DoesNotContain("player_events_truncated", named.Bundle.Missingness);
            Assert.DoesNotContain("player_reviewed_observations_truncated", named.Bundle.Missingness);
            var legacyRecords = raw.Events.Concat(raw.Possessions).Where(r => named.Bundle.EventProvenance.Any(e => e.RecordId == r.RecordId)).ToArray();
            Assert.Equal(legacyRecords.Any(r => r.Evidence.Length > MatchWorkflowLimits.EvidencePerEvent),
                named.Bundle.Missingness.Contains("player_provenance_truncated"));
            foreach (var record in legacyRecords)
                Assert.Equal(record.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).Select(e => e.EvidenceId),
                    named.Bundle.EventProvenance.Where(e => e.RecordId == record.RecordId).Select(e => e.EvidenceId));
            Assert.InRange(named.Bundle.Sources.Length, 1, MatchWorkflowLimits.Sources);
            Assert.All(named.Bundle.EventProvenance, e => Assert.Contains(named.Bundle.Sources, s => s.SourceId == e.SourceId));
            var factualIds = Pack.Binding.ContextSourceIds.Concat(named.Bundle.EventProvenance.Select(e => e.SourceId)).ToHashSet(StringComparer.Ordinal);
            Assert.True(factualIds.SetEquals(named.Bundle.Sources.Select(s => s.SourceId)));
            Assert.All(named.Bundle.Sources, source => Assert.Contains(raw.Dataset.Manifest.Sources.Concat(raw.PlayerEvidence.Sources),
                registered => registered.SourceId == source.SourceId && registered.ArtifactHash == source.ArtifactHash));
            Assert.InRange(JsonSerializer.Serialize(named.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
            Assert.Null(named.Bundle.Context.SelectedEvent);
        }
        Assert.Equal(14, raw.Events.Length); Assert.Equal(22, raw.Frame!.Players.Length); Assert.Equal(8, raw.Dataset.Manifest.Sources.Length);
    }

    [Theory]
    [InlineData("person-pl-reference-brentford-ajer", "starter", 0)] [InlineData("person-pl-reference-brentford-hickey", "bench", 1)]
    public async Task ParticipationOnlyGapsRemainMissingPerformanceInsteadOfZero(string id, string selection, int eventCount)
    {
        var raw = Prepared.Attach(await View(5880000, id));
        Assert.Empty(PlayerEvidenceProjection.ForSubject(raw, id)!.Observations);
        using var data = JsonDocument.Parse(MatchPlayerEvidence.Build(raw, id).Item.Text);
        Assert.Equal("no_eligible_reviewed_observations", data.RootElement.GetProperty("reviewedBrief").GetProperty("availability").GetString());
        Assert.Equal("not_supplied", data.RootElement.GetProperty("reviewedBrief").GetProperty("metrics").GetString());
        Assert.Equal(selection, data.RootElement.GetProperty("rosterSelection").GetString());
        Assert.Equal(eventCount, data.RootElement.GetProperty("eligibleTargetEvents").GetInt32());
    }

    [Fact]
    public async Task SelectingAnEarlierGoalDoesNotReleaseUnrelatedSecondHalfPlayerReports()
    {
        var raw = await View(3765000); var executor = new MatchWorkflowExecutor(async (q, token) => Prepared.Attach(await Store.QueryAsync(q, token)));
        var request = new MatchWorkflowRequest("selected_goal_event", MatchWorkflowRegistry.Version,
            new("unit-owner", 1, new(raw.Context, 966, new("goal-1", 3780000, "61")), MatchWorkflowTemporalMode.SelectedGoalRetrospective));
        var goal = (await View(3780000)).Events.Single(e => e.RecordId == "goal-1");
        var existingProvenanceLimit = goal.Evidence.Length > MatchWorkflowLimits.EvidencePerEvent;
        var result = await executor.ExecuteAsync(request, _ => true);
        Assert.Equal(existingProvenanceLimit ? MatchWorkflowStatus.Partial : MatchWorkflowStatus.Success, result.Status);
        if (existingProvenanceLimit) Assert.Contains("event_provenance_truncated", result.Bundle!.Missingness);
        Assert.Equal("goal-1", Assert.Single(result.Bundle!.EventProvenance.Select(e => e.RecordId).Distinct()));
        Assert.All(result.Bundle.EventProvenance, e => Assert.Equal("goal-1", e.RecordId));
        Assert.Equal(goal.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).Select(e => e.EvidenceId), result.Bundle.EventProvenance.Select(e => e.EvidenceId));
        Assert.InRange(result.Bundle.EventProvenance.Length, 1, MatchWorkflowLimits.EvidencePerEvent);
        Assert.DoesNotContain("rp-", JsonSerializer.Serialize(result.Bundle)); Assert.DoesNotContain("vp-", JsonSerializer.Serialize(result.Bundle));
        Assert.DoesNotContain(Prepared.Attach(raw).PlayerEvidence!.Observations, o => o.FootballTime.Precision == "unknown"
            && o.FootballTime.PeriodId.EndsWith("-second", StringComparison.Ordinal));
    }
    private sealed class CountingStore(IFootballStore inner) : IFootballStore
    {
        public int Reads;
        public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => throw new InvalidOperationException("Read-only test.");
        public Task<FootballLedger> ReadAsync(CancellationToken token) => throw new InvalidOperationException("API must not recover a ledger.");
        public Task<FootballDataset[]> CatalogAsync(CancellationToken token) => inner.CatalogAsync(token);
        public Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token) { Interlocked.Increment(ref Reads); return inner.QueryAsync(query, token); }
    }
}
