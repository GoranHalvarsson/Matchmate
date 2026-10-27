namespace Matchmate.Contracts;

// Derived generated replay movement, deliberately separate from measured football statistics.
public sealed record GeneratedPlayerDistance(string PersonId, long? CumulativeCentimetres);
public sealed record GeneratedDistanceView(int Version, string PackId, string PackSha256, long CursorMs,
    GeneratedPlayerDistance[] Players);
public sealed record GeneratedDistancePackMetadata(int Version, string Id, FootballDataset Dataset,
    string SourceManifestSha256, string SourceChunksSha256, string GeneratorSha256,
    int CadenceMs, double ContinuityMetresPerSecond, int FrameCount, int ExcludedJumps);
public readonly record struct GeneratedDistanceSample(int CursorMs, int CumulativeCentimetres);
// Only these reviewed source profiles can derive a pack; their native gates remain separate.
public sealed record GeneratedDistanceProfile
{
    private GeneratedDistanceProfile(int cadenceMs, string precision, string frameState, int durationMs, int frames, bool excludeNonPlay)
    { CadenceMs = cadenceMs; Precision = precision; FrameState = frameState; DurationMs = durationMs; Frames = frames; ExcludeNonPlay = excludeNonPlay; }
    public int CadenceMs { get; }
    public string Precision { get; }
    public string FrameState { get; }
    public int DurationMs { get; }
    public int Frames { get; }
    public bool ExcludeNonPlay { get; }
    public static GeneratedDistanceProfile ChelseaFullV5 { get; } = new(100, "source_sample", "generated-complete-model", 6_000_000, 60_000, false);
    public static GeneratedDistanceProfile LiverpoolCardsV1 { get; } = new(200, "presentation_sample", "generated", 6_540_000, 32_701, true);
    public static GeneratedDistanceProfile ForDataset(string datasetId) => datasetId switch
    {
        "brentford-existing-illustration" => ChelseaFullV5,
        "brentford-liverpool-illustration" => LiverpoolCardsV1,
        _ => throw new InvalidDataException("Unsupported generated distance source profile.")
    };
}
public static class GeneratedDistanceLimits
{
    public const int CompressedBytes = 8_000_000, ExpandedBytes = 16_000_000, HeaderBytes = 131_072;
    public const int People = 64, Frames = 60_000, Samples = 1_500_000, DurationMs = 6_000_000;
    public const int CadenceMs = 100;
    // A conservative continuity filter for authored geometry, never a measured player speed.
    public const double ContinuityMetresPerSecond = 15;
}
