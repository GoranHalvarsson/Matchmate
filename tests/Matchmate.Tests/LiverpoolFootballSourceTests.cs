using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.SharedFootballTests;
using FootballPitch = Matchmate.Web.Components.Football.FootballPitch;

namespace Matchmate.Tests;

public sealed class LiverpoolFootballSourceTests(ITestOutputHelper output)
{
    private static string Input => Path.Combine(LocalPaths.FindRoot(), "output", "brentford-liverpool-generated", "dataset-v2");

    [Fact]
    public async Task FrozenLiverpoolExportNormalizesThroughCanonicalCodecAndPreparedStoreWithoutChangingInputs()
    {
        var originalHashes = Directory.GetFiles(Input).ToDictionary(p => p, p => ReplayBatchCodec.Hash(File.ReadAllBytes(p)));
        Assert.Equal(LiverpoolFootballSource.ManifestHash, originalHashes[Path.Combine(Input, "manifest.json")]);
        Assert.Equal(LiverpoolFootballSource.FactsHash, originalHashes[Path.Combine(Input, "reported-facts.json")]);
        using var directory = new TestDirectory(); var store = new ChunkedLocalFootballStore(directory.Path);
        var frames = 0; var reports = new List<FootballRecord>(); long lastMs = -200; var batches = 0;
        FootballBatch? firstBatch = null, lastBatch = null;
        await foreach (var source in new LiverpoolFootballSource(Path.Combine(Input, "manifest.json")).ReadBatchesAsync(default))
        {
            var batch = FootballCodec.Decode(FootballCodec.Encode(source)); batches++;
            firstBatch ??= batch; lastBatch = batch;
            foreach (var record in batch.Records)
            {
                if (record.Payload is not TrackingPayload frame) { if (record.Payload is MatchEventPayload) reports.Add(record); continue; }
                var ms = long.Parse(record.RecordId[6..]); Assert.Equal(lastMs + 200, ms); lastMs = ms; frames++;
                Assert.Equal("presentation_sample", record.FootballTime!.Precision); Assert.Null(frame.SourceTimestamp);
                Assert.Equal(frame.Players.Select(p => p.SubjectId), frame.ActiveSubjectIds);
                Assert.All(frame.Players.Append(frame.Ball), p => Assert.Equal("generated", p.Method));
                Assert.All(record.Evidence, e => { Assert.Equal("synthetic", e.Origin); Assert.Null(e.AvailableAtUtc); });
            }
            Assert.True(await store.AppendAsync(batch, default));
        }
        Assert.Equal(32701, frames); Assert.Equal(6540000, lastMs); Assert.Equal(23, reports.Count);
        var restart = new ChunkedLocalFootballStore(directory.Path); var dataset = Assert.Single(await restart.CatalogAsync(default));
        Assert.False(await restart.AppendAsync(firstBatch!, default)); Assert.False(await restart.AppendAsync(lastBatch!, default));
        Assert.Equal(GeneratedDistanceProjection.Fingerprint(dataset), GeneratedDistanceProjection.Fingerprint(Assert.Single(await new ChunkedLocalFootballStore(directory.Path).CatalogAsync(default))));
        Assert.Equal(LiverpoolFootballSource.DatasetId, dataset.Manifest.DatasetId); Assert.True(dataset.Closed);
        Assert.Equal(5, dataset.Manifest.ManifestRevision); Assert.Contains("cards-v1-actions-v1", dataset.Manifest.DatasetVersion);
        Assert.NotEqual("liverpool-generated-v2-ms-v1-" + LiverpoolFootballSource.ManifestHash[..12], dataset.Manifest.DatasetVersion);
        Assert.NotEqual("run-liverpool-generated-v2-ms-v1-" + LiverpoolFootballSource.ManifestHash[..12], dataset.Stream.StreamId);
        Assert.Equal("fixture-pl-2645256", dataset.Manifest.FixtureId); Assert.Equal(40, dataset.People.Length);
        Assert.Equal(2, dataset.Fixture!.Periods.Length); Assert.Equal(3, dataset.Schedule!.Segments.Length);
        Assert.Contains(FootballActionPolicy.GeneratedModelActionsV1, dataset.Manifest.Capabilities);
        Assert.True(ExperiencePresentation.Generated(dataset)); Assert.Contains("not observed", ExperiencePresentation.SourceLabel(dataset));
        async Task<FootballView> Read(long cursor) => await restart.QueryAsync(FullFootballSourceTests.Query(dataset, cursor), default);
        var end = await Read(6540000); Assert.Equal(23, end.Events.Length); Assert.Equal((1, 2), (end.HomeGoals, end.AwayGoals));
        Assert.Equal(reports.Select(r => r.RecordId), end.Events.Select(r => r.RecordId));
        Assert.Equal(10, end.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "substitution" }));
        Assert.Equal(2, end.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "red_card" }));
        Assert.Equal(2, end.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "var_decision" }));
        Assert.Equal("straight_red", ((MatchEventPayload)end.Events.Single(e => e.RecordId == "dismissal-1").Payload).Decision);
        Assert.Equal("second_yellow", ((MatchEventPayload)end.Events.Single(e => e.RecordId == "dismissal-2").Payload).Decision);
        Assert.Contains("not a second dismissal", end.Events.Single(e => e.RecordId == "var-2").Evidence[0].OriginalFlags!);
        Assert.NotNull(end.IllustrationStatistics); Assert.Null(end.GeneratedDistance); Assert.Equal(727, end.GeneratedActionRecordIds!.Length);
        await CheckSelectedGoalSequences(restart, dataset, end);
        await CheckCurrentEventWorkflowBudget(restart, dataset, end);
        Assert.All(reports, r => { Assert.All(r.Evidence, e => Assert.Null(e.AvailableAtUtc)); Assert.Contains("\"seconds\": null", r.Evidence[0].OriginalFlags!); });
        foreach (var report in reports)
        {
            var release = dataset.Schedule.Releases.Single(r => r.RecordId == report.RecordId);
            Assert.DoesNotContain((await Read(release.CursorMs - 1)).Events, e => e.RecordId == report.RecordId);
            Assert.Contains((await Read(release.CursorMs)).Events, e => e.RecordId == report.RecordId);
            Assert.Contains((await Read(release.CursorMs + 1)).Events, e => e.RecordId == report.RecordId);
        }
        foreach (var cursor in new long[] { 3359999, 3360000, 3360001, 3419999, 3420000, 3420001 })
        {
            var view = await Read(cursor); Assert.NotNull(view.Frame);
            if (cursor is >= 3360000 and < 3420000)
            {
                Assert.Equal("halftime_transition", view.Phase); Assert.Equal(3360000, ExperiencePresentation.MatchClock(view));
                Assert.Contains("clock paused", ExperiencePresentation.PeriodLabel(view));
            }
            else { Assert.Null(view.Phase); Assert.Null(view.PausedDisplayMs); }
        }
        foreach (var (cursor, homeCount) in new[] { (0L, 11), (3179999L, 11), (3180000L, 10), (6239999L, 10), (6240000L, 9), (6540000L, 9) })
        {
            var view = await Read(cursor); Assert.Equal(homeCount, view.Frame!.ActiveSubjectIds.Count(id => id.StartsWith("person-pl-reference-brentford-", StringComparison.Ordinal)));
            Assert.Equal(11, view.Frame.ActiveSubjectIds.Count(id => id.StartsWith("person-pl-reference-liverpool-", StringComparison.Ordinal)));
        }
        foreach (var (cursor, player, active) in new[] { (3359999L, "liverpool-frimpong", false), (3360000L, "liverpool-frimpong", true), (3839999L, "liverpool-frimpong", true), (3840000L, "liverpool-frimpong", false), (3840000L, "liverpool-ngumoha", true) })
            Assert.Equal(active, (await Read(cursor)).Frame!.ActiveSubjectIds.Contains("person-pl-reference-" + player));
        var before = await Read(2639999); Assert.Equal((0, 0), (before.HomeGoals, before.AwayGoals));
        Assert.Equal((1, 0), ((await Read(2640000)).HomeGoals, (await Read(2640000)).AwayGoals));
        Assert.Equal((1, 1), ((await Read(4440000)).HomeGoals, (await Read(4440000)).AwayGoals));
        var rewind = await Read(0); Assert.Equal((0, 0), (rewind.HomeGoals, rewind.AwayGoals)); Assert.Equal(22, rewind.Frame!.ActiveSubjectIds.Length); Assert.Empty(rewind.Events);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await Read(2639999)));
        var selected = new HashSet<long> { 1860000, 2640000, 3180000, 3360000, 3420000, 3840000, 6240000, 6540000 };
        var latest = new Dictionary<long, JsonElement>();
        foreach (var path in Directory.GetFiles(Input, "frames-*.json.gz").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(FootballTransport.Expand(await File.ReadAllBytesAsync(path), 3000000));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var ms = (long)Math.Round(row.GetProperty("t").GetDouble() * 1000, MidpointRounding.AwayFromZero);
                if (selected.Contains(ms)) latest[ms] = row.Clone();
            }
        }
        foreach (var (ms, row) in latest)
        {
            var frame = (await Read(ms)).Frame!;
            Assert.Equal("generated-t-" + row.GetProperty("t").GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture), frame.SourceFrameId);
            Assert.Equal(row.GetProperty("ball")[0].GetDouble(), frame.Ball.XM);
            Assert.Equal(row.GetProperty("players").GetArrayLength(), frame.Players.Length);
            Assert.DoesNotContain(frame.Players, p => row.GetProperty("inactiveTransitions").EnumerateArray().Any(i => "person-pl-reference-" + i[0].GetString()!.Replace(':', '-') == p.SubjectId));
        }
        await Assert.ThrowsAsync<FootballQueryException>(() => restart.QueryAsync(FullFootballSourceTests.Query(dataset, 0) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = DateTimeOffset.UtcNow }, default));
        Assert.Equal(originalHashes, Directory.GetFiles(Input).ToDictionary(p => p, p => ReplayBatchCodec.Hash(File.ReadAllBytes(p))));
        output.WriteLine($"Liverpool canonical import: {frames} frames, {reports.Count} reports, {batches} batches; 2500 rounded-ms collisions; prepared-store restart, all report boundaries +/-1ms, halftime, roster, parity and rewind passed.");
    }

    private static async Task CheckSelectedGoalSequences(IFootballStore store, FootballDataset dataset, FootballView end)
    {
        Assert.Contains("shared-event-sequences-v1", dataset.Manifest.Capabilities);
        Assert.Equal(9, dataset.Manifest.Sources.Length);
        Assert.Equal("ce355648a3dad0dcc04f6c86c8e9579b902937d1ec67b06a588c804024e2cb90", dataset.Manifest.Sources.Single(s => s.SourceId == "liverpool-generated-config").ArtifactHash);
        Assert.Equal(LiverpoolFootballSource.GoalReviewHash, dataset.Manifest.Sources.Single(s => s.SourceId == "liverpool-goal-review").ArtifactHash);
        Assert.Equal(LiverpoolFootballSource.GoalReviewHash, ReplayBatchCodec.Hash(await File.ReadAllBytesAsync(Path.Combine(Input, "..", "..", "brentford-liverpool-goal-review", "goal-review.v1.json"))));
        var goals = end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal" }).ToArray();
        Assert.Equal(3, goals.Length); Assert.All(end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "substitution" or "var_decision" }), r => Assert.Null(((MatchEventPayload)r.Payload).Sequence));
        async Task<FootballView> Read(long cursor) => await store.QueryAsync(FullFootballSourceTests.Query(dataset, cursor), default);
        foreach (var record in goals)
        {
            var goal = (MatchEventPayload)record.Payload; var sequence = Assert.IsType<MatchEventSequence>(goal.Sequence);
            var expectedKinds = record.RecordId switch { "goal-1" => new[] { "release_pass", "cross", "header_finish" }, "goal-2" => ["release_pass", "advance", "shot"], _ => ["cross", "headed_assist", "shot"] };
            var expectedActors = record.RecordId switch { "goal-1" => new[] { "brentford-van-den-berg", "brentford-thiago", "brentford-lewis-potter" }, "goal-2" => ["liverpool-wirtz", "liverpool-munoz", "liverpool-munoz"], _ => ["liverpool-mac-allister", "liverpool-barcola", "liverpool-isak"] };
            Assert.Equal(expectedKinds, sequence.Steps.Select(s => s.Kind));
            Assert.Equal(expectedActors.Select(id => "person-pl-reference-" + id), sequence.Steps.Select(s => s.ActorId));
            Assert.Contains(sequence.Steps, s => s.ActorId == goal.AssistPersonId); Assert.Equal(goal.PersonId, sequence.Steps[^1].ActorId);
            Assert.Equal(new[] { -12000, -6000, 0 }, sequence.Steps.Select(s => s.PresentationOffsetMs));
            Assert.All(sequence.Steps, step => Assert.All(step.EvidenceIds, id => Assert.Equal("reported", record.Evidence.Single(e => e.EvidenceId == id).Method)));
            Assert.All(sequence.Steps, step => { Assert.StartsWith("Club report:", step.Text); Assert.Contains("exact source seconds are unknown", step.Uncertainty); });
            Assert.Equal("real", record.Evidence.Single(e => e.EvidenceId == "goal-report").Origin);
            Assert.Equal("liverpool-goal-review", record.Evidence.Single(e => e.EvidenceId == "goal-report").SourceId);
            Assert.Equal("generated", record.Evidence.Single(e => e.EvidenceId == "sequence-layout").Method);
            Assert.Equal(new[] { "sequence-window" }, record.FieldEvidence["/payload/sequence/steps/0/presentationOffsetMs"]);
            var release = dataset.Schedule!.Releases.Single(r => r.RecordId == record.RecordId).CursorMs;
            var lead = await Read(release - 15000);
            var selection = new MatchSelectedEvent(record.RecordId, release, record.FootballTime!.ReportedClock!, "goal");
            var context = new MatchChatContext(lead.Context, dataset.HeadSequence, selection);
            var visual = MatchEventVisual.Create(lead.Dataset, record, context); Assert.NotNull(visual);
            Assert.Equal(release - 15000, visual.TargetCursorMs); Assert.Equal(record.RecordId, visual.EventId);
            var scene = MatchSequenceProjector.Event(lead.Dataset, record, lead.Context.CursorMs, true); Assert.NotNull(scene);
            Assert.Equal("retrospective_selected_goal", scene.Scope);
            var presentation = MatchPitchPresenter.Project(lead, [scene], true, false);
            Assert.True(presentation.GeneratedLayout); Assert.Equal(3, presentation.Path.Length); Assert.Equal(3, presentation.Path.Distinct().Count());
            Assert.All(sequence.Steps, step => Assert.Contains(presentation.Frame!.Players, p => p.SubjectId == step.ActorId && p.Availability == "present"));
            Assert.DoesNotContain(lead.Events, r => r.RecordId == record.RecordId);
            var executor = new MatchWorkflowExecutor((q, token) => store.QueryAsync(q, token));
            var request = new MatchWorkflowRequest("selected_goal_event", MatchWorkflowRegistry.Version,
                new("test-owner", 1, context, MatchWorkflowTemporalMode.SelectedGoalRetrospective), VisualIntent: MatchEventVisualIntent.ReportedSequence);
            var result = await executor.ExecuteAsync(request, _ => true);
            Assert.True(result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial, result.Message); Assert.NotNull(result.EventVisual);
            var selected = Assert.Single(result.Bundle!.Items, item => item.Id == "selected_goal");
            Assert.All(expectedKinds, kind => Assert.Contains(kind, selected.Text)); Assert.Contains("Club report:", selected.Text);
            Assert.Contains("geometry and offsets are generated", selected.Text);
            Assert.DoesNotContain("unprompted_machine_commentary", selected.Text); Assert.DoesNotContain("asr_segment_refs", selected.Text);
            foreach (var other in goals.Where(g => g.RecordId != record.RecordId)) Assert.DoesNotContain(other.RecordId, selected.Text);
            var before = await Read(release - 1); var at = await Read(release); var after = await Read(release + 1);
            Assert.DoesNotContain(before.Events, r => r.RecordId == record.RecordId); Assert.Contains(at.Events, r => r.RecordId == record.RecordId); Assert.Contains(after.Events, r => r.RecordId == record.RecordId);
            Assert.Equal((before.HomeGoals, before.AwayGoals), (lead.HomeGoals, lead.AwayGoals));
            Assert.Equal(1, at.HomeGoals + at.AwayGoals - before.HomeGoals - before.AwayGoals);
            Assert.Equal(JsonSerializer.Serialize(lead), JsonSerializer.Serialize(await Read(release - 15000)));
            Assert.Null(MatchSequenceProjector.Event(lead.Dataset, record, lead.Context.CursorMs));
            CheckRenderedGoal(lead, scene, presentation);
        }
        var sub = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "substitution" });
        var unsupported = new MatchWorkflowRequest("current_match_events", MatchWorkflowRegistry.Version,
            new("test-owner", 1, new(end.Context, dataset.HeadSequence, new(sub.RecordId, dataset.Schedule!.Releases.Single(r => r.RecordId == sub.RecordId).CursorMs, sub.FootballTime!.ReportedClock!, "substitution")), MatchWorkflowTemporalMode.ReplayAtCursor),
            VisualIntent: MatchEventVisualIntent.ReportedSequence);
        Assert.Equal(MatchWorkflowStatus.Unavailable, (await new MatchWorkflowExecutor((q, token) => store.QueryAsync(q, token)).ExecuteAsync(unsupported, _ => true)).Status);
    }

    private static async Task CheckCurrentEventWorkflowBudget(IFootballStore store, FootballDataset dataset, FootballView end)
    {
        var executor = new MatchWorkflowExecutor((q, token) => store.QueryAsync(q, token));
        var goals = end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal" }).ToArray();
        var cursors = goals.SelectMany(r => { var release = dataset.Schedule!.Releases.Single(s => s.RecordId == r.RecordId).CursorMs; return new[] { release - 1, release, release + 1 }; }).Append(6540000);
        foreach (var cursor in cursors)
        {
            var query = FullFootballSourceTests.Query(dataset, cursor);
            var result = await executor.ExecuteAsync(new("current_match_events", MatchWorkflowRegistry.Version,
                new("test-owner", 1, new(query, dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
            Assert.True(result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial, $"cursor {cursor}: {result.Status}; {result.Message}");
            Assert.NotNull(result.Bundle); Assert.InRange(result.Bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars);
            var item = Assert.Single(result.Bundle.Items, i => i.Id == "current_events");
            using var document = JsonDocument.Parse(item.Text);
            foreach (var projected in document.RootElement.GetProperty("shown").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "goal"))
            {
                var record = goals.Single(r => r.RecordId == projected.GetProperty("record").GetString());
                var sequence = ((MatchEventPayload)record.Payload).Sequence!; var serialized = projected.GetProperty("sequence");
                var qualifier = sequence.Steps[0].Uncertainty!;
                Assert.Equal(qualifier, serialized.GetProperty("sharedStepUncertainty").GetString());
                Assert.Equal(1, serialized.GetRawText().Split(JsonSerializer.Serialize(qualifier), StringSplitOptions.None).Length - 1);
                Assert.Equal(record.FootballTime!.ReportedClock, projected.GetProperty("reportedClock").GetString());
                var steps = serialized.GetProperty("steps").EnumerateArray().ToArray(); Assert.Equal(sequence.Steps.Length, steps.Length);
                for (var index = 0; index < steps.Length; index++)
                {
                    var expected = sequence.Steps[index]; var actual = steps[index];
                    Assert.False(actual.TryGetProperty("uncertainty", out _));
                    Assert.Equal(expected.Id, actual.GetProperty("id").GetString()); Assert.Equal(expected.Kind, actual.GetProperty("kind").GetString());
                    Assert.Equal(expected.Text, actual.GetProperty("text").GetString());
                    Assert.Equal(ExperiencePresentation.Person(dataset, expected.ActorId), actual.GetProperty("actor").GetString());
                    Assert.Equal(expected.EvidenceIds, actual.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()));
                    if (expected.RelatedActorId is { } related) Assert.Equal(ExperiencePresentation.Person(dataset, related), actual.GetProperty("relatedActor").GetString());
                    else Assert.False(actual.TryGetProperty("relatedActor", out _));
                }
            }
        }
    }

    [Theory]
    [InlineData("shared")] [InlineData("mixed")] [InlineData("distinct")] [InlineData("whitespace")] [InlineData("empty")] [InlineData("null")] [InlineData("single")]
    public void SequenceUncertaintyHoistsOnlyIdenticalNonemptyQualificationsAndNeverChangesCanonicalRecords(string shape)
    {
        const string shared = "Names are reported; exact contacts remain unknown.";
        string?[] qualifications = shape switch {
            "mixed" => [shared, null, shared], "distinct" => ["Delivery unknown.", "Contact uncertain.", "Location unavailable."],
            "whitespace" => ["  ", "  ", "  "], "empty" => ["", "", ""], "null" => [null, null, null], "single" => [shared], _ => [shared, shared, shared]
        };
        var fixture = Fixture(); fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = [.. fixture.Manifest.Capabilities, "shared-event-sequences-v1"] } };
        var record = Goal(fixture); var original = (MatchEventPayload)record.Payload;
        var steps = qualifications.Select((qualification, index) => new MatchEventStep("step-" + index, "reported_finish", "person-one", null,
            index * 1000, "Reported source step.", ["source"], qualification)).ToArray();
        record = record with { Payload = original with { Sequence = new(1, "qualified-sequence", 0, 5000, steps) } };
        var context = Context(fixture); context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Releases = [new(record.RecordId, 60000)] } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, [.. context, record]); var view = ledger.Query(Query(fixture, 80000));
        var before = JsonSerializer.Serialize(record, FootballCodec.Json); var item = new MatchEvidenceProjection(view).Get("current_events");
        using var document = JsonDocument.Parse(item.Text); var sequence = document.RootElement.GetProperty("shown")[0].GetProperty("sequence");
        var projected = sequence.GetProperty("steps").EnumerateArray().ToArray(); Assert.Equal(qualifications.Length, projected.Length);
        if (shape == "shared")
        {
            Assert.Equal(shared, sequence.GetProperty("sharedStepUncertainty").GetString());
            Assert.All(projected, step => Assert.False(step.TryGetProperty("uncertainty", out _)));
        }
        else
        {
            Assert.False(sequence.TryGetProperty("sharedStepUncertainty", out _));
            for (var index = 0; index < projected.Length; index++)
                if (qualifications[index] is { } qualification) Assert.Equal(qualification, projected[index].GetProperty("uncertainty").GetString());
                else Assert.False(projected[index].TryGetProperty("uncertainty", out _));
        }
        Assert.Equal(before, JsonSerializer.Serialize(record, FootballCodec.Json));
    }

    [Fact]
    public async Task StoredTwoStepAndRichGoalStreamsSurviveColdRecoveryAndAppendOfPreferredActionRevisionFive()
    {
        FootballBatch? first = null;
        await foreach (var batch in new LiverpoolFootballSource(Path.Combine(Input, "manifest.json")).ReadBatchesAsync(default)) { first = batch; break; }
        Assert.NotNull(first); Assert.Equal(5, first.Manifest.ManifestRevision);
        var oldVersion = "liverpool-generated-v2-seq-v1-" + LiverpoolFootballSource.ManifestHash[..12];
        var oldManifest = first.Manifest with { DatasetVersion = oldVersion, ManifestRevision = 2 };
        var oldStream = first.Stream with { StreamId = "run-" + oldVersion, DatasetVersion = oldVersion };
        FootballRecord Legacy(FootballRecord record)
        {
            var old = WithoutCards(record) with { DatasetVersion = oldVersion };
            if (old.Payload is not MatchEventPayload { EventKind: "goal" } goal) return old;
            // This is the earlier imported two-step representation, retained in a separate immutable stream.
            var sequence = new MatchEventSequence(1, "liverpool-sequence-" + old.RecordId, 15000, 5000,
                [new("assist", "reported_assist", goal.AssistPersonId, goal.PersonId, -6000, "Officially credited assist; delivery type unknown.", ["assist"]),
                new("finish", "reported_finish", goal.PersonId, null, 0, "Officially reported goal; actual contact unknown.", ["reported"])]);
            var fields = old.FieldEvidence.Where(p => !p.Key.StartsWith("/payload/sequence/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
            return old with { Payload = goal with { Sequence = sequence }, FieldEvidence = fields };
        }
        using var directory = new TestDirectory(); var writer = new ChunkedLocalFootballStore(directory.Path);
        foreach (var batch in FootballCodec.Batches(oldManifest, oldStream, first.Records.Select(Legacy)))
            Assert.True(await writer.AppendAsync(FootballCodec.Decode(FootballCodec.Encode(batch)), default));
        var goalVersion = "liverpool-generated-v2-seq-v1-" + LiverpoolFootballSource.ManifestHash[..12] + "-" + LiverpoolFootballSource.GoalReviewHash[..12];
        var goalManifest = first.Manifest with { DatasetVersion = goalVersion, ManifestRevision = 3 };
        var goalStream = first.Stream with { StreamId = "run-" + goalVersion, DatasetVersion = goalVersion };
        foreach (var batch in FootballCodec.Batches(goalManifest, goalStream, first.Records.Select(r => WithoutCards(r) with { DatasetVersion = goalVersion })))
            Assert.True(await writer.AppendAsync(batch, default));
        var immutable = Directory.GetFiles(Path.Combine(directory.Path, "chunks"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => ReplayBatchCodec.Hash(File.ReadAllBytes(path)));
        var coldWriter = new ChunkedLocalFootballStore(directory.Path);
        var oldCatalog = (await coldWriter.ReadAsync(default)).Catalog(); Assert.Equal(2, oldCatalog.Length);
        var legacy = oldCatalog.Single(d => d.Manifest.ManifestRevision == 2);
        Assert.Equal(2, legacy.Manifest.ManifestRevision); Assert.True(legacy.Closed);
        var legacyView = await coldWriter.QueryAsync(FullFootballSourceTests.Query(legacy, 6540000), default);
        Assert.Equal(3, legacyView.Events.Count(r => r.Payload is MatchEventPayload { Sequence.Steps: [{ Kind: "reported_assist" }, { Kind: "reported_finish" }] }));
        var oldGoals = await coldWriter.QueryAsync(FullFootballSourceTests.Query(oldCatalog.Single(d => d.Manifest.ManifestRevision == 3), 6540000), default);
        Assert.All(oldGoals.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal" }), r => Assert.Equal(3, ((MatchEventPayload)r.Payload).Sequence!.Steps.Length));
        foreach (var batch in FootballCodec.Batches(first.Manifest, first.Stream, first.Records)) Assert.True(await coldWriter.AppendAsync(batch, default));
        var reader = new ChunkedLocalFootballStore(directory.Path); var catalog = await reader.CatalogAsync(default);
        Assert.Equal(3, catalog.Length); Assert.Equal(new[] { 2, 3, 5 }, catalog.Select(d => d.Manifest.ManifestRevision).Order());
        var latest = catalog.OrderByDescending(d => d.Manifest.ManifestRevision).ThenBy(d => d.Stream.StreamId, StringComparer.Ordinal).First();
        Assert.Equal(first.Stream, latest.Stream); Assert.Equal(5, latest.Manifest.ManifestRevision);
        var rich = await reader.QueryAsync(FullFootballSourceTests.Query(latest, 6540000), default);
        Assert.All(rich.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal" }), r => Assert.Equal(3, ((MatchEventPayload)r.Payload).Sequence!.Steps.Length));
        var recoveredOld = await reader.QueryAsync(FullFootballSourceTests.Query(catalog.Single(d => d.Manifest.ManifestRevision == 2), 6540000), default);
        Assert.Equal(JsonSerializer.Serialize(legacyView), JsonSerializer.Serialize(recoveredOld));
        Assert.Equal(JsonSerializer.Serialize(oldGoals), JsonSerializer.Serialize(await reader.QueryAsync(FullFootballSourceTests.Query(catalog.Single(d => d.Manifest.ManifestRevision == 3), 6540000), default)));
        Assert.All(immutable, saved => Assert.Equal(saved.Value, ReplayBatchCodec.Hash(File.ReadAllBytes(saved.Key))));
    }

    private static FootballRecord WithoutCards(FootballRecord record) => record.Payload is MatchEventPayload { EventKind: "yellow_card" or "red_card" } card
        ? record with { Payload = card with { Sequence = null }, Evidence = record.Evidence.Where(e => e.EvidenceId != "card-layout").ToArray(),
            FieldEvidence = record.FieldEvidence.Where(p => !p.Key.StartsWith("/payload/sequence/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value) } : record;

    [Theory]
    [InlineData("person-one", "person-one", "person-one", "goal", "reported")]
    [InlineData("person-two", "person-two", "person-one", "goal", "reported")]
    [InlineData("person-two", "person-one", "person-two", "goal", "reported")]
    [InlineData("person-two", "person-one", "person-one", "yellow_card", "reported")]
    [InlineData("person-two", "person-one", "person-one", "goal", "generated")]
    public void LegacyReportedAssistStillRejectsMismatchedParticipantsAndUnsupportedClaims(string actor, string related, string finisher, string kind, string method)
    {
        var fixture = Fixture(); var goal = Goal(fixture);
        goal = goal with { Payload = new MatchEventPayload(kind, "team-home", "person-one", "person-two", Sequence: new(1, "legacy-assist-finish", 15000, 5000,
            [new("assist", "reported_assist", actor, related, -6000, "Reported assist", ["source"]), new("finish", "reported_finish", finisher, null, 0, "Reported goal", ["source"])])),
            Evidence = [goal.Evidence[0] with { Method = method }] };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 1, [goal])));
    }

#pragma warning disable BL0006 // Inspect the actual rendered components, matching the existing shared UI tests.
    private static void CheckRenderedGoal(FootballView view, MatchSequenceProjection scene, MatchPitchPresentation presentation)
    {
        var pitch = new RenderablePitch(); typeof(FootballPitch).GetProperty("View")!.SetValue(pitch, view); typeof(FootballPitch).GetProperty("Sequences")!.SetValue(pitch, new[] { scene });
        using var tree = new RenderTreeBuilder(); pitch.Render(tree); var frames = tree.GetFrames();
        var participants = scene.Sequence.Steps.SelectMany(s => new[] { s.ActorId, s.RelatedActorId }).Where(id => id is not null).Distinct().Count();
        Assert.Equal(participants, frames.Array.Take(frames.Count).Count(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && Equals(f.AttributeValue, "event-ring")));
        var schematic = new RenderableSchematic(); typeof(MatchSequenceSchematic).GetProperty("Scenes")!.SetValue(schematic, new[] { scene });
        typeof(MatchSequenceSchematic).GetProperty("Dataset")!.SetValue(schematic, view.Dataset); typeof(MatchSequenceSchematic).GetProperty("Presentation")!.SetValue(schematic, presentation);
        using var diagram = new RenderTreeBuilder(); schematic.Render(diagram); var diagramFrames = diagram.GetFrames();
        Assert.Contains(diagramFrames.Array.Take(diagramFrames.Count), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && Equals(f.AttributeValue, "sequence-path"));
    }
    private sealed class RenderablePitch : FootballPitch { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class RenderableSchematic : MatchSequenceSchematic { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
#pragma warning restore BL0006

    [Theory]
    [InlineData("scorer")] [InlineData("assist")] [InlineData("actor")] [InlineData("provenance")] [InlineData("source-time")]
    public async Task AcceptedGoalReviewPinRejectsAlteredParticipantsProvenanceAndSourceTime(string mutation)
    {
        using var directory = new TestDirectory(); var dataset = Path.Combine(directory.Path, "brentford-liverpool-generated", "dataset-v2");
        var review = Path.Combine(directory.Path, "brentford-liverpool-goal-review"); Directory.CreateDirectory(dataset); Directory.CreateDirectory(review);
        File.Copy(Path.Combine(Input, "manifest.json"), Path.Combine(dataset, "manifest.json"));
        var edited = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Input, "..", "..", "brentford-liverpool-goal-review", "goal-review.v1.json")))!;
        var goal = edited["goals"]![0]!; var first = goal["steps"]![0]!;
        if (mutation == "scorer") goal["scorer_id"] = "liverpool:isak";
        if (mutation == "assist") goal["official_assist_id"] = "brentford:jensen";
        if (mutation == "actor") first["actor_id"] = "brentford:kayode";
        if (mutation == "provenance") first["provenance"] = "observed";
        if (mutation == "source-time") first["match_clock"] = JsonNode.Parse("{\"period\":\"1H\",\"seconds\":2630}");
        await File.WriteAllTextAsync(Path.Combine(review, "goal-review.v1.json"), edited.ToJsonString());
        await using var batches = new LiverpoolFootballSource(Path.Combine(dataset, "manifest.json")).ReadBatchesAsync(default).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<RecordingValidationException>(async () => { await batches.MoveNextAsync(); });
        Assert.Contains("accepted goal review hash mismatch", error.Message);
    }

    [Fact]
    public void UnknownTimeWithoutAuthoredReleaseStaysWithheldAndKnownThenNeverUsesAuthoredStaging()
    {
        var fixture = Fixture(); var records = Context(fixture); var report = Goal(fixture) with { FootballTime = new("period-one", null, null, "unknown", null, "HT") };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, [.. records, report]);
        Assert.Empty(ledger.Query(Query(fixture, 80000)).Events);
        var schedule = records[2] with { Revision = 2, SupersedesRevision = 1, Payload = ((SchedulePayload)records[2].Payload) with { Releases = [new(report.RecordId, 50000)] } };
        Add(ledger, fixture, 2, schedule);
        Assert.Empty(ledger.Query(Query(fixture, 49999)).Events); Assert.Single(ledger.Query(Query(fixture, 50000)).Events);
        Assert.Empty(ledger.Query(Query(fixture, 80000) with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = At("13:00") }).Events);
        var earlier = report with { RecordId = "earlier-report", FootballTime = new("period-one", 40000, 40000, "source_exact", true, "1") };
        Add(ledger, fixture, 3, earlier);
        Assert.Equal(new[] { "earlier-report", report.RecordId }, ledger.Query(Query(fixture, 80000)).Events.Select(r => r.RecordId));
    }

    [Fact]
    public void OptionalPhaseFieldsPreserveExistingSerializedBytes()
    {
        Assert.Equal("{\"periodId\":\"period-one\",\"cursorStartMs\":0,\"cursorEndMs\":100000,\"localStartMs\":0}", JsonSerializer.Serialize(new PresentationSegment("period-one", 0, 100000, 0), FootballCodec.Json));
        var fixture = Fixture(); var view = Ledger(fixture).Query(Query(fixture, 0));
        Assert.DoesNotContain("pausedDisplayMs", JsonSerializer.Serialize(view, FootballCodec.Json)); Assert.DoesNotContain("\"phase\"", JsonSerializer.Serialize(view, FootballCodec.Json));
    }

    [Fact]
    public void ReportedBookingVisualUsesPlayingSegmentWhenHalftimeReusesPeriodIdentity()
    {
        var fixture = Fixture(); fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = [.. fixture.Manifest.Capabilities, "shared-event-sequences-v1"] } };
        var context = Context(fixture); var schedule = (SchedulePayload)context[2].Payload;
        context[2] = context[2] with { Payload = schedule with { Segments = [new("period-one", 0, 100000, 0), new("period-one", 100000, 101000, 100000, "halftime_transition", 100000)], Releases = [new("booking", 60000)] } };
        var report = Goal(fixture) with { RecordId = "booking", FootballTime = new("period-one", 60000, 60000, "source_exact", true, "1"),
            Payload = new MatchEventPayload("yellow_card", "team-home", "person-one", Sequence: new(1, "reported-booking", 0, 1000,
                [new("booking", "booking", "person-one", null, 0, "Reported booking", ["source"])])) };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, [.. context, report]); var view = ledger.Query(Query(fixture, 60000));
        var visual = MatchEventVisual.Create(view.Dataset, report, new(view.Context, view.Dataset.HeadSequence, new("booking", 60000, "1", "yellow_card")));
        Assert.NotNull(visual); Assert.Equal("period-one", visual.PeriodId); Assert.Equal(60000, visual.ReleaseCursorMs);
    }

    [Fact]
    public void PresentationSegmentMustReferenceAnActualFixturePeriod()
    {
        var fixture = Fixture(); var context = Context(fixture); var schedule = (SchedulePayload)context[2].Payload;
        context[2] = context[2] with { Payload = schedule with { Segments = [new("person-one", 0, 100000, 0, "halftime_transition", 0)] } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, context);
        Assert.Throws<FootballQueryException>(() => ledger.Query(Query(fixture, 0)));
    }

    [Theory]
    [InlineData("break", null)] [InlineData("play", 0L)] [InlineData("halftime_transition", null)] [InlineData("halftime_transition", -1L)]
    public void InvalidPhaseAndPausedClockAreRejected(string phase, long? paused)
    {
        var fixture = Fixture(); var record = Context(fixture)[2]; var schedule = (SchedulePayload)record.Payload;
        record = record with { Payload = schedule with { Segments = [new("period-one", 0, 100000, 0, phase, paused)] } };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 1, [record])));
    }

    [Fact]
    public void PresentationSamplesRequireGeneratedTrackingAndOrderedSegments()
    {
        var fixture = Fixture(); var record = Frame(fixture, 0) with { FootballTime = new("period-one", 0, 0, "presentation_sample", true) };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 1, [record])));
        var generated = fixture with { Manifest = fixture.Manifest with { Capabilities = [.. fixture.Manifest.Capabilities, FootballPresentationPolicy.GeneratedMovement] } };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(generated, 1, [record])));
        var schedule = Context(fixture)[2]; schedule = schedule with { Payload = ((SchedulePayload)schedule.Payload) with { Segments = [new("period-one", 10000, 90000, 0), new("period-one", 0, 50000, 0)] } };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(Batch(fixture, 1, [schedule])));
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void RedCardAndVarLabelsAreLocalized(MatchLanguage language)
    {
        Assert.DoesNotContain("_", ReplayText.Kind(language, "red_card")); Assert.DoesNotContain("_", ReplayText.Kind(language, "var_decision"));
        Assert.DoesNotContain("_", ReplayText.Value(language, "second_yellow")); Assert.DoesNotContain("_", ReplayText.Value(language, "card_upgraded"));
        Assert.Equal(ReplayText.Get(language, "StepReportedAssist"), MatchPitchPresenter.Label("reported_assist", language));
    }
}
