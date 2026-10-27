using System.Reflection;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components.Authorization;
using Xunit;

namespace Matchmate.Tests;

public sealed partial class MatchGoalVisualTests
{
    [Theory]
    [InlineData("substitution", false)] [InlineData("substitution", true)]
    [InlineData("yellow_card", false)] [InlineData("yellow_card", true)]
    [InlineData("completed_possession", false)] [InlineData("completed_possession", true)]
    public async Task F_EV_001_MalformedPossessionCollectionFailsBeforeSelectedLookupWithoutFutureReadOrNarrator(string kind, bool nullEntry)
    {
        var end = EndOf(kind == "completed_possession" ? await PossessionSource.Value : await Production.Value);
        var record = kind == "completed_possession" ? end.Possessions[0] : end.Events.First(r => r.Payload is MatchEventPayload e && e.EventKind == kind);
        var context = new MatchChatContext(end.Context, end.Dataset.HeadSequence, Select(end, record));
        var malformed = end with { Possessions = nullEntry ? [null!] : null! };
        foreach (var route in new[] { "current_match_events", "current_match_moment" })
        {
            var reads = new List<FootballQuery>();
            var result = await new MatchWorkflowExecutor((query, _) => { reads.Add(query); return Task.FromResult(malformed with { Context = query }); })
                .ExecuteAsync(new(route, MatchWorkflowRegistry.Version, new("test-owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor),
                    VisualIntent: MatchEventVisualIntent.ReportedSequence), _ => true);
            Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle); Assert.Null(result.EventVisual);
            Assert.Equal(context.Query.CursorMs, Assert.Single(reads).CursorMs); Assert.Equal(context.HeadSequence, reads[0].ApplicationSequence);
        }
        using var h = new Harness(end); h.Set(context); h.Router.Route = "current_match_events"; h.Api.View = malformed;
        var answer = await h.Ask("Show this event on a diagram");
        Assert.False(answer.Accepted); Assert.Equal(MatchChatOutcome.RetrievalFailure, answer.Outcome); Assert.Empty(h.Session.Turns); Assert.Equal(0, h.Model.Calls);
        Assert.Equal(context.Query.CursorMs, Assert.Single(h.Api.Queries).CursorMs); Assert.Equal(context.HeadSequence, h.Api.Queries[0].ApplicationSequence);
    }

    [Theory]
    [InlineData("substitution", "current_match_events")] [InlineData("substitution", "current_match_moment")]
    [InlineData("yellow_card", "current_match_events")] [InlineData("yellow_card", "current_match_moment")]
    [InlineData("completed_possession", "current_match_events")] [InlineData("completed_possession", "current_match_moment")]
    public async Task F_EV_001_ValidSelectedNonGoalStillUsesOneCurrentReadThroughEitherAllowedRoute(string kind, string route)
    {
        var end = EndOf(kind == "completed_possession" ? await PossessionSource.Value : await Production.Value);
        var record = kind == "completed_possession" ? end.Possessions[0] : end.Events.First(r => r.Payload is MatchEventPayload e && e.EventKind == kind);
        using var h = new Harness(end); h.Set(new(end.Context, end.Dataset.HeadSequence, Select(end, record))); h.Router.Route = route;
        Assert.True((await h.Ask("Show this event on a diagram")).Accepted);
        var turn = Assert.Single(h.Session.Turns); Assert.Equal(record.RecordId, turn.EventVisual!.EventId); Assert.Equal(kind, turn.EventVisual.Kind);
        Assert.Equal(1, h.Model.Calls); Assert.Equal(end.Context.CursorMs, Assert.Single(h.Api.Queries).CursorMs);
        Assert.Contains(h.Model.Request!.Messages, m => m.Content.StartsWith("Prepared app-computed") && m.Content.Contains("selected_event"));
        Assert.Contains(h.Model.Request.Messages, m => m.Content == CommentatorPrompt.EventVisualPresentation);
    }

    private static readonly Lazy<Task<FootballLedger>> PossessionSource = new(async () => {
        var ledger = new FootballLedger();
        foreach (var batch in await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
        return ledger;
    });
    private static FootballView EndOf(FootballLedger ledger)
    {
        var dataset = Assert.Single(ledger.Catalog());
        return ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
    }
    private static MatchSelectedEvent Select(FootballView view, FootballRecord record)
    {
        var kind = record.Payload is MatchEventPayload report ? report.EventKind : "completed_possession";
        var projection = record.Payload is PossessionPayload ? MatchSequenceProjector.Possession(view.Dataset, record, view.Context.CursorMs)!
            : MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs, kind == "goal")!;
        return new(record.RecordId, projection.ReleaseCursorMs, kind == "completed_possession" ? MatchEventVisual.IntervalLabel(projection) : record.FootballTime!.ReportedClock!, kind);
    }

    [Theory] [InlineData(false, 14)] [InlineData(true, 5)]
    public async Task D100_AllNineteenSupportedRecordsShareExactBoundedVisualEvidenceAndExistingProjection(bool possessions, int expected)
    {
        var ledger = possessions ? await PossessionSource.Value : await Production.Value; var end = EndOf(ledger);
        var records = possessions ? end.Possessions : end.Events; Assert.Equal(expected, records.Length);
        var highlights = ExperiencePresentation.Highlights(end); Assert.Equal(expected, highlights.Length);
        if (!possessions) { Assert.Equal(3, highlights.Count(h => h.Kind == "goal")); Assert.Equal(9, highlights.Count(h => h.Kind == "substitution")); Assert.Equal(2, highlights.Count(h => h.Kind == "yellow_card")); }
        foreach (var record in records)
        {
            var selection = Select(end, record); var context = new MatchChatContext(end.Context, end.Dataset.HeadSequence, selection);
            var reads = 0; var goal = selection.Kind == "goal";
            var result = await new MatchWorkflowExecutor((q, _) => { reads++; return Task.FromResult(ledger.Query(q)); }).ExecuteAsync(
                new(goal ? "selected_goal_event" : "current_match_moment", MatchWorkflowRegistry.Version,
                    new("test-owner", 1, context, goal ? MatchWorkflowTemporalMode.SelectedGoalRetrospective : MatchWorkflowTemporalMode.ReplayAtCursor), VisualIntent: MatchEventVisualIntent.ReportedSequence), _ => true);
            Assert.True(result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial, result.Message); Assert.Equal(1, reads);
            var visual = Assert.IsType<MatchEventVisual>(result.EventVisual); Assert.Equal(selection.Kind, visual.Kind); Assert.Equal(record.Revision, visual.EventRevision);
            Assert.InRange(result.Bundle!.Items.Length, 1, 4); Assert.Equal(selection, result.Bundle.Context.SelectedEvent);
            var source = record.Payload is PossessionPayload ? MatchSequenceProjector.Possession(end.Dataset, record, end.Context.CursorMs)! : MatchSequenceProjector.Event(end.Dataset, record, end.Context.CursorMs, goal)!;
            Assert.Equal(source.Sequence.Id, visual.SequenceId); Assert.Equal(source.Sequence.Steps.Select(s => s.Text), visual.Steps.Select(s => s.Text));
            Assert.Equal(source.Sequence.Steps.Select(s => s.Uncertainty), visual.Steps.Select(s => s.Uncertainty));
            Assert.All(visual.Steps, s => { Assert.Equal(source.Sequence.Steps.Single(x => x.Id == s.Id).EvidenceIds, s.EvidenceIds); if (s.ActorId is not null) Assert.Equal(end.Dataset.People.Single(p => p.PersonId == s.ActorId).Name, s.ActorName); });
            Assert.DoesNotContain("EventVisual", JsonSerializer.Serialize(result.Bundle)); Assert.Equal(selection.ReleaseCursorMs - (goal ? 15000 : 0), visual.TargetCursorMs);
            Assert.Contains(highlights, h => h.RecordId == visual.EventId && h.CursorMs == visual.ReleaseCursorMs && h.Kind == visual.Kind);
            if (!goal)
            {
                var evidence = Assert.Single(result.Bundle.Items, i => i.Id == "selected_event"); Assert.Contains(source.Sequence.Id, evidence.Text);
                Assert.DoesNotContain(result.Bundle.Items, i => i.Id == "selected_goal");
                var pitch = MatchPitchPresenter.Project(end, [source], true, false); Assert.Empty(pitch.Path); Assert.Null(pitch.Contact);
                var before = context with { Query = context.Query with { CursorMs = selection.ReleaseCursorMs - 1 } };
                Assert.Null(MatchEventVisual.Create(end.Dataset, record, before));
                reads = 0;
                var future = await new MatchWorkflowExecutor((q, _) => { reads++; return Task.FromResult(ledger.Query(q)); }).ExecuteAsync(
                    new("current_match_moment", MatchWorkflowRegistry.Version, new("test-owner", 1, before, MatchWorkflowTemporalMode.ReplayAtCursor), VisualIntent: MatchEventVisualIntent.ReportedSequence), _ => true);
                Assert.Equal(1, reads); Assert.Equal(MatchWorkflowStatus.Unavailable, future.Status); Assert.Null(future.EventVisual);
            }
            if (selection.Kind == "substitution") { var substitution = (MatchEventPayload)record.Payload; Assert.Equal(substitution.OutgoingPersonId, visual.Steps[0].ActorId); Assert.Equal(substitution.IncomingPersonId, visual.Steps[0].RelatedActorId); }
            if (selection.Kind == "completed_possession") { Assert.Equal(record.FootballTime!.StartMs, visual.IntervalStartMs); Assert.Equal(record.FootballTime.EndMs, visual.IntervalEndMs); Assert.Contains("not possession now", result.Bundle.Items.Single(i => i.Id == "selected_event").Text); }
        }
    }

    [Theory] [InlineData(61800, "01:01.8")] [InlineData(65200, "01:05.2")] [InlineData(131300, "02:11.3")] [InlineData(60000, "01:00")]
    public void D100_IntervalLabelsPreserveFractionalSourceEndpoints(long value, string label) => Assert.Equal(label, ExperiencePresentation.IntervalTime(value));

    [Theory] [InlineData("Show the change in Danny Welbeck’s play", false)] [InlineData("Draw the change in Danny's performance", false)]
    [InlineData("Diagram the change in possession", true)] [InlineData("Show this player change on a diagram", true)]
    public void D100_VisualIntentDoesNotHijackOrdinaryNamedPlayerChanges(string question, bool visual)
    {
        Assert.Equal(visual, MatchEventVisual.Intent(question) is not null);
        if (question.Contains("possession")) Assert.True(MatchEventVisual.MatchesRequestedKind(question, new("interval", 1, "point", "completed_possession")));
    }

    [Theory] [InlineData("substitution")] [InlineData("yellow_card")] [InlineData("completed_possession")]
    public async Task D100_NonGoalSourceFallbackRetainsOnlyItsOwnTurnAndNormalRewindStillPrunes(string kind)
    {
        var ledger = kind == "completed_possession" ? await PossessionSource.Value : await Production.Value; var end = EndOf(ledger);
        var record = kind == "completed_possession" ? end.Possessions[0] : end.Events.First(r => ((MatchEventPayload)r.Payload).EventKind == kind);
        var selection = Select(end, record); using var h = new Harness(end); h.Set(new(end.Context, end.Dataset.HeadSequence, selection)); h.Router.Route = "current_match_moment";
        h.Model.Fail = "invalid"; Assert.True((await h.Ask("Show this event on a diagram")).Accepted); var kept = Assert.Single(h.Session.Turns);
        Assert.True(kept.EvidenceOnly); Assert.True(kept.SelectedEventScope); Assert.NotNull(kept.EventVisual);
        Assert.Contains(kept.Evidence, e => e.Id == "selected_event"); h.Model.Fail = null;
        await h.Ask("Explain the current replay moment"); Assert.Equal(2, h.Session.Turns.Count); Assert.Null(h.Session.Turns[1].EventVisual);
        var index = end.Events.Concat(end.Possessions).ToArray();
        var permit = Assert.IsType<MatchEventPresentationTransition>(await h.Session.BeginEventPresentationAsync(new(kept.Id), h.Context, end.Dataset, index));
        Assert.Equal(selection.ReleaseCursorMs, permit.TargetCursorMs); Assert.True(h.Session.BeginEventPresentationFetch(permit.Id));
        var target = h.Context with { Query = h.Context.Query with { CursorMs = permit.TargetCursorMs } };
        Assert.True(await h.Session.CompleteEventPresentationAsync(permit.Id, target, end.Dataset, index)); Assert.Equal(kept.Id, Assert.Single(h.Session.Turns).Id);
        h.Set(target with { Query = target.Query with { CursorMs = target.Query.CursorMs - 1 } }); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task D100_SameReleaseSubstitutionSelectionAndOldShowFinallyCannotClearNewerTransitionPin()
    {
        var end = EndOf(await Production.Value); var pair = ExperiencePresentation.Highlights(end).Where(h => h.Kind == "substitution").GroupBy(h => h.CursorMs).First(g => g.Count() > 1).Take(2).ToArray();
        using var h = new Harness(end); h.Router.Route = "current_match_moment"; h.Set(new(end.Context, end.Dataset.HeadSequence, new(pair[0].RecordId, pair[0].CursorMs, pair[0].ReportedClock, pair[0].Kind)));
        await h.Ask("Show this substitution on a diagram"); var first = Assert.Single(h.Session.Turns); var page = ReplayPage(h); var js = new PitchJs();
        typeof(FootballReplay).GetProperty("JS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, js);
        var show = typeof(FootballReplay).GetMethod("ShowEventOnPitchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var old = (Task)show.Invoke(page, [new MatchEventVisualAction(first.Id)])!; await js.Module.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var view = (FootballView)Get(page, "_view")!; var secondSelection = new MatchSelectedEvent(pair[1].RecordId, pair[1].CursorMs, pair[1].ReportedClock, pair[1].Kind);
        Set(page, "_selectedEvent", secondSelection); h.Set(new(view.Context, view.Dataset.HeadSequence, secondSelection));
        await h.Ask("Show this substitution on a diagram"); var second = Assert.Single(h.Session.Turns);
        var state = h.Auth.State(); var hold = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var authCalls = 0;
        h.Auth.Respond = () => { if (++authCalls != 2) return Task.FromResult(state); entered.SetResult(); return hold.Task; };
        var newer = (Task)show.Invoke(page, [new MatchEventVisualAction(second.Id)])!; await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var id = Get(page, "_eventPresentationId"); Assert.NotNull(id); js.Module.Release.SetResult(); await old.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(id, Get(page, "_eventPresentationId")); Assert.Equal(h.Context, Get(page, "_eventPresentationContext"));
        var scenes = (MatchSequenceProjection[])typeof(FootballReplay).GetProperty("PitchSequences", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal(pair[1].RecordId, Assert.Single(scenes).RecordId);
        hold.SetResult(state); await newer.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(second.Id, Assert.Single(h.Session.Turns).Id); Assert.False((bool)Get(page, "_playing")!);
        await page.DisposeAsync();
    }

    [Fact]
    public async Task D100_PossessionHighlightAndPitchActionUseEligibleCompletionWithoutGoalReleaseOrAutoplay()
    {
        var ledger = await PossessionSource.Value; var end = EndOf(ledger); using var h = new Harness(end); h.Api.Read = q => ledger.Query(q);
        var page = ReplayPage(h); Set(page, "_possessionIndex", end.Possessions);
        var highlight = ExperiencePresentation.Highlights(end)[0]; Assert.Equal("completed_possession", highlight.Kind);
        Assert.DoesNotContain(end.Dataset.Schedule!.Releases, r => r.RecordId == highlight.RecordId);
        await (Task)typeof(FootballReplay).GetMethod("HighlightAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [highlight])!;
        Assert.Equal(highlight.CursorMs, Get(page, "_cursorMs")); Assert.False((bool)Get(page, "_playing")!);
        var context = (MatchChatContext)typeof(FootballReplay).GetProperty("ConversationContext", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal("completed_possession", context.SelectedEvent!.Kind); h.Set(context); h.Router.Route = "current_match_moment";
        Assert.True((await h.Ask("Show this completed possession on a diagram")).Accepted); var turn = Assert.Single(h.Session.Turns);
        var js = new PitchJs(); js.Module.Release.SetResult(); typeof(FootballReplay).GetProperty("JS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, js);
        await (Task)typeof(FootballReplay).GetMethod("ShowEventOnPitchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [new MatchEventVisualAction(turn.Id)])!;
        Assert.Equal(highlight.CursorMs, Get(page, "_cursorMs")); Assert.False((bool)Get(page, "_playing")!); Assert.Equal(1, js.Module.FocusCalls); Assert.Equal(1, h.Model.Calls);
        var scenes = (MatchSequenceProjection[])typeof(FootballReplay).GetProperty("PitchSequences", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal(highlight.RecordId, Assert.Single(scenes).RecordId); Assert.Equal(turn.Id, Assert.Single(h.Session.Turns).Id);
        Assert.Contains("01:01.8–01:05.2", highlight.ReportedClock); await page.DisposeAsync();
    }

    [Theory] [InlineData("reset")] [InlineData("owner")] [InlineData("cancel")] [InlineData("cursor")]
    public async Task D100_NonGoalPendingNarrationCannotCommitAfterSessionChanges(string change)
    {
        var end = EndOf(await Production.Value); var record = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "substitution" });
        using var h = new Harness(end); h.Set(new(end.Context, end.Dataset.HeadSequence, Select(end, record))); h.Router.Route = "current_match_moment";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var late = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Model.Wait = () => { entered.SetResult(); return late.Task; }; var asking = h.Ask("Show this substitution on a diagram"); await entered.Task;
        if (change == "reset") h.Session.NewChat(); if (change == "owner") h.Auth.Change("other"); if (change == "cancel") h.Session.Cancel();
        if (change == "cursor") h.Set(h.Context with { Query = h.Context.Query with { CursorMs = h.Context.Query.CursorMs - 1 } });
        Assert.False((await asking.WaitAsync(TimeSpan.FromSeconds(2))).Accepted); late.SetResult(new("invalid", [])); Assert.Empty(h.Session.Turns);
    }
}
