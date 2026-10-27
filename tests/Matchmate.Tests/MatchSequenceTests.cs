using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable BL0006 // Inspect the actual component render tree, matching the existing UI regressions.

namespace Matchmate.Tests;

public sealed class MatchSequenceTests(ITestOutputHelper output)
{
    private static string Export(string version = "v3") => Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", "brentford-full-" + version);
    private static readonly Lazy<Task<FootballLedger>> Source = new(() => ReadLedger("v3"));
    private static readonly Lazy<Task<FootballLedger>> Participants = new(() => ReadLedger("v4"));
    private static async Task<FootballLedger> ReadLedger(string version) {
        var ledger = new FootballLedger();
        await foreach (var batch in new FullBrentfordFootballSource(Path.Combine(Export(version), "manifest.json")).ReadBatchesAsync(default)) {
            ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow));
            var dataset = Assert.Single(ledger.Catalog());
            if (dataset.Schedule is not null && ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)).Events.Length == 14) break;
        }
        return ledger;
    }
    private static FootballView End(FootballLedger ledger) => ledger.Query(FullFootballSourceTests.Query(Assert.Single(ledger.Catalog()), 5880000));

    [Fact]
    public async Task D087_AcceptedRolesKeepReportedActionsPointIdentityAndFormalAssistUnknownSeparate()
    {
        var view = End(await Participants.Value); Assert.Equal(5, view.Dataset.Manifest.ManifestRevision); Assert.StartsWith("brent-full-v4-", view.Dataset.Manifest.DatasetVersion);
        Assert.Equal(8, view.Dataset.Manifest.Sources.Length);
        var twoRecord = view.Events.Single(r => r.RecordId == "goal-2"); var two = (MatchEventPayload)twoRecord.Payload;
        Assert.Null(two.AssistPersonId); Assert.Equal("83", twoRecord.FootballTime!.ReportedClock);
        Assert.Equal(new[] { "release_pass", "shot", "save", "rebound_finish" }, two.Sequence!.Steps.Select(s => s.Kind));
        Assert.Equal(new[] { "person-pl-reference-brentford-damsgaard", "person-pl-reference-brentford-schade", "person-pl-reference-chelsea-martinez", "person-pl-reference-brentford-thiago" }, two.Sequence.Steps.Select(s => s.ActorId));
        Assert.Equal(two.Sequence.Steps[1].ActorId, two.Sequence.Steps[0].RelatedActorId);
        Assert.All(two.Sequence.Steps, step => Assert.All(step.EvidenceIds, id => Assert.Equal("reported", twoRecord.Evidence.Single(e => e.EvidenceId == id).Method)));
        Assert.Contains(two.Details!, d => d.Kind == "reported_minute_disagreement" && d.Text.Contains("80"));
        Assert.Contains(two.Details!, d => d.Kind == "reviewed_observation" && d.Uncertainty!.Contains("pinned native points"));
        Assert.Equal("observed", twoRecord.Evidence.Single(e => e.EvidenceId == "participant-point").Method);

        var threeRecord = view.Events.Single(r => r.RecordId == "goal-3"); var three = (MatchEventPayload)threeRecord.Payload; Assert.Null(three.AssistPersonId);
        Assert.Equal(new[] { "throw_in", "into_area", "cross", "deflection", "reported_finish" }, three.Sequence!.Steps.Select(s => s.Kind));
        Assert.All(three.Sequence.Steps.Take(3), s => Assert.EndsWith("schade", s.ActorId));
        Assert.EndsWith("acheampong", three.Sequence.Steps[3].ActorId); Assert.EndsWith("carvalho", three.Sequence.Steps[4].ActorId);
        Assert.All(three.Sequence.Steps.Take(2), s => Assert.All(s.EvidenceIds, id => Assert.Equal("observed", threeRecord.Evidence.Single(e => e.EvidenceId == id).Method)));
        Assert.Equal(new[] { "participant-brentford" }, three.Sequence.Steps[3].EvidenceIds); Assert.Contains("Single-source", three.Sequence.Steps[3].Uncertainty);
        Assert.Single(three.Sequence.Steps, s => s.Uncertainty?.Contains("Intermediate throw recipient/return passer is unresolved") == true);
        Assert.All(view.Events, r => FootballCodec.ValidateMetadataRecord(r, view.Dataset.Manifest));
    }

    [Fact]
    public async Task D087_OnlyTwoGoalSequencesChangeAndAllPriorFramesArtifactsRemainPinned()
    {
        var old = End(await Source.Value); var current = End(await Participants.Value);
        foreach (var record in current.Events.Where(r => r.RecordId is not "goal-2" and not "goal-3"))
        {
            var before = (MatchEventPayload)old.Events.Single(r => r.RecordId == record.RecordId).Payload; var after = (MatchEventPayload)record.Payload;
            Assert.Equal(JsonSerializer.Serialize(before.Sequence), JsonSerializer.Serialize(after.Sequence)); Assert.Equal(JsonSerializer.Serialize(before.Details), JsonSerializer.Serialize(after.Details));
        }
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export("v4"), "manifest.json")));
        foreach (var version in new[] { "v1", "v2", "v3" }) { using var earlier = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export(version), "manifest.json")));
            Assert.Equal(earlier.RootElement.GetProperty("chunks").GetRawText(), receipt.RootElement.GetProperty("chunks").GetRawText()); }
        Assert.Equal(919, receipt.RootElement.GetProperty("chunks").GetArrayLength()); Assert.Equal(58801, receipt.RootElement.GetProperty("frameCount").GetInt32());
        Assert.Equal(8, AzureFootballStore.MaxStreamsPerDataset); // D108 retains all four prior generations when adding v5; the ninth-stream rejection is tested separately.
        using var participants = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export("v4"), "source-input-002.json")));
        Assert.Equal("accepted-goal-participants-v1", participants.RootElement.GetProperty("policy").GetString());
        Assert.Equal("1a36395d77700b2e38393268b5c90839e5b15cbf1042eefa20b4c623e2f7e4ae", participants.RootElement.GetProperty("review").GetProperty("sha256").GetString());
        Assert.Contains(participants.RootElement.GetProperty("limits").EnumerateArray(), p => p.GetString()!.Contains("Runtime uses this portable curated receipt"));
    }

    [Theory] [InlineData("current_match_events")] [InlineData("current_match_moment")] [InlineData("goal-2")] [InlineData("goal-3")]
    public async Task D087_CompactEvidenceResolvesEverySemanticSourceAndRetainsScopeWithinExistingBudgets(string workflow)
    {
        var ledger = await Participants.Value; var end = End(ledger); var selected = workflow.StartsWith("goal-");
        var release = selected ? end.Dataset.Schedule!.Releases.Single(r => r.RecordId == workflow).CursorMs : 5880000;
        var cursor = selected ? release - 15000 : release;
        var request = new MatchWorkflowRequest(selected ? "selected_goal_event" : workflow, MatchWorkflowRegistry.Version,
            new("owner", 1, new(end.Context with { CursorMs = cursor }, end.Dataset.HeadSequence,
                selected ? new(workflow, release, end.Events.Single(r => r.RecordId == workflow).FootballTime!.ReportedClock!) : null),
                selected ? MatchWorkflowTemporalMode.SelectedGoalRetrospective : MatchWorkflowTemporalMode.ReplayAtCursor));
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(request, _ => true);
        Assert.True(result.Bundle is not null, $"{result.Status}; raw event chars {new MatchEvidenceProjection(end).Get("current_events").Text.Length}"); var bundle = result.Bundle;
        var item = bundle.Items.Single(i => i.Id == (selected ? "selected_goal" : "current_events")); using var data = JsonDocument.Parse(item.Text);
        var rows = selected ? new[] { data.RootElement.GetProperty("selectedEvent") } : data.RootElement.GetProperty("shown").EnumerateArray().ToArray();
        foreach (var row in rows) foreach (var step in row.GetProperty("sequence").GetProperty("steps").EnumerateArray()) foreach (var id in step.GetProperty("evidenceIds").EnumerateArray())
        {
            var source = Assert.Single(row.GetProperty("evidence").EnumerateArray(), e => e.GetProperty("id").GetString() == id.GetString());
            var joined = Assert.Single(bundle.EventProvenance, p => p.RecordId == row.GetProperty("record").GetString() && p.EvidenceId == id.GetString());
            Assert.Equal(source.GetProperty("source").GetString(), joined.SourceId); Assert.Equal(source.GetProperty("method").GetString(), joined.Method);
            var canonical = end.Events.Single(e => e.RecordId == joined.RecordId).Evidence.Single(e => e.EvidenceId == joined.EvidenceId);
            Assert.Equal(canonical.SourceId, joined.SourceId); Assert.Equal(canonical.Method, joined.Method); Assert.Equal(canonical.Origin, joined.Origin); Assert.Equal(canonical.ClaimStatus, joined.ClaimStatus);
        }
        Assert.InRange(bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars); Assert.InRange(JsonSerializer.Serialize(bundle).Length, 1, MatchWorkflowLimits.BundleChars);
        output.WriteLine($"D087 {workflow}: evidence {bundle.Items.Sum(i => i.Text.Length)}; bundle {JsonSerializer.Serialize(bundle).Length}.");
        if (workflow == "goal-2") { Assert.Contains("Thiago", item.Text); Assert.Contains("Martinez", item.Text); Assert.Contains("Formal assist remains uncredited", item.Text); Assert.Contains("minute80", item.Text); Assert.DoesNotContain("goal-3", JsonSerializer.Serialize(bundle)); }
        if (workflow == "goal-3") { Assert.Contains("Brentford report alone", item.Text); Assert.Contains("unresolved", item.Text); Assert.DoesNotContain("Hickey", item.Text); }
        if (selected) { using var summary = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "match_summary").Text); Assert.Equal(workflow == "goal-2" ? 1 : 2, summary.RootElement.GetProperty("homeReportedGoals").GetInt32()); }
    }

    [Theory] [InlineData("goal-2")] [InlineData("goal-3")]
    public async Task D087_NamedStagesKeepTwentyTwoPlayersAndRepeatedActorsFollowCurrentSharedStep(string id)
    {
        var ledger = await Participants.Value; var end = End(ledger); var record = end.Events.Single(r => r.RecordId == id); var release = end.Dataset.Schedule!.Releases.Single(r => r.RecordId == id).CursorMs;
        foreach (var step in ((MatchEventPayload)record.Payload).Sequence!.Steps)
        {
            var view = await ViewAt(ledger, release + step.PresentationOffsetMs, "v4"); var scene = MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs, true)!;
            var plan = MatchPitchPresenter.Project(view, [scene], true, false); var actor = plan.Frame!.Players.Single(p => p.SubjectId == step.ActorId);
            Assert.Equal(plan.Contact!.Value.X, actor.XM); Assert.Equal(plan.Contact.Value.Y, actor.YM); Assert.Equal(22, plan.Frame.Players.Length);
            var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "Sequences", new[] { scene }); using var tree = new RenderTreeBuilder(); pitch.Render(tree);
            Assert.Equal(22, Attrs(tree, "class").Count(c => c.StartsWith("marker "))); Assert.Single(Attrs(tree, "class"), c => c.StartsWith("ball "));
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Id), Attrs(tree, "data-step-id")); Assert.Contains(id == "goal-2" ? "Igor Thiago" : "Fábio Carvalho", Text(tree));
        }
    }

    [Fact]
    public async Task AllFourteenReportsShareExactSequenceIdsKindsOrderAndAttributionWithToolsAndRenderer()
    {
        var view = End(await Source.Value);
        Assert.Equal(4, view.Dataset.Manifest.ManifestRevision); Assert.StartsWith("brent-full-v3-", view.Dataset.Manifest.DatasetVersion);
        var scenes = MatchSequenceProjector.Current(view); Assert.Equal(14, scenes.Length);
        using var evidence = JsonDocument.Parse(new MatchEvidenceProjection(view).Get("current_events").Text);
        foreach (var scene in scenes) {
            var record = view.Events.Single(r => r.RecordId == scene.RecordId); var payload = (MatchEventPayload)record.Payload;
            Assert.Same(payload.Sequence, scene.Sequence); Assert.NotEmpty(payload.Details!);
            var item = evidence.RootElement.GetProperty("shown").EnumerateArray().Single(e => e.GetProperty("record").GetString() == scene.RecordId).GetProperty("sequence");
            Assert.Equal(scene.Sequence.Id, item.GetProperty("id").GetString());
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Id), item.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Kind), item.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
            var renderer = new RenderableSchematic(); Parameter(renderer, "Scenes", new[] { scene }); Parameter(renderer, "Dataset", view.Dataset);
            using var tree = new RenderTreeBuilder(); renderer.Render(tree);
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Id), Attrs(tree, "data-step-id"));
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Kind), Attrs(tree, "data-step-kind"));
            Assert.All(scene.Sequence.Steps, s => Assert.All(s.EvidenceIds, id => Assert.Contains(record.Evidence, e => e.EvidenceId == id)));
        }
        var first = scenes.Single(s => s.RecordId == "goal-1");
        Assert.Equal(new[] { "corner", "headed_assist", "header_finish" }, first.Sequence.Steps.Select(s => s.Kind));
        Assert.EndsWith("damsgaard", first.Sequence.Steps[0].ActorId); Assert.EndsWith("schuster", first.Sequence.Steps[1].ActorId); Assert.EndsWith("anthony", first.Sequence.Steps[2].ActorId);
        var goalTwo = scenes.Single(s => s.RecordId == "goal-2"); Assert.All(goalTwo.Sequence.Steps.Take(3), s => Assert.Null(s.ActorId));
        Assert.Contains("Pass versus parry is unknown", goalTwo.Sequence.Steps[2].Uncertainty);
        Assert.All(scenes.Single(s => s.RecordId == "goal-3").Sequence.Steps.Take(2), s => Assert.Null(s.ActorId));
        Assert.All(scenes.Where(s => s.RecordId.StartsWith("sub-")), s => { Assert.Single(s.Sequence.Steps); Assert.Equal(0, s.Sequence.Steps[0].PresentationOffsetMs); });
        var substitutions = view.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "substitution" }).GroupBy(r => ((MatchEventPayload)r.Payload).GroupId);
        Assert.Equal(new[] { 2, 2, 2, 3 }, substitutions.Select(g => g.Count()).Order());
    }

    [Fact]
    public async Task BeforeReleaseOnlySelectedGoalGetsSequenceAndCurrentSummaryNeverGetsFutureGoals()
    {
        var ledger = await Source.Value; var end = End(ledger); var view = ledger.Query(end.Context with { CursorMs = 3765000 });
        var goal = end.Events.Single(r => r.RecordId == "goal-1"); Assert.Null(MatchSequenceProjector.Event(view.Dataset, goal, view.Context.CursorMs));
        Assert.Empty(MatchSequenceProjector.Current(view)); Assert.True(MatchSequenceProjector.MasksUnreleasedGoal(view.Dataset, end.Events, view.Context.CursorMs));
        Assert.True(MatchSequenceProjector.MasksUnreleasedGoal(view.Dataset, end.Events, 3720000)); Assert.False(MatchSequenceProjector.MasksUnreleasedGoal(view.Dataset, end.Events, 3719999));
        var scene = Assert.IsType<MatchSequenceProjection>(MatchSequenceProjector.Event(view.Dataset, goal, view.Context.CursorMs, true));
        Assert.Equal("retrospective_selected_goal", scene.Scope); Assert.True(MatchSequenceProjector.InWindow(scene, view.Context.CursorMs));
        using var summary = JsonDocument.Parse(new MatchEvidenceProjection(view).Get("match_summary").Text); Assert.Equal(0, summary.RootElement.GetProperty("homeReportedGoals").GetInt32());
        var selected = new MatchEvidenceProjection(view).SelectedGoal(goal, new("goal-1", 3780000, "61"));
        Assert.Contains("sequence-goal-1", selected.Text); Assert.DoesNotContain("goal-2", selected.Text); Assert.DoesNotContain("goal-3", selected.Text);
        Assert.DoesNotContain("sequence-goal", new MatchEvidenceProjection(view).Get("current_events").Text);
        Assert.Null(MatchSequenceProjector.Event(view.Dataset, goal with { DatasetVersion = "wrong-version" }, 3780000, true));
        Assert.Null(MatchSequenceProjector.Event(view.Dataset with { Schedule = view.Dataset.Schedule! with { Releases = [] } }, goal, 3780000, true));
        Assert.Null(MatchSequenceProjector.Event(view.Dataset, end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "substitution" }), 3765000, true));
    }

    [Fact]
    public async Task PageUsesOnlyBoundIndexAndClearRestoresFactFreeWindowWithoutChangingScore()
    {
        var ledger = await Source.Value; var end = End(ledger); var current = ledger.Query(end.Context with { CursorMs = 3765000 });
        current = current with { Frame = ledger.Query(end.Context with { CursorMs = 0 }).Frame }; Assert.NotNull(current.Frame);
        await using var page = new RenderablePage(); Set(page, "_view", current); Set(page, "_sequenceIndex", end.Events); Set(page, "_highlightHead", end.Dataset.HeadSequence);
        Assert.Empty(Scenes(page)); Assert.True((bool)Property(page, "MaskGoalWindow")!);
        Set(page, "_selectedEvent", new MatchSelectedEvent("goal-1", 3780000, "61")); Assert.Single(Scenes(page)); Assert.False((bool)Property(page, "MaskGoalWindow")!);
        Set(page, "_selectedEvent", null); Assert.Empty(Scenes(page)); Assert.True((bool)Property(page, "MaskGoalWindow")!);
        Set(page, "_highlightHead", end.Dataset.HeadSequence - 1); Assert.Empty(Scenes(page)); Assert.False((bool)Property(page, "SequenceIndexAvailable")!);
        using var stalePitch = new RenderTreeBuilder(); PitchFromPage(page).Render(stalePitch); AssertNeutralLoading(stalePitch);
        Assert.Empty(current.Events); Assert.Equal(0, ExperiencePresentation.ReportedGoals(current, current.Dataset.Fixture!.Home.Id));
    }

    [Theory]
    [InlineData("absent")] [InlineData("stale")] [InlineData("error")]
    public async Task F_D086_001_UnavailableIndexSuppressesOldMotionAndStaleScenesUntilValidatedReady(string state)
    {
        var ledger = await Source.Value; var end = End(ledger);
        var current = ledger.Query(end.Context with { CursorMs = 3765000 }) with { Frame = ledger.Query(end.Context with { CursorMs = 0 }).Frame };
        Assert.NotNull(current.Frame); Assert.NotEmpty(current.Frame.Players); Assert.Equal("present", current.Frame.Ball.Availability);
        var fail = state == "error";
        using var http = new HttpClient(new SequenceIndexHandler(async (request, token) => {
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            var query = await request.Content!.ReadFromJsonAsync<FootballQuery>(token);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(end with { Context = query! }) };
        })) { BaseAddress = new("https://test.invalid/") };
        await using var page = new RenderablePage(); Set(page, "Api", new ReplayApiClient(http)); Set(page, "_view", current);
        Set(page, "_selectedStream", current.Dataset.Stream.StreamId); Set(page, "_cursorMs", current.Context.CursorMs);
        Set(page, "_selectedEvent", new MatchSelectedEvent("goal-1", 3780000, "61"));
        if (state == "stale") { Set(page, "_sequenceIndex", end.Events); Set(page, "_highlightHead", end.Dataset.HeadSequence - 1); }
        if (state == "error") await LoadIndex(page, current.Dataset);
        Assert.False((bool)Property(page, "SequenceIndexAvailable")!); Assert.Empty(Scenes(page)); Assert.False((bool)Property(page, "MaskGoalWindow")!);
        var pitch = PitchFromPage(page);
        // Even an accidentally retained scene cannot bypass the explicit availability flag at the renderer.
        Parameter(pitch, "Sequences", new[] { MatchSequenceProjector.Event(end.Dataset, end.Events.Single(r => r.RecordId == "goal-1"), current.Context.CursorMs, true)! });
        using var pending = new RenderTreeBuilder(); pitch.Render(pending); AssertNeutralLoading(pending);
        Assert.Empty(current.Events); Assert.Equal(0, ExperiencePresentation.ReportedGoals(current, current.Dataset.Fixture!.Home.Id));

        fail = false; await LoadIndex(page, current.Dataset);
        Assert.True((bool)Property(page, "SequenceIndexAvailable")!); Assert.Equal("retrospective_selected_goal", Assert.Single(Scenes(page)).Scope);
        using var ready = new RenderTreeBuilder(); PitchFromPage(page).Render(ready);
        Assert.DoesNotContain("Loading event sequences", Text(ready)); Assert.Contains("Selected goal", Text(ready)); Assert.Equal(3, Attrs(ready, "data-step-id").Length);

        // A new head invalidates the old schematic before a replacement index is ready.
        Set(page, "_view", current with { Dataset = current.Dataset with { HeadSequence = current.Dataset.HeadSequence + 1 } });
        using var advanced = new RenderTreeBuilder(); PitchFromPage(page).Render(advanced); AssertNeutralLoading(advanced); Assert.Empty(Scenes(page));

        // At a normal cursor a ready index restores ordinary generated playback, without changing the source frame.
        Set(page, "_selectedEvent", null); var opening = ledger.Query(end.Context with { CursorMs = 1000 }); Set(page, "_view", opening);
        Assert.True((bool)Property(page, "SequenceIndexAvailable")!); Assert.Empty(Scenes(page)); Assert.False((bool)Property(page, "MaskGoalWindow")!);
        using var normal = new RenderTreeBuilder(); PitchFromPage(page).Render(normal);
        Assert.Contains(Attrs(normal, "class"), c => c.StartsWith("marker ")); Assert.Contains(Attrs(normal, "class"), c => c.StartsWith("ball "));
    }

    [Fact]
    public async Task F_D086_001_AustralianAndLegacyPitchKeepFramesWithoutSequenceIndex()
    {
        var ledger = await Source.Value; var end = End(ledger); var original = ledger.Query(end.Context with { CursorMs = 0 }); Assert.NotNull(original.Frame);
        var legacy = original with { Dataset = original.Dataset with { Manifest = original.Dataset.Manifest with { Capabilities = original.Dataset.Manifest.Capabilities.Where(c => c != "shared-event-sequences-v1").ToArray() } } };
        var batches = await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var australia = new FootballLedger(); foreach (var batch in batches) australia.Accept(new(batch, DateTimeOffset.UtcNow));
        var actual = australia.Query(FullFootballSourceTests.Query(Assert.Single(australia.Catalog()), 65200)); Assert.NotNull(actual.Frame);
        foreach (var view in new[] { legacy, actual }) {
            await using var page = new RenderablePage(); Set(page, "_view", view); Assert.True((bool)Property(page, "SequenceIndexAvailable")!);
            var pitch = PitchFromPage(page); Parameter(pitch, "SequenceIndexAvailable", false);
            using var tree = new RenderTreeBuilder(); pitch.Render(tree);
            Assert.DoesNotContain("Loading event sequences", Text(tree)); Assert.Contains(Attrs(tree, "class"), c => c.StartsWith("marker "));
        }
    }

    [Theory]
    [InlineData("goal-1")] [InlineData("goal-2")] [InlineData("goal-3")]
    public async Task F_D086_001_EventPresentationKeepsTwentyTwoPlayersUsesSharedStepsAndOnlyStagesAttributedActors(string id)
    {
        var ledger = await Source.Value; var end = End(ledger); var record = end.Events.Single(r => r.RecordId == id);
        var release = end.Dataset.Schedule!.Releases.Single(r => r.RecordId == id).CursorMs;
        foreach (var authoredStep in ((MatchEventPayload)record.Payload).Sequence!.Steps) {
            var cursor = release + authoredStep.PresentationOffsetMs; var view = await ViewAt(ledger, cursor);
            var scene = MatchSequenceProjector.Event(view.Dataset, record, cursor, true)!;
            var original = JsonSerializer.Serialize(view.Frame);
            var plan = MatchPitchPresenter.Project(view, [scene], true, false);
            Assert.Equal(22, plan.Frame!.Players.Count(p => p.Availability == "present")); Assert.Same(authoredStep, plan.ActiveStep);
            Assert.Equal(view.Frame!.Players.Select(p => p.SubjectId).Order(), plan.Frame.Players.Select(p => p.SubjectId).Order());
            Assert.True(plan.Contact!.Value.X < view.Dataset.Fixture!.Pitch.LengthM / 2); // Existing half-two home attack points left.
            var neutral = MatchPitchPresenter.Project(view, [], true, true);
            var known = scene.Sequence.Steps.Where(s => s.ActorId is not null).Select(s => s.ActorId).ToHashSet();
            foreach (var player in plan.Frame.Players.Where(p => !known.Contains(p.SubjectId))) {
                var expected = neutral.Frame!.Players.Single(p => p.SubjectId == player.SubjectId);
                Assert.Equal(expected.XM, player.XM); Assert.Equal(expected.YM, player.YM);
            }
            var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "Sequences", new[] { scene });
            using var tree = new RenderTreeBuilder(); pitch.Render(tree);
            Assert.Equal(22, Attrs(tree, "class").Count(c => c.StartsWith("marker "))); Assert.Single(Attrs(tree, "class"), c => c.StartsWith("ball "));
            Assert.Equal(scene.Sequence.Steps.Select(s => s.Id), Attrs(tree, "data-step-id"));
            Assert.Contains(ExperiencePresentation.Person(view.Dataset, scene.Sequence.Steps.Last(s => s.ActorId is not null).ActorId), Text(tree));
            Assert.DoesNotContain("identity unknown", Text(tree), StringComparison.OrdinalIgnoreCase);
            var annotation = new RenderableSchematic(); Parameter(annotation, "Scenes", new[] { scene }); Parameter(annotation, "Dataset", view.Dataset); Parameter(annotation, "Presentation", plan);
            using var drawing = new RenderTreeBuilder(); annotation.Render(drawing);
            if (authoredStep.Kind == "corner") Assert.Contains("corner-arc", Attrs(drawing, "class"));
            if (authoredStep.Kind is "headed_assist" or "header_finish") Assert.Single(Attrs(drawing, "class"), c => c == "head-contact");
            Assert.DoesNotContain("actor", Attrs(drawing, "class")); // Anonymous actions get no extra named or invented player dot.
            Assert.Equal(original, JsonSerializer.Serialize(view.Frame));
        }
    }

    [Fact]
    public async Task F_D086_001_AllFourteenEligibleEventsAndNeutralWindowsKeepFullSourcePlayerSet()
    {
        var ledger = await Source.Value; var end = End(ledger);
        foreach (var record in end.Events) {
            var release = end.Dataset.Schedule!.Releases.Single(r => r.RecordId == record.RecordId).CursorMs;
            var view = await ViewAt(ledger, release); var scenes = MatchSequenceProjector.Current(view).Where(s => s.ReleaseCursorMs == release).ToArray();
            var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "Sequences", scenes);
            using var tree = new RenderTreeBuilder(); pitch.Render(tree);
            Assert.Equal(22, Attrs(tree, "class").Count(c => c.StartsWith("marker "))); Assert.Single(Attrs(tree, "class"), c => c.StartsWith("ball "));
            Assert.Equal(scenes.SelectMany(s => s.Sequence.Steps.Select(step => step.Id)), Attrs(tree, "data-step-id"));
            if (((MatchEventPayload)record.Payload).EventKind == "substitution" && scenes.Length > 1) Assert.Contains("order unknown", Text(tree));
        }
        var before = await ViewAt(ledger, 3765700); var source = JsonSerializer.Serialize(before.Frame);
        var neutral = MatchPitchPresenter.Project(before, [], true, true); var unavailable = MatchPitchPresenter.Project(before, [], false, false);
        Assert.Equal(JsonSerializer.Serialize(neutral.Frame), JsonSerializer.Serialize(unavailable.Frame));
        Assert.Equal(before.Dataset.Fixture!.Pitch.LengthM / 2, neutral.Frame!.Ball.XM);
        foreach (var ready in new[] { true, false }) {
            var pitch = new RenderablePitch(); Parameter(pitch, "View", before); Parameter(pitch, "SequenceIndexAvailable", ready); Parameter(pitch, "MaskGoalWindow", ready);
            using var tree = new RenderTreeBuilder(); pitch.Render(tree); Assert.Equal(22, Attrs(tree, "class").Count(c => c.StartsWith("marker ")));
            Assert.Single(Attrs(tree, "class"), c => c.StartsWith("ball ")); Assert.Empty(Attrs(tree, "data-step-id")); Assert.DoesNotContain("Corner kick", Text(tree));
        }
        Assert.Equal(source, JsonSerializer.Serialize(before.Frame));
    }

    [Fact]
    public async Task AllFiveAustralianAnnotationsWaitForCompletionAndKeepCurrentFramesAndGapMissingness()
    {
        var batches = await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var ledger = new FootballLedger(); foreach (var batch in batches) ledger.Accept(new(batch, DateTimeOffset.UtcNow)); var dataset = Assert.Single(ledger.Catalog());
        var final = ledger.Query(FullFootballSourceTests.Query(dataset, 240000)); Assert.Equal(5, final.Possessions.Length);
        foreach (var record in final.Possessions) {
            var possession = (PossessionPayload)record.Payload; Assert.Null(MatchSequenceProjector.Possession(dataset, record, possession.EligibleAfterMs - 1));
            var view = ledger.Query(FullFootballSourceTests.Query(dataset, possession.EligibleAfterMs)); var before = JsonSerializer.Serialize(view.Frame);
            var scene = Assert.IsType<MatchSequenceProjection>(MatchSequenceProjector.Possession(dataset, record, possession.EligibleAfterMs));
            Assert.Equal("completed_possession", scene.Scope); Assert.Equal(possession.PersonId, Assert.Single(scene.Sequence.Steps).ActorId);
            using var projection = JsonDocument.Parse(new MatchEvidenceProjection(view).Get("moment_context").Text);
            Assert.Contains(projection.RootElement.GetProperty("completedPossessions").EnumerateArray(), p => p.GetProperty("sequence").GetProperty("id").GetString() == scene.Sequence.Id);
            Assert.Equal(before, JsonSerializer.Serialize(view.Frame));
            var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "Sequences", new[] { scene }); using var tree = new RenderTreeBuilder(); pitch.Render(tree);
            Assert.DoesNotContain("event-schematic", Attrs(tree, "class")); Assert.Contains("not possession now", Text(tree));
        }
        Assert.True(ledger.Query(FullFootballSourceTests.Query(dataset, 175900)).TrackingGap);
    }

    [Theory]
    [InlineData("actor")] [InlineData("evidence")] [InlineData("order")] [InlineData("offset")] [InlineData("count")] [InlineData("kind")] [InlineData("text")]
    [InlineData("release_related")] [InlineData("rebound_actor")] [InlineData("save_actor")]
    public async Task SequenceContractRejectsInventedIdentityUnattributedOrUnboundedSteps(string fault)
    {
        var view = End(await Source.Value); var record = view.Events.Single(r => r.RecordId == "goal-1"); var payload = (MatchEventPayload)record.Payload; var sequence = payload.Sequence!;
        var first = sequence.Steps[0]; first = fault switch { "actor" => first with { ActorId = "person-unknown" }, "evidence" => first with { EvidenceIds = ["unjoined"] },
            "offset" => first with { PresentationOffsetMs = -60001 }, "kind" => first with { Kind = "invented_tackle" }, "text" => first with { Text = new('x', 181) },
            "release_related" => first with { Kind = "release_pass", RelatedActorId = null }, "rebound_actor" => first with { Kind = "rebound_finish" }, "save_actor" => first with { Kind = "save", ActorId = null }, _ => first };
        var steps = sequence.Steps.ToArray(); steps[0] = first;
        if (fault == "order") Array.Reverse(steps); if (fault == "count") steps = Enumerable.Repeat(first, 6).ToArray();
        Assert.Throws<RecordingValidationException>(() => FootballCodec.ValidateMetadataRecord(record with { Payload = payload with { Sequence = sequence with { Steps = steps } } }, view.Dataset.Manifest));
    }

    [Fact]
    public async Task LateEvidenceStaysWithinExistingBudgetsAndV3KeepsAllOriginalFrameChunks()
    {
        var ledger = await Source.Value; var end = End(ledger); var executor = new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q)));
        foreach (var workflow in new[] { "current_match_events", "current_match_moment" }) {
            var request = new MatchWorkflowRequest(workflow, MatchWorkflowRegistry.Version, new("owner", 1, new(end.Context, end.Dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor));
            var result = await executor.ExecuteAsync(request, _ => true);
            Assert.True(result.Bundle is not null, $"{result.Status}; raw event chars {new MatchEvidenceProjection(end).Get("current_events").Text.Length}");
            Assert.InRange(result.Bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars); Assert.InRange(JsonSerializer.Serialize(result.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
            output.WriteLine($"{workflow}: event chars {result.Bundle.Items.Single(i => i.Id == "current_events").Text.Length}; all evidence chars {result.Bundle.Items.Sum(i => i.Text.Length)}; serialized bundle chars {JsonSerializer.Serialize(result.Bundle).Length}.");
        }
        using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export("v1"), "manifest.json"))); using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export(), "manifest.json")));
        Assert.Equal(old.RootElement.GetProperty("chunks").GetRawText(), current.RootElement.GetProperty("chunks").GetRawText());
        Assert.Equal(58801, current.RootElement.GetProperty("frameCount").GetInt32()); Assert.Equal(14, current.RootElement.GetProperty("sequences").GetProperty("events").GetArrayLength());
        Assert.Contains("27 unaligned", current.RootElement.GetProperty("sequences").GetProperty("associationPolicy").GetString());
    }

    [Fact]
    public async Task MalformedSequenceWithoutDetailsIsRejectedAtReadBoundaryAndCannotCrashPitchProjection()
    {
        var view = End(await Source.Value); var record = view.Events.Single(r => r.RecordId == "goal-1"); var goal = (MatchEventPayload)record.Payload;
        var malformed = record with { Payload = goal with { Details = null, Sequence = goal.Sequence! with { Steps = null! } } };
        var badView = view with { Events = [malformed] };
        var executor = new MatchWorkflowExecutor((_, _) => Task.FromResult(badView));
        var request = new MatchWorkflowRequest("current_match_events", MatchWorkflowRegistry.Version, new("owner", 1, new(view.Context, view.Dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor));
        var result = await executor.ExecuteAsync(request, _ => true); Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle);
        Assert.Null(MatchSequenceProjector.Event(view.Dataset, malformed, view.Context.CursorMs));
    }

    [Theory] [InlineData("policy")] [InlineData("artifact")] [InlineData("pin")]
    public async Task V3ReceiptRejectsChangedSequenceInputsBeforeYieldingAnyBatch(string fault)
    {
        using var temp = new TestDirectory(); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Export(), "manifest.json")))!;
        foreach (var file in new[] { "official-record.json", "event-enrichment.json", "event-sequences.json", "source-input-000.json", "source-input-001.json", "source-input-002.json" }) File.Copy(Path.Combine(Export(), file), Path.Combine(temp.Path, file));
        if (fault == "policy") manifest["sequencePolicy"] = "changed";
        if (fault == "pin") manifest["sequences"]!["sources"]![0]!["sha256"] = new string('0', 64);
        if (fault == "artifact") await File.AppendAllTextAsync(Path.Combine(temp.Path, "event-sequences.json"), " ");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "manifest.json"), manifest.ToJsonString()); var yielded = 0;
        await Assert.ThrowsAsync<RecordingValidationException>(async () => { await foreach (var _ in new FullBrentfordFootballSource(Path.Combine(temp.Path, "manifest.json")).ReadBatchesAsync(default)) yielded++; }); Assert.Equal(0, yielded);
    }

    [Theory] [InlineData("participant")] [InlineData("context_policy")] [InlineData("sequence_policy")]
    public async Task D087_PortableReceiptRejectsChangedCuratedInputOrPolicyBeforeAnyBatch(string fault)
    {
        using var temp = new TestDirectory(); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Export("v4"), "manifest.json")))!;
        foreach (var file in new[] { "official-record.json", "event-enrichment.json", "event-sequences.json", "source-input-000.json", "source-input-001.json", "source-input-002.json", "source-input-003.json" }) File.Copy(Path.Combine(Export("v4"), file), Path.Combine(temp.Path, file));
        if (fault == "participant") await File.AppendAllTextAsync(Path.Combine(temp.Path, "source-input-002.json"), " ");
        else manifest[fault == "context_policy" ? "normalizationPolicy" : "sequencePolicy"] = "unapproved";
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "manifest.json"), manifest.ToJsonString()); var yielded = 0;
        await Assert.ThrowsAsync<RecordingValidationException>(async () => { await foreach (var _ in new FullBrentfordFootballSource(Path.Combine(temp.Path, "manifest.json")).ReadBatchesAsync(default)) yielded++; }); Assert.Equal(0, yielded);
    }

    private static async Task<FootballView> ViewAt(FootballLedger ledger, long cursor, string version = "v3") {
        var end = End(ledger); var view = ledger.Query(end.Context with { CursorMs = cursor });
        if (view.Frame is not null) return view;
        var index = cursor / 100; using var raw = JsonDocument.Parse(FootballTransport.Expand(await File.ReadAllBytesAsync(Path.Combine(Export(version), $"frames-{index / 64:D6}.json.gz")), 2_000_000));
        var sample = raw.RootElement[(int)(index % 64)];
        var players = sample.GetProperty("players").EnumerateArray().Select(p => new FootballObservation("person-pl-reference-" + p.GetProperty("player").GetProperty("id").GetString()!.Replace(':', '-'), p.GetProperty("slotId").GetString(), "present", p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), null, "generated", "unknown", ["generated"])).ToArray();
        var ball = sample.GetProperty("ball");
        return view with { Frame = new("model-" + cursor, null, players, new("ball-pl-2645236-active", null, "present", ball.GetProperty("x").GetDouble(), ball.GetProperty("y").GetDouble(), null, "generated", "unknown", ["generated"]), players.Select(p => p.SubjectId).ToArray(), "generated-complete-model"), TrackingGap = false };
    }
    private static void Parameter(object component, string name, object? value) => component.GetType().GetProperty(name)!.SetValue(component, value);
    private static void Set(FootballReplay page, string name, object? value) {
        if (typeof(FootballReplay).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } property) property.SetValue(page, value);
        else typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    }
    private static object? Property(FootballReplay page, string name) => typeof(FootballReplay).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static MatchSequenceProjection[] Scenes(FootballReplay page) => (MatchSequenceProjection[])Property(page, "PitchSequences")!;
    private static string[] Attrs(RenderTreeBuilder tree, string name) => tree.GetFrames().Array.Take(tree.GetFrames().Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).Select(f => f.AttributeValue?.ToString() ?? "").ToArray();
    private static string Text(RenderTreeBuilder tree) => string.Concat(tree.GetFrames().Array.Take(tree.GetFrames().Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    private sealed class RenderablePitch : Matchmate.Web.Components.Football.FootballPitch { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderableSchematic : MatchSequenceSchematic { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderablePage : FootballReplay { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class SequenceIndexHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private static Task LoadIndex(FootballReplay page, FootballDataset dataset) => (Task)typeof(FootballReplay).GetMethod("LoadHighlightsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [dataset, 0L])!;
    private static RenderablePitch PitchFromPage(RenderablePage page) {
        static RenderTreeFrame[] Frames(RenderTreeBuilder builder) {
            var range = builder.GetFrames(); var direct = range.Array.Take(range.Count).ToArray();
            var nested = new List<RenderTreeFrame>(direct);
            foreach (var fragment in direct.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).Select(frame => frame.AttributeValue).OfType<Microsoft.AspNetCore.Components.RenderFragment>()) {
                using var child = new RenderTreeBuilder(); fragment(child); nested.AddRange(Frames(child));
            }
            return nested.ToArray();
        }
        using var tree = new RenderTreeBuilder(); page.Render(tree); var frames = Frames(tree);
        var index = Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(Matchmate.Web.Components.Football.FootballPitch));
        Assert.True(index >= 0); Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(PlayerRoster));
        var pitch = new RenderablePitch();
        foreach (var frame in frames.Skip(index + 1).Take(frames[index].ComponentSubtreeLength - 1).Where(f => f.FrameType == RenderTreeFrameType.Attribute)) Parameter(pitch, frame.AttributeName, frame.AttributeValue);
        return pitch;
    }
    private static void AssertNeutralLoading(RenderTreeBuilder tree) {
        Assert.Contains("Event evidence is loading", Text(tree)); Assert.Contains("Use Refresh to retry", Text(tree));
        Assert.Equal(22, Attrs(tree, "class").Count(c => c.StartsWith("marker "))); Assert.Single(Attrs(tree, "class"), c => c.StartsWith("ball "));
        Assert.Empty(Attrs(tree, "data-step-id")); Assert.DoesNotContain("Corner kick", Text(tree));
    }
}
