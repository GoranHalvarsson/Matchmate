using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Chat;

// MatchChatSession authenticates and captures its immutable
// owner/generation/context snapshot and check authoritative current state in isCurrent before AND after
// retrieval. It must check again when committing a result. Comparing a supplied owner ID alone is unsafe.
public sealed partial class MatchWorkflowExecutor(Func<FootballQuery, CancellationToken, Task<FootballView>> read)
{
    public async Task<MatchWorkflowResult> ExecuteAsync(MatchWorkflowRequest request,
        Func<MatchWorkflowSnapshot, bool> isCurrent, CancellationToken token = default)
    {
        if (request?.Snapshot is not { } snapshot || !MatchWorkflowRegistry.Workflows.TryGetValue(request.WorkflowId ?? "", out var workflow)
            || request.Version != workflow.Version || !workflow.TemporalModes.Contains(snapshot.TemporalMode) || !IsValidSnapshot(snapshot)
            || request.VisualIntent is not null && (workflow.Id is not "selected_goal_event" and not "current_match_events" and not "current_match_moment" || request.VisualIntent != MatchEventVisualIntent.ReportedSequence)
            || (workflow.Id == "named_player_context" ? request.PlayerQuestion is not { } playerQuestion || !MatchPlayerResolver.ValidText(playerQuestion.Question)
                || playerQuestion.OriginalQuestion is not null && !MatchPlayerResolver.ValidText(playerQuestion.OriginalQuestion)
                || playerQuestion.ReferenceSubjectId is not null && !DatasetIdentity.IsValid(playerQuestion.ReferenceSubjectId) : request.PlayerQuestion is not null))
            return Result(MatchWorkflowStatus.InvalidRequest);

        MatchWorkflowResult? Interrupted()
        {
            if (token.IsCancellationRequested) return Result(MatchWorkflowStatus.Cancelled);
            return isCurrent(snapshot) ? null : Result(MatchWorkflowStatus.Stale);
        }
        if (Interrupted() is { } before) return before;
        if (workflow.Id == "clarify") return Result(MatchWorkflowStatus.Clarify);
        if (workflow.Id == "unsupported") return Result(MatchWorkflowStatus.Unsupported);
        if (workflow.Id == "other_live_matches_stats") return new(MatchWorkflowStatus.Unavailable,
            "The requested data capability is unavailable. Recordings cannot substitute for live games.");
        if (workflow.RequiresPlayer && snapshot.Context.Query.SubjectId is null) return Result(MatchWorkflowStatus.Clarify);
        var selectedWorkflow = workflow.Id == "selected_goal_event";
        var selectedNonGoal = request.VisualIntent is not null && snapshot.Context.SelectedEvent is { Kind: not "goal" };
        if (request.VisualIntent is not null && snapshot.Context.SelectedEvent is null) return new(MatchWorkflowStatus.Clarify, "Choose a supported event in the highlights first.");
        if (selectedWorkflow && snapshot.Context.SelectedEvent is { Kind: not "goal" }) return new(MatchWorkflowStatus.Clarify, "Choose a goal for this goal question.");
        if (request.VisualIntent is not null && snapshot.Context.SelectedEvent is { Kind: "goal" } && !selectedWorkflow)
            return new(MatchWorkflowStatus.Clarify, "Ask about the selected goal to show its supported sequence.");
        if (selectedWorkflow && snapshot.Context.SelectedEvent is null) return request.VisualIntent is not null
            ? new(MatchWorkflowStatus.Clarify, "Choose a goal in the highlights, then ask how it happened.") : Result(MatchWorkflowStatus.InvalidRequest);

        var query = snapshot.Context.Query with { ApplicationSequence = snapshot.Context.Query.ApplicationSequence ?? snapshot.Context.HeadSequence };
        FootballView view;
        try { view = await read(query, token).WaitAsync(token); }
        catch (OperationCanceledException) { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
        catch (Exception) { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
        if (Interrupted() is { } after) return after;
        if (!WellFormed(view)) return Result(MatchWorkflowStatus.RetrievalFailure);
        if (!Bound(view, query, snapshot.Context.HeadSequence)) return Result(MatchWorkflowStatus.Stale);
        if (workflow.Id == MatchSourceQuestions.ExplainMomentWorkflow)
            return await ExplainSnapshotAsync(request, query, view, isCurrent, token);
        if (workflow.Id == MatchSourceQuestions.CompareTeamsWorkflow)
        {
            // The bounded report-backed replay keeps its reported comparison when generated actions are also present.
            if (!ReportedReplayProjection.Supports(view) && view.Dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
                && view.IllustrationStatistics is not null)
                workflow = MatchWorkflowRegistry.Workflows["current_replay_statistics"];
            else if (!ExperiencePresentation.SupportsEventReports(view.Dataset))
                return new(MatchWorkflowStatus.Unavailable, "This recording does not supply reported match events or generated replay statistics.");
        }
        if (workflow.Id == "current_replay_statistics" && (!view.Dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1) || view.IllustrationStatistics is null))
            return new(MatchWorkflowStatus.Unavailable, "Generated replay statistics are unavailable for this recording at this moment. Actual match statistics are not supplied by the illustration.");
        if ((workflow.Tools.Contains("moment_context") || workflow.Tools.Contains("player_involvement")) && !WellFormedMoment(view)) return Result(MatchWorkflowStatus.RetrievalFailure);
        if (workflow.RequiresPlayer && !view.Dataset.People.Any(person => person.PersonId == query.SubjectId)) return Result(MatchWorkflowStatus.Clarify);
        if (workflow.Id == "current_match_events" && !selectedNonGoal && !ExperiencePresentation.SupportsEventReports(view.Dataset)) return new(MatchWorkflowStatus.Unavailable,
            "This recording does not supply reported match events.");

        string? resolvedSubject = null;
        if (workflow.Id == "named_player_context")
        {
            if (view.Dataset.People.Any(p => !DatasetIdentity.IsValid(p.PersonId) || !MatchPlayerResolver.ValidText(p.Name))
                || view.Dataset.People.Select(p => p.PersonId).Distinct(StringComparer.Ordinal).Count() != view.Dataset.People.Length) return Result(MatchWorkflowStatus.RetrievalFailure);
            var resolution = MatchPlayerResolver.Resolve(view.Dataset.People, request.PlayerQuestion!);
            if (resolution.SubjectId is null) return new(MatchWorkflowStatus.Clarify, resolution.Message);
            resolvedSubject = resolution.SubjectId;
        }
        FootballRecord? selectedRecord = null;
        if (selectedWorkflow)
        {
            var selection = snapshot.Context.SelectedEvent!;
            if (!HasSelectedRelease(view, selection)) return Result(MatchWorkflowStatus.InvalidRequest);
            var selectedView = view;
            if (!view.Events.Any(record => record.RecordId == selection.RecordId))
            {
                if (Interrupted() is { } selectedBefore) return selectedBefore;
                var selectedQuery = query with { CursorMs = selection.ReleaseCursorMs };
                try { selectedView = await read(selectedQuery, token).WaitAsync(token); }
                catch (Exception) { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
                if (Interrupted() is { } selectedAfter) return selectedAfter;
                if (!WellFormed(selectedView)) return Result(MatchWorkflowStatus.RetrievalFailure);
                if (!Bound(selectedView, selectedQuery, snapshot.Context.HeadSequence) || !HasSelectedRelease(selectedView, selection)) return Result(MatchWorkflowStatus.Stale);
            }
            var matches = selectedView.Events.Where(record => record.RecordId == selection.RecordId).ToArray();
            if (matches.Length != 1 || matches[0] is not { Payload: MatchEventPayload { EventKind: "goal" }, RecordState: "active" } record
                || record.DatasetId != query.DatasetId || record.DatasetVersion != query.DatasetVersion || record.FixtureId != query.FixtureId
                || (record.FootballTime?.ReportedClock ?? "reported clock unavailable") != selection.ReportedClock
                || !new[] { view.Dataset.Fixture!.Home.Id, view.Dataset.Fixture.Away.Id }.Contains(((MatchEventPayload)record.Payload).TeamId)) return Result(MatchWorkflowStatus.InvalidRequest);
            selectedRecord = record;
        }

        if (selectedNonGoal)
        {
            if (view.Possessions is null || view.Possessions.Any(record => record is null))
                return Result(MatchWorkflowStatus.RetrievalFailure);
            var matches = view.Events.Concat(view.Possessions).Where(r => r.RecordId == snapshot.Context.SelectedEvent!.RecordId).ToArray();
            if (matches.Length != 1 || MatchEventVisual.Create(view.Dataset, matches[0], snapshot.Context) is null)
                return new(MatchWorkflowStatus.Unavailable, "The selected event has no supported sequence eligible at this replay moment.");
            selectedRecord = matches[0];
        }

        try
        {
            var playerView = resolvedSubject is null ? view : view with { Context = view.Context with { SubjectId = resolvedSubject } };
            var projection = new MatchEvidenceProjection(playerView, compactEventSources: true);
            // Player evidence has its own source budget; remove the irrelevant aggregate descriptor before that projection too.
            var playerSources = playerView.Dataset.Manifest.Sources.Where(source => source.SourceId != MatchIllustrationStatistics.SourceId).ToArray();
            var playerEvidenceView = playerView with { Dataset = playerView.Dataset with { Manifest = playerView.Dataset.Manifest with { Sources = playerSources } } };
            var playerEvidence = workflow.Tools.Contains("player_involvement") ? MatchPlayerEvidence.Build(playerEvidenceView, resolvedSubject ?? query.SubjectId!) : null;
            var momentWorkflow = workflow.Id == "current_match_moment";
            var tools = workflow.Tools.Where(t => t != "selected_event" && (!selectedNonGoal || t != "current_events")).ToArray();
            if (selectedNonGoal) tools = ["selected_event", .. tools];
            var items = tools.Select(tool => tool == "selected_goal" ? projection.SelectedGoal(selectedRecord!, snapshot.Context.SelectedEvent!)
                : tool == "selected_event" ? projection.SelectedEvent(selectedRecord!, snapshot.Context.SelectedEvent!)
                : tool == "player_involvement" ? playerEvidence!.Item
                : momentWorkflow && tool == "current_events" ? projection.Get(tool, MatchWorkflowLimits.MomentEvents, recentEvents: true)
                : projection.Get(tool, includeReportSummary: workflow.Id == "current_match_summary" && tool == "match_summary")).Select(item =>
                new MatchWorkflowItem(item.Id, item.Tool, item.Text, item.NumericTokens.ToImmutableArray())).ToImmutableArray();
            // Each workflow cites its relevant authority. Generated aggregates never become sources for reported/player facts.
            var relevantSources = workflow.Id == "current_replay_statistics" ? new[] { MatchIllustrationStatistics.Source(view) }
                : (playerEvidence?.Sources ?? view.Dataset.Manifest.Sources.ToImmutableArray()).Where(source => source.SourceId != MatchIllustrationStatistics.SourceId);
            var sources = relevantSources.Select(source => new MatchWorkflowSource(source.SourceId, source.ArtifactHash)).ToImmutableArray();
            var aggregateReports = workflow.Id is "current_match_summary" or MatchSourceQuestions.CompareTeamsWorkflow;
            var usesEvents = workflow.Tools.Contains("current_events") || workflow.Tools.Contains("match_summary") || workflow.Tools.Contains("reported_comparison");
            // Totals use the full eligible view; the bounded recent provenance window covers the three displayed incidents.
            var events = selectedRecord is not null ? [selectedRecord] : momentWorkflow ? view.Events.TakeLast(MatchWorkflowLimits.MomentEvents).ToArray()
                : aggregateReports ? view.Events.TakeLast(MatchWorkflowLimits.Events).ToArray() : usesEvents ? view.Events.Take(MatchWorkflowLimits.Events).ToArray() : [];
            var provenance = playerEvidence?.Provenance ?? events.SelectMany(record => MatchEvidenceProjection.WorkflowEventEvidence(record).Select(evidence =>
                new MatchWorkflowProvenance(record.RecordId, record.Revision, record.FootballTime, evidence.EvidenceId, evidence.SourceId,
                    evidence.Origin, evidence.Method, evidence.ClaimStatus, evidence.AvailableAtUtc))).ToImmutableArray();
            // FootballView exposes observation methods but not tracking-record origin/claim metadata. Do not infer it.
            var methods = (resolvedSubject is null ? (view.Frame?.Players.Select(p => p.Method) ?? []).Append(view.Frame?.Ball.Method ?? "unknown")
                : [view.Frame?.Players.FirstOrDefault(p => p.SubjectId == resolvedSubject)?.Method ?? "unknown"])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            var missing = ImmutableArray.CreateBuilder<string>();
            if (!ExperiencePresentation.SupportsEventReports(view.Dataset)) missing.Add("event_reports_not_supplied");
            if (view.Dataset.Manifest.Capabilities.Contains("unaligned-commentary-not-exposed")) missing.Add("unaligned_source_media_narrative_not_supplied");
            if (view.TrackingGap) missing.Add("tracking_gap");
            if (view.Frame?.Ball.Availability != "present") missing.Add("ball_position_missing");
            missing.Add("tracking_record_provenance_not_exposed_by_view");
            if (workflow.RequiresPlayer && view.Frame?.Players.FirstOrDefault(p => p.SubjectId == query.SubjectId)?.Availability != "present") missing.Add("selected_player_position_missing");
            if (events.Length < view.Events.Length && tools.Contains("current_events")) missing.Add("events_truncated");
            if (events.Length < view.Events.Length && usesEvents && !selectedWorkflow && !selectedNonGoal) missing.Add("event_provenance_truncated");
            if (events.Any(record => record.Evidence.Length > MatchWorkflowLimits.EvidencePerEvent)) missing.Add("event_provenance_truncated");
            if (playerEvidence is not null) missing.AddRange(playerEvidence.Missingness);
            if (resolvedSubject is not null && view.Frame?.Players.FirstOrDefault(p => p.SubjectId == resolvedSubject)?.Availability != "present") missing.Add("resolved_player_position_missing");
            var bundle = new MatchWorkflowBundle(workflow.Id, workflow.Version, snapshot.Context with { Query = query, SelectedEvent = selectedWorkflow || selectedNonGoal ? snapshot.Context.SelectedEvent : null }, snapshot.TemporalMode,
                view.Dataset.Stream.DeliveryMode, items, sources, provenance, methods, missing.ToImmutable(), resolvedSubject);
            if (items.Length > MatchWorkflowLimits.Items || items.Sum(item => item.Text.Length) > MatchWorkflowLimits.EvidenceChars
                || sources.Length > MatchWorkflowLimits.Sources || JsonSerializer.Serialize(bundle).Length > MatchWorkflowLimits.BundleChars)
                return Result(MatchWorkflowStatus.RetrievalFailure);
            if (Interrupted() is { } final) return final;
            var visual = request.VisualIntent is null ? null : MatchEventVisual.Create(view.Dataset, selectedRecord!, snapshot.Context);
            if (request.VisualIntent is not null && visual is null) return new(MatchWorkflowStatus.Unavailable,
                "A supported sequence is unavailable for this event. The sources cannot establish a diagram.");
            var partial = missing.Any(reason => reason is "selected_player_position_missing" or "events_truncated" or "event_provenance_truncated" or "player_reviewed_observations_sparse" or "player_reviewed_observations_truncated")
                || resolvedSubject is not null && (!ExperiencePresentation.SupportsEventReports(view.Dataset) || missing.Any(m => m.StartsWith("player_", StringComparison.Ordinal) || m == "resolved_player_position_missing"))
                || workflow.Id == "current_match_summary" && !ExperiencePresentation.SupportsEventReports(view.Dataset);
            return new(partial ? MatchWorkflowStatus.Partial : MatchWorkflowStatus.Success, "Bound recorded evidence is ready; retain its coverage and missingness labels.", bundle, resolvedSubject, visual);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException)
        { return Interrupted() ?? Result(MatchWorkflowStatus.RetrievalFailure); }
    }

    public static bool IsValidSnapshot(MatchWorkflowSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.OwnerKey) || snapshot.OwnerKey.Length > 512 || snapshot.Generation < 0 || snapshot.Context?.Query is not { } query) return false;
        return new[] { query.DatasetId, query.DatasetVersion, query.StreamId, query.FixtureId, query.PresentationPolicyId }.All(DatasetIdentity.IsValid)
            && (query.SubjectId is null || DatasetIdentity.IsValid(query.SubjectId)) && query.CursorMs >= 0 && snapshot.Context.HeadSequence >= 1
            && query.PresentationPolicyRevision >= 1 && query.KnowledgeMode == "retrospective_replay"
            && (query.ApplicationSequence is null || query.ApplicationSequence >= 1 && query.ApplicationSequence <= snapshot.Context.HeadSequence)
            && (snapshot.Context.SelectedEvent is not { } goal || DatasetIdentity.IsValid(goal.RecordId) && goal.ReleaseCursorMs >= 0
                && MatchEventVisual.SupportedKind(goal.Kind) && !string.IsNullOrWhiteSpace(goal.ReportedClock) && goal.ReportedClock.Length <= 180)
            && (snapshot.Context.SelectedPassage is not { } passage || snapshot.Context.SelectedEvent is null && query.SubjectId is null
                && ReplayStory.ValidLocator(passage) && query.CursorMs >= passage.EndCursorMs)
            && (query.EffectiveDate is null || DateOnly.TryParseExact(query.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    private static bool HasSelectedRelease(FootballView view, MatchSelectedEvent goal) => view.Dataset.Schedule is { Releases: not null, Segments.Length: > 0 } schedule
        && schedule.Segments[0] is not null && schedule.Segments[^1] is not null
        && schedule.Releases.Count(release => release?.RecordId == goal.RecordId) == 1
        && schedule.Releases.Any(release => release?.RecordId == goal.RecordId && release.CursorMs == goal.ReleaseCursorMs)
        && goal.ReleaseCursorMs >= schedule.Segments[0].CursorStartMs && goal.ReleaseCursorMs <= schedule.Segments[^1].CursorEndMs;

    private static bool WellFormed(FootballView? view) => view is { Context: not null, Events: not null,
        Dataset: { Manifest: { Sources: not null, Capabilities: not null }, Stream: not null, People: not null,
            Fixture: { Home: not null, Away: not null, Periods: not null } } }
        && view.Events.All(record => record is { Evidence: not null, Payload: MatchEventPayload }
            && record.Evidence.All(evidence => evidence is not null) && ValidDetails(record, view.Dataset.Manifest))
        && view.Dataset.Manifest.Sources.All(source => source is not null) && view.Dataset.People.All(person => person is not null)
        && (view.Frame is null || view.Frame is { Ball: not null, Players: not null, ActiveSubjectIds: not null }
            && view.Frame.Players.All(player => player is not null));

    private static bool ValidDetails(FootballRecord record, FootballManifest manifest)
    {
        if (record.Payload is MatchEventPayload { Details: null, Sequence: null }) return true;
        try { FootballCodec.ValidateMetadataRecord(record, manifest); return true; }
        catch (RecordingValidationException) { return false; }
    }
    private static bool WellFormedMoment(FootballView view)
    {
        static bool Observation(FootballObservation observation) => observation.Availability != "present"
            ? observation.XM is null && observation.YM is null : observation.XM is { } x && double.IsFinite(x) && observation.YM is { } y && double.IsFinite(y);
        return (view.Frame is null || view.Frame.Players.Append(view.Frame.Ball).All(Observation)) && view.Possessions is not null
            && view.Possessions.All(record => record is { RecordState: "active", Payload: PossessionPayload, Evidence: { Length: > 0 }, FootballTime: not null }
                && record.DatasetId == view.Context.DatasetId && record.DatasetVersion == view.Context.DatasetVersion && record.FixtureId == view.Context.FixtureId
                && record.FootballTime.PeriodId == view.PeriodId && record.FootballTime.StartMs >= 0 && record.FootballTime.EndMs >= record.FootballTime.StartMs
                && record.FootballTime.EndMs <= view.LocalMs && ((PossessionPayload)record.Payload).EligibleAfterMs <= view.LocalMs
                && record.Evidence.All(e => e is not null));
    }

    private static bool Bound(FootballView view, FootballQuery query, long head) => view.Context == query
        && view.Dataset.Manifest.DatasetId == query.DatasetId && view.Dataset.Manifest.DatasetVersion == query.DatasetVersion
        && view.Dataset.Manifest.FixtureId == query.FixtureId && view.Dataset.Stream.StreamId == query.StreamId
        && view.Dataset.Stream.DatasetId == query.DatasetId && view.Dataset.Stream.DatasetVersion == query.DatasetVersion
        && view.Dataset.Stream.FixtureId == query.FixtureId && view.Dataset.Stream.DeliveryMode == "replay"
        && view.Dataset.Fixture is not null && view.Dataset.HeadSequence == head
        && view.Dataset.Schedule?.PolicyId == query.PresentationPolicyId && view.Dataset.Schedule.PolicyRevision == query.PresentationPolicyRevision;

    private static MatchWorkflowResult Result(MatchWorkflowStatus status) => new(status, status switch
    {
        MatchWorkflowStatus.Clarify => "Resolve the requested player, time or scope before retrieving facts.",
        MatchWorkflowStatus.Unsupported => "This workflow does not support the requested capability.",
        MatchWorkflowStatus.Unavailable => "The requested data capability is unavailable.",
        MatchWorkflowStatus.InvalidRequest => "The workflow or trusted request context is invalid.",
        MatchWorkflowStatus.Stale => "The session or football context changed; evidence was discarded.",
        MatchWorkflowStatus.Cancelled => "The evidence request was cancelled.",
        _ => "The requested evidence could not be retrieved safely. No fallback facts were supplied."
    });
}
