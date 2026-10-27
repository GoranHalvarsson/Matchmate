using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;

#pragma warning disable BL0006
namespace Matchmate.Tests;

public sealed partial class MatchGoalVisualTests
{
    [Theory] [InlineData("Show me how this goal happened", true)] [InlineData("Draw a diagram of the goal", true)]
    [InlineData("Visualize the selected goal", true)] [InlineData("Show me the goal scorers so far", false)]
    [InlineData("Give me a visual of the goal", true)] [InlineData("Sketch this goal", true)]
    [InlineData("Show how many goals have been reported", false)] [InlineData("Explain how this goal happened", false)]
    [InlineData("Show the current score", false)]
    public void PresentationIntentDoesNotReinterpretOrdinaryScoreOrEventRequests(string question, bool expected)
        => Assert.Equal(expected, MatchEventVisual.Intent(question) is not null);

    private static readonly Lazy<Task<FootballLedger>> Production = new(async () => {
        var ledger = new FootballLedger();
        await foreach (var batch in new FullBrentfordFootballSource(Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", "brentford-full-v4", "manifest.json")).ReadBatchesAsync(default))
        {
            ledger.Accept(new(batch, DateTimeOffset.UtcNow)); var dataset = Assert.Single(ledger.Catalog());
            if (dataset.Schedule is not null && ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)).Events.Length == 14) break;
        }
        return ledger;
    });

    [Theory] [InlineData("goal-1")] [InlineData("goal-2")] [InlineData("goal-3")]
    public async Task EveryProductionGoalUsesExactSharedStepsWithTwoExistingReadsAndNoVisualInNarratorBundle(string id)
    {
        var ledger = await Production.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)); var record = end.Events.Single(r => r.RecordId == id);
        var release = dataset.Schedule!.Releases.Single(r => r.RecordId == id).CursorMs;
        var query = FullFootballSourceTests.Query(dataset, release - 15000);
        var context = new MatchChatContext(query, dataset.HeadSequence, new(id, release, record.FootballTime!.ReportedClock!));
        var reads = 0;
        var result = await new MatchWorkflowExecutor((q, _) => { reads++; return Task.FromResult(ledger.Query(q)); }).ExecuteAsync(
            new("selected_goal_event", MatchWorkflowRegistry.Version, new("unit-owner", 1, context, MatchWorkflowTemporalMode.SelectedGoalRetrospective), VisualIntent: MatchEventVisualIntent.ReportedSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Partial, result.Status); Assert.Contains("event_provenance_truncated", result.Bundle!.Missingness); Assert.Equal(2, reads);
        var visual = Assert.IsType<MatchEventVisual>(result.EventVisual); var sequence = ((MatchEventPayload)record.Payload).Sequence!;
        Assert.Equal(sequence.Id, visual.SequenceId); Assert.Equal(record.Revision, visual.EventRevision);
        Assert.Equal(sequence.Steps.Select(s => s.Kind), visual.Steps.Select(s => s.Kind));
        Assert.Equal(sequence.Steps.Select(s => s.Text), visual.Steps.Select(s => s.Text));
        Assert.Equal(sequence.Steps.Select(s => s.Uncertainty), visual.Steps.Select(s => s.Uncertainty));
        Assert.All(visual.Steps, s => { Assert.Equal(sequence.Steps.Single(x => x.Id == s.Id).EvidenceIds, s.EvidenceIds); if (s.ActorId is not null) Assert.Equal(dataset.People.Single(p => p.PersonId == s.ActorId).Name, s.ActorName); });
        Assert.All(visual.Evidence, e => Assert.Contains(record.Evidence, r => r.EvidenceId == e.Id && r.SourceId == e.SourceId && r.Method == e.Method));
        Assert.Equal(record.FootballTime.PeriodId, visual.PeriodId);
        var json = JsonSerializer.Serialize(result.Bundle); Assert.DoesNotContain("GoalVisual", json); Assert.DoesNotContain("TargetCursorMs", json);
        Assert.DoesNotContain("AttacksRight", json); Assert.DoesNotContain("Steps", JsonSerializer.Serialize(visual.Context));
        foreach (var other in end.Events.Where(r => r.RecordId != id && dataset.Schedule.Releases.Single(s => s.RecordId == r.RecordId).CursorMs > query.CursorMs))
            Assert.DoesNotContain(other.RecordId, json);
    }

    [Fact]
    public void GenericFixtureSnapshotDoesNotRetainMutableContractArraysOrInventAnonymousActors()
    {
        var (view, context) = Unit(); var record = view.Events[0]; var sequence = ((MatchEventPayload)record.Payload).Sequence!;
        var visual = Assert.IsType<MatchEventVisual>(MatchEventVisual.Create(view.Dataset, record, context));
        Assert.Equal("One", visual.Steps[0].ActorName); Assert.Null(visual.Steps[1].ActorId); Assert.Null(visual.Steps[1].ActorName);
        Assert.Equal("Two", visual.Steps[0].RelatedActorName); Assert.Null(((MatchEventPayload)record.Payload).AssistPersonId);
        var before = JsonSerializer.Serialize(visual);
        sequence.Steps[0].EvidenceIds[0] = "mutated"; sequence.Steps[0] = sequence.Steps[0] with { Text = "mutated" };
        view.Dataset.People[0] = view.Dataset.People[0] with { Name = "mutated" }; view.Dataset.Manifest.Sources[0] = new("mutated", "mutated", null);
        Assert.Equal(before, JsonSerializer.Serialize(visual));
        Assert.Throws<NotSupportedException>(() => ((IList<MatchEventVisualStep>)visual.Steps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)visual.Steps[0].EvidenceIds).Clear());
        Assert.All(typeof(MatchEventVisual).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Theory] [InlineData("too-many")] [InlineData("actor")] [InlineData("related")] [InlineData("source")]
    [InlineData("release")] [InlineData("clock")] [InlineData("head")] [InlineData("policy")] [InlineData("period")]
    [InlineData("missing")] [InlineData("text")]
    public void MissingMalformedOrUnboundSequenceCannotBecomeVisual(string fault)
    {
        var (view, context) = Unit(); var record = view.Events[0]; var goal = (MatchEventPayload)record.Payload; var sequence = goal.Sequence!;
        if (fault == "too-many") record = record with { Payload = goal with { Sequence = sequence with { Steps = Enumerable.Range(0, 6).Select(i => sequence.Steps[0] with { Id = "step-" + i }).ToArray() } } };
        if (fault is "actor" or "related" or "text") sequence.Steps[0] = fault switch { "actor" => sequence.Steps[0] with { ActorId = "unmapped-person" }, "related" => sequence.Steps[0] with { RelatedActorId = "team-home" }, _ => sequence.Steps[0] with { Text = new('x', 181) } };
        if (fault == "source") view.Dataset.Manifest.Sources[0] = new("other-source", "test", null);
        if (fault == "release") context = context with { SelectedEvent = context.SelectedEvent! with { ReleaseCursorMs = 60001 } };
        if (fault == "clock") context = context with { SelectedEvent = context.SelectedEvent! with { ReportedClock = "2" } };
        if (fault == "head") context = context with { HeadSequence = 99 };
        if (fault == "policy") context = context with { Query = context.Query with { PresentationPolicyRevision = 2 } };
        if (fault == "period") record = record with { FootballTime = record.FootballTime! with { PeriodId = "unknown-period" } };
        if (fault == "missing") record = record with { Payload = goal with { Sequence = null } };
        Assert.Null(MatchEventVisual.Create(view.Dataset, record, context));
    }

    [Fact]
    public void FiveStepBoundAndEventPeriodGeometryAreSharedWithMainPitchAcrossHalfBoundary()
    {
        var (view, context) = Unit(); var record = view.Events[0]; var goal = (MatchEventPayload)record.Payload;
        var steps = Enumerable.Range(0, 5).Select(i => goal.Sequence!.Steps[0] with { Id = "step-" + i, PresentationOffsetMs = -15000 + i * 3000 }).ToArray();
        record = record with { Payload = goal with { Sequence = goal.Sequence! with { Steps = steps } } };
        var visual = Assert.IsType<MatchEventVisual>(MatchEventVisual.Create(view.Dataset, record, context)); Assert.Equal(5, visual.Steps.Length);
        var frame = (TrackingPayload)SharedFootballTests.Frame(SharedFootballTests.Fixture(), 0).Payload;
        view = view with { Frame = frame, PeriodId = "unrelated-second", Events = [record] };
        var projected = MatchSequenceProjector.Event(view.Dataset, record, context.Query.CursorMs, true)!;
        var pitch = MatchPitchPresenter.Project(view, [projected], true, false);
        var expected = MatchSequenceLayout.Point(steps[0].Kind, visual.AttacksRight, view.Dataset.Fixture!.Pitch.LengthM, view.Dataset.Fixture.Pitch.WidthM);
        Assert.Equal(expected, pitch.Path[0]); Assert.Equal(103.95, expected.X, 5);
        Assert.False(MatchSequenceLayout.AttacksRight(2, "team-home", "team-home"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VisualSurvivesNarratorTransportOrValidationFailureButOrdinaryQuestionsKeepFailurePolicy(bool transport)
    {
        using var h = new Harness(); h.Model.Fail = transport ? "transport" : "invalid";
        Assert.True((await h.Ask("Show me how this goal happened")).Accepted);
        var visual = Assert.Single(h.Session.Turns); Assert.NotNull(visual.EventVisual); Assert.True(visual.EvidenceOnly); Assert.True(visual.SelectedEventScope);
        Assert.Contains(visual.Evidence, e => e.Id == "selected_goal"); Assert.All(visual.Evidence, e => Assert.Contains(e.Id, new[] { "selected_goal", "coverage" }));
        h.Session.NewChat(); var ordinary = await h.Ask("Explain the selected goal");
        Assert.False(ordinary.Accepted); Assert.Equal(MatchChatOutcome.NarratorUnavailable, ordinary.Outcome); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task VisualIsIndependentOfNarratorCitationChoiceAndIntentKeepsOriginalQuestion()
    {
        using var h = new Harness(); const string question = "Show me how this goal happened";
        Assert.True((await h.Ask(question)).Accepted); var turn = Assert.Single(h.Session.Turns);
        Assert.NotNull(turn.EventVisual); Assert.False(turn.EvidenceOnly); Assert.Single(turn.Evidence, e => e.Id == "coverage");
        Assert.Equal(question, h.Router.Last!.Question); Assert.Equal(question, h.Model.Request!.Messages[^1].Content);
        Assert.Contains(h.Model.Request.Messages, m => m.Content == CommentatorPrompt.EventVisualPresentation);
        Assert.DoesNotContain("GoalVisual", string.Join('\n', h.Model.Request.Messages.Select(m => m.Content)));
        Assert.Single(h.Api.Queries); h.Session.NewChat(); await h.Ask("Explain the selected goal");
        Assert.Null(Assert.Single(h.Session.Turns).EventVisual); Assert.DoesNotContain(h.Model.Request.Messages, m => m.Content == CommentatorPrompt.EventVisualPresentation);
    }

    [Theory] [InlineData("no-selection")] [InlineData("no-events")] [InlineData("missing-sequence")]
    public async Task VisualPrerequisitesClarifyOrReportUnavailableWithoutNarrator(string fault)
    {
        using var h = new Harness();
        if (fault == "no-selection") h.Set(h.Context with { SelectedEvent = null });
        if (fault == "no-events") { h.Set(h.Context with { SelectedEvent = null }); h.Api.View = h.Api.View with { Events = [] }; h.Router.Route = "current_match_moment"; }
        if (fault == "missing-sequence") h.Api.View = h.Api.View with { Events = h.Api.View.Events.Select(r => r with { Payload = ((MatchEventPayload)r.Payload) with { Sequence = null } }).ToArray() };
        var result = await h.Ask("Show me how this goal happened"); Assert.True(result.Accepted); Assert.Null(Assert.Single(h.Session.Turns).EventVisual);
        Assert.Equal(fault == "missing-sequence" ? MatchChatOutcome.Unavailable : MatchChatOutcome.Clarify, result.Outcome);
        Assert.Null(h.Model.Request); if (fault != "missing-sequence") { Assert.Contains("Choose a goal", result.Message); Assert.Empty(h.Api.Queries); }
    }

    [Fact]
    public async Task AustralianRecordingWithNoGoalAsksForSelectionWithoutInventingASequence()
    {
        var ledger = new FootballLedger();
        foreach (var batch in await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
        var dataset = Assert.Single(ledger.Catalog()); var view = ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        Assert.Empty(view.Events); using var h = new Harness(view); h.Router.Route = "current_match_moment";
        var result = await h.Ask("Show me how this goal happened"); Assert.Equal(MatchChatOutcome.Clarify, result.Outcome);
        Assert.Contains("Choose a goal", result.Message); Assert.Null(Assert.Single(h.Session.Turns).EventVisual); Assert.Empty(h.Api.Queries); Assert.Null(h.Model.Request);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VisualRetrievalFailureOrStaleHeadNeverUsesNarratorFallback(bool stale)
    {
        using var h = new Harness();
        if (stale) h.Api.View = h.Api.View with { Dataset = h.Api.View.Dataset with { HeadSequence = 99 } };
        else h.Api.Fail = true;
        var result = await h.Ask("Show me how this goal happened"); Assert.False(result.Accepted);
        Assert.Equal(stale ? MatchChatOutcome.Stale : MatchChatOutcome.RetrievalFailure, result.Outcome); Assert.Empty(h.Session.Turns); Assert.Null(h.Model.Request);
    }

    [Fact]
    public async Task PresentationKeepsOnlyItsNamedFutureVisualTurnAndTokenIsSingleUseWhileNormalRewindPrunes()
    {
        using var h = new Harness(); await h.Ask("Show me how this goal happened"); var visual = h.Session.Turns[0]; await h.Ask("Explain the selected goal");
        var permit = Assert.IsType<MatchEventPresentationTransition>(await h.Begin(visual.Id));
        Assert.Equal(45000, permit.TargetCursorMs); Assert.True(h.Session.BeginEventPresentationFetch(permit.Id));
        Assert.False(h.Session.BeginEventPresentationFetch(permit.Id));
        var target = h.Context with { Query = h.Context.Query with { CursorMs = permit.TargetCursorMs }, SelectedEvent = permit.Selection };
        Assert.True(await h.Session.CompleteEventPresentationAsync(permit.Id, target, h.Api.View.Dataset, h.Api.View.Events));
        Assert.Equal(visual.Id, Assert.Single(h.Session.Turns).Id); Assert.Equal(80000, visual.Context.Query.CursorMs); Assert.True(visual.SelectedEventScope);
        Assert.False(await h.Session.CompleteEventPresentationAsync(permit.Id, target, h.Api.View.Dataset, h.Api.View.Events));
        h.Set(target with { Query = target.Query with { CursorMs = 44000 } }); Assert.Empty(h.Session.Turns);
    }

    [Theory] [InlineData("cancel")] [InlineData("reset")] [InlineData("owner")] [InlineData("head")]
    [InlineData("policy")] [InlineData("domain")] [InlineData("cursor")] [InlineData("revision")]
    [InlineData("sequence")] [InlineData("failed")] [InlineData("superseded")] [InlineData("selection")]
    public async Task PresentationRejectsChangedOwnerDomainHeadPolicyRevisionSequenceOrSupersedingIntent(string change)
    {
        using var h = new Harness(); await h.Ask("Show me how this goal happened"); var turn = Assert.Single(h.Session.Turns);
        var permit = Assert.IsType<MatchEventPresentationTransition>(await h.Begin(turn.Id)); Assert.True(h.Session.BeginEventPresentationFetch(permit.Id));
        var target = h.Context with { Query = h.Context.Query with { CursorMs = permit.TargetCursorMs }, SelectedEvent = permit.Selection };
        if (change == "cancel") h.Session.Cancel(); if (change == "reset") h.Session.NewChat(); if (change == "owner") h.Auth.Change("another");
        if (change == "head") target = target with { HeadSequence = target.HeadSequence + 1 };
        if (change == "policy") target = target with { Query = target.Query with { PresentationPolicyRevision = 2 } };
        if (change == "domain") target = target with { Query = target.Query with { StreamId = "another-stream" } };
        if (change == "cursor") target = target with { Query = target.Query with { CursorMs = target.Query.CursorMs + 1 } };
        if (change == "selection") target = target with { SelectedEvent = permit.Selection with { RecordId = "another-goal" } };
        if (change == "revision") h.Api.View.Events[0] = h.Api.View.Events[0] with { Revision = 2, SupersedesRevision = 1 };
        if (change == "sequence") { var goal = (MatchEventPayload)h.Api.View.Events[0].Payload; h.Api.View.Events[0] = h.Api.View.Events[0] with { Payload = goal with { Sequence = goal.Sequence! with { Id = "changed-sequence" } } }; }
        if (change == "failed") h.Session.CancelEventPresentation(permit.Id);
        if (change == "superseded") Assert.NotNull(await h.Begin(turn.Id));
        Assert.False(await h.Session.CompleteEventPresentationAsync(permit.Id, target, h.Api.View.Dataset, h.Api.View.Events));
        Assert.False(await h.Session.CompleteEventPresentationAsync(permit.Id, target, h.Api.View.Dataset, h.Api.View.Events));
    }

    [Fact]
    public async Task StaleTurnIdsAndNormalRewindCannotStartPresentation()
    {
        using var h = new Harness(); Assert.Null(await h.Begin(Guid.NewGuid())); await h.Ask("Explain the selected goal");
        Assert.Null(await h.Begin(h.Session.Turns[0].Id)); await h.Ask("Show me how this goal happened"); var visual = h.Session.Turns[^1];
        h.Set(h.Context with { Query = h.Context.Query with { CursorMs = 70000 } }); Assert.Empty(h.Session.Turns); Assert.Null(await h.Begin(visual.Id));
    }

    [Theory] [InlineData("cancel")] [InlineData("reset")] [InlineData("owner")] [InlineData("cursor")]
    public async Task PendingNarratorCannotCommitEvenVisualFallbackAfterCancellationOrContextChange(string change)
    {
        using var h = new Harness(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Model.Wait = () => { entered.SetResult(); return finish.Task; };
        var pending = h.Ask("Show me how this goal happened"); await entered.Task;
        if (change == "cancel") h.Session.Cancel(); if (change == "reset") h.Session.NewChat(); if (change == "owner") h.Auth.Change("another");
        if (change == "cursor") h.Set(h.Context with { Query = h.Context.Query with { CursorMs = 70000 } });
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Accepted); finish.SetException(new HttpRequestException()); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task DiagramRendersNumberedSemanticsAndStronglyTypedTurnAction()
    {
        using var h = new Harness(); await h.Ask("Show me how this goal happened"); var turn = Assert.Single(h.Session.Turns);
        var component = new RenderableDiagram(); var visual = turn.EventVisual!;
        typeof(EventSequenceDiagram).GetProperty("Visual")!.SetValue(component, visual); typeof(EventSequenceDiagram).GetProperty("TurnId")!.SetValue(component, turn.Id);
        MatchEventVisualAction? action = null;
        var callback = EventCallback.Factory.Create<MatchEventVisualAction>(new object(), a => action = a);
        typeof(EventSequenceDiagram).GetProperty("OnShowOnPitch")!.SetValue(component, callback);
        using var tree = new RenderTreeBuilder(); component.Render(tree); var frames = tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
        var text = string.Join(' ', frames.Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
        Assert.Contains("How this goal happened", text); Assert.Contains("Illustration of the reported sequence", text); Assert.Contains("Related participant:", text);
        Assert.Equal(visual.Steps.Length, frames.Count(f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "li"));
        var click = Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick");
        await ((Func<Task>)click.AttributeValue)(); Assert.Equal(turn.Id, action!.TurnId);
        var root = LocalPaths.FindRoot(); var css = File.ReadAllText(Path.Combine(root, "src/Matchmate.Web/Components/Experience/EventSequenceDiagram.razor.css"));
        Assert.Contains("min-height:44px", css); Assert.Contains("@media(max-width:600px)", css); Assert.Contains("focus-visible", css);
        var js = File.ReadAllText(Path.Combine(root, "src/Matchmate.Web/Components/Pages/FootballReplay.razor.js"));
        Assert.Contains("prefers-reduced-motion: reduce", js); Assert.Contains("preventScroll: true", js); Assert.DoesNotContain("fetch(", js);
        var page = File.ReadAllText(Path.Combine(root, "src/Matchmate.Web/Components/Pages/FootballReplay.razor"));
        Assert.Contains("OnShowOnPitch=\"ShowEventOnPitchAsync\"", page); Assert.Contains("@ref=\"_pitchElement\" tabindex=\"-1\"", page);
    }

    private sealed class RenderableDiagram : EventSequenceDiagram { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RejectedFetchTokenRestoresAcceptedCursorWithoutBlockingOrReading(bool clearSelection)
    {
        using var h = new Harness(); var page = ReplayPage(h); Set(page, "_cursorMs", 45000L);
        var original = clearSelection ? h.Context with { SelectedEvent = null } : h.Context;
        Set(page, "_eventPresentationContext", original);
        await (Task)typeof(FootballReplay).GetMethod("FetchCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [h.Api.View.Dataset, Guid.NewGuid()])!;
        Assert.Equal(80000L, Get(page, "_cursorMs")); Assert.False((bool)Get(page, "_updating")!); Assert.False((bool)Get(page, "_contextUnavailable")!);
        Assert.Equal(original.SelectedEvent, Get(page, "_selectedEvent")); Assert.Empty(h.Api.Queries); await page.DisposeAsync();
    }

    [Fact]
    public async Task PagePinsOriginalConversationDuringCompletionAuthenticationThenFocusesWithoutAnotherModelRequest()
    {
        using var h = new Harness(); await h.Ask("Show me how this goal happened"); var turn = Assert.Single(h.Session.Turns); var page = ReplayPage(h);
        var state = h.Auth.State(); var authentication = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        h.Auth.Respond = () => { if (++calls != 2) return Task.FromResult(state); entered.SetResult(); return authentication.Task; };
        var js = new PitchJs(); typeof(FootballReplay).GetProperty("JS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, js);
        var showing = (Task)typeof(FootballReplay).GetMethod("ShowEventOnPitchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [new MatchEventVisualAction(turn.Id)])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pinned = (MatchChatContext)typeof(FootballReplay).GetProperty("ConversationContext", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal(h.Context, pinned); Assert.Equal(45000, ((FootballView)Get(page, "_view")!).Context.CursorMs);
        var conversation = new RenderableConversation(); typeof(MatchConversation).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(conversation, h.Session);
        typeof(MatchConversation).GetProperty("Context")!.SetValue(conversation, pinned); typeof(MatchConversation).GetProperty("Updating")!.SetValue(conversation, true);
        conversation.Apply(); // The real Updating/render path must not invalidate an idle presentation transition.
        authentication.SetResult(state); await js.Module.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(turn.Id, Assert.Single(h.Session.Turns).Id); Assert.Equal(1, h.Model.Calls); Assert.Equal(2, h.Api.Queries.Count);
        Set(page, "_intentGeneration", (long)Get(page, "_intentGeneration")! + 1); // Supersede while focus is pending: no old autoplay may start.
        js.Module.Release.SetResult(); await showing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False((bool)Get(page, "_playing")!); Assert.Equal(1, js.Module.FocusCalls);
        await page.DisposeAsync(); Assert.True(js.Module.Disposed);
    }

    [Fact]
    public async Task F_GV_001_DisposeDuringImportSuppressesAllFocusAndDisposesLateModuleExactlyOnce()
    {
        var js = new LifecycleJs { HoldImport = true }; var interop = new ReplayPitchInterop(js);
        var first = interop.FocusAsync(default).AsTask(); await js.ImportEntered.Task;
        var concurrent = interop.FocusAsync(default).AsTask();
        var disposal = interop.DisposeAsync().AsTask(); var repeatedDisposal = interop.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(1, js.ImportCalls); Assert.Equal(0, js.Module.FocusCalls);
        js.ImportRelease.SetResult(js.Module);
        await Task.WhenAll(first, concurrent, disposal, repeatedDisposal).WaitAsync(TimeSpan.FromSeconds(2));
        await interop.FocusAsync(default); await interop.DisposeAsync();
        Assert.Equal(1, js.ImportCalls); Assert.Equal(0, js.Module.FocusCalls); Assert.Equal(1, js.Module.DisposeCalls);
    }

    [Fact]
    public async Task F_GV_001_RepeatedConnectedFocusReusesImportAndDisposalIsIdempotent()
    {
        var js = new LifecycleJs(); var interop = new ReplayPitchInterop(js);
        await interop.FocusAsync(default); await interop.FocusAsync(default);
        Assert.Equal(1, js.ImportCalls); Assert.Equal(2, js.Module.FocusCalls); Assert.Equal(0, js.Module.DisposeCalls);
        await interop.DisposeAsync(); await interop.DisposeAsync(); await interop.FocusAsync(default);
        Assert.Equal(1, js.ImportCalls); Assert.Equal(2, js.Module.FocusCalls); Assert.Equal(1, js.Module.DisposeCalls);
    }

    [Fact]
    public async Task F_GV_001_DisposalWaitsForAdmittedFocusAndSuppressesQueuedFocus()
    {
        var js = new LifecycleJs(); js.Module.HoldFocus = true; var interop = new ReplayPitchInterop(js);
        var first = interop.FocusAsync(default).AsTask(); await js.Module.FocusEntered.Task;
        var queued = interop.FocusAsync(default).AsTask(); var disposal = interop.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.Equal(0, js.Module.DisposeCalls);
        js.Module.FocusRelease.SetResult();
        await Task.WhenAll(first, queued, disposal).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, js.ImportCalls); Assert.Equal(1, js.Module.FocusCalls); Assert.Equal(1, js.Module.DisposeCalls);
    }

    [Theory] [InlineData("import")] [InlineData("focus")] [InlineData("dispose")] [InlineData("late-dispose")]
    public async Task F_GV_001_DisconnectedCircuitRemainsSafeDuringImportFocusAndDisposal(string stage)
    {
        var js = new LifecycleJs { DisconnectImport = stage == "import", HoldImport = stage == "late-dispose" };
        js.Module.DisconnectFocus = stage == "focus"; js.Module.DisconnectDispose = stage is "dispose" or "late-dispose";
        var interop = new ReplayPitchInterop(js); var focus = interop.FocusAsync(default).AsTask();
        if (stage == "late-dispose")
        {
            await js.ImportEntered.Task; var disposing = interop.DisposeAsync().AsTask(); js.ImportRelease.SetResult(js.Module);
            await Task.WhenAll(focus, disposing).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, js.Module.FocusCalls);
        }
        else { await focus; await interop.DisposeAsync(); }
        await interop.DisposeAsync(); await interop.FocusAsync(default);
        Assert.Equal(1, js.ImportCalls); Assert.Equal(stage == "import" ? 0 : 1, js.Module.DisposeCalls);
    }

    private sealed class RenderableConversation : MatchConversation { public void Apply() => OnParametersSet(); }
    private static FootballReplay ReplayPage(Harness h)
    {
        var page = new FootballReplay();
        typeof(FootballReplay).GetProperty("Api", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, h.Client);
        typeof(FootballReplay).GetProperty("ChatSession", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, h.Session);
        foreach (var (field, value) in new (string, object)[] { ("_view", h.Api.View), ("_catalog", new[] { h.Api.View.Dataset }),
            ("_selectedStream", h.Context.Query.StreamId), ("_cursorMs", h.Context.Query.CursorMs), ("_selectedEvent", h.Context.SelectedEvent!),
            ("_highlights", ExperiencePresentation.Highlights(h.Api.View)), ("_sequenceIndex", h.Api.View.Events), ("_highlightHead", h.Context.HeadSequence), ("_indexLoaded", true) }) Set(page, field, value);
        return page;
    }
    private static void Set(object target, string name, object value) => typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object? Get(object target, string name) => typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    // Deterministic test doubles never invoke browser JS; production wrapper retains all interop guards.
    #pragma warning disable BL0016
    private sealed class LifecycleJs : IJSRuntime
    {
        public readonly LifecycleModule Module = new();
        public readonly TaskCompletionSource ImportEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IJSObjectReference> ImportRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldImport, DisconnectImport; public int ImportCalls;
        private async ValueTask<TValue> Import<TValue>(string identifier, object?[]? args)
        {
            Assert.Equal("import", identifier); Assert.Equal("./Components/Pages/FootballReplay.razor.js", args![0]);
            ImportCalls++; ImportEntered.TrySetResult();
            if (DisconnectImport) throw new JSDisconnectedException("Synthetic test disconnect during import.");
            var module = HoldImport ? await ImportRelease.Task : Module;
            return (TValue)(object)module;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Import<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => Import<TValue>(identifier, args);
    }
    private sealed class LifecycleModule : IJSObjectReference
    {
        public readonly TaskCompletionSource FocusEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), FocusRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldFocus, DisconnectFocus, DisconnectDispose; public int FocusCalls, DisposeCalls;
        private async ValueTask<TValue> Focus<TValue>(string identifier, object?[]? args)
        {
            Assert.Equal("focusPitch", identifier); Assert.IsType<ElementReference>(args![0]); FocusCalls++; FocusEntered.TrySetResult();
            if (DisconnectFocus) throw new JSDisconnectedException("Synthetic test disconnect during focus.");
            if (HoldFocus) await FocusRelease.Task;
            return default!;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Focus<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => Focus<TValue>(identifier, args);
        public ValueTask DisposeAsync() { DisposeCalls++; if (DisconnectDispose) throw new JSDisconnectedException("Synthetic test disconnect during disposal."); return ValueTask.CompletedTask; }
    }
    private sealed class PitchJs : IJSRuntime
    {
        public readonly PitchModule Module = new();
        private ValueTask<TValue> Import<TValue>(string identifier, object?[]? args) { Assert.Equal("import", identifier); Assert.Equal("./Components/Pages/FootballReplay.razor.js", args![0]); return ValueTask.FromResult((TValue)(object)Module); }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Import<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => Import<TValue>(identifier, args);
    }
    private sealed class PitchModule : IJSObjectReference
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FocusCalls; public bool Disposed;
        private async ValueTask<TValue> Focus<TValue>(string identifier, object?[]? args) { Assert.Equal("focusPitch", identifier); Assert.IsType<ElementReference>(args![0]); FocusCalls++; Entered.TrySetResult(); await Release.Task; return default!; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Focus<TValue>(identifier, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => Focus<TValue>(identifier, args);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    #pragma warning restore BL0016
    internal static (FootballView View, MatchChatContext Context) Unit()
    {
        var f = SharedFootballTests.Fixture("generic-version", "generic-stream");
        f = f with { Manifest = f.Manifest with { Capabilities = [.. f.Manifest.Capabilities, "shared-event-sequences-v1"] } };
        var ledger = new FootballLedger();
        SharedFootballTests.Add(ledger, f, 1, SharedFootballTests.Context(f).Select(r => r.Payload is SchedulePayload schedule ? r with { Payload = schedule with { Releases = [new("generic-goal", 60000)] } } : r).ToArray());
        var record = SharedFootballTests.Goal(f) with { RecordId = "generic-goal", FootballTime = SharedFootballTests.Goal(f).FootballTime! with { ReportedClock = "1" } };
        record = record with { Payload = ((MatchEventPayload)record.Payload) with { Sequence = new(1, "generic-sequence", 15000, 5000,
            [new("corner", "corner", "person-one", "person-two", -15000, "Reported corner.", ["source"]),
             new("unknown", "uncertain_goalmouth", null, null, -8000, "Intermediate contact unknown.", ["source"], "Contact and actor unconfirmed."),
             new("finish", "header_finish", "person-one", null, -2000, "Reported finish.", ["source"])]) } };
        SharedFootballTests.Add(ledger, f, 2, record);
        var view = ledger.Query(SharedFootballTests.Query(f, 80000));
        return (view, new(view.Context, view.Dataset.HeadSequence, new(record.RecordId, 60000, "1")));
    }

    private sealed class Harness : IDisposable
    {
        public readonly Auth Auth = new(); public readonly Router Router = new(); public readonly Model Model = new();
        public readonly EvidenceApi Api; public readonly MatchChatSession Session; private readonly HttpClient _http;
        public ReplayApiClient Client => new(_http);
        public MatchChatContext Context { get; private set; }
        public Harness(FootballView? actual = null) { var unit = Unit(); Context = actual is null ? unit.Context : new(actual.Context, actual.Dataset.HeadSequence); Api = new(actual ?? unit.View); _http = new(Api) { BaseAddress = new("https://unit.invalid") }; Session = new(Auth, new ReplayApiClient(_http), Router, Model, Options.Create(new MatchChatOptions())); Session.SetContext(Context); }
        public void Set(MatchChatContext context) { Context = context; Session.SetContext(context); }
        public Task<MatchChatResult> Ask(string question) => Session.AskAsync(Context, question);
        public Task<MatchEventPresentationTransition?> Begin(Guid id) => Session.BeginEventPresentationAsync(new(id), Context, Api.View.Dataset, Api.View.Events);
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    private sealed class Auth : AuthenticationStateProvider
    {
        private string _id = "unit-owner";
        public Func<Task<AuthenticationState>>? Respond;
        public AuthenticationState State() => new(new ClaimsPrincipal(new ClaimsIdentity([new("sub", _id), new("roles", "Tester")], "unit", "sub", "roles")));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Respond?.Invoke() ?? Task.FromResult(State());
        public void Change(string id) { _id = id; NotifyAuthenticationStateChanged(Task.FromResult(State())); }
    }
    private sealed class Router : IMatchWorkflowRouter
    {
        public bool Configured => true; public string Route = "selected_goal_event"; public MatchRoutingInput? Last;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Last = input; return Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, Route)); }
    }
    private sealed class Model : IMatchChatModel
    {
        public bool Configured => true; public string? Fail; public MatchNarratorRequest? Request; public Func<Task<MatchModelReply>>? Wait; public int Calls;
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { Request = request; Calls++; if (Wait is not null) return Wait(); if (Fail == "transport") throw new HttpRequestException(); return Task.FromResult(new MatchModelReply(Fail == "invalid" ? "invalid JSON" : JsonSerializer.Serialize(new { answer = "The selected goal has a reported sequence.", evidenceIds = new[] { "coverage" } }), [])); }
    }
    private sealed class EvidenceApi(FootballView view) : HttpMessageHandler
    {
        public FootballView View = view; public readonly List<FootballQuery> Queries = []; public bool Fail; public Func<FootballQuery, FootballView>? Read;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; Queries.Add(query); return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = JsonContent.Create(Read is null ? View with { Context = query } : Read(query)) }; }
    }
}
#pragma warning restore BL0006
