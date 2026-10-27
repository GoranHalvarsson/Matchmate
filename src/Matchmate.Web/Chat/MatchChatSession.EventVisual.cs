using Matchmate.Contracts;

namespace Matchmate.Web.Chat;

public sealed partial class MatchChatSession
{
    private sealed record EventPresentation(MatchEventPresentationTransition Transition, MatchEventVisual Visual,
        MatchChatContext Start, string Owner, long Generation, bool Fetching);
    private EventPresentation? _eventPresentation;
    private long _presentationIntent;

    public async Task<MatchEventPresentationTransition?> BeginEventPresentationAsync(MatchEventVisualAction action,
        MatchChatContext context, FootballDataset dataset, IReadOnlyList<FootballRecord> index)
    {
        long generation, intent;
        lock (_gate) { _eventPresentation = null; intent = ++_presentationIntent; if (_disposed || _context != context) return null; generation = _generation; }
        var owner = Owner((await _authentication.GetAuthenticationStateAsync()).User);
        lock (_gate)
        {
            if (intent != _presentationIntent || !Current(generation, context) || owner is null || owner != _owner) return null;
            var turn = _turns.SingleOrDefault(t => t.Id == action.TurnId);
            if (turn is not { SelectedEventScope: true, EventVisual: { } visual } || turn.Context != visual.Context
                || !MatchEventVisual.SameDomain(visual.Context, context) || visual.Context.HeadSequence != context.HeadSequence
                || visual.Context.Query.ApplicationSequence != context.Query.ApplicationSequence) return null;
            var records = index.Where(r => r.RecordId == visual.EventId).ToArray();
            if (records.Length != 1 || !visual.Matches(dataset, records[0], context)) return null;
            var transition = new MatchEventPresentationTransition(Guid.NewGuid(), turn.Id,
                new(visual.EventId, visual.ReleaseCursorMs, visual.ReportedClock, visual.Kind), visual.TargetCursorMs);
            _eventPresentation = new(transition, visual, context, owner, generation, false);
            return transition;
        }
    }

    // Only the exact replay fetch may advance the pending transition across its expected cancellation.
    // Ordinary Cancel, SetContext, reset and authentication changes invalidate the token.
    public bool BeginEventPresentationFetch(Guid id)
    {
        lock (_gate)
        {
            if (_eventPresentation is not { Fetching: false } pending || pending.Transition.Id != id
                || !Current(pending.Generation, pending.Start) || pending.Owner != _owner) return false;
            CancelLocked();
            _eventPresentation = pending with { Generation = _generation, Fetching = true };
            return true;
        }
    }

    public async Task<bool> CompleteEventPresentationAsync(Guid id, MatchChatContext context,
        FootballDataset dataset, IReadOnlyList<FootballRecord> index)
    {
        var owner = Owner((await _authentication.GetAuthenticationStateAsync()).User);
        lock (_gate)
        {
            if (_eventPresentation is not { Fetching: true } pending || pending.Transition.Id != id) return false;
            _eventPresentation = null; // Single use, including a failed completion.
            var expected = pending.Start with { Query = pending.Start.Query with { CursorMs = pending.Transition.TargetCursorMs }, SelectedEvent = pending.Transition.Selection };
            if (!Current(pending.Generation, pending.Start) || owner is null || owner != pending.Owner || _owner != owner
                || context != expected || index.Count(r => r.RecordId == pending.Visual.EventId) != 1
                || !pending.Visual.Matches(dataset, index.Single(r => r.RecordId == pending.Visual.EventId), context)) return false;
            var kept = _turns.SingleOrDefault(t => t.Id == pending.Transition.TurnId && ReferenceEquals(t.EventVisual, pending.Visual));
            if (kept is null) return false;
            // Apply ordinary selection/rewind pruning to every other turn, then retain only this named retrospective turn.
            if (pending.Start.SelectedEvent != context.SelectedEvent) ClearHistoryLocked();
            else _turns.RemoveAll(t => t.Context.Query.CursorMs > context.Query.CursorMs);
            if (!_turns.Contains(kept)) _turns.Add(kept);
            CancelLocked(); _namedSubject = null; _context = _lastKnown = context;
            return true;
        }
    }

    public void CancelEventPresentation(Guid id)
    { lock (_gate) if (_eventPresentation?.Transition.Id == id) _eventPresentation = null; }
}
