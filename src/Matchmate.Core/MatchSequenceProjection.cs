using Matchmate.Contracts;

namespace Matchmate.Core;

public sealed record MatchSequenceProjection(string RecordId, MatchEventSequence Sequence, string Scope, string? ReportedClock,
    long ReleaseCursorMs, long? IntervalStartMs, long? IntervalEndMs, int ActiveStep, double StepProgress);

/// <summary>One semantic projection for pitch and tools. Only the pitch may supply a validated full index, for masking or an explicitly selected goal.</summary>
public static class MatchSequenceProjector
{
    public static MatchSequenceProjection? Event(FootballDataset dataset, FootballRecord record, long cursor, bool selectedGoal = false)
    {
        if (record.DatasetId != dataset.Manifest.DatasetId || record.DatasetVersion != dataset.Manifest.DatasetVersion
            || record.FixtureId != dataset.Manifest.FixtureId || record.RecordState != "active"
            || record.Payload is not MatchEventPayload { Sequence: { } sequence } payload || dataset.Schedule is null) return null;
        if (sequence.Version != 1 || sequence.PresentationLeadInMs is < 0 or > MatchEventSequenceLimits.LeadInMs
            || sequence.PresentationTailMs is < 0 or > MatchEventSequenceLimits.TailMs
            || sequence.Steps is not { Length: > 0 and <= MatchEventSequenceLimits.Steps } || sequence.Steps.Any(step => step is null)) return null;
        var releases = dataset.Schedule.Releases.Where(r => r.RecordId == record.RecordId).ToArray();
        if (releases.Length != 1 || selectedGoal && payload.EventKind != "goal") return null;
        var release = releases[0].CursorMs;
        if (!selectedGoal && cursor < release) return null;
        var offset = cursor - release; var active = 0;
        while (active + 1 < sequence.Steps.Length && offset >= sequence.Steps[active + 1].PresentationOffsetMs) active++;
        var from = sequence.Steps[active].PresentationOffsetMs;
        var to = active + 1 < sequence.Steps.Length ? sequence.Steps[active + 1].PresentationOffsetMs : sequence.PresentationTailMs;
        var progress = to <= from ? 1 : Math.Clamp((double)(offset - from) / (to - from), 0, 1);
        return new(record.RecordId, sequence, selectedGoal ? "retrospective_selected_goal" : "eligible_report", record.FootballTime?.ReportedClock, release, null, null, active, progress);
    }
    public static MatchSequenceProjection? Possession(FootballDataset dataset, FootballRecord record, long cursor)
    {
        if (record.DatasetId != dataset.Manifest.DatasetId || record.DatasetVersion != dataset.Manifest.DatasetVersion || record.FixtureId != dataset.Manifest.FixtureId
            || record.RecordState != "active" || record.Payload is not PossessionPayload possession || record.FootballTime is not { EndMs: { } end, StartMs: { } start } time
            || dataset.Schedule is null) return null;
        var segment = dataset.Schedule.Segments.FirstOrDefault(s => s.PeriodId == time.PeriodId && end >= s.LocalStartMs && end <= s.LocalStartMs + s.CursorEndMs - s.CursorStartMs);
        if (segment is null || cursor < segment.CursorStartMs + Math.Max(end, possession.EligibleAfterMs) - segment.LocalStartMs) return null;
        var step = new MatchEventStep("completed", "completed_possession", possession.PersonId, null, 0,
            "Completed source-annotated possession interval; not possession now.", record.Evidence.Select(e => e.EvidenceId).Take(3).ToArray(),
            possession.StartMatched && possession.EndMatched ? null : "One or both annotated interval endpoints are not matched to a tracking frame.");
        return new(record.RecordId, new(1, "possession-" + record.RecordId, 0, 15000, [step]), "completed_possession", null,
            segment.CursorStartMs + Math.Max(end, possession.EligibleAfterMs) - segment.LocalStartMs, start, end, 0, 1);
    }
    public static bool InWindow(MatchSequenceProjection projection, long cursor) => cursor >= projection.ReleaseCursorMs - projection.Sequence.PresentationLeadInMs
        && cursor <= projection.ReleaseCursorMs + projection.Sequence.PresentationTailMs;
    public static bool MasksUnreleasedGoal(FootballDataset dataset, IEnumerable<FootballRecord> validatedIndex, long cursor) => validatedIndex.Any(record =>
        record.Payload is MatchEventPayload { EventKind: "goal" } && Event(dataset, record, cursor, true) is { } scene
        && cursor < scene.ReleaseCursorMs && InWindow(scene, cursor));
    public static MatchSequenceProjection[] Current(FootballView view) => view.Events.Select(r => Event(view.Dataset, r, view.Context.CursorMs))
        .Concat(view.Possessions.Select(r => Possession(view.Dataset, r, view.Context.CursorMs))).Where(p => p is not null).Cast<MatchSequenceProjection>().ToArray();
}
