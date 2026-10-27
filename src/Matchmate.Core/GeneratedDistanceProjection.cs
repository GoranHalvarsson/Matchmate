using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Contracts;

namespace Matchmate.Core;

// Immutable prepared lookup state: no recording reads or arithmetic accumulation in a request.
public sealed class GeneratedDistanceProjection
{
    private readonly string _datasetHash, _packId, _packHash;
    private readonly GeneratedDistanceProfile _profile;
    public const string LiverpoolDatasetSha256 = "87edf4604d2ace97a612abc5378a86dbbc81229c4f264166ccd254eba5417d9e";
    public const string LiverpoolActionsDatasetSha256 = "2f7169ab1d23364e7f4a22b027bc8dac3cb3b365dfcccd9234ae58b142309e7e";
    private readonly (string PersonId, GeneratedDistanceSample[] Samples)[] _players;
    public GeneratedDistanceProjection(GeneratedDistancePackMetadata metadata,
        IReadOnlyDictionary<string, GeneratedDistanceSample[]> players, string packSha256)
    {
        ValidateMetadata(metadata);
        Require(Hash(packSha256), "Invalid generated distance pack fingerprint.");
        var dataset = metadata.Dataset;
        _profile = GeneratedDistanceProfile.ForDataset(dataset.Manifest.DatasetId);
        Require(players.Count == dataset.People.Length && players.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(dataset.People.Select(p => p.PersonId)), "Distance roster mismatch.");
        var total = 0;
        foreach (var samples in players.Values)
        {
            Require(samples is not null && samples.Length <= metadata.FrameCount, "Invalid distance prefix length.");
            total = checked(total + samples.Length);
            var previousCursor = -1; var previousCm = 0;
            foreach (var sample in samples)
            {
                Require(sample.CursorMs > previousCursor && sample.CursorMs % metadata.CadenceMs == 0
                    && sample.CursorMs <= dataset.Schedule!.Segments[^1].CursorEndMs && sample.CumulativeCentimetres >= previousCm
                    && sample.CumulativeCentimetres <= _profile.DurationMs / 1000 * GeneratedDistanceLimits.ContinuityMetresPerSecond * 100,
                    "Invalid generated distance prefix.");
                if (previousCursor == -1) Require(sample.CumulativeCentimetres == 0, "First generated position must begin at zero.");
                previousCursor = sample.CursorMs; previousCm = sample.CumulativeCentimetres;
            }
        }
        Require(total <= GeneratedDistanceLimits.Samples, "Too many generated distance prefixes.");
        _datasetHash = Fingerprint(dataset); _packId = metadata.Id; _packHash = packSha256;
        _players = dataset.People.Select(p => (p.PersonId, players[p.PersonId].ToArray())).ToArray();
    }
    public FootballView Attach(FootballView view)
    {
        if (!Matches(view)) return view with { GeneratedDistance = null };
        var cursor = view.Context.CursorMs;
        return view with { GeneratedDistance = new(1, _packId, _packHash, cursor, _players.Select(p =>
            new GeneratedPlayerDistance(p.PersonId, Prefix(p.Samples, cursor))).ToArray()) };
    }
    public static int? Prefix(ReadOnlySpan<GeneratedDistanceSample> samples, long cursor)
    {
        var low = 0; var high = samples.Length - 1; var found = -1;
        while (low <= high) { var mid = low + (high - low) / 2; if (samples[mid].CursorMs <= cursor) { found = mid; low = mid + 1; } else high = mid - 1; }
        return found < 0 ? null : samples[found].CumulativeCentimetres;
    }
    private bool Matches(FootballView view)
    {
        var d = view.Dataset; var q = view.Context;
        if (d is not { Closed: true, PendingRecords: 0, Manifest: not null, Stream: not null, Fixture: not null, Schedule: not null }
            || q is null || q.KnowledgeMode != "retrospective_replay" || q.KnowledgeAsOfUtc is not null || q.EffectiveDate is not null
            || q.ApplicationSequence is { } sequence && sequence != d.HeadSequence
            || q.DatasetId != d.Manifest.DatasetId || q.DatasetVersion != d.Manifest.DatasetVersion || q.StreamId != d.Stream.StreamId || q.FixtureId != d.Manifest.FixtureId
            || q.PresentationPolicyId != d.Schedule.PolicyId || q.PresentationPolicyRevision != d.Schedule.PolicyRevision
            || q.CursorMs < 0 || q.CursorMs > _profile.DurationMs || d.People is not { Length: > 0 and <= GeneratedDistanceLimits.People }
            || d.Schedule.Segments is not { Length: > 0 and <= 8 } || d.Schedule.Segments.Any(s => s is null)
            || d.Manifest.Sources is not { Length: > 0 and <= 16 }) return false;
        var segment = d.Schedule.Segments.FirstOrDefault(s => q.CursorMs >= s.CursorStartMs && q.CursorMs < s.CursorEndMs)
            ?? (q.CursorMs == d.Schedule.Segments[^1].CursorEndMs ? d.Schedule.Segments[^1] : null);
        return segment is not null && view.PeriodId == segment.PeriodId
            && view.LocalMs == segment.LocalStartMs + q.CursorMs - segment.CursorStartMs
            && view.Phase == segment.Phase && view.PausedDisplayMs == segment.PausedDisplayMs && Fingerprint(d) == _datasetHash;
    }
    public static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, FootballCodec.Json)));
    public static void ValidateMetadata(GeneratedDistancePackMetadata metadata)
    {
        Require(metadata is not null && metadata.Version == 1 && DatasetIdentity.IsValid(metadata.Id)
            && Hash(metadata.SourceManifestSha256) && Hash(metadata.SourceChunksSha256) && Hash(metadata.GeneratorSha256)
            && metadata.ContinuityMetresPerSecond == GeneratedDistanceLimits.ContinuityMetresPerSecond
            && metadata.FrameCount is > 0 and <= GeneratedDistanceLimits.Frames && metadata.ExcludedJumps >= 0, "Invalid generated distance metadata.");
        var d = metadata.Dataset;
        Require(d?.Manifest is not null, "Missing generated distance dataset.");
        var profile = GeneratedDistanceProfile.ForDataset(d.Manifest.DatasetId);
        var liverpool = profile == GeneratedDistanceProfile.LiverpoolCardsV1;
        Require(metadata.CadenceMs == profile.CadenceMs && metadata.FrameCount <= profile.Frames, "Invalid source-specific distance bounds.");
        Require(d is { Closed: true, PendingRecords: 0, HeadSequence: > 0, Manifest.ManifestRevision: > 0, Stream.DeliveryMode: "replay", Fixture: not null, Schedule: not null }
            && d.Manifest.SchemaVersion == FootballContract.Version
            && DatasetIdentity.IsValid(d.Manifest.DatasetVersion) && (liverpool ? Fingerprint(d) is LiverpoolDatasetSha256 or LiverpoolActionsDatasetSha256 : d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal))
            && d.Stream.DatasetId == d.Manifest.DatasetId && d.Stream.DatasetVersion == d.Manifest.DatasetVersion && d.Stream.FixtureId == d.Manifest.FixtureId
            && new[] { d.Manifest.FixtureId, d.Manifest.DatasetVersion, d.Stream.StreamId, d.Schedule.PolicyId }.All(DatasetIdentity.IsValid)
            && d.People is { Length: > 0 and <= GeneratedDistanceLimits.People } && d.People.All(p => p is not null && DatasetIdentity.IsValid(p.PersonId))
            && d.People.Select(p => p.PersonId).Distinct(StringComparer.Ordinal).Count() == d.People.Length
            && d.Manifest.Sources is { Length: > 0 and <= 16 } && d.Manifest.Sources.All(s => s is not null && Hash(s.ArtifactHash))
            && d.Fixture.Periods is { Length: > 0 and <= 8 } && d.Fixture.Periods.All(p => p is not null && DatasetIdentity.IsValid(p.Id))
            && d.Fixture.Pitch is { Basis: "top-left-metres-x-right-y-down", LengthM: > 0 and <= 150, WidthM: > 0 and <= 100 }
            && d.Schedule.Segments is { Length: > 0 and <= 8 } && d.Schedule.PolicyRevision > 0, "Invalid generated distance dataset binding.");
        long end = 0;
        foreach (var segment in d.Schedule.Segments)
        {
            Require(segment is not null && segment.CursorStartMs == end && segment.CursorEndMs > end
                && segment.CursorEndMs <= profile.DurationMs && segment.LocalStartMs >= 0
                && d.Fixture.Periods.Any(p => p.Id == segment.PeriodId), "Invalid generated distance schedule.");
            end = segment.CursorEndMs;
        }
        Require(end / metadata.CadenceMs + 1 == metadata.FrameCount, "Generated distance coverage mismatch.");
    }
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
