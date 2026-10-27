using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmate.Tests;

public sealed class SharedFootballTests
{
    [Fact]
    public async Task OpenFirstBatchIsQueryableThroughRealProtectedApiBeforeCloseAndAfterRestart()
    {
        using var directory = new TestDirectory(); var store = new LocalFootballStore(directory.Path); var fixture = Fixture();
        var frame = Frame(fixture, 0, emptyPlayers: true);
        var first = Batch(fixture, 1, Context(fixture).Append(frame).ToArray());
        Assert.True(await store.AppendAsync(FootballCodec.Decode(FootballCodec.Encode(first)), default));
        await using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IFootballStore>(new LocalFootballStore(directory.Path))));
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/football/datasets")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        var dataset = Assert.Single((await client.GetFromJsonAsync<FootballDataset[]>("/api/football/datasets"))!);
        Assert.False(dataset.Closed); Assert.Equal(1, dataset.HeadSequence);
        var response = await client.PostAsJsonAsync("/api/football/view", Query(fixture, 0));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var view = (await response.Content.ReadFromJsonAsync<FootballView>())!;
        Assert.True(view.TrackingGap); Assert.Empty(view.Frame!.Players); Assert.Equal(0, view.Frame.Ball.XM); Assert.Equal("present", view.Frame.Ball.Availability);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/football/view", new { cursorMs = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/football/view", Query(fixture, 0) with { DatasetVersion = "wrong-version" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/football/view", Query(fixture, 0) with { KnowledgeMode = "unknown-mode" })).StatusCode);
        var restart = new LocalFootballStore(directory.Path); Assert.False(await restart.AppendAsync(first, default));
        var conflicting = Batch(fixture, 1, Context(fixture).Append(frame with { Payload = ((TrackingPayload)frame.Payload) with { SourceFrameState = "conflict" } }).ToArray());
        await Assert.ThrowsAsync<RecordingValidationException>(() => restart.AppendAsync(conflicting, default));
        Assert.Equal(1, Assert.Single((await restart.ReadAsync(default)).Catalog()).HeadSequence);
        await restart.AppendAsync(Batch(fixture, 2, [], close: true), default);
        Assert.True(Assert.Single((await new LocalFootballStore(directory.Path).ReadAsync(default)).Catalog()).Closed);
    }

    [Fact]
    public void AC_SFC_01_LatestKnowledgeEligibleRevisionAndUnknownAvailability()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture);
        var goal = Goal(fixture); Add(ledger, fixture, 2, goal);
        var correction = Revise(goal, 2, 70000, At("12:05")); Add(ledger, fixture, 3, correction);
        Assert.Single(ledger.Query(Query(fixture, 65000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") }).Events);
        Assert.Empty(ledger.Query(Query(fixture, 65000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:06") }).Events);
        var unknown = Revise(correction, 3, 80000, null); Add(ledger, fixture, 4, unknown);
        Assert.Single(ledger.Query(Query(fixture, 75000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:06") }).Events);
        Assert.Empty(ledger.Query(Query(fixture, 75000)).Events);
    }

    [Fact]
    public void AC_SFC_02_ArchivedEvidenceDoesNotBecomeAnotherRunsMembershipOrAnotherVersion()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var goal = Goal(fixture); Add(ledger, fixture, 2, goal);
        var other = fixture with { Stream = fixture.Stream with { StreamId = "replay-b" } };
        Add(ledger, other, 1, Context(other));
        Assert.Empty(ledger.Query(Query(other, 80000)).Events);
        Add(ledger, other, 2, goal); Assert.Single(ledger.Query(Query(other, 80000)).Events);
        Assert.False(ledger.Accept(new(Batch(other, 2, [goal]), DateTimeOffset.UtcNow)));
        Assert.Empty(ledger.Query(Query(other, 80000) with { ApplicationSequence = 1 }).Events);
        var version = Fixture("version-two", "replay-c"); Add(ledger, version, 1, Context(version));
        Assert.Empty(ledger.Query(Query(version, 80000)).Events);
        Assert.Throws<RecordingValidationException>(() => Add(ledger, version with { Stream = version.Stream with { StreamId = fixture.Stream.StreamId } }, 3, Goal(version)));
    }

    [Fact]
    public void AC_SFC_03_IndependentValidityAllowedButTrackingRequiresFixture()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture);
        var reference = Record(fixture, "person-name", new ReferencePayload("person-one", "person", "Earlier name"), null) with
        { FixtureId = null, EffectiveTime = new("calendar_date", "2026-01-01", "2026-02-01", "calendar_date", false) };
        Add(ledger, fixture, 2, reference);
        Assert.Single(ledger.Query(Query(fixture, 0) with { EffectiveDate = "2026-01-31" }).References);
        Assert.Empty(ledger.Query(Query(fixture, 0) with { EffectiveDate = "2026-02-01" }).References);
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 3, [Frame(fixture, 0) with { FixtureId = null }])));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AC_SFC_04_WithdrawalOrDisallowedCorrectionRecomputesOnceAndCannotBeRestoredByLateOldRevision(bool withdrawn)
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var goal = Goal(fixture); Add(ledger, fixture, 2, goal);
        Assert.Equal(1, ledger.Query(Query(fixture, 80000)).HomeGoals);
        var correction = Revise(goal, 2, 60000, At("12:05")) with
        { RecordState = withdrawn ? "withdrawn" : "active", WithdrawalReason = withdrawn ? "Source withdrew the goal" : null,
            Payload = ((MatchEventPayload)goal.Payload) with { Decision = withdrawn ? "confirmed" : "disallowed" } };
        Add(ledger, fixture, 3, correction); Add(ledger, fixture, 4, goal);
        Assert.Equal(0, ledger.Query(Query(fixture, 80000)).HomeGoals);
        Assert.Equal(1, ledger.Query(Query(fixture, 80000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("12:03") }).HomeGoals);
        Assert.False(ledger.Accept(new(Batch(fixture, 4, [goal]), DateTimeOffset.UtcNow)));
        Assert.Equal(0, ledger.Query(Query(fixture, 80000)).HomeGoals);
    }

    [Fact]
    public void AC_SFC_05_SelectRevisionBeforeTimingAndApplicationBoundaryNeverFallsBack()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var goal = Goal(fixture); Add(ledger, fixture, 2, goal);
        Add(ledger, fixture, 3, Revise(goal, 2, 70000, At("12:05")));
        Assert.Empty(ledger.Query(Query(fixture, 65000)).Events);
        Assert.Single(ledger.Query(Query(fixture, 65000) with { ApplicationSequence = 2 }).Events);
        Assert.Single(ledger.Query(Query(fixture, 70000)).Events);
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("football")]
    [InlineData("presentation")]
    public void F001_ContextRevisionIsSelectedBeforeDateFootballAndPresentationEligibility(string boundary)
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); Add(ledger, fixture, 2, Goal(fixture));
        var original = Context(fixture); var oldFixture = (FixturePayload)original[0].Payload;
        FootballRecord Correct(FootballRecord record) => record with
        {
            Revision = 2, SupersedesRevision = 1,
            EffectiveTime = new("calendar_date", "2026-02-01", null, "calendar_date", null),
            FootballTime = boundary == "football" ? new("period-one", 80000, 80000, "source_exact", true) : null
        };
        var correctedFixture = Correct(original[0]) with { Payload = oldFixture with { Label = "Future context", Home = oldFixture.Away, Away = oldFixture.Home } };
        var correctedSquad = Correct(original[1]) with { Payload = new SquadPayload([new("person-one", "team-away", "Future roster name", 9, "starter")]) };
        var corrections = new List<FootballRecord> { correctedFixture, correctedSquad };
        if (boundary == "presentation")
            corrections.Add(original[2] with { Revision = 2, SupersedesRevision = 1, Payload = ((SchedulePayload)original[2].Payload) with
            { Releases = [new("fixture-context", 80000), new("match-squad", 80000)] } });
        Add(ledger, fixture, 3, corrections.ToArray());

        var earlierDate = ledger.Query(Query(fixture, 80000) with { EffectiveDate = "2026-01-31" });
        Assert.Null(earlierDate.Dataset.Fixture); Assert.Empty(earlierDate.Dataset.People); Assert.Equal(0, earlierDate.HomeGoals); Assert.Equal(0, earlierDate.AwayGoals);
        var earlierApplication = ledger.Query(Query(fixture, 80000) with { EffectiveDate = "2026-01-31", ApplicationSequence = 2 });
        Assert.Equal("Unit", earlierApplication.Dataset.Fixture!.Label); Assert.Equal("One", earlierApplication.Dataset.People[0].Name); Assert.Equal(1, earlierApplication.HomeGoals);
        if (boundary != "effective")
        {
            var earlierCursor = ledger.Query(Query(fixture, 70000) with { EffectiveDate = "2026-02-01" });
            Assert.Null(earlierCursor.Dataset.Fixture); Assert.Empty(earlierCursor.Dataset.People);
        }
        var applicable = ledger.Query(Query(fixture, 80000) with { EffectiveDate = "2026-02-01" });
        Assert.Equal("Future context", applicable.Dataset.Fixture!.Label); Assert.Equal("Future roster name", Assert.Single(applicable.Dataset.People).Name);
        Assert.Equal(0, applicable.HomeGoals); Assert.Equal(1, applicable.AwayGoals);
    }

    [Fact]
    public void PendingMissingRevisionIsDurableAndResolvesWithoutRegressingLatestView()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var goal = Goal(fixture);
        Add(ledger, fixture, 2, Revise(goal, 2, 70000, At("12:05")));
        Assert.Equal(1, Assert.Single(ledger.Catalog()).PendingRecords); Assert.Empty(ledger.Query(Query(fixture, 80000)).Events);
        Add(ledger, fixture, 3, goal);
        Assert.Equal(0, Assert.Single(ledger.Catalog()).PendingRecords); Assert.Equal(2, Assert.Single(ledger.Query(Query(fixture, 80000)).Events).Revision);
    }

    [Fact]
    public void PackingCapsBytesAndCountAndRejectsSingleOversizedRecord()
    {
        var fixture = Fixture(); var record = Goal(fixture);
        var large = record with { Evidence = [record.Evidence[0] with { OriginalFlags = new string('x', 180000) }] };
        var batches = FootballCodec.Batches(fixture.Manifest, fixture.Stream, Enumerable.Range(0, 5).Select(i => large with { RecordId = "goal-" + i }));
        Assert.True(batches.Count > 2); Assert.All(batches, batch => Assert.InRange(FootballCodec.Encode(batch).Length, 1, FootballCodec.MaxBatchBytes));
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Batches(fixture.Manifest, fixture.Stream, [record with { Evidence = [record.Evidence[0] with { OriginalFlags = new string('x', 600000) }] }]));
    }

    internal sealed record TestFixture(FootballManifest Manifest, FootballStream Stream);
    internal static DateTimeOffset At(string clock) => DateTimeOffset.Parse("2026-01-01T" + clock + ":00Z");
    internal static TestFixture Fixture(string version = "version-one", string stream = "replay-a")
    {
        var ids = new[] { "fixture-one", "team-home", "team-away", "person-one", "person-two", "period-one", "ball-one" };
        var manifest = new FootballManifest(FootballContract.Version, "test-football", version, "fixture-one", 1, "open", [new("test-source", "Inline synthetic test evidence", null)],
            ids.Select(id => new IdentityMapping(id, "unit", JsonSerializer.SerializeToElement(id))).ToArray(), ["tracking_frame", "match_event"], ["retrospective_replay", "known_then", "completed_reference"], "unit top-left", ["person-one"]);
        return new(manifest, new(stream, manifest.DatasetId, version, manifest.FixtureId, "replay"));
    }
    internal static FootballRecord Record(TestFixture fixture, string id, FootballPayload payload, FootballTime? time) => new(FootballContract.Version, fixture.Manifest.DatasetId, fixture.Manifest.DatasetVersion, fixture.Manifest.FixtureId,
        [], id, 1, null, "active", time, null, [new("source", "test-source", "inline test record", "synthetic", "observed", "confirmed", At("01:00"))], [], payload);
    internal static FootballRecord[] Context(TestFixture fixture) => [
        Record(fixture, "fixture-context", new FixturePayload("Unit", "Unit", new("team-home", "Home"), new("team-away", "Away"), new(105, 68), [new("period-one", 1, "First half", 0, null)]), null),
        Record(fixture, "match-squad", new SquadPayload([new("person-one", "team-home", "One", 1, "starter"), new("person-two", "team-away", "Two", 2, "bench")]), null),
        Record(fixture, "presentation-policy", new SchedulePayload("unit-policy", 1, [new("period-one", 0, 100000, 0)], [], [new("Start", 0)], 100, "unit test"), null)];
    internal static FootballRecord Frame(TestFixture fixture, long time, bool emptyPlayers = false) => Record(fixture, "frame-" + time,
        new TrackingPayload(time.ToString(), null, emptyPlayers ? [] : [new("person-one", null, "present", 1.25, 2.75, null, "observed", "unknown", ["source"])],
            new("ball-one", null, "present", 0, 0, null, "observed", "unknown", ["source"]), ["person-one"], emptyPlayers ? "empty" : "present"), new("period-one", time, time, "source_sample", true));
    internal static FootballRecord Goal(TestFixture fixture) => Record(fixture, "goal-1", new MatchEventPayload("goal", "team-home", "person-one", Decision: "confirmed"), new("period-one", 60000, 60000, "source_exact", true)) with
    { Evidence = [new("source", "test-source", "unit goal", "synthetic", "reported", "reported", At("12:00"))] };
    internal static FootballRecord Revise(FootballRecord record, int revision, long time, DateTimeOffset? available) => record with { Revision = revision, SupersedesRevision = revision - 1,
        FootballTime = record.FootballTime! with { StartMs = time, EndMs = time }, Evidence = [record.Evidence[0] with { AvailableAtUtc = available }] };
    internal static FootballBatch Batch(TestFixture fixture, long sequence, FootballRecord[] records, bool close = false) => FootballCodec.Sign(new(fixture.Manifest, fixture.Stream, sequence, records, close, ""));
    internal static FootballLedger Ledger(TestFixture fixture) { var ledger = new FootballLedger(); Add(ledger, fixture, 1, Context(fixture)); return ledger; }
    internal static void Add(FootballLedger ledger, TestFixture fixture, long sequence, params FootballRecord[] records) => ledger.Accept(new(Batch(fixture, sequence, records), DateTimeOffset.UtcNow));
    internal static FootballQuery Query(TestFixture fixture, long cursor) => new(fixture.Manifest.DatasetId, fixture.Manifest.DatasetVersion, fixture.Stream.StreamId, fixture.Manifest.FixtureId, cursor, "retrospective_replay", "unit-policy", 1);
}
