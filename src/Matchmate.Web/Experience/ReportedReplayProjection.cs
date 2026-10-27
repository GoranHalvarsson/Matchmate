using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public sealed record ReportedTeamCounts(string TeamId, int Goals, int YellowCards, int RedCards, int Substitutions);
public sealed record ReportedReplayStatistics(ReportedTeamCounts Home, ReportedTeamCounts Away, int ReportCount, string AuthorityHash);

// A bounded report projection. Generated positions never supply match statistics or report provenance.
public static class ReportedReplayProjection
{
    public static bool Supports(FootballView view) => view.Dataset.Manifest.Capabilities.Contains("complete-generated-200ms-illustration")
        && view.Dataset.Manifest.Capabilities.Contains("match_event");

    public static bool SupportsTable(FootballView view) => Supports(view)
        || view.Dataset.Manifest.Capabilities.Contains("complete-existing-generated-100ms-model")
            && view.Dataset.Manifest.Capabilities.Contains("match_event");

    public static bool Bound(FootballView? view)
    {
        if (view is not { Dataset: { Manifest: { Sources: not null, Capabilities: not null }, Fixture: { Home: not null, Away: not null, Periods: not null },
            Schedule: { Segments.Length: > 0, Releases: not null }, Stream: not null }, Context: not null }) return false;
        var d = view.Dataset; var q = view.Context; var s = d.Schedule!;
        if (!MatchWorkflowExecutor.IsValidSnapshot(new("reported-replay", 0, new(q, d.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor))
            || q.DatasetId != d.Manifest.DatasetId || q.DatasetVersion != d.Manifest.DatasetVersion || q.FixtureId != d.Manifest.FixtureId
            || q.DatasetId != d.Stream.DatasetId || q.DatasetVersion != d.Stream.DatasetVersion || q.FixtureId != d.Stream.FixtureId
            || q.StreamId != d.Stream.StreamId || d.Stream.DeliveryMode != "replay"
            || q.PresentationPolicyId != s.PolicyId || q.PresentationPolicyRevision != s.PolicyRevision
            || (q.ApplicationSequence ?? d.HeadSequence) != d.HeadSequence
            || s.Segments.Any(segment => segment is null) || s.Segments[0].CursorStartMs != 0
            || q.CursorMs < 0 || q.CursorMs > s.Segments[^1].CursorEndMs
            || d.Fixture!.Periods.Any(period => period is null)
            || d.Fixture.Periods.Select(period => period.Id).Distinct(StringComparer.Ordinal).Count() != d.Fixture.Periods.Length
            || !d.Closed && (view.Frame is null || view.TrackingGap)) return false;
        for (var i = 0; i < s.Segments.Length; i++)
        {
            var segment = s.Segments[i];
            if (segment.CursorEndMs <= segment.CursorStartMs || segment.LocalStartMs < 0
                || !d.Fixture.Periods.Any(period => period.Id == segment.PeriodId)
                || i > 0 && segment.CursorStartMs != s.Segments[i - 1].CursorEndMs) return false;
        }
        var current = s.Segments.FirstOrDefault(segment => q.CursorMs < segment.CursorEndMs) ?? s.Segments[^1];
        return view.PeriodId == current.PeriodId && view.LocalMs == current.LocalStartMs + q.CursorMs - current.CursorStartMs
            && view.Phase == current.Phase && view.PausedDisplayMs == current.PausedDisplayMs;
    }

    public static ReportedReplayStatistics? Project(FootballView? view) => Project(view, table: false);
    public static ReportedReplayStatistics? ProjectTable(FootballView? view) => Project(view, table: true);

    private static ReportedReplayStatistics? Project(FootballView? view, bool table)
    {
        if (!Bound(view) || !(table ? SupportsTable(view!) : Supports(view!)) || view!.Events is null) return null;
        var d = view.Dataset; var fixture = d.Fixture!; var schedule = d.Schedule!;
        var teams = new[] { fixture.Home, fixture.Away };
        if (teams.Any(team => !DatasetIdentity.IsValid(team.Id) || string.IsNullOrWhiteSpace(team.Name) || team.Name.Length > 180
                || team.Name.Any(c => char.IsControl(c) || c is '<' or '>')) || fixture.Home.Id == fixture.Away.Id
            || d.Manifest.Sources.Length == 0 || d.Manifest.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.SourceId)
                || string.IsNullOrWhiteSpace(source.Locator) || source.ArtifactHash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            || d.Manifest.Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() != d.Manifest.Sources.Length
            || schedule.Releases.Any(release => release is null || release.CursorMs < 0 || release.CursorMs > schedule.Segments[^1].CursorEndMs)
            || schedule.Releases.Select(release => release.RecordId).Distinct(StringComparer.Ordinal).Count() != schedule.Releases.Length
            || view.Events.Any(record => record is null)
            || view.Events.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).Count() != view.Events.Length) return null;
        // The ledger supplies type membership for actions, including future ones. Never infer it
        // from an ID prefix or silently discard an unknown release from this complete report index.
        var actionIds = view.GeneratedActionRecordIds ?? [];
        var generated = d.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1);
        if (generated && view.GeneratedActionRecordIds is null || !generated && actionIds.Length != 0
            || actionIds.Length > FootballActionPolicy.MaxGeneratedActionRecordIds
            || actionIds.Any(id => !DatasetIdentity.IsValid(id)) || actionIds.Distinct(StringComparer.Ordinal).Count() != actionIds.Length
            || actionIds.Any(id => schedule.Releases.Count(release => release.RecordId == id) != 1 || view.Events.Any(record => record.RecordId == id))) return null;
        var actions = actionIds.ToHashSet(StringComparer.Ordinal);
        if (!schedule.Releases.Where(release => release.CursorMs <= view.Context.CursorMs && !actions.Contains(release.RecordId)).Select(release => release.RecordId).Order(StringComparer.Ordinal)
            .SequenceEqual(view.Events.Select(record => record.RecordId).Order(StringComparer.Ordinal))) return null;
        foreach (var record in view.Events)
        {
            var inheritedReport = table && d.Manifest.Capabilities.Contains("complete-existing-generated-100ms-model")
                && record.FieldEvidence is not null && !record.FieldEvidence.ContainsKey("/payload") && !record.FieldEvidence.ContainsKey("/footballTime")
                && record.Evidence is not null && record.Evidence.Count(e => e is { EvidenceId: "reported", SourceId: "official-record" } && ReportEvidence(e)) == 1;
            if (record.DatasetId != view.Context.DatasetId || record.DatasetVersion != view.Context.DatasetVersion || record.FixtureId != view.Context.FixtureId
                || record.SchemaVersion != d.Manifest.SchemaVersion || record.RecordState != "active" || record.Revision < 1
                || record.Payload is not MatchEventPayload report || !teams.Any(team => team.Id == report.TeamId)
                || report.EventKind is not ("goal" or "yellow_card" or "red_card" or "substitution" or "var_decision")
                || record.FootballTime is null || !fixture.Periods.Any(period => period.Id == record.FootballTime.PeriodId)
                || fixture.Periods.Single(period => period.Id == record.FootballTime.PeriodId).Order > fixture.Periods.Single(period => period.Id == view.PeriodId).Order
                || record.Evidence is null || record.Evidence.Any(e => e is null)
                || record.Evidence.Select(e => e.EvidenceId).Distinct(StringComparer.Ordinal).Count() != record.Evidence.Length
                || record.FieldEvidence is null || !ReportedField("/payload") || !ReportedField("/footballTime")) return null;
            if (record.FieldEvidence.ContainsKey("/payload/decision") && !ReportedField("/payload/decision")) return null;
            bool ReportedField(string field) => record.FieldEvidence.TryGetValue(field, out var ids) ? ids is { Length: > 0 }
                && ids.All(id => record.Evidence.Count(e => e.EvidenceId == id && ReportEvidence(e)) == 1)
                : inheritedReport && field is "/payload" or "/footballTime";
            bool ReportEvidence(FootballEvidence evidence) => evidence.Origin == "real" && evidence.Method == "reported"
                && evidence.ClaimStatus == "reported" && !string.IsNullOrWhiteSpace(evidence.Locator)
                && d.Manifest.Sources.Count(source => source.SourceId == evidence.SourceId) == 1;
        }
        var authority = JsonSerializer.Serialize(new
        {
            sources = d.Manifest.Sources.OrderBy(source => source.SourceId, StringComparer.Ordinal),
            reports = view.Events.OrderBy(record => record.RecordId, StringComparer.Ordinal).Select(record => new
            {
                record.RecordId, record.Revision, record.FootballTime,
                report = record.Payload is MatchEventPayload e ? new { e.EventKind, e.TeamId, e.PersonId, e.Decision, e.IncomingPersonId, e.OutgoingPersonId } : null,
                evidence = record.Evidence.Where(e => e.Method == "reported"), record.FieldEvidence
            })
        });
        return new(MatchReportedEventSummary.Counts(view, fixture.Home.Id), MatchReportedEventSummary.Counts(view, fixture.Away.Id), view.Events.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority))));
    }

    public static string Limitation(MatchLanguage language) => ReplayText.Get(language, "ReportedPanelCoverage");
}
