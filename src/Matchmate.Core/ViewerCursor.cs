using Matchmate.Contracts;

namespace Matchmate.Core;

// Construct one per viewer/circuit, never register as a singleton or mutate ingestion.
public sealed class ViewerCursor(ReplayMetadata metadata)
{
    public double Time { get; private set; } = metadata.Start;
    public bool Playing { get; private set; }
    private string? _continuedGap;
    public CoverageGap? Gap => metadata.Gaps.FirstOrDefault(g => Time >= g.Start
        && Time < Math.Round(g.End + metadata.Cadence, 8) && Time <= metadata.End);
    public void Seek(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        Time = Math.Clamp(time, metadata.Start, metadata.End); Pause();
    }
    public void Pause() { Playing = false; _continuedGap = null; }
    public void Play() { Playing = Time < metadata.End && Gap is null; _continuedGap = null; }
    public void ContinueGap() { _continuedGap = Gap?.Id; Playing = _continuedGap is not null && Time < metadata.End; }
    public void Advance(double elapsed)
    {
        if (!Playing) return;
        var target = Math.Min(metadata.End, Time + Math.Max(0, elapsed));
        if (Gap is { } current && current.Id != _continuedGap) { Pause(); return; }
        var crossed = metadata.Gaps.FirstOrDefault(g => g.Start > Time && g.Start <= target && g.Id != _continuedGap);
        if (crossed is not null) { Time = crossed.Start; Pause(); return; }
        Time = target; Playing = target < metadata.End;
        if (Gap?.Id != _continuedGap) _continuedGap = null;
    }
    public double? Adjacent(bool forward)
    {
        var gap = Gap;
        if (gap is null) return null;
        var candidate = Math.Round((forward ? gap.End + metadata.Cadence : gap.Start - metadata.Cadence), 8);
        return candidate >= metadata.Start && candidate <= metadata.End ? candidate : null;
    }
}
