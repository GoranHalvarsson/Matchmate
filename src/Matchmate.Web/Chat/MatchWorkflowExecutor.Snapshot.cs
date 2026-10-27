using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Chat;

public sealed partial class MatchWorkflowExecutor
{
    private async Task<MatchWorkflowResult> ExplainSnapshotAsync(MatchWorkflowRequest request, FootballQuery query,
        FootballView view, Func<MatchWorkflowSnapshot, bool> isCurrent, CancellationToken token)
    {
        var snapshot = request.Snapshot;
        MatchWorkflowResult? Interrupted() => token.IsCancellationRequested ? Result(MatchWorkflowStatus.Cancelled)
            : !isCurrent(snapshot) ? Result(MatchWorkflowStatus.Stale) : null;
        try
        {
            if (!WellFormedMoment(view) || view.Dataset.Schedule is not { Segments.Length: > 0, Releases: not null } schedule
                || schedule.Segments.Any(s => s is null) || !MatchSnapshotExplanation.ValidFrame(view)) return Result(MatchWorkflowStatus.RetrievalFailure);
            FootballRecord[] index = [];
            var sequenceCapable = view.Dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1");
            if (sequenceCapable)
            {
                if (Interrupted() is { } before) return before;
                var endQuery = query with { CursorMs = schedule.Segments[^1].CursorEndMs };
                // This second view supplies control metadata only. Never use its frame, score, statistics,
                // possession or other end facts to explain the current moment.
                var end = endQuery == query ? view : await read(endQuery, token).WaitAsync(token);
                if (Interrupted() is { } after) return after;
                if (!WellFormed(end)) return Result(MatchWorkflowStatus.RetrievalFailure);
                if (!Bound(end, endQuery, snapshot.Context.HeadSequence)
                    || JsonSerializer.Serialize(view.Dataset) != JsonSerializer.Serialize(end.Dataset)
                    || !(view.GeneratedActionRecordIds ?? []).SequenceEqual(end.GeneratedActionRecordIds ?? [])) return Result(MatchWorkflowStatus.Stale);
                index = end.Events;
                if (!MatchPitchScene.CompleteReportedSequenceIndex(view, schedule, index)
                    || index.Any(r => r.RecordState != "active" || r.Revision < 1 || !ValidSnapshotReport(r, view.Dataset.Manifest))
                    || view.Events.Any(r => index.Count(i => i.RecordId == r.RecordId && JsonSerializer.Serialize(i) == JsonSerializer.Serialize(r)) != 1))
                    return Result(MatchWorkflowStatus.RetrievalFailure);
            }
            FootballRecord? selectedRecord = null;
            MatchEventVisual? selectedVisual = null;
            MatchSnapshotStep[]? selectedReportSteps = null;
            if (snapshot.Context.SelectedEvent is { } selection)
            {
                // The report index may authorize only the explicitly selected goal. All other kinds
                // must already occur in the cursor-bound view and pass their ordinary eligibility.
                var candidates = selection.Kind == "goal" ? index : view.Events.Concat(view.Possessions);
                var matching = candidates.Where(r => r.RecordId == selection.RecordId).ToArray();
                if (matching.Length != 1) return Result(MatchWorkflowStatus.Unavailable);
                selectedVisual = MatchEventVisual.Create(view.Dataset, matching[0], snapshot.Context);
                if (selectedVisual is null && (selectedReportSteps = SubstitutionReport(view, matching[0], selection)) is null)
                    return Result(MatchWorkflowStatus.Unavailable);
                selectedRecord = matching[0];
            }
            var scenes = MatchPitchScene.Select(view, snapshot.Context.SelectedEvent, view.Dataset.HeadSequence, index);
            var mask = MatchPitchScene.Mask(view, view.Dataset.HeadSequence, index, scenes);
            var presentation = MatchPitchPresenter.Project(view, scenes, true, mask);
            var data = MatchSnapshotExplanation.Capture(view, presentation, scenes, mask, selectedVisual,
                selectedVisual is null ? snapshot.Context.SelectedEvent : null, selectedReportSteps);
            var text = JsonSerializer.Serialize(data);
            var item = new MatchWorkflowItem("pitch_snapshot", "pitch_snapshot", text, []);
            var evidenceIds = selectedVisual?.Evidence.Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal)
                ?? selectedRecord?.Evidence.Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal) ?? [];
            var sources = view.Dataset.Manifest.Sources.Where(s => evidenceIds.Contains(s.SourceId))
                .Select(s => new MatchWorkflowSource(s.SourceId, s.ArtifactHash)).ToImmutableArray();
            var provenance = selectedRecord is null ? [] : MatchEvidenceProjection.WorkflowEventEvidence(selectedRecord).Select(e =>
                new MatchWorkflowProvenance(selectedRecord.RecordId, selectedRecord.Revision, selectedRecord.FootballTime,
                    e.EvidenceId, e.SourceId, e.Origin, e.Method, e.ClaimStatus, e.AvailableAtUtc)).ToImmutableArray();
            var missing = data.NoFrame ? ImmutableArray.Create("current_frame_missing") : data.BallThird is null ? ["ball_position_missing"] : ImmutableArray<string>.Empty;
            var bundle = new MatchWorkflowBundle(MatchSourceQuestions.ExplainMomentWorkflow, request.Version,
                snapshot.Context with { Query = query }, snapshot.TemporalMode, view.Dataset.Stream.DeliveryMode,
                [item], sources, provenance, [], missing);
            if (text.Length > MatchWorkflowLimits.EvidenceChars || JsonSerializer.Serialize(bundle).Length > MatchWorkflowLimits.BundleChars)
                return Result(MatchWorkflowStatus.RetrievalFailure);
            if (Interrupted() is { } final) return final;
            return new(MatchWorkflowStatus.Success, "Bound displayed-pitch explanation is ready.", bundle);
        }
        catch (OperationCanceledException) { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException or RecordingValidationException)
        { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
    }

    private static bool ValidSnapshotReport(FootballRecord record, FootballManifest manifest)
    {
        try { FootballCodec.ValidateMetadataRecord(record, manifest); return true; }
        catch (RecordingValidationException) { return false; }
    }

    private static MatchSnapshotStep[]? SubstitutionReport(FootballView view, FootballRecord record, MatchSelectedEvent selection)
    {
        if (selection.Kind != "substitution" || record is not { RecordState: "active", Revision: > 0,
                Payload: MatchEventPayload { EventKind: "substitution", Sequence: null } payload, FootballTime: { } time }
            || !ValidSnapshotReport(record, view.Dataset.Manifest) || !HasSelectedRelease(view, selection)
            || selection.ReleaseCursorMs > view.Context.CursorMs || selection.ReportedClock != time.ReportedClock
            || record.DatasetId != view.Context.DatasetId || record.DatasetVersion != view.Context.DatasetVersion || record.FixtureId != view.Context.FixtureId
            || payload.TeamId != view.Dataset.Fixture!.Home.Id && payload.TeamId != view.Dataset.Fixture.Away.Id) return null;
        if (!record.FieldEvidence.TryGetValue("/payload", out var payloadEvidence)
            || !record.Evidence.Any(e => payloadEvidence.Contains(e.EvidenceId) && e.Method == "reported")) return null;
        var segment = view.Dataset.Schedule!.Segments.SingleOrDefault(s => s.PeriodId == time.PeriodId && FootballPresentationPolicy.IsPlay(s));
        var cutoff = time.Precision == "reported_minute" ? time.EndMs : time.StartMs;
        if (segment is null) return null;
        if (cutoff is { } knownCutoff)
        {
            if (knownCutoff < segment.LocalStartMs || knownCutoff > segment.LocalStartMs + segment.CursorEndMs - segment.CursorStartMs
                || segment.CursorStartMs + knownCutoff - segment.LocalStartMs > view.Context.CursorMs) return null;
        }
        else if (time.Precision != "unknown" || time.StartMs is not null || time.EndMs is not null) return null;
        // Unknown halftime endpoints stay unknown. Only membership in the bound current view plus
        // the exact already-eligible authored release above authorizes this non-spatial report.
        string? Name(string? id)
        {
            if (id is null) return null;
            var person = view.Dataset.People.SingleOrDefault(p => p.PersonId == id);
            return person is not null && person.TeamId == payload.TeamId && MatchPlayerResolver.ValidText(person.Name) && person.Name.Length <= 180 ? person.Name : null;
        }
        // Missing explicit on/off identity stays missing; PersonId never supplies an inferred direction.
        var outgoing = Name(payload.OutgoingPersonId); var incoming = Name(payload.IncomingPersonId);
        if (payload.OutgoingPersonId is not null && outgoing is null || payload.IncomingPersonId is not null && incoming is null
            || payload.PersonId is not null && Name(payload.PersonId) is null
            || payload.OutgoingPersonId is not null && payload.OutgoingPersonId == payload.IncomingPersonId) return null;
        if (outgoing is null && incoming is null && payload.PersonId is { } participant)
            return [new("substitution_participant", Name(participant), null, "", null)];
        return [new("substitution_report", outgoing, incoming, "", null)];
    }
}
