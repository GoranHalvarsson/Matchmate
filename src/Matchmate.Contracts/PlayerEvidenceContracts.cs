using System.Collections.Immutable;

namespace Matchmate.Contracts;

// Prepared retrospective observations are separate from recording records and their immutable manifest.
public sealed record PlayerEvidenceBinding(string DatasetId, string DatasetVersion, string StreamId, string FixtureId,
    long HeadSequence, int ManifestRevision, string RosterSha256, ImmutableArray<SourceReference> SourcePins,
    ImmutableArray<string> ContextSourceIds, ImmutableArray<string> PeriodIds);
public sealed record PlayerEvidenceObservation(string Id, string PersonId, FootballTime FootballTime, long EligibleAfterMs,
    string Text, string Qualification, ImmutableArray<FootballEvidence> Evidence, ImmutableArray<string> RelatedPersonIds);
public sealed record PreparedPlayerEvidencePack(int Version, string Id, PlayerEvidenceBinding Binding,
    ImmutableArray<SquadPerson> Roster, ImmutableArray<SourceReference> Sources,
    ImmutableArray<PlayerEvidenceObservation> Observations, string Coverage);
public sealed record PlayerEvidenceView(int Version, string PackId, string PackSha256, PlayerEvidenceBinding Binding,
    ImmutableArray<SourceReference> Sources, ImmutableArray<PlayerEvidenceObservation> Observations, string Coverage);
public static class PlayerEvidenceLimits
{
    public const int PackBytes = 131072, Observations = 128, PerPlayer = 8, People = 64, Sources = 8;
    public const int TextChars = 480, QualificationChars = 720, CoverageChars = 480, LocatorChars = 720, Evidence = 3;
}
