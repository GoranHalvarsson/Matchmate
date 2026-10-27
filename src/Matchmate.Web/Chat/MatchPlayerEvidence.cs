using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Chat;

public sealed record MatchPlayerProjection(MatchChatEvidence Item, ImmutableArray<MatchWorkflowProvenance> Provenance, ImmutableArray<string> Missingness, ImmutableArray<SourceReference> Sources);
public static class MatchPlayerEvidence
{
    private const int ItemChars = 8000, ProvenanceChars = 12000;
    private static string[] Roles(MatchEventPayload e, string id) => new[] {
        e.PersonId == id ? "reported_player" : null, e.AssistPersonId == id ? "formal_assist" : null,
        e.OutgoingPersonId == id ? "outgoing" : null, e.IncomingPersonId == id ? "incoming" : null,
        e.Sequence?.Steps.Any(s => s.ActorId == id) == true ? "sequence_actor" : null,
        e.Sequence?.Steps.Any(s => s.RelatedActorId == id) == true ? "sequence_related_actor" : null
    }.OfType<string>().ToArray();
    public static MatchPlayerProjection Build(FootballView view, string subject)
    {
        var person = view.Dataset.People.Single(p => p.PersonId == subject);
        var relevant = view.Events.Where(r => r.Payload is MatchEventPayload e && Roles(e, subject).Length > 0).ToArray();
        var possessions = view.Possessions.Where(r => r.Payload is PossessionPayload p && p.PersonId == subject)
            .OrderByDescending(r => ((PossessionPayload)r.Payload).EligibleAfterMs).ThenBy(r => r.RecordId, StringComparer.Ordinal).ToArray();
        var prepared = PlayerEvidenceProjection.ForSubject(view, subject);
        var briefs = prepared?.Observations.ToArray() ?? [];
        var briefCount = Math.Min(briefs.Length, PlayerEvidenceLimits.PerPlayer);
        var eventCount = Math.Min(relevant.Length, MatchWorkflowLimits.Events); var possessionCount = Math.Min(possessions.Length, MatchWorkflowLimits.Possessions);
        while (true)
        {
            var events = relevant.Take(eventCount).ToArray(); var completed = possessions.Take(possessionCount).ToArray();
            var reviewed = briefs.Take(briefCount).ToArray();
            var records = events.Concat(completed).ToArray();
            var provenance = records.SelectMany(r => r.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).Select(e =>
                    new MatchWorkflowProvenance(r.RecordId, r.Revision, r.FootballTime, e.EvidenceId, e.SourceId, e.Origin, e.Method, e.ClaimStatus, e.AvailableAtUtc)))
                .Concat(reviewed.SelectMany(o => o.Evidence.Select(e => new MatchWorkflowProvenance(o.Id, 1, o.FootballTime,
                    e.EvidenceId, e.SourceId, e.Origin, e.Method, e.ClaimStatus, e.AvailableAtUtc)))).ToImmutableArray();
            var registry = view.Dataset.Manifest.Sources.Concat(prepared?.Sources ?? []).GroupBy(s => s.SourceId, StringComparer.Ordinal).ToArray();
            if (registry.Any(g => g.Distinct().Count() != 1)) throw new InvalidDataException("Conflicting player source identifiers.");
            var sourceIds = prepared is null ? view.Dataset.Manifest.Sources.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal)
                : prepared.Binding.ContextSourceIds.Concat(provenance.Select(p => p.SourceId)).ToHashSet(StringComparer.Ordinal);
            var sources = registry.Where(g => sourceIds.Contains(g.Key)).Select(g => g.First()).ToImmutableArray();
            if (sourceIds.Count != sources.Length || provenance.Any(p => !sourceIds.Contains(p.SourceId))) throw new InvalidDataException("Missing player provenance source.");
            MatchChatEvidence projected;
            try { projected = new MatchEvidenceProjection(view with { Events = relevant }).Get("current_events", eventCount); }
            catch (InvalidDataException) when (eventCount > 0) { eventCount--; continue; }
            using var document = JsonDocument.Parse(projected.Text);
            var intervals = completed.Select(r => new { record = r.RecordId, r.Revision,
                start = ExperiencePresentation.Clock(r.FootballTime!.StartMs!.Value), end = ExperiencePresentation.Clock(r.FootballTime.EndMs!.Value),
                precision = r.FootballTime.Precision, ((PossessionPayload)r.Payload).StartMatched, ((PossessionPayload)r.Payload).EndMatched,
                evidenceIds = r.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).Select(e => e.EvidenceId) }).ToArray();
            var text = JsonSerializer.Serialize(new { scope = "resolved_player_at_current_cursor", playerId = subject, player = person.Name,
                team = new[] { view.Dataset.Fixture!.Home, view.Dataset.Fixture.Away }.FirstOrDefault(t => t.Id == person.TeamId)?.Name,
                shirt = person.Shirt, rosterSelection = person.Selection, eligibleTargetEvents = relevant.Length,
                shown = document.RootElement.GetProperty("shown"), roles = events.Select(r => new { record = r.RecordId, roles = Roles((MatchEventPayload)r.Payload, subject),
                    formalAssistSupplied = ((MatchEventPayload)r.Payload).AssistPersonId is not null,
                    formalAssist = ((MatchEventPayload)r.Payload).AssistPersonId is { } assist ? ExperiencePresentation.Person(view.Dataset, assist) : null,
                    uncertainty = ((MatchEventPayload)r.Payload).Details?.Select(d => d.Uncertainty).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal) }),
                omittedEvents = relevant.Length - eventCount, completedPossessions = intervals, eligibleTargetCompletedPossessions = possessions.Length, omittedPossessions = possessions.Length - possessionCount,
                reviewedBrief = new { packId = prepared?.PackId, packSha256 = prepared?.PackSha256,
                    availability = prepared is null ? "not_supplied" : reviewed.Length == 0 ? "no_eligible_reviewed_observations" : "sparse_reviewed_observations",
                    observations = reviewed.Select(o => new { id = o.Id, reportedClock = o.FootballTime.ReportedClock, precision = o.FootballTime.Precision,
                        text = o.Text, qualification = o.Qualification, relatedPlayers = o.RelatedPersonIds.Select(id => ExperiencePresentation.Person(view.Dataset, id)),
                        evidence = o.Evidence.Select(e => new { id = e.EvidenceId, source = e.SourceId, method = e.Method, status = e.ClaimStatus }) }),
                    omitted = briefs.Length - briefCount, metrics = "not_supplied", coverage = prepared?.Coverage ?? "No prepared reviewed observations supplied; missing does not mean no contribution." },
                limitation = "Only this player's eligible attributed involvements. Counterparts belong to those events. Missing samples may reflect bench/substitution or coverage, not proof of nonparticipation. No invented rating, unsupported statistics, inferred formal assist or current possession. Reported timing/conflicts and generated-position limits remain." });
            if (text.Length <= ItemChars && JsonSerializer.Serialize(provenance).Length <= ProvenanceChars && sources.Length <= MatchWorkflowLimits.Sources)
            {
                var missing = ImmutableArray.CreateBuilder<string>();
                if (eventCount < relevant.Length) missing.Add("player_events_truncated");
                if (possessionCount < possessions.Length) missing.Add("player_possessions_truncated");
                if (records.Any(r => r.Evidence.Length > MatchWorkflowLimits.EvidencePerEvent)) missing.Add("player_provenance_truncated");
                if (prepared is not null) missing.Add("player_reviewed_observations_sparse");
                if (briefCount < briefs.Length) missing.Add("player_reviewed_observations_truncated");
                var numbers = projected.NumericTokens.Concat(intervals.SelectMany(i => MatchReplyValidation.Numbers(i.start).Concat(MatchReplyValidation.Numbers(i.end))))
                    .Concat(person.Shirt is { } shirt ? [shirt.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [])
                    .Concat(reviewed.SelectMany(o => MatchReplyValidation.Numbers(o.Text).Concat(MatchReplyValidation.Numbers(o.Qualification)).Concat(MatchReplyValidation.Numbers(o.FootballTime.ReportedClock ?? ""))))
                    .Append((briefs.Length - briefCount).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Concat(new[] { relevant.Length, relevant.Length - eventCount, possessions.Length, possessions.Length - possessionCount }.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new(new("player_involvement", "player_involvement", text, numbers.Distinct(StringComparer.Ordinal).ToArray()), provenance, missing.ToImmutable(), sources);
            }
            if (eventCount > 0) eventCount--; else if (possessionCount > 0) possessionCount--; else if (briefCount > 0) briefCount--; else throw new InvalidDataException("Player evidence budget exceeded.");
        }
    }
}
