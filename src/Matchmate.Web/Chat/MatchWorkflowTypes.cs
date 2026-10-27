using System.Collections.Immutable;
using Matchmate.Contracts;

namespace Matchmate.Web.Chat;

public enum MatchWorkflowTemporalMode { ReplayAtCursor, LiveNow, HistoricalAtCursor, SelectedGoalRetrospective }
public enum MatchWorkflowStatus { Success, Partial, Clarify, Unsupported, Unavailable, InvalidRequest, Stale, Cancelled, RetrievalFailure }

// Capture only from trusted server state. OwnerKey is a comparison key, not proof of authentication.
public sealed record MatchWorkflowSnapshot(string OwnerKey, long Generation, MatchChatContext Context, MatchWorkflowTemporalMode TemporalMode);
public sealed record MatchWorkflowRequest(string WorkflowId, string Version, MatchWorkflowSnapshot Snapshot, MatchPlayerQuestion? PlayerQuestion = null,
    MatchEventVisualIntent? VisualIntent = null);
public sealed record MatchWorkflowDefinition(string Id, string Version, ImmutableArray<string> Tools,
    ImmutableArray<MatchWorkflowTemporalMode> TemporalModes, bool RequiresPlayer, int MaxReads, string Persona);
public sealed record MatchWorkflowItem(string Id, string Tool, string Text, ImmutableArray<string> NumericTokens);
public sealed record MatchWorkflowSource(string SourceId, string? ArtifactHash);
public sealed record MatchWorkflowProvenance(string RecordId, int Revision, FootballTime? FootballTime,
    string EvidenceId, string SourceId, string Origin, string Method, string ClaimStatus, DateTimeOffset? AvailableAtUtc);
public sealed record MatchWorkflowBundle(string WorkflowId, string Version, MatchChatContext Context,
    MatchWorkflowTemporalMode TemporalMode, string DeliveryMode, ImmutableArray<MatchWorkflowItem> Items,
    ImmutableArray<MatchWorkflowSource> Sources, ImmutableArray<MatchWorkflowProvenance> EventProvenance,
    ImmutableArray<string> ObservationMethods, ImmutableArray<string> Missingness, string? ResolvedSubjectId = null);
public sealed record MatchWorkflowResult(MatchWorkflowStatus Status, string Message, MatchWorkflowBundle? Bundle = null, string? ResolvedSubjectId = null,
    MatchEventVisual? EventVisual = null);

public static class MatchWorkflowLimits
{
    public const int Items = 4, Events = 20, MomentEvents = 4, TrackingPlayers = 6, Possessions = 5, Sources = 8, EvidencePerEvent = 3,
        EvidenceChars = MatchChatLimits.EvidenceChars, BundleChars = 32000;
}
