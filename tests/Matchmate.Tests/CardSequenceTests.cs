using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

public sealed class CardSequenceTests(ITestOutputHelper output)
{
    private static readonly Lazy<Task<FootballLedger>> Source = new(ReadBoundedSource);
    private static async Task<FootballLedger> ReadBoundedSource()
    {
        var records = new List<FootballRecord>(); FootballManifest? manifest = null; FootballStream? stream = null;
        var times = new HashSet<long> { 0, 6540000 };
        await foreach (var batch in new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json")).ReadBatchesAsync(default))
        {
            manifest ??= batch.Manifest; stream ??= batch.Stream;
            foreach (var record in batch.Records)
            {
                if (record.Payload is SchedulePayload schedule)
                    foreach (var release in schedule.Releases) { times.Add(release.CursorMs); times.Add(release.CursorMs - 200); }
                if (record.Payload is not TrackingPayload || times.Contains(long.Parse(record.RecordId[6..]))) records.Add(record);
            }
        }
        var ledger = new FootballLedger();
        foreach (var batch in FootballCodec.Batches(manifest!, stream!, records)) ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow));
        return ledger;
    }

    [Fact]
    public async Task BudgetFirstAllEightSelectedCardsAndEveryReleaseBoundaryUseExistingLimits()
    {
        var ledger = await Source.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 6540000));
        var cards = end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "yellow_card" or "red_card" }).ToArray();
        Assert.Equal(8, cards.Length); Assert.Equal(6, cards.Count(r => ((MatchEventPayload)r.Payload).EventKind == "yellow_card"));
        var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query)));
        var failures = new List<string>();
        var cursors = cards.SelectMany(card => { var release = dataset.Schedule!.Releases.Single(r => r.RecordId == card.RecordId).CursorMs; return new[] { release - 1, release, release + 1 }; }).Append(6540000);
        foreach (var cursor in cursors)
        {
            var result = await executor.ExecuteAsync(new("current_match_events", MatchWorkflowRegistry.Version,
                new("card-budget-first", 1, new(FullFootballSourceTests.Query(dataset, cursor), dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
            var measured = new MatchEvidenceProjection(ledger.Query(FullFootballSourceTests.Query(dataset, cursor)), compactEventSources: true);
            var sizes = new[] { "current_events", "match_summary", "coverage" }.ToDictionary(tool => tool, tool => measured.Get(tool).Text.Length);
            output.WriteLine($"items at {cursor}: {JsonSerializer.Serialize(sizes)} = {sizes.Values.Sum()}");
            if (result.Status is not (MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial)) { failures.Add($"current_events {cursor}: {result.Status}; {sizes.Values.Sum()}/{MatchWorkflowLimits.EvidenceChars}"); continue; }
            Assert.NotNull(result.Bundle); var chars = result.Bundle.Items.Sum(item => item.Text.Length);
            Assert.InRange(chars, 1, MatchWorkflowLimits.EvidenceChars); Assert.InRange(JsonSerializer.Serialize(result.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
            VerifyProvenanceJoins(result.Bundle, end.Events);
            output.WriteLine($"current_events {cursor}: {result.Status}, {chars}/{MatchWorkflowLimits.EvidenceChars}; bundle {JsonSerializer.Serialize(result.Bundle).Length}/{MatchWorkflowLimits.BundleChars}");
        }
        foreach (var card in cards)
        {
            var kind = ((MatchEventPayload)card.Payload).EventKind; var release = dataset.Schedule!.Releases.Single(r => r.RecordId == card.RecordId).CursorMs;
            var context = new MatchChatContext(FullFootballSourceTests.Query(dataset, release), dataset.HeadSequence, new(card.RecordId, release, card.FootballTime!.ReportedClock!, kind));
            var result = await executor.ExecuteAsync(new("current_match_events", MatchWorkflowRegistry.Version, new("selected-card-budget", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor),
                VisualIntent: MatchEventVisualIntent.ReportedSequence), _ => true);
            Assert.True(result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial, $"{card.RecordId}: {result.Status}; {result.Message}");
            Assert.NotNull(result.EventVisual); Assert.NotNull(result.Bundle);
            Assert.InRange(result.Bundle.Items.Sum(item => item.Text.Length), 1, MatchWorkflowLimits.EvidenceChars);
            Assert.InRange(JsonSerializer.Serialize(result.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
            var selected = Assert.Single(result.Bundle.Items, item => item.Id == "selected_event");
            Assert.Contains(((MatchEventPayload)card.Payload).Sequence!.Steps[0].Text, selected.Text);
            output.WriteLine($"{card.RecordId}: {result.Status}, {result.Bundle.Items.Sum(i => i.Text.Length)}/{MatchWorkflowLimits.EvidenceChars}");
        }
        Assert.Empty(failures);
    }

    private static void VerifyProvenanceJoins(MatchWorkflowBundle bundle, FootballRecord[] canonical)
    {
        using var data = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "current_events").Text);
        foreach (var row in data.RootElement.GetProperty("shown").EnumerateArray())
        {
            var record = canonical.Single(r => r.RecordId == row.GetProperty("record").GetString());
            Assert.Equal(record.Revision, row.GetProperty("revision").GetInt32());
            foreach (var evidence in row.GetProperty("evidence").EnumerateArray())
            {
                var id = evidence.GetProperty("id").GetString(); var source = evidence.GetProperty("source").GetString();
                var original = record.Evidence.Single(e => e.EvidenceId == id && e.SourceId == source);
                var full = Assert.Single(bundle.EventProvenance, p => p.RecordId == record.RecordId && p.Revision == record.Revision && p.EvidenceId == id && p.SourceId == source);
                Assert.Equal(original.Method, full.Method); Assert.Equal(original.Origin, full.Origin); Assert.Equal(original.ClaimStatus, full.ClaimStatus);
                if (!row.TryGetProperty("sequence", out _)) { Assert.Equal(original.Origin, evidence.GetProperty("origin").GetString()); Assert.Equal(original.ClaimStatus, evidence.GetProperty("status").GetString()); }
            }
        }
    }

    [Fact]
    public async Task AllEightCardsRenderSharedFiguresAndSourceReasonsWithoutRestoringDismissedPlayers()
    {
        var ledger = await Source.Value; var dataset = Assert.Single(ledger.Catalog()); Assert.Equal(4, dataset.Manifest.ManifestRevision);
        Assert.Contains("cards-v1", dataset.Manifest.DatasetVersion);
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 6540000));
        foreach (var record in end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "yellow_card" or "red_card" }))
        {
            var card = (MatchEventPayload)record.Payload; var red = card.EventKind == "red_card";
            var sequence = Assert.IsType<MatchEventSequence>(card.Sequence); var step = Assert.Single(sequence.Steps);
            Assert.Equal(red ? "red_card" : "booking", step.Kind); Assert.Equal(card.PersonId, step.ActorId); Assert.Null(step.RelatedActorId);
            Assert.Equal(0, sequence.PresentationLeadInMs); Assert.Equal(0, step.PresentationOffsetMs);
            Assert.All(step.EvidenceIds, id => Assert.Equal("reported", record.Evidence.Single(e => e.EvidenceId == id).Method));
            var reason = card.Details?.FirstOrDefault(d => d.Kind == "reason")?.Text;
            Assert.Contains(reason ?? "reason not supplied", step.Text);
            var release = dataset.Schedule!.Releases.Single(r => r.RecordId == record.RecordId).CursorMs;
            var selection = new MatchSelectedEvent(record.RecordId, release, record.FootballTime!.ReportedClock!, card.EventKind);
            var before = ledger.Query(FullFootballSourceTests.Query(dataset, release - 1));
            Assert.Null(MatchEventVisual.Create(before.Dataset, record, new(before.Context, dataset.HeadSequence, selection)));
            var view = ledger.Query(FullFootballSourceTests.Query(dataset, release)); var frameBefore = JsonSerializer.Serialize(view.Frame);
            var visual = MatchEventVisual.Create(view.Dataset, record, new(view.Context, dataset.HeadSequence, selection)); Assert.NotNull(visual);
            Assert.Equal(release, visual.TargetCursorMs); Assert.Equal(red ? card.Decision : null, visual.Decision);
            var scene = MatchSequenceProjector.Event(view.Dataset, record, release)!;
            var presentation = MatchPitchPresenter.Project(view, [scene], true, false);
            Assert.False(presentation.GeneratedLayout); Assert.Empty(presentation.Path); Assert.Null(presentation.Contact);
            Assert.Equal(frameBefore, JsonSerializer.Serialize(presentation.Frame));
            if (red) { Assert.DoesNotContain(card.PersonId, view.Frame!.ActiveSubjectIds); Assert.DoesNotContain(view.Frame.Players, p => p.SubjectId == card.PersonId); }
            else Assert.Contains(card.PersonId, view.Frame!.ActiveSubjectIds);
            VerifyCardRendering(view, visual, scene, presentation, red);
            Assert.Equal(frameBefore, JsonSerializer.Serialize(ledger.Query(FullFootballSourceTests.Query(dataset, release)).Frame));
        }
        var rewind = ledger.Query(FullFootballSourceTests.Query(dataset, 0)); Assert.Empty(rewind.Events); Assert.Equal(22, rewind.Frame!.ActiveSubjectIds.Length);
        Assert.Equal((0, 0), (rewind.HomeGoals, rewind.AwayGoals));
    }

#pragma warning disable BL0006 // Inspect actual shared components without a browser substitute host.
    private static void VerifyCardRendering(FootballView view, MatchEventVisual visual, MatchSequenceProjection scene, MatchPitchPresentation presentation, bool red)
    {
        var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "Sequences", new[] { scene });
        using var tree = new RenderTreeBuilder(); pitch.Render(tree);
        Assert.Equal(red ? 0 : 1, AttributeCount(tree, "class", "event-ring"));
        Assert.Equal(red ? 1 : 0, AttributeCount(tree, "class", "dismissal-annotation"));
        if (red) { Assert.Contains(ReplayText.Value(MatchLanguage.English, visual.Decision), Text(tree)); Assert.Contains("no player location", Text(tree)); }
        var schematic = new RenderableSchematic(); Parameter(schematic, "Dataset", view.Dataset); Parameter(schematic, "Scenes", new[] { scene }); Parameter(schematic, "Presentation", presentation);
        using var symbols = new RenderTreeBuilder(); schematic.Render(symbols); Assert.Equal(red ? 0 : 1, AttributeCount(symbols, "class", "booking"));
        foreach (var language in Enum.GetValues<MatchLanguage>())
        {
            var diagram = new RenderableDiagram(); Parameter(diagram, "Visual", visual); Parameter(diagram, "Language", language);
            using var figure = new RenderTreeBuilder(); diagram.Render(figure);
            Assert.Equal(1, AttributeCount(figure, "class", red ? "reported-card is-red" : "reported-card"));
            Assert.Contains(visual.Steps[0].ActorName!, Text(figure)); Assert.Contains(visual.Steps[0].Text, Text(figure));
            if (red) Assert.Contains(ReplayText.Value(language, visual.Decision), Text(figure));
        }
    }
    private static void Parameter(object component, string name, object? value) => component.GetType().GetProperty(name)!.SetValue(component, value);
    private static int AttributeCount(RenderTreeBuilder tree, string name, string value) { var frames = tree.GetFrames(); return frames.Array.Take(frames.Count).Count(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name && Equals(f.AttributeValue, value)); }
    private static string Text(RenderTreeBuilder tree) { var frames = tree.GetFrames(); return string.Concat(frames.Array.Take(frames.Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent)); }
    private sealed class RenderablePitch : Matchmate.Web.Components.Football.FootballPitch { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class RenderableSchematic : MatchSequenceSchematic { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class RenderableDiagram : EventSequenceDiagram { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
#pragma warning restore BL0006

    [Theory]
    [InlineData("Show this card on a diagram", "yellow_card", true)] [InlineData("Show this card on a diagram", "red_card", true)]
    [InlineData("Show this red card on a diagram", "yellow_card", false)] [InlineData("Show this yellow card on a diagram", "red_card", false)]
    [InlineData("Show this second yellow card on a diagram", "red_card", true)] [InlineData("Show this booking on a diagram", "red_card", false)]
    [InlineData("Visa det gula kortet i ett diagram", "red_card", false)] [InlineData("Muestra esta tarjeta roja en un diagrama", "yellow_card", false)]
    public void CardPresentationGuardRespectsExplicitColorAndGenericSelectedCard(string question, string kind, bool allowed)
    {
        Assert.NotNull(MatchEventVisual.Intent(question));
        Assert.Equal(allowed, MatchEventVisual.MatchesRequestedKind(question, new("selected-card", 1000, "1", kind)));
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void LocalizedRedPresetRequestsStayInTheSelectedDismissalScope(MatchLanguage language)
    {
        var question = ReplayText.Get(language, "VisualRedCardQuestion"); Assert.NotNull(MatchEventVisual.Intent(question));
        Assert.True(MatchEventVisual.MatchesRequestedKind(question, new("red", 1000, "1", "red_card")));
        Assert.False(MatchEventVisual.MatchesRequestedKind(question, new("yellow", 1000, "1", "yellow_card")));
    }

    [Theory]
    [InlineData("pure", false)] [InlineData("absent", true)] [InlineData("mixed", true)] [InlineData("semantic", true)]
    [InlineData("detail", true)] [InlineData("conflict", true)] [InlineData("real", true)] [InlineData("reported", true)] [InlineData("unknown-index", true)]
    public void WorkflowWitnessSelectionRemovesOnlyUnusedExclusivePresentationWitnessesWithoutBackfill(string shape, bool retained)
    {
        var f = SharedFootballTests.Fixture(); var record = SharedFootballTests.Goal(f);
        var fact = record.Evidence[0]; var layout = fact with { EvidenceId = "layout", Origin = "synthetic", Method = "generated", ClaimStatus = "authored" };
        var other = fact with { EvidenceId = "other" }; var fourth = fact with { EvidenceId = "fourth" };
        var sequence = new MatchEventSequence(1, "witness-sequence", 0, 1000, [new("booking", "booking", "person-one", null, 0, "Reported booking", shape == "semantic" ? ["source", "layout"] : ["source"])]);
        var payload = new MatchEventPayload("yellow_card", "team-home", "person-one", Sequence: sequence,
            Details: shape == "detail" ? [new("reason", "Attributed reason", ["layout"])] : null);
        var fields = new Dictionary<string, string[]> { ["/payload/sequence/steps/0/presentationOffsetMs"] = ["layout"] };
        if (shape == "absent") fields.Clear(); if (shape == "mixed") fields["/payload/personId"] = ["layout"];
        if (shape == "unknown-index") fields = new() { ["/payload/sequence/steps/1/presentationOffsetMs"] = ["layout"] };
        if (shape == "real") layout = layout with { Origin = "real" }; if (shape == "reported") layout = layout with { Method = "reported" };
        record = record with { Payload = payload, Evidence = [fact, layout, other, fourth], FieldEvidence = fields,
            Conflicts = shape == "conflict" ? [new("reason", ["layout"], "Retained disagreement")] : null };
        var before = JsonSerializer.Serialize(record, FootballCodec.Json); var selected = MatchEvidenceProjection.WorkflowEventEvidence(record);
        Assert.Equal(retained, selected.Any(e => e.EvidenceId == "layout")); Assert.DoesNotContain(selected, e => e.EvidenceId == "fourth");
        Assert.Contains(selected, e => e.EvidenceId == "source"); Assert.Contains(selected, e => e.EvidenceId == "other");
        Assert.Equal(before, JsonSerializer.Serialize(record, FootballCodec.Json));
    }

    [Theory]
    [InlineData("actor")] [InlineData("related")] [InlineData("offset")] [InlineData("lead-in")]
    [InlineData("decision")] [InlineData("wrong-color")][InlineData("generated-claim")] [InlineData("missing-actor")]
    public void RedCardSequencesRejectWrongActorColorDecisionAndUnsupportedClaim(string fault)
    {
        var fixture = SharedFootballTests.Fixture(); var record = SharedFootballTests.Goal(fixture);
        var step = new MatchEventStep("dismissal", "red_card", "person-one", null, 0, "Reported straight-red dismissal.", ["source"]);
        if (fault == "actor") step = step with { ActorId = "person-two" };
        if (fault == "related") step = step with { RelatedActorId = "person-two" };
        if (fault == "offset") step = step with { PresentationOffsetMs = 1 };
        if (fault == "wrong-color") step = step with { Kind = "booking" };
        if (fault == "missing-actor") step = step with { ActorId = null };
        var sequence = new MatchEventSequence(1, "dismissal-sequence", fault == "lead-in" ? 1 : 0, 15000, [step]);
        record = record with { Payload = new MatchEventPayload("red_card", "team-home", "person-one", Decision: fault == "decision" ? "confirmed" : "straight_red", Sequence: sequence) };
        if (fault == "generated-claim") record = record with { Evidence = [record.Evidence[0] with { Method = "generated" }] };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.Encode(SharedFootballTests.Batch(fixture, 1, [record])));
    }

    [Fact]
    public void StandaloneProjectionAndMetadataFallbackRetainFullProvenance()
    {
        var fixture = SharedFootballTests.Fixture(); fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = [.. fixture.Manifest.Capabilities, "shared-event-sequences-v1"] } };
        var record = SharedFootballTests.Goal(fixture); var payload = (MatchEventPayload)record.Payload;
        record = record with { Payload = payload with { Sequence = new(1, "one-step", 0, 1000, [new("finish", "reported_finish", "person-one", null, 0, "Reported goal", ["source"])]) } };
        var context = SharedFootballTests.Context(fixture); context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Releases = [new(record.RecordId, 60000)] } };
        var ledger = new FootballLedger(); SharedFootballTests.Add(ledger, fixture, 1, [.. context, record]); var view = ledger.Query(SharedFootballTests.Query(fixture, 80000));
        using var standalone = JsonDocument.Parse(new MatchEvidenceProjection(view).Get("current_events").Text);
        var evidence = standalone.RootElement.GetProperty("shown")[0].GetProperty("evidence")[0]; Assert.True(evidence.TryGetProperty("origin", out _)); Assert.True(evidence.TryGetProperty("status", out _));
        var fallback = view with { Events = [record with { Payload = payload }] };
        using var metadata = JsonDocument.Parse(new MatchEvidenceProjection(fallback, compactEventSources: true).Get("current_events").Text);
        var full = metadata.RootElement.GetProperty("shown")[0].GetProperty("evidence")[0]; Assert.True(full.TryGetProperty("origin", out _)); Assert.True(full.TryGetProperty("status", out _));
    }
}
