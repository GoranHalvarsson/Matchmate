using Matchmate.Contracts;

namespace Matchmate.Web.Experience;

public sealed record GeneratedPitchMotionPlan(int DurationMs, IReadOnlySet<(string Subject, string? Slot)> Players, bool Ball)
{
    public static GeneratedPitchMotionPlan Snap { get; } = new(0, new HashSet<(string, string?)>(), false);
}

// Display-only transitions between already accepted generated frames. Never supplies evidence or future positions.
public sealed class GeneratedPitchMotion
{
    private FootballView? _previous;
    private MatchPitchPresentation? _presentation;
    private bool _playing;
    private long _epoch;
    private int _step;
    private string _phase = "";
    private double _arrivalMs;
    private GeneratedPitchMotionPlan _plan = GeneratedPitchMotionPlan.Snap;

    public static bool Generated(FootballView view, FootballObservation observation) => ExperiencePresentation.Generated(view.Dataset)
        && observation.Method == "generated" && observation.Availability == "present"
        && observation.XM is { } x && double.IsFinite(x) && observation.YM is { } y && double.IsFinite(y);

    public GeneratedPitchMotionPlan Observe(FootballView view, MatchPitchPresentation presentation, bool playing, long epoch,
        int stepMs, string phase, double arrivalMs)
    {
        // Selection, language and other rerenders cannot restart or retime an accepted-frame transition.
        if (ReferenceEquals(view, _previous) && playing == _playing && epoch == _epoch && stepMs == _step && phase == _phase) return _plan;
        var sameFrame = ReferenceEquals(view, _previous);
        var playbackChanged = playing != _playing || epoch != _epoch;
        var continuous = !sameFrame && playing && _playing && epoch == _epoch && stepMs == _step && phase == _phase
            && stepMs is 100 or 400 or 1200 && _previous is { TrackingGap: false, Frame: not null } previous
            && !view.TrackingGap && view.Frame is not null && presentation.Frame is not null && _presentation?.Frame is not null
            && ExperiencePresentation.Generated(view.Dataset) && ExperiencePresentation.Generated(previous.Dataset)
            && view.Context == (previous.Context with { CursorMs = previous.Context.CursorMs + stepMs })
            && view.PeriodId == previous.PeriodId && view.PeriodId is not null
            && view.Dataset.HeadSequence == previous.Dataset.HeadSequence
            && SameRoster(view.Frame, previous.Frame);
        var players = new HashSet<(string Subject, string? Slot)>();
        var ball = false;
        var duration = 0;
        if (continuous)
        {
            // Small headroom bridges ordinary response jitter; the browser never extrapolates past this accepted target.
            duration = (int)Math.Clamp(Math.Ceiling((arrivalMs - _arrivalMs + 25) / 50) * 50, 100, 400);
            foreach (var current in presentation.Frame!.Players)
            {
                var prior = _presentation!.Frame!.Players.FirstOrDefault(p => p.SubjectId == current.SubjectId && p.SlotId == current.SlotId);
                var raw = view.Frame!.Players.FirstOrDefault(p => p.SubjectId == current.SubjectId && p.SlotId == current.SlotId);
                var oldRaw = _previous!.Frame!.Players.FirstOrDefault(p => p.SubjectId == current.SubjectId && p.SlotId == current.SlotId);
                if (prior is not null && raw is not null && oldRaw is not null && Generated(view, raw) && Generated(_previous, oldRaw)
                    && ContinuousObject(view, current, _previous, prior, 8)) players.Add((current.SubjectId, current.SlotId));
            }
            ball = Generated(view, view.Frame!.Ball) && Generated(_previous!, _previous!.Frame!.Ball)
                && ContinuousObject(view, presentation.Frame.Ball, _previous!, _presentation!.Frame!.Ball, 24);
        }
        _plan = new(duration, players, ball);
        _previous = view; _presentation = presentation; _playing = playing; _epoch = epoch; _step = stepMs; _phase = phase;
        if (!sameFrame || playbackChanged) _arrivalMs = arrivalMs;
        return _plan;
    }

    private static bool ContinuousObject(FootballView view, FootballObservation current, FootballView priorView, FootballObservation prior, double maxJump)
        => Generated(view, current) && Generated(priorView, prior) && current.SubjectId == prior.SubjectId && current.SlotId == prior.SlotId
            && Math.Pow(current.XM!.Value - prior.XM!.Value, 2) + Math.Pow(current.YM!.Value - prior.YM!.Value, 2) <= maxJump * maxJump;

    private static bool SameRoster(TrackingPayload current, TrackingPayload previous) => current.Players.Select(p => (p.SubjectId, p.SlotId)).Order()
        .SequenceEqual(previous.Players.Select(p => (p.SubjectId, p.SlotId)).Order());
}
