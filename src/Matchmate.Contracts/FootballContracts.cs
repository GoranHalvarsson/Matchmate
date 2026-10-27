using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matchmate.Contracts;

public static class FootballContract { public const string Version = "0.4.0-draft.1"; }
public sealed record SourceReference(string SourceId, string Locator, string? ArtifactHash);
public sealed record IdentityMapping(string CanonicalId, string Namespace, JsonElement OriginalValue);
public sealed record FootballTime(string PeriodId, long? StartMs, long? EndMs, string Precision, bool? EndInclusive, string? ReportedClock = null);
public sealed record EffectiveTime(string Basis, string? From, string? To, string Precision, bool? EndInclusive);
public sealed record FootballEvidence(string EvidenceId, string SourceId, string Locator, string Origin, string Method,
    string ClaimStatus, DateTimeOffset? AvailableAtUtc = null, string? OriginalFlags = null);
public sealed record FootballConflict(string Field, string[] EvidenceIds, string Description);
public sealed record FootballRecord(string SchemaVersion, string DatasetId, string DatasetVersion, string? FixtureId,
    string[] EntityRefs, string RecordId, int Revision, int? SupersedesRevision, string RecordState,
    FootballTime? FootballTime, EffectiveTime? EffectiveTime, FootballEvidence[] Evidence,
    Dictionary<string, string[]> FieldEvidence, FootballPayload Payload, FootballConflict[]? Conflicts = null,
    string? WithdrawalReason = null)
{
    public string RecordType => Payload switch
    {
        FixturePayload => "fixture", SquadPayload => "match_squad", TrackingPayload => "tracking_frame",
        MatchEventPayload => "match_event", PossessionPayload => "player_possession", FootballActionPayload => "football_action", SchedulePayload => "presentation_schedule",
        CoveragePayload => "coverage", ReferencePayload => "reference", _ => "unsupported"
    };
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FixturePayload), "fixture")]
[JsonDerivedType(typeof(SquadPayload), "match_squad")]
[JsonDerivedType(typeof(TrackingPayload), "tracking_frame")]
[JsonDerivedType(typeof(MatchEventPayload), "match_event")]
[JsonDerivedType(typeof(PossessionPayload), "player_possession")]
[JsonDerivedType(typeof(FootballActionPayload), "football_action")]
[JsonDerivedType(typeof(SchedulePayload), "presentation_schedule")]
[JsonDerivedType(typeof(CoveragePayload), "coverage")]
[JsonDerivedType(typeof(ReferencePayload), "reference")]
public abstract record FootballPayload;
public sealed record FootballTeam(string Id, string Name);
public sealed record FootballPitch(double LengthM, double WidthM, string Basis = "top-left-metres-x-right-y-down");
public sealed record PeriodDefinition(string Id, int Order, string Label, long DisplayBaseMs, long? ActualDurationMs);
public sealed record FixturePayload(string Label, string Competition, FootballTeam Home, FootballTeam Away,
    FootballPitch Pitch, PeriodDefinition[] Periods) : FootballPayload;
public sealed record SquadPerson(string PersonId, string TeamId, string Name, int? Shirt, string Selection);
public sealed record SquadPayload(SquadPerson[] People) : FootballPayload;
public sealed record FootballObservation(string SubjectId, string? SlotId, string Availability, double? XM, double? YM,
    double? ZM, string Method, string Quality, string[] EvidenceIds, bool? OriginalDetected = null, string? MissingReason = null);
public sealed record TrackingPayload(string SourceFrameId, string? SourceTimestamp, FootballObservation[] Players,
    FootballObservation Ball, string[] ActiveSubjectIds, string SourceFrameState) : FootballPayload;
public sealed record MatchEventPayload(string EventKind, string TeamId, string? PersonId = null, string? AssistPersonId = null,
    string? Decision = null, bool? OwnGoal = null, string? OutgoingPersonId = null, string? IncomingPersonId = null,
    string? GroupId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatchEventDetail[]? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatchEventSequence? Sequence = null) : FootballPayload;
public sealed record MatchEventDetail(string Kind, string Text, string[] EvidenceIds, string? Uncertainty = null);
public static class MatchEventDetailLimits { public const int Count = 6, TextChars = 320, UncertaintyChars = 240; }
// Presentation offsets describe an authored schematic, never exact football contact times or tracking.
public sealed record MatchEventSequence(int Version, string Id, int PresentationLeadInMs, int PresentationTailMs, MatchEventStep[] Steps);
public sealed record MatchEventStep(string Id, string Kind, string? ActorId, string? RelatedActorId,
    int PresentationOffsetMs, string Text, string[] EvidenceIds, string? Uncertainty = null);
public static class MatchEventSequenceLimits
{
    public const int Steps = 5, TextChars = 180, UncertaintyChars = 240, LeadInMs = 60000, TailMs = 15000;
    public static bool EventKind(string kind) => kind is "corner" or "headed_assist" or "header_finish" or "advance" or "keeper_out"
        or "uncertain_goalmouth" or "reported_finish" or "throw_in" or "into_area" or "substitution" or "booking"
        or "release_pass" or "shot" or "save" or "rebound_finish" or "cross" or "deflection" or "reported_assist" or "red_card";
}
public sealed record PossessionPayload(string PersonId, string TeamId, string SourceStartFrame, string SourceEndFrame,
    bool StartMatched, bool EndMatched, long EligibleAfterMs, string DependencyRecordId) : FootballPayload;
// Authored model actions remain separate from reported events and real possession annotations.
public sealed record FootballActionPayload(string ActionKind, string TeamId, string? PersonId, string? RecipientId,
    string? RecipientTeamId, string Outcome, string? FromSlotId = null, string? ToSlotId = null) : FootballPayload;
public static class FootballActionPolicy
{
    public const string GeneratedModelActionsV1 = "generated-model-actions-v1";
    public const int MaxGeneratedActionRecordIds = 2000;
}
public sealed record FootballTeamIllustrationStatistics(string TeamId, int PassAttempts, int PassCompleted, int Shots,
    long ControlledMs, decimal? ControlSharePercent)
{
    public decimal? PassCompletionPercent => PassAttempts == 0 ? null : 100m * PassCompleted / PassAttempts;
}
public sealed record FootballIllustrationStatistics(FootballTeamIllustrationStatistics[] Teams, long TotalClassifiedControlMs);
// Calculated from authored generated actions, never evidence of actual match tactics or statistics.
public sealed record FootballStoryEvidence(string RecordId, int Revision, string Fingerprint, string[] EvidenceIds);
public sealed record FootballStoryPassage(string Id, string PeriodId, string TeamId, long StartCursorMs, long EndCursorMs,
    int PassAttempts, int PassCompleted, int Turnovers, int Shots, string Ending, FootballStoryEvidence[] Evidence, string Fingerprint)
{
    public long DurationMs => EndCursorMs - StartCursorMs;
}
public sealed record FootballStory(FootballStoryPassage[] Passages);
public sealed record PresentationSegment(string PeriodId, long CursorStartMs, long CursorEndMs, long LocalStartMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PausedDisplayMs = null);
public static class FootballPresentationPolicy
{
    public static bool IsPlay(PresentationSegment segment) => segment.Phase is null or "play";
    public const string GeneratedMovement = "generated-movement";
}
public sealed record PresentationRelease(string RecordId, long CursorMs, string? GroupId = null);
public sealed record PresentationMilestone(string Label, long CursorMs);
public sealed record SchedulePayload(string PolicyId, int PolicyRevision, PresentationSegment[] Segments,
    PresentationRelease[] Releases, PresentationMilestone[] Milestones, long SampleHoldMs, string TimingMethod) : FootballPayload;
public sealed record CoveragePayload(string[] SubjectIds, string Status, long? CadenceMs, string Reason) : FootballPayload;
public sealed record ReferencePayload(string SubjectId, string EntityKind, string Name) : FootballPayload;
public sealed record FootballManifest(string SchemaVersion, string DatasetId, string DatasetVersion, string FixtureId,
    int ManifestRevision, string Lifecycle, SourceReference[] Sources, IdentityMapping[] IdentityMappings,
    string[] Capabilities, string[] QueryModes, string Normalization, string[] IndependentDependencies);
public sealed record FootballStream(string StreamId, string DatasetId, string DatasetVersion, string FixtureId, string DeliveryMode);
public sealed record FootballBatch(FootballManifest Manifest, FootballStream Stream, long Sequence, FootballRecord[] Records,
    bool CloseStream, string Digest);
public sealed record AcceptedFootballBatch(FootballBatch Batch, DateTimeOffset ReceivedAtUtc);
public sealed record FootballDataset(FootballManifest Manifest, FootballStream Stream, long HeadSequence, bool Closed,
    FixturePayload? Fixture, SquadPerson[] People, SchedulePayload? Schedule, int PendingRecords);
public sealed record FootballQuery(string DatasetId, string DatasetVersion, string StreamId, string FixtureId, long CursorMs,
    string KnowledgeMode, string PresentationPolicyId, int PresentationPolicyRevision, long? ApplicationSequence = null,
    DateTimeOffset? KnowledgeAsOfUtc = null, string? SubjectId = null, string? EffectiveDate = null);
public sealed record FootballView(FootballDataset Dataset, FootballQuery Context, string? PeriodId, long? LocalMs,
    TrackingPayload? Frame, FootballRecord[] Events, FootballRecord[] Possessions, int HomeGoals, int AwayGoals,
    bool TrackingGap, string CoverageMessage, FootballRecord[] References,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlayerEvidenceView? PlayerEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FootballIllustrationStatistics? IllustrationStatistics = null,
    // Type membership only: no future action times, participants or outcomes are exposed here.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? GeneratedActionRecordIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeneratedDistanceView? GeneratedDistance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PausedDisplayMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FootballStory? Story = null);
