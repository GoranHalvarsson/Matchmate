using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;

namespace Matchmate.Web.Experience;

// One selection policy for the actual replay and its local explanation. The complete report
// index is control metadata: only eligible/current or explicitly selected goal scenes leave it.
public static class MatchPitchScene
{
    public static bool IndexAvailable(FootballView view, long indexHead, FootballRecord[] index) =>
        !view.Dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1")
        || indexHead == view.Dataset.HeadSequence && view.Dataset.Schedule is { Releases: not null } schedule
            && CompleteReportedSequenceIndex(view, schedule, index);

    public static bool CompleteReportedSequenceIndex(FootballView view, SchedulePayload schedule, FootballRecord[] index)
    {
        var actionIds = view.GeneratedActionRecordIds ?? [];
        if (actionIds.Length > FootballActionPolicy.MaxGeneratedActionRecordIds
            || actionIds.Any(id => !DatasetIdentity.IsValid(id))
            || actionIds.Length > 0 && !view.Dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)) return false;
        var typedActions = actionIds.ToHashSet(StringComparer.Ordinal);
        if (typedActions.Count != actionIds.Length || index.Any(record => record is null || typedActions.Contains(record.RecordId))
            || schedule.Releases.Any(release => release is null)) return false;
        var reportedReleases = schedule.Releases.Where(release => !typedActions.Contains(release.RecordId)).ToArray();
        return index.Length == reportedReleases.Length && reportedReleases.All(release => index.Count(record => record.RecordId == release.RecordId
            && record.DatasetId == view.Dataset.Manifest.DatasetId && record.DatasetVersion == view.Dataset.Manifest.DatasetVersion
            && record.FixtureId == view.Dataset.Manifest.FixtureId) == 1);
    }

    public static MatchSequenceProjection[] Select(FootballView view, MatchSelectedEvent? selected, long indexHead, FootballRecord[] index)
    {
        if (!IndexAvailable(view, indexHead, index)) return [];
        if (indexHead == view.Dataset.HeadSequence && selected is { Kind: "goal" }
            && index.SingleOrDefault(r => r.RecordId == selected.RecordId) is { } goal
            && MatchSequenceProjector.Event(view.Dataset, goal, view.Context.CursorMs, true) is { } retrospective
            && retrospective.ReleaseCursorMs == selected.ReleaseCursorMs && retrospective.ReportedClock == selected.ReportedClock
            && MatchSequenceProjector.InWindow(retrospective, view.Context.CursorMs)) return [retrospective];
        if (selected is { Kind: not "goal" } chosen && indexHead == view.Dataset.HeadSequence)
        {
            var record = view.Events.Concat(view.Possessions).SingleOrDefault(r => r.RecordId == chosen.RecordId);
            var visual = record is null ? null : MatchEventVisual.Create(view.Dataset, record, new(view.Context, view.Dataset.HeadSequence, chosen));
            var projection = visual is null ? null : record!.Payload is PossessionPayload
                ? MatchSequenceProjector.Possession(view.Dataset, record, view.Context.CursorMs) : MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs);
            if (projection is not null && MatchSequenceProjector.InWindow(projection, view.Context.CursorMs)) return [projection];
        }
        var current = MatchSequenceProjector.Current(view).Where(p => MatchSequenceProjector.InWindow(p, view.Context.CursorMs)).ToArray();
        if (current.Length == 0) return [];
        var latest = current.Max(p => p.ReleaseCursorMs);
        return current.Where(p => p.ReleaseCursorMs == latest).OrderBy(p => p.RecordId, StringComparer.Ordinal).ToArray();
    }

    public static bool Mask(FootballView view, long indexHead, FootballRecord[] index, MatchSequenceProjection[] scenes) =>
        IndexAvailable(view, indexHead, index) && indexHead == view.Dataset.HeadSequence && scenes.Length == 0
            && MatchSequenceProjector.MasksUnreleasedGoal(view.Dataset, index, view.Context.CursorMs);
}
