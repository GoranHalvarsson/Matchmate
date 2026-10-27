namespace Matchmate.Contracts;

// Local replay API contract v1. This is not the existing football domain schema v0.3.
public sealed record Team(int Id, string Name);
public sealed record Pitch(double Length, double Width, string Units, string Origin);
public sealed record Player(int Id, int TeamId, int? Number, string Name, string Crosswalk, string SourceLocator);
public sealed record CoverageGap(string Id, double Start, double End, int Slots);
public sealed record ReplayMetadata(
    int MatchId, string Version, string SourceHash, string Source, string Pin, string Origin,
    string Competition, string DateTime, Team Home, Team Away, Pitch Pitch,
    int Period, double Start, double End, double Cadence, Player[] Players,
    int FrameCount, int EmptyFrames, CoverageGap[] Gaps, long SourceBytes, long GzipBytes,
    string License, string SourceUrl, string Limits, string ImportMode);
public sealed record Position(double? X, double? Y, bool? IsDetected, string? MissingReason);
public sealed record PlayerPosition(int PlayerId, double? X, double? Y, bool? IsDetected, string? MissingReason);
public sealed record RecordedFrame(int FrameId, int Period, string SourceTimestamp, double Clock,
    PlayerPosition[] Players, Position Ball, string FrameState);
public sealed record ReplaySnapshot(int ContractVersion, ReplayMetadata Metadata, RecordedFrame[] Frames);
public sealed record Observation(double? X, double? Y, bool Available, string Quality,
    string? MissingReason, bool OutOfBounds);
public sealed record PlayerView(Player Player, Observation Position);
public sealed record FrameView(string Version, int MatchId, double Cursor, double? SourceClock,
    int? SourceFrameId, string State, bool FullGap, int AvailablePlayers,
    PlayerView[] Players, Observation Ball);
// Dataset identity is distinct from source match/player integer identities.
public sealed record ReplayDataset(string DatasetId, long PublicationRevision, ReplayMetadata Metadata, DateTimeOffset IngestedAtUtc);
