using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public enum ReplayRecapKind { HalfTime, FullTime }
public sealed record ReplayRecapBoundary(ReplayRecapKind Kind, long CursorMs, long EvidenceCursorMs, long MatchClockMs);
public sealed record ReplayRecapAction(ReplayRecapBoundary Boundary, FootballQuery Query, long HeadSequence, string? ArtifactHash, string? ReportedAuthorityHash = null);
public sealed record ReplayRecapPreparation(Guid Id, ReplayRecapAction Action, MatchChatContext Context);
public sealed record ReplayRecap(string Heading, string ReportedSummary, string? ReportedDetail, string? GeneratedSummary,
    string ControlNote, ReplayRecapAction Action)
{
    public bool IsReported => Action.ReportedAuthorityHash is not null;
    // A complete generated recording is an explicit capability, not an inference from the final sample.
    public static ReplayRecapBoundary? BoundaryFor(FootballView? view)
    {
        if (view is not null && ReportedReplayProjection.Bound(view) && ReportedReplayProjection.Supports(view))
            return ReportedBoundary(view);
        if (view is not { Dataset: { Closed: true, PendingRecords: 0, Fixture: { Periods.Length: 2 } fixture,
            Schedule: { Segments.Length: 2 } schedule, Manifest: { Capabilities: not null } manifest } dataset }
            || !Bound(view) || !manifest.Capabilities.Contains("complete-existing-generated-100ms-model")
            || !manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
            || !manifest.Capabilities.Contains("match_event")) return null;
        var first = schedule.Segments[0]; var second = schedule.Segments[1];
        if (fixture.Periods.Any(p => p is null)) return null;
        var periods = fixture.Periods.OrderBy(p => p.Order).ToArray();
        if (first is null || second is null || periods[0].Order != 1 || periods[1].Order != 2
            || periods[0].Id == periods[1].Id || first.PeriodId != periods[0].Id || second.PeriodId != periods[1].Id
            || first.CursorStartMs != 0 || first.LocalStartMs != 0 || second.LocalStartMs != 0
            || first.CursorEndMs <= 0 || second.CursorStartMs != first.CursorEndMs || second.CursorEndMs <= second.CursorStartMs
            || periods[0].DisplayBaseMs != 0 || periods[1].DisplayBaseMs <= 0
            || view.Context.CursorMs < first.CursorStartMs || view.Context.CursorMs > second.CursorEndMs) return null;
        if (view.Context.CursorMs >= second.CursorEndMs)
            return new(ReplayRecapKind.FullTime, second.CursorEndMs, second.CursorEndMs,
                periods[1].DisplayBaseMs + second.CursorEndMs - second.CursorStartMs);
        if (view.Context.CursorMs < first.CursorEndMs) return null;
        return new(ReplayRecapKind.HalfTime, first.CursorEndMs, first.CursorEndMs, first.CursorEndMs);
    }

    private static ReplayRecapBoundary? ReportedBoundary(FootballView view)
    {
        if (view.Dataset is not { Closed: true, PendingRecords: 0, Fixture: { Periods.Length: 2 } fixture,
            Schedule: { Segments.Length: 3 } schedule } || !view.Dataset.Manifest.Capabilities.Contains("paused-halftime-transition")) return null;
        var periods = fixture.Periods.OrderBy(period => period.Order).ToArray();
        var first = schedule.Segments[0]; var transition = schedule.Segments[1]; var second = schedule.Segments[2];
        if (periods[0].Order != 1 || periods[1].Order != 2 || periods[0].DisplayBaseMs != 0 || periods[1].DisplayBaseMs <= 0
            || !FootballPresentationPolicy.IsPlay(first) || !FootballPresentationPolicy.IsPlay(second)
            || first.PausedDisplayMs is not null || second.PausedDisplayMs is not null
            || first.PeriodId != periods[0].Id || transition.PeriodId != periods[0].Id || second.PeriodId != periods[1].Id
            || first.LocalStartMs != 0 || second.LocalStartMs != 0 || transition.Phase != "halftime_transition"
            || transition.LocalStartMs != first.CursorEndMs || transition.PausedDisplayMs != first.CursorEndMs
            || periods[0].ActualDurationMs != first.CursorEndMs
            || periods[1].ActualDurationMs != second.CursorEndMs - second.CursorStartMs) return null;
        if (view.Context.CursorMs == second.CursorEndMs)
            return new(ReplayRecapKind.FullTime, second.CursorEndMs, second.CursorEndMs,
                periods[1].DisplayBaseMs + second.CursorEndMs - second.CursorStartMs);
        return view.Context.CursorMs < first.CursorEndMs ? null
            : new(ReplayRecapKind.HalfTime, first.CursorEndMs, first.CursorEndMs, first.CursorEndMs);
    }

    private static bool Bound(FootballView view)
    {
        var d = view.Dataset; var q = view.Context;
        return q is not null && d.Manifest.Sources is not null && d.Stream is not null && d.Schedule is not null
            && MatchWorkflowExecutor.IsValidSnapshot(new("replay-recap", 0, new(q, d.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor))
            && q.DatasetId == d.Manifest.DatasetId && q.DatasetVersion == d.Manifest.DatasetVersion && q.FixtureId == d.Manifest.FixtureId
            && q.DatasetId == d.Stream.DatasetId && q.DatasetVersion == d.Stream.DatasetVersion && q.FixtureId == d.Stream.FixtureId
            && q.StreamId == d.Stream.StreamId && d.Stream.DeliveryMode == "replay"
            && q.PresentationPolicyId == d.Schedule.PolicyId && q.PresentationPolicyRevision == d.Schedule.PolicyRevision
            && (q.ApplicationSequence ?? d.HeadSequence) == d.HeadSequence;
    }

    public static bool SameAuthority(FootballDataset a, FootballDataset b) => a.Stream == b.Stream
        && a.Manifest.DatasetId == b.Manifest.DatasetId && a.Manifest.DatasetVersion == b.Manifest.DatasetVersion
        && a.Manifest.FixtureId == b.Manifest.FixtureId && a.Manifest.ManifestRevision == b.Manifest.ManifestRevision
        && a.HeadSequence == b.HeadSequence && a.Closed == b.Closed && a.PendingRecords == b.PendingRecords
        && a.Manifest.Sources.SequenceEqual(b.Manifest.Sources) && a.Manifest.Capabilities.SequenceEqual(b.Manifest.Capabilities)
        && a.Fixture?.Home == b.Fixture?.Home && a.Fixture?.Away == b.Fixture?.Away
        && a.Fixture is not null && b.Fixture is not null && a.Fixture.Periods.SequenceEqual(b.Fixture.Periods)
        && a.Schedule is not null && b.Schedule is not null && a.Schedule.PolicyId == b.Schedule.PolicyId
        && a.Schedule.PolicyRevision == b.Schedule.PolicyRevision && a.Schedule.Segments.SequenceEqual(b.Schedule.Segments)
        && a.Schedule.Releases.SequenceEqual(b.Schedule.Releases);

    public static ReplayRecap? Project(FootballView? accepted, FootballView? evidence,
        MatchAudienceMode audience = MatchAudienceMode.Fan, MatchLanguage language = MatchLanguage.English)
    {
        var boundary = BoundaryFor(accepted);
        if (boundary is null || evidence is null || !Bound(evidence) || !SameAuthority(accepted!.Dataset, evidence.Dataset)
            || evidence.Context != (accepted.Context with { CursorMs = boundary.EvidenceCursorMs })
            || evidence.Events is null || evidence.Dataset.Schedule!.Releases is null) return null;
        try
        {
            if (ReportedReplayProjection.Supports(evidence))
                return ProjectReported(accepted!, evidence, boundary, audience, language);
            // This is the same count, source-pin and denominator authority used by replay statistics/chat.
            MatchIllustrationStatistics.Build(evidence);
            var fixture = evidence.Dataset.Fixture!; var schedule = evidence.Dataset.Schedule;
            // Actions have positive duration and start at/after local zero (shared validator/projector), so no
            // second-period action can complete at its opening. Keep first-period actions finishing exactly here.
            // A second-period report at zero would broaden the ordinary chat scope: withhold this bounded recap.
            if (boundary.Kind == ReplayRecapKind.HalfTime && (evidence.PeriodId != schedule!.Segments[1].PeriodId || evidence.LocalMs != 0
                || evidence.Possessions.Any(r => r.FootballTime?.PeriodId != schedule.Segments[0].PeriodId))) return null;
            var home = evidence.IllustrationStatistics!.Teams.Single(t => t.TeamId == fixture.Home.Id);
            var away = evidence.IllustrationStatistics.Teams.Single(t => t.TeamId == fixture.Away.Id);
            if (evidence.Events.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != evidence.Events.Length
                || evidence.Events.Any(r => r.DatasetId != evidence.Context.DatasetId || r.DatasetVersion != evidence.Context.DatasetVersion
                    || r.FixtureId != evidence.Context.FixtureId || r.Payload is not MatchEventPayload e
                    || e.TeamId != fixture.Home.Id && e.TeamId != fixture.Away.Id
                    || boundary.Kind == ReplayRecapKind.HalfTime && r.FootballTime?.PeriodId != schedule!.Segments[0].PeriodId
                    || schedule!.Releases.Count(release => release.RecordId == r.RecordId && release.CursorMs <= boundary.EvidenceCursorMs) != 1)) return null;
            string T(string key, params object?[] args) => ReplayText.Get(language, key, args);
            string Percent(decimal? value) => value is null ? T("RecapUnavailable") : value.Value.ToString("0.#", MatchLanguages.Culture(language)) + "%";
            var analyst = MatchAudiencePresentation.Normalize(audience) == MatchAudienceMode.Analyst;
            var goals = evidence.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal", Decision: not "disallowed" })
                .OrderBy(r => schedule!.Releases.Single(release => release.RecordId == r.RecordId).CursorMs).ToArray();
            var score = T("RecapScore", fixture.Home.Name, ExperiencePresentation.ReportedGoals(evidence, fixture.Home.Id),
                ExperiencePresentation.ReportedGoals(evidence, fixture.Away.Id), fixture.Away.Name);
            var scorers = goals.Length == 0 ? T("RecapNoGoals") : T("RecapGoals", string.Join("; ", goals.Select(r =>
                T("RecapScorer", ExperiencePresentation.Person(evidence.Dataset, ((MatchEventPayload)r.Payload).PersonId, language), r.FootballTime?.ReportedClock ?? T("RecapUnavailable")))));
            var reported = T("RecapReported", score, scorers);
            var detail = analyst ? T("RecapEvents", evidence.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "yellow_card" or "booking" }),
                evidence.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "substitution" })) : null;
            var generated = analyst ? T("RecapGeneratedAnalyst", fixture.Home.Name, home.PassCompleted, home.PassAttempts, Percent(home.PassCompletionPercent), home.Shots,
                Percent(home.ControlSharePercent), fixture.Away.Name, away.PassCompleted, away.PassAttempts, Percent(away.PassCompletionPercent), away.Shots, Percent(away.ControlSharePercent))
                : T("RecapGeneratedFan", home.Shots == away.Shots ? T("RecapShotsLevel") : T("RecapShotsLead", home.Shots > away.Shots ? fixture.Home.Name : fixture.Away.Name),
                    home.ControlSharePercent is null ? T("RecapControlUnavailable") : home.ControlSharePercent is >= 45m and <= 55m ? T("RecapControlLevel")
                        : T("RecapControlLead", home.ControlSharePercent > away.ControlSharePercent ? fixture.Home.Name : fixture.Away.Name));
            var total = evidence.IllustrationStatistics.TotalClassifiedControlMs;
            return new(T(boundary.Kind == ReplayRecapKind.HalfTime ? "RecapHalfTime" : "RecapFullTime"), reported, detail, generated,
                T("RecapControlNote", ExperiencePresentation.Clock(total), ExperiencePresentation.IntervalTime(boundary.EvidenceCursorMs)),
                new(boundary, evidence.Context, evidence.Dataset.HeadSequence, MatchIllustrationStatistics.Source(evidence).ArtifactHash!));
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private static ReplayRecap? ProjectReported(FootballView accepted, FootballView evidence, ReplayRecapBoundary boundary,
        MatchAudienceMode audience, MatchLanguage language)
    {
        var reports = ReportedReplayProjection.Project(evidence);
        if (reports is null || ReportedBoundary(accepted) != boundary
            || boundary.Kind == ReplayRecapKind.HalfTime && evidence.Events.Any(record => record.FootballTime?.PeriodId != evidence.Dataset.Schedule!.Segments[0].PeriodId)) return null;
        var fixture = evidence.Dataset.Fixture!; var schedule = evidence.Dataset.Schedule!;
        string T(string key, params object?[] args) => ReplayText.Get(language, key, args);
        var goals = evidence.Events.Where(record => record.Payload is MatchEventPayload { EventKind: "goal", Decision: not "disallowed" })
            .OrderBy(record => schedule.Releases.Single(release => release.RecordId == record.RecordId).CursorMs).ToArray();
        var score = T("RecapScore", fixture.Home.Name, reports.Home.Goals, reports.Away.Goals, fixture.Away.Name);
        var scorers = goals.Length == 0 ? T("RecapNoGoals") : T("RecapGoals", string.Join("; ", goals.Select(record =>
            T("RecapScorer", ExperiencePresentation.Person(evidence.Dataset, ((MatchEventPayload)record.Payload).PersonId, language),
                record.FootballTime?.ReportedClock ?? T("RecapUnavailable")))));
        var analyst = MatchAudiencePresentation.Normalize(audience) == MatchAudienceMode.Analyst;
        var detail = string.Join(" ", new[] { reports.Home, reports.Away }.Select(team =>
            T(analyst ? "ReportedRecapTeamAnalyst" : "ReportedRecapTeamFan", team.TeamId == fixture.Home.Id ? fixture.Home.Name : fixture.Away.Name,
                team.YellowCards, team.RedCards, team.Substitutions)));
        return new(T(boundary.Kind == ReplayRecapKind.HalfTime ? "RecapHalfTime" : "RecapFullTime"), T("RecapReported", score, scorers), detail, null,
            T(boundary.Kind == ReplayRecapKind.HalfTime ? "ReportedHalfTimeNote" : "ReportedFullTimeNote", ExperiencePresentation.IntervalTime(boundary.EvidenceCursorMs))
                + " " + ReportedReplayProjection.Limitation(language),
            new(boundary, evidence.Context, evidence.Dataset.HeadSequence, null, reports.AuthorityHash));
    }

    public static string Question(ReplayRecapAction action, MatchLanguage language) => ReplayText.Get(language,
        action.ReportedAuthorityHash is not null
            ? action.Boundary.Kind == ReplayRecapKind.HalfTime ? "ReportedHalfTimeQuestion" : "ReportedFullTimeQuestion"
            : action.Boundary.Kind == ReplayRecapKind.HalfTime ? "RecapHalfTimeQuestion" : "RecapFullTimeQuestion",
        ExperiencePresentation.IntervalTime(action.Query.CursorMs));

    public static bool MatchesEvidence(ReplayRecapAction action, FootballView view) => Bound(view)
        && action.Query == view.Context && action.HeadSequence == view.Dataset.HeadSequence
        && action.Query.CursorMs == action.Boundary.EvidenceCursorMs
        && (action.ReportedAuthorityHash is not null
            ? ReportedReplayProjection.Project(view)?.AuthorityHash == action.ReportedAuthorityHash
            : view.Dataset.Manifest.Sources.Count(s => s.SourceId == "generated-model-actions" && s.ArtifactHash == action.ArtifactHash) == 1);
}
