using Matchmate.Contracts;

namespace Matchmate.Core;

// Offline only. Sum unrounded Euclidean deltas; round the cumulative prefix to centimetres.
public sealed class GeneratedDistanceCalculator
{
    private sealed record Point(long Cursor, string Period, string Slot, double X, double Y);
    private sealed class Player
    {
        public Point? Previous;
        public double Metres;
        public List<GeneratedDistanceSample> Samples { get; } = [];
    }
    private readonly Dictionary<string, Player> _players;
    private readonly FootballPitch _pitch;
    private readonly SchedulePayload _schedule;
    private readonly GeneratedDistanceProfile _profile;
    private long _lastCursor = -1;
    public int FrameCount { get; private set; }
    public int ExcludedJumps { get; private set; }
    public GeneratedDistanceCalculator(FootballPitch pitch, IEnumerable<SquadPerson> roster, SchedulePayload schedule,
        GeneratedDistanceProfile? profile = null)
    {
        if (pitch.Basis != "top-left-metres-x-right-y-down" || !double.IsFinite(pitch.LengthM) || !double.IsFinite(pitch.WidthM)
            || pitch.LengthM is <= 0 or > 150 || pitch.WidthM is <= 0 or > 100) throw new InvalidDataException("Invalid generated distance pitch.");
        _pitch = pitch; _schedule = schedule; _profile = profile ?? GeneratedDistanceProfile.ChelseaFullV5;
        _players = roster.ToDictionary(p => p.PersonId, _ => new Player(), StringComparer.Ordinal);
        if (_players.Count is < 1 or > GeneratedDistanceLimits.People) throw new InvalidDataException("Invalid generated distance roster.");
    }
    public void Append(FootballRecord record)
    {
        if (record.Payload is not TrackingPayload frame) return;
        if (++FrameCount > _profile.Frames) throw new InvalidDataException("Too many generated distance frames.");
        var time = record.FootballTime;
        var segments = _schedule.Segments.Where(s => s.PeriodId == time?.PeriodId && time?.StartMs >= s.LocalStartMs
            && (time.StartMs < s.LocalStartMs + s.CursorEndMs - s.CursorStartMs
                || time.StartMs == s.LocalStartMs + s.CursorEndMs - s.CursorStartMs && (!_profile.ExcludeNonPlay || s == _schedule.Segments[^1]))).ToArray();
        if (record.RecordState != "active" || time is not { StartMs: not null, EndMs: not null } || time.Precision != _profile.Precision
            || time.StartMs != time.EndMs || segments.Length != 1 || frame.SourceFrameState != _profile.FrameState)
        { Break(); return; }
        var cursor = segments[0].CursorStartMs + time.StartMs.Value - segments[0].LocalStartMs;
        if (cursor < 0 || cursor > _profile.DurationMs || cursor <= _lastCursor)
            throw new InvalidDataException("Generated distance samples must be ordered and bounded.");
        _lastCursor = cursor;
        var play = !_profile.ExcludeNonPlay || FootballPresentationPolicy.IsPlay(segments[0]);
        if (!play) Break();
        var duplicates = frame.Players.GroupBy(p => p.SubjectId).Where(g => g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var duplicateSlots = frame.Players.Where(p => p.SlotId is not null).GroupBy(p => p.SlotId!).Where(g => g.Count() != 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var active = frame.ActiveSubjectIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, player) in _players)
        {
            var observation = frame.Players.FirstOrDefault(p => p.SubjectId == id);
            if (observation is not { Availability: "present", Method: "generated", XM: not null, YM: not null, SlotId: not null }
                || !active.Contains(id) || duplicates.Contains(id) || duplicateSlots.Contains(observation.SlotId)
                || string.IsNullOrWhiteSpace(observation.SlotId) || !double.IsFinite(observation.XM.Value) || !double.IsFinite(observation.YM.Value)
                || observation.XM < 0 || observation.XM > _pitch.LengthM || observation.YM < 0 || observation.YM > _pitch.WidthM)
            { player.Previous = null; continue; }
            var current = new Point(cursor, time.PeriodId, observation.SlotId, observation.XM.Value, observation.YM.Value);
            if (play && player.Previous is { } previous && cursor - previous.Cursor == _profile.CadenceMs
                && previous.Period == current.Period && previous.Slot == current.Slot)
            {
                var dx = current.X - previous.X; var dy = current.Y - previous.Y;
                var delta = Math.Sqrt(dx * dx + dy * dy);
                if (delta <= GeneratedDistanceLimits.ContinuityMetresPerSecond * _profile.CadenceMs / 1000d)
                    player.Metres += delta;
                else ExcludedJumps++;
            }
            player.Previous = play ? current : null;
            player.Samples.Add(new((int)cursor, checked((int)Math.Round(player.Metres * 100, MidpointRounding.AwayFromZero))));
        }
    }
    private void Break() { foreach (var player in _players.Values) player.Previous = null; }
    public IReadOnlyDictionary<string, GeneratedDistanceSample[]> Snapshot() => _players.ToDictionary(p => p.Key, p => p.Value.Samples.ToArray(), StringComparer.Ordinal);
}
