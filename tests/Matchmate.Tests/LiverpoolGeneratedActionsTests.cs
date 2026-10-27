using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;

namespace Matchmate.Tests;

public sealed class LiverpoolGeneratedActionsTests
{
    private sealed record Snapshot(FootballLedger Ledger, FootballDataset Dataset, FootballRecord[] Records, HashSet<(string Person, string Slot)> Slots)
    {
        public FootballView View(long cursor) => Ledger.Query(FullFootballSourceTests.Query(Dataset, cursor));
    }
    private static readonly Lazy<Task<Snapshot>> Source = new(Load);
    private static string Input => Path.Combine(LocalPaths.FindRoot(), "output/brentford-liverpool-generated/dataset-v2");
    private static async Task<Snapshot> Load()
    {
        var ledger = new FootballLedger(); var records = new List<FootballRecord>(); var slots = new HashSet<(string, string)>();
        await foreach (var batch in new LiverpoolFootballSource(Path.Combine(Input, "manifest.json")).ReadBatchesAsync(default))
        {
            foreach (var record in batch.Records)
                if (record.Payload is TrackingPayload tracking)
                    foreach (var player in tracking.Players) slots.Add((player.SubjectId, player.SlotId!));
                else records.Add(record);
            // Preserve canonical sequence/closed identity while avoiding retention of 32,701 frames in these metadata checks.
            var metadata = FootballCodec.Sign(batch with { Records = batch.Records.Where(r => r.Payload is not TrackingPayload).ToArray() });
            ledger.Accept(new(metadata, DateTimeOffset.UnixEpoch));
        }
        return new(ledger, Assert.Single(ledger.Catalog()), records.ToArray(), slots);
    }

    [Fact]
    public async Task PinnedAuthoredContactsHaveCanonicalProvenanceCodecAndActiveParticipantsThroughout()
    {
        var source = await Source.Value; var d = source.Dataset;
        Assert.Equal(5, d.Manifest.ManifestRevision); Assert.Equal(528, d.HeadSequence);
        Assert.Equal("liverpool-generated-v2-cards-v1-actions-v1-f068a719fb27-45d0952330c2", d.Manifest.DatasetVersion);
        Assert.Equal("run-" + d.Manifest.DatasetVersion, d.Stream.StreamId);
        Assert.Equal(GeneratedDistanceProjection.LiverpoolActionsDatasetSha256, GeneratedDistanceProjection.Fingerprint(d));
        var actions = source.Records.Where(r => r.Payload is FootballActionPayload).ToArray();
        Assert.Equal(727, actions.Length);
        Assert.Equal(592, actions.Count(r => r.Payload is FootballActionPayload { ActionKind: "pass" }));
        Assert.Equal(3, actions.Count(r => r.Payload is FootballActionPayload { ActionKind: "shot" }));
        Assert.Equal(132, actions.Count(r => r.Payload is FootballActionPayload { ActionKind: "possession" }));
        var pin = Assert.Single(d.Manifest.Sources, s => s.SourceId == "generated-model-actions");
        Assert.Equal(ReplayBatchCodec.Hash(await File.ReadAllBytesAsync(Path.Combine(Input, "generated-ball-actions.json"))), pin.ArtifactHash);
        using var participation = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Input, "generated-participation.json")));
        var people = participation.RootElement.GetProperty("players").EnumerateArray().ToDictionary(p => "person-pl-reference-" + p.GetProperty("playerId").GetString()!.Replace(':', '-'));
        foreach (var r in actions)
        {
            var p = (FootballActionPayload)r.Payload; var time = r.FootballTime!; var offset = time.PeriodId.EndsWith("-second", StringComparison.Ordinal) ? 3420000 : 0;
            var start = time.StartMs!.Value + offset; var end = time.EndMs!.Value + offset;
            Assert.True(start < 3360000 && end <= 3360000 || start >= 3420000 && end <= 6540000);
            Assert.Equal(end, d.Schedule!.Releases.Single(release => release.RecordId == r.RecordId).CursorMs);
            Assert.Equal("source_interval", time.Precision); Assert.False(time.EndInclusive);
            Assert.All(r.Evidence, e => { Assert.Equal("generated-model-actions", e.SourceId); Assert.Equal("synthetic", e.Origin); Assert.Equal("generated", e.Method); Assert.Equal("authored", e.ClaimStatus); Assert.Null(e.AvailableAtUtc); });
            Assert.Equal(9, r.FieldEvidence.Count); Assert.All(r.FieldEvidence.Values, ids => Assert.Equal(new[] { "generated-action" }, ids));
            foreach (var (person, slot) in new[] { (p.PersonId, p.FromSlotId), (p.RecipientId, p.ToSlotId) }.Where(pair => pair.Item1 is not null))
            {
                var lifecycle = people[person!]; Assert.Equal(lifecycle.GetProperty("slotId").GetString(), slot);
                Assert.Contains((person!, slot!), source.Slots);
                Assert.True(lifecycle.GetProperty("entrySeconds").GetDouble() * 1000 <= start);
                Assert.True(lifecycle.GetProperty("exitSeconds").ValueKind == JsonValueKind.Null || lifecycle.GetProperty("exitSeconds").GetDouble() * 1000 >= end);
            }
        }
        var decoded = FootballCodec.Batches(d.Manifest, d.Stream, actions).SelectMany(batch => FootballCodec.Decode(FootballCodec.Encode(batch)).Records).ToArray();
        Assert.Equal(actions.Select(FootballCodec.ContentHash), decoded.Select(FootballCodec.ContentHash));
    }

    [Fact]
    public async Task CompletedStoryStatisticsAndDistanceShareTheNewStreamAndRewindBoundaries()
    {
        var source = await Source.Value; var end = source.View(6540000); var story = Assert.IsType<FootballStory>(end.Story);
        Assert.Equal(132, story.Passages.Length); Assert.Equal(50000, story.Passages.Max(p => p.DurationMs));
        Assert.Equal(5, story.Passages.Max(p => p.Evidence.Length - 1));
        var records = source.Records.ToDictionary(r => r.RecordId, StringComparer.Ordinal);
        foreach (var passage in story.Passages)
        {
            Assert.InRange(passage.DurationMs, 1, FootballStoryProjector.MaxDurationMs);
            Assert.InRange(passage.Evidence.Length - 1, 0, FootballStoryProjector.MaxActions);
            Assert.All(passage.Evidence, evidence =>
            {
                var r = records[evidence.RecordId]; var action = Assert.IsType<FootballActionPayload>(r.Payload);
                Assert.Equal(passage.TeamId, action.TeamId); Assert.Equal(passage.PeriodId, r.FootballTime!.PeriodId);
                Assert.Equal(FootballCodec.ContentHash(r), evidence.Fingerprint);
                Assert.True(end.Dataset.Schedule!.Releases.Single(release => release.RecordId == r.RecordId).CursorMs <= passage.EndCursorMs);
            });
        }
        foreach (var passage in new[] { story.Passages[0], story.Passages.First(p => p.Shots > 0), story.Passages[^1] })
        {
            Assert.DoesNotContain(source.View(passage.EndCursorMs - 1).Story!.Passages, p => p.Id == passage.Id);
            Assert.Contains(source.View(passage.EndCursorMs).Story!.Passages, p => p.Id == passage.Id);
            if (passage.EndCursorMs < 6540000) Assert.Contains(source.View(passage.EndCursorMs + 1).Story!.Passages, p => p.Id == passage.Id);
        }
        var stats = Assert.IsType<FootballIllustrationStatistics>(end.IllustrationStatistics);
        Assert.Equal(5996000, stats.TotalClassifiedControlMs);
        Assert.Equal(new[] { (290, 231, 1, 2934000L), (302, 244, 2, 3062000L) }, stats.Teams.Select(t => (t.PassAttempts, t.PassCompleted, t.Shots, t.ControlledMs)));
        var kickoff = source.View(0); Assert.Empty(kickoff.Story!.Passages); Assert.All(kickoff.IllustrationStatistics!.Teams, t => Assert.Equal(0, t.PassAttempts));
        foreach (var cursor in new long[] { 3359999, 3360000, 3360001, 3419999, 3420000, 3420001 })
            Assert.All(source.View(cursor).Story!.Passages, p => Assert.True(p.EndCursorMs <= cursor && (p.EndCursorMs <= 3360000 || p.StartCursorMs >= 3420000)));
        var firstPass = source.Records.First(r => r.Payload is FootballActionPayload { ActionKind: "pass" });
        var release = source.Dataset.Schedule!.Releases.Single(r => r.RecordId == firstPass.RecordId).CursorMs;
        Assert.Equal(0, source.View(release - 1).IllustrationStatistics!.Teams.Sum(t => t.PassAttempts));
        Assert.Equal(1, source.View(release).IllustrationStatistics!.Teams.Sum(t => t.PassAttempts));
        Assert.Equal(1, source.View(release + 1).IllustrationStatistics!.Teams.Sum(t => t.PassAttempts));
        var distance = PreparedGeneratedDistanceStore.LoadEmbedded();
        foreach (var cursor in new long[] { 0, 60000, 1920000, 3180000, 3360000, 3420000, 3840000, 6240000, 6540000, 0 })
        {
            var view = source.View(cursor); var attached = distance.Attach(view);
            Assert.NotNull(attached.GeneratedDistance); Assert.Equal(cursor, attached.GeneratedDistance.CursorMs);
            Assert.Same(view.IllustrationStatistics, attached.IllustrationStatistics); Assert.Same(view.Story, attached.Story);
        }
        Assert.Null(distance.Attach(end with { Dataset = end.Dataset with { HeadSequence = end.Dataset.HeadSequence - 1 } }).GeneratedDistance);
    }

    [Fact]
    public async Task LiverpoolReportsRetainCutoffsCardsSubstitutionsAndReportedNarrativeAlongsideGeneratedCounts()
    {
        var source = await Source.Value; var end = source.View(6540000);
        var reports = Assert.IsType<ReportedReplayStatistics>(ReportedReplayProjection.Project(end)); Assert.Equal(23, reports.ReportCount);
        Assert.Equal((1, 3, 2, 5), (reports.Home.Goals, reports.Home.YellowCards, reports.Home.RedCards, reports.Home.Substitutions));
        Assert.Equal((2, 3, 0, 5), (reports.Away.Goals, reports.Away.YellowCards, reports.Away.RedCards, reports.Away.Substitutions));
        Assert.True(ReplayInsight.Project(end)!.IsReported); Assert.Equal(ReplayRecapKind.FullTime, ReplayRecap.BoundaryFor(end)!.Kind);
        Assert.Equal(ReplayRecapKind.HalfTime, ReplayRecap.BoundaryFor(source.View(3360000))!.Kind);
        Assert.NotNull(ReportedReplayProjection.Project(source.View(3420000)));
        foreach (var report in end.Events)
        {
            var release = source.Dataset.Schedule!.Releases.Single(r => r.RecordId == report.RecordId).CursorMs;
            var before = ReportedReplayProjection.Project(source.View(release - 1))!;
            var at = ReportedReplayProjection.Project(source.View(release))!;
            Assert.NotNull(before); Assert.NotNull(at); Assert.True(at.ReportCount > before.ReportCount);
            Assert.Equal(at, ReportedReplayProjection.Project(source.View(release + 1)));
        }
        Assert.Equal(0, ReportedReplayProjection.Project(source.View(0))!.ReportCount);
        Assert.Equal(reports, ReportedReplayProjection.Project(source.View(6540000)));
    }

    [Theory]
    [InlineData("missing-type-index")] [InlineData("duplicate-type-id")] [InlineData("unknown-type-id")]
    [InlineData("report-as-action")] [InlineData("unknown-release")] [InlineData("missing-report")]
    public async Task CompleteReportIndexRejectsUnrecognizedOrAmbiguousReleaseMembership(string change)
    {
        var source = await Source.Value; var view = source.View(6540000);
        view = change switch
        {
            "missing-type-index" => view with { GeneratedActionRecordIds = null },
            "duplicate-type-id" => view with { GeneratedActionRecordIds = [.. view.GeneratedActionRecordIds!, view.GeneratedActionRecordIds![0]] },
            "unknown-type-id" => view with { GeneratedActionRecordIds = [.. view.GeneratedActionRecordIds!, "unknown-action"] },
            "report-as-action" => view with { GeneratedActionRecordIds = [.. view.GeneratedActionRecordIds!, view.Events[0].RecordId] },
            "unknown-release" => view with { Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with { Releases = [.. view.Dataset.Schedule!.Releases, new("unknown-release", 1)] } } },
            "missing-report" => view with { Events = view.Events[1..] },
            _ => throw new InvalidOperationException()
        };
        Assert.Null(ReportedReplayProjection.Project(view)); Assert.Null(ReportedReplayProjection.ProjectTable(view));
    }

    [Fact]
    public async Task ChelseaAddsReportedTableWhileKeepingItsGeneratedInsightAndRecapAuthority()
    {
        var view = await GeneratedPlayerDistanceTests.ExistingView(5880000);
        Assert.Null(ReportedReplayProjection.Project(view));
        var reports = Assert.IsType<ReportedReplayStatistics>(ReportedReplayProjection.ProjectTable(view)); Assert.Equal(14, reports.ReportCount);
        Assert.Equal(3, reports.Home.Goals + reports.Away.Goals); Assert.Equal(2, reports.Home.YellowCards + reports.Away.YellowCards);
        Assert.Equal(9, reports.Home.Substitutions + reports.Away.Substitutions); Assert.Equal(0, reports.Home.RedCards + reports.Away.RedCards);
        Assert.False(ReplayInsight.Project(view)!.IsReported); Assert.Equal(ReplayRecapKind.FullTime, ReplayRecap.BoundaryFor(view)!.Kind);
        foreach (var report in view.Events)
        {
            var release = view.Dataset.Schedule!.Releases.Single(r => r.RecordId == report.RecordId).CursorMs;
            var before = ReportedReplayProjection.ProjectTable(await GeneratedPlayerDistanceTests.ExistingView(release - 1));
            var at = ReportedReplayProjection.ProjectTable(await GeneratedPlayerDistanceTests.ExistingView(release));
            Assert.NotNull(before); Assert.NotNull(at); Assert.True(at.ReportCount > before.ReportCount);
        }
        Assert.Equal(0, ReportedReplayProjection.ProjectTable(await GeneratedPlayerDistanceTests.ExistingView(0))!.ReportCount);
        var australian = (await GeneratedPlayerDistanceTests.ExistingStore.CatalogAsync(default)).Single(d => d.Manifest.DatasetId == "australian-recorded");
        Assert.Null(ReportedReplayProjection.ProjectTable(await GeneratedPlayerDistanceTests.ExistingStore.QueryAsync(FullFootballSourceTests.Query(australian, 60000), default)));
    }

    [Theory]
    [InlineData("mixed-root")] [InlineData("wrong-id")] [InlineData("duplicate-id")] [InlineData("unbound-source")]
    [InlineData("generated-origin")] [InlineData("generated-method")] [InlineData("disputed-status")] [InlineData("empty-locator")]
    public async Task LegacyTableInheritanceRequiresOneUniquelyBoundReportedAuthority(string change)
    {
        var view = await GeneratedPlayerDistanceTests.ExistingView(5880000); var original = view.Events[0];
        Assert.NotNull(ReportedReplayProjection.ProjectTable(view));
        Assert.False(original.FieldEvidence.ContainsKey("/payload")); Assert.False(original.FieldEvidence.ContainsKey("/footballTime"));
        var report = original.Evidence.Single(e => e.EvidenceId == "reported");
        var altered = change switch
        {
            "wrong-id" => report with { EvidenceId = "not-reported" },
            "unbound-source" => report with { SourceId = "missing-source" },
            "generated-origin" => report with { Origin = "synthetic" },
            "generated-method" => report with { Method = "generated" },
            "disputed-status" => report with { ClaimStatus = "disputed" },
            "empty-locator" => report with { Locator = " " },
            _ => report
        };
        var evidence = original.Evidence.Select(e => e.EvidenceId == "reported" ? altered : e).ToArray();
        if (change == "duplicate-id") evidence = [.. evidence, report];
        var fields = original.FieldEvidence.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (change == "mixed-root") fields["/payload"] = ["reported"];
        var invalid = original with { Evidence = evidence, FieldEvidence = fields };
        Assert.Null(ReportedReplayProjection.ProjectTable(view with { Events = [invalid, .. view.Events[1..]] }));
        fields["/payload"] = ["reported"]; fields["/footballTime"] = ["reported"];
        Assert.NotNull(ReportedReplayProjection.ProjectTable(view with { Events = [original with { FieldEvidence = fields }, .. view.Events[1..]] }));
        var liverpool = (await Source.Value).View(6540000); var first = liverpool.Events[0];
        var noRoots = first with { FieldEvidence = first.FieldEvidence.Where(pair => pair.Key is not "/payload" and not "/footballTime").ToDictionary(pair => pair.Key, pair => pair.Value) };
        Assert.Null(ReportedReplayProjection.ProjectTable(liverpool with { Events = [noRoots, .. liverpool.Events[1..]] }));
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task BothFixtureTablesRenderWithSeparateLocalizedQualifications(MatchLanguage language)
    {
        foreach (var view in new[] { (await Source.Value).View(6540000), await GeneratedPlayerDistanceTests.ExistingView(5880000) })
        {
            var component = new RenderableStatistics(); typeof(IllustrationStatistics).GetProperty("View")!.SetValue(component, view);
            typeof(IllustrationStatistics).GetProperty("Language")!.SetValue(component, language);
            using var tree = new RenderTreeBuilder(); component.Render(tree);
#pragma warning disable BL0006
            var frames = tree.GetFrames(); var rendered = frames.Array.Take(frames.Count).ToArray();
            Assert.Equal(2, rendered.Count(frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "table"));
            var text = string.Join(" ", rendered.Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent));
#pragma warning restore BL0006
            Assert.Contains(ReplayText.Get(language, "GeneratedStats"), text); Assert.Contains(ReplayText.Get(language, "ReportedStats"), text);
            Assert.Contains(ReplayText.Get(language, "ReportedStatsWithGeneratedDescription"), text);
            Assert.DoesNotContain(ReplayText.Get(language, "ReportedStatsDescription"), text);
        }
    }
    private sealed class RenderableStatistics : IllustrationStatistics { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
}
