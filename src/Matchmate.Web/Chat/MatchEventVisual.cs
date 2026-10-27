using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Localization;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Chat;

public enum MatchEventVisualIntent { ReportedSequence }
public sealed record MatchEventVisualAction(Guid TurnId);
public sealed record MatchEventVisualStep(string Id, string Kind, string? ActorId, string? ActorName,
    string? RelatedActorId, string? RelatedActorName, string Text, string? Uncertainty, ImmutableArray<string> EvidenceIds);
public sealed record MatchEventVisualEvidence(string Id, string SourceId, string Origin, string Method, string ClaimStatus, string? ArtifactHash);
public sealed record MatchEventPresentationTransition(Guid Id, Guid TurnId, MatchSelectedEvent Selection, long TargetCursorMs);

// A bounded server snapshot, separate from the narrator bundle. No frames, contract arrays or model markup survive here.
public sealed class MatchEventVisual
{
    public string Kind { get; }
    public string? Decision { get; }
    public long? IntervalStartMs { get; }
    public long? IntervalEndMs { get; }
    public string Title => Kind switch { "goal" => "How this goal happened", "substitution" => "The player change", "yellow_card" => "The reported booking", "red_card" => "The reported dismissal", _ => "Completed possession interval" };
    public MatchChatContext Context { get; }
    public string EventId { get; }
    public int EventRevision { get; }
    public string SequenceId { get; }
    public int SequenceVersion { get; }
    public string PeriodId { get; }
    public int PeriodOrder { get; }
    public string TeamId { get; }
    public string HomeTeamId { get; }
    public string ReportedClock { get; }
    public long ReleaseCursorMs { get; }
    public long TargetCursorMs { get; }
    public ImmutableArray<MatchEventVisualStep> Steps { get; }
    public ImmutableArray<MatchEventVisualEvidence> Evidence { get; }
    public bool AttacksRight => MatchSequenceLayout.AttacksRight(PeriodOrder, TeamId, HomeTeamId);

    private MatchEventVisual(MatchChatContext context, FootballRecord record, MatchSequenceProjection projection,
        PeriodDefinition period, string teamId, string homeTeamId, long target,
        ImmutableArray<MatchEventVisualStep> steps, ImmutableArray<MatchEventVisualEvidence> evidence)
    {
        Context = context; EventId = record.RecordId; EventRevision = record.Revision;
        SequenceId = projection.Sequence.Id; SequenceVersion = projection.Sequence.Version;
        PeriodId = period.Id; PeriodOrder = period.Order; TeamId = teamId; HomeTeamId = homeTeamId;
        Kind = record.Payload is MatchEventPayload e ? e.EventKind : "completed_possession";
        Decision = record.Payload is MatchEventPayload { EventKind: "red_card" } card ? card.Decision : null;
        IntervalStartMs = projection.IntervalStartMs; IntervalEndMs = projection.IntervalEndMs;
        ReportedClock = context.SelectedEvent!.ReportedClock; ReleaseCursorMs = projection.ReleaseCursorMs;
        TargetCursorMs = target; Steps = steps; Evidence = evidence;
    }

    public static MatchEventVisualIntent? Intent(string question)
    {
        // Presentation only: Decision-1 still chooses retrieval, and receives the original question unchanged.
        if (question.Length > MatchChatLimits.InputChars) return null;
        var text = MatchLocalizedIntent.Canonical(question.Trim());
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        return Regex.IsMatch(text, @"\b(goal|substitution|booking|card|second[- ]yellow|dismissal|possession|event|sequence|player[- ]change)\b", options)
            && !Regex.IsMatch(text, @"\b(score|scorers|how many)\b", options)
            && (Regex.IsMatch(text, @"\b(draw|diagram|visual|visuali[sz]e|sketch|illustrat(e|ion))\b", options)
                || Regex.IsMatch(text, @"\bshow\b", options) && Regex.IsMatch(text, @"\b(how|substitution|booking|card|second[- ]yellow|dismissal|possession|event|player[- ]change|led)\b", options))
            ? MatchEventVisualIntent.ReportedSequence : null;
    }

    public static bool SupportedKind(string kind) => kind is "goal" or "substitution" or "yellow_card" or "red_card" or "completed_possession";
    private static string? RequestedKind(string original) => RequestedCanonicalKind(MatchLocalizedIntent.Canonical(original));
    private static string? RequestedCanonicalKind(string question) => Regex.IsMatch(question, @"\bgoal\b", RegexOptions.IgnoreCase) ? "goal"
        : Regex.IsMatch(question, @"\b(substitution|player[- ]change)\b", RegexOptions.IgnoreCase) ? "substitution"
        : Regex.IsMatch(question, @"\b(red[- ]card|second[- ]yellow|dismissal|sending[- ]off)\b", RegexOptions.IgnoreCase) ? "red_card"
        : Regex.IsMatch(question, @"\b(booking|yellow[- ]card)\b", RegexOptions.IgnoreCase) ? "yellow_card"
        : Regex.IsMatch(question, @"\bcard\b", RegexOptions.IgnoreCase) ? "card"
        : Regex.IsMatch(question, @"\bpossession\b", RegexOptions.IgnoreCase) ? "completed_possession" : null;
    public static bool MatchesRequestedKind(string question, MatchSelectedEvent? selection) => selection is not null && SupportedKind(selection.Kind)
        && (RequestedKind(question) is not { } kind || kind == selection.Kind || kind == "card" && selection.Kind is "yellow_card" or "red_card");
    public static string Clarification(string question) => RequestedKind(question) switch {
        "goal" => "Choose a goal in the highlights, then ask how it happened.", "substitution" => "Choose a substitution in the highlights, then ask for its diagram.",
        "yellow_card" or "card" => "Choose a booking in the highlights, then ask for its diagram.", "completed_possession" => "Choose a completed possession interval in the highlights, then ask for its diagram.",
        _ => "Choose a supported event in the highlights, then ask for its diagram." };
    public static string ClarificationForLanguage(string question, MatchLanguage language) => ChatText.Get(language, "Visual." + (RequestedKind(question) switch {
        "goal" => "Goal", "substitution" => "Substitution", "yellow_card" or "card" => "Card", "completed_possession" => "Possession", _ => "Event" }));

    public static MatchEventVisual? Create(FootballDataset dataset, FootballRecord record, MatchChatContext context)
    {
        if (context.SelectedEvent is not { } selection || dataset.Fixture is not { } fixture
            || dataset.Schedule is not { Segments.Length: > 0 } schedule || !Bound(dataset, context)
            || record.RecordId != selection.RecordId || record.Revision < 1
            || !SupportedKind(selection.Kind) || record.FootballTime is not { } time
            || fixture.Periods.Count(p => p.Id == time.PeriodId) != 1
            || dataset.People.Select(p => p.PersonId).Distinct(StringComparer.Ordinal).Count() != dataset.People.Length)
            return null;
        try { FootballCodec.ValidateMetadataRecord(record, dataset.Manifest); }
        catch (RecordingValidationException) { return null; }
        var isPossession = record.Payload is PossessionPayload;
        var kind = record.Payload is MatchEventPayload goal ? goal.EventKind : isPossession ? "completed_possession" : null;
        var teamId = record.Payload is MatchEventPayload report ? report.TeamId : (record.Payload as PossessionPayload)?.TeamId;
        if (kind != selection.Kind || teamId != fixture.Home.Id && teamId != fixture.Away.Id
            || !isPossession && (!dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1") || time.ReportedClock != selection.ReportedClock)) return null;
        var projected = isPossession ? MatchSequenceProjector.Possession(dataset, record, context.Query.CursorMs)
            : MatchSequenceProjector.Event(dataset, record, context.Query.CursorMs, selection.Kind == "goal");
        if (projected is null || projected.ReleaseCursorMs != selection.ReleaseCursorMs
            || selection.Kind != "goal" && (projected.ReleaseCursorMs > context.Query.CursorMs || !Eligible(dataset, record, context.Query.CursorMs))
            || isPossession && selection.ReportedClock != IntervalLabel(projected)) return null;
        var sequence = projected.Sequence;
        if (sequence.Steps.Length is < 1 or > MatchEventSequenceLimits.Steps) return null;
        var steps = ImmutableArray.CreateBuilder<MatchEventVisualStep>();
        foreach (var step in sequence.Steps)
        {
            var actor = step.ActorId is null ? null : dataset.People.SingleOrDefault(p => p.PersonId == step.ActorId);
            var related = step.RelatedActorId is null ? null : dataset.People.SingleOrDefault(p => p.PersonId == step.RelatedActorId);
            if (step.ActorId is not null && actor is null || step.RelatedActorId is not null && related is null
                || actor is not null && !ValidName(actor.Name) || related is not null && !ValidName(related.Name)
                || step.EvidenceIds.Length is < 1 or > 3 || step.EvidenceIds.Any(id => record.Evidence.Count(e => e.EvidenceId == id) != 1)) return null;
            steps.Add(new(step.Id, step.Kind, step.ActorId, actor?.Name, step.RelatedActorId, related?.Name,
                step.Text, step.Uncertainty, step.EvidenceIds.ToImmutableArray()));
        }
        var evidence = ImmutableArray.CreateBuilder<MatchEventVisualEvidence>();
        foreach (var id in steps.SelectMany(s => s.EvidenceIds).Distinct(StringComparer.Ordinal))
        {
            var e = record.Evidence.Single(e => e.EvidenceId == id);
            var sources = dataset.Manifest.Sources.Where(s => s.SourceId == e.SourceId).ToArray();
            if (sources.Length != 1) return null;
            evidence.Add(new(e.EvidenceId, e.SourceId, e.Origin, e.Method, e.ClaimStatus, sources[0].ArtifactHash));
        }
        var period = fixture.Periods.Single(p => p.Id == time.PeriodId);
        if (period.Order is < 1 or > 4 || selection.ReleaseCursorMs < schedule.Segments[0].CursorStartMs
            || selection.ReleaseCursorMs > schedule.Segments[^1].CursorEndMs) return null;
        var target = Math.Clamp(selection.ReleaseCursorMs - (selection.Kind == "goal" ? 15000 : 0), schedule.Segments[0].CursorStartMs, schedule.Segments[^1].CursorEndMs);
        return new(context, record, projected, period, teamId!, fixture.Home.Id, target, steps.ToImmutable(), evidence.ToImmutable());
    }
    public static string IntervalLabel(MatchSequenceProjection p) => ExperiencePresentation.IntervalTime(p.IntervalStartMs!.Value) + "–" + ExperiencePresentation.IntervalTime(p.IntervalEndMs!.Value)
        + (p.IntervalStartMs == p.IntervalEndMs ? " · point annotation" : "");
    private static bool Eligible(FootballDataset dataset, FootballRecord record, long cursor)
    {
        var t = record.FootballTime!;
        var cutoff = record.Payload is PossessionPayload possession ? t.EndMs is { } completed ? Math.Max(completed, possession.EligibleAfterMs) : (long?)null
            : t.Precision == "reported_minute" ? t.EndMs : t.StartMs;
        var segment = dataset.Schedule!.Segments.SingleOrDefault(s => s.PeriodId == t.PeriodId && FootballPresentationPolicy.IsPlay(s));
        return cutoff is { } end && segment is not null && end >= segment.LocalStartMs
            && segment.CursorStartMs + end - segment.LocalStartMs <= cursor;
    }

    public bool Matches(FootballDataset dataset, FootballRecord record, MatchChatContext current)
        => SameDomain(Context, current) && Context.HeadSequence == current.HeadSequence
            && Context.Query.ApplicationSequence == current.Query.ApplicationSequence
            && Create(dataset, record, Context) is { } fresh
            && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(fresh);

    internal static bool SameDomain(MatchChatContext a, MatchChatContext b)
        => (a.Query with { CursorMs = 0, SubjectId = null, ApplicationSequence = null })
            == (b.Query with { CursorMs = 0, SubjectId = null, ApplicationSequence = null });
    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 180 && MatchPlayerResolver.ValidText(name);
    private static bool Bound(FootballDataset d, MatchChatContext c) => d.Manifest.DatasetId == c.Query.DatasetId
        && d.Manifest.DatasetVersion == c.Query.DatasetVersion && d.Manifest.FixtureId == c.Query.FixtureId
        && d.Stream.StreamId == c.Query.StreamId && d.Stream.DatasetId == c.Query.DatasetId
        && d.Stream.DatasetVersion == c.Query.DatasetVersion && d.Stream.FixtureId == c.Query.FixtureId
        && d.Stream.DeliveryMode == "replay" && d.HeadSequence == c.HeadSequence
        && d.Schedule?.PolicyId == c.Query.PresentationPolicyId && d.Schedule.PolicyRevision == c.Query.PresentationPolicyRevision;
}
