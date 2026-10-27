using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;

// Local derivation only: reads the pinned existing export; no ingestion, network or journal writes.
if (args.Length != 4 || args[0] != "--manifest" || args[2] != "--output")
{ Console.Error.WriteLine("Usage: Matchmate.GeneratedDistance --manifest <pinned Chelsea full-v5 or Liverpool dataset-v2 manifest.json> --output <distance-v1.gz>"); return 2; }
var manifestPath = Path.GetFullPath(args[1]);
var sourceBytes = await File.ReadAllBytesAsync(manifestPath);
var sourceHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
var liverpool = sourceHash == LiverpoolFootballSource.ManifestHash;
if (!liverpool && sourceHash != "ce79cf8f307fbc596396a78edcf3e9c9f8fb41798a0b73ad06d7e4cd2465e6de")
    throw new InvalidDataException("Only the two reviewed, pinned generated exports are supported.");
var profile = liverpool ? GeneratedDistanceProfile.LiverpoolCardsV1 : GeneratedDistanceProfile.ChelseaFullV5;
using var export = JsonDocument.Parse(sourceBytes);
var chunksHash = GeneratedDistanceProjection.Fingerprint(export.RootElement.GetProperty("chunks"));
var root = LocalPaths.FindRoot();
var generatorSources = new List<(string Path, string Text)>();
foreach (var path in GeneratedDistanceSourceFingerprint.InputPaths)
    generatorSources.Add((path, await File.ReadAllTextAsync(Path.Combine(root, path))));
var generatorHash = GeneratedDistanceSourceFingerprint.Compute(generatorSources);
FootballManifest? manifest = null; FootballStream? stream = null; FixturePayload? fixture = null; SquadPerson[]? people = null;
SchedulePayload? schedule = null; GeneratedDistanceCalculator? calculator = null; long head = 0; var closed = false;
IStreamingFootballSource source = liverpool ? new LiverpoolFootballSource(manifestPath) : new FullBrentfordFootballSource(manifestPath);
await foreach (var batch in source.ReadBatchesAsync(default))
{
    manifest ??= batch.Manifest; stream ??= batch.Stream;
    if (batch.Sequence != ++head || closed || batch.Manifest != manifest && GeneratedDistanceProjection.Fingerprint(batch.Manifest) != GeneratedDistanceProjection.Fingerprint(manifest))
        throw new InvalidDataException("Inconsistent canonical export stream.");
    closed = batch.CloseStream;
    foreach (var record in batch.Records)
    {
        if (record.Payload is FixturePayload f) fixture = f;
        if (record.Payload is SquadPayload s) people = s.People;
        if (record.Payload is SchedulePayload p) schedule = p;
        if (record.Payload is not TrackingPayload) continue;
        calculator ??= new(fixture?.Pitch ?? throw new InvalidDataException("Missing canonical pitch."),
            people ?? throw new InvalidDataException("Missing canonical roster."), schedule ?? throw new InvalidDataException("Missing canonical schedule."), profile);
        calculator.Append(record);
    }
}
if (manifest is null || stream is null || fixture is null || people is null || schedule is null || calculator is null || !closed
    || head != (liverpool ? 528 : 979) || manifest.ManifestRevision != (liverpool ? 5 : 6) || calculator.FrameCount != (liverpool ? 32701 : 58801))
    throw new InvalidDataException($"Incomplete pinned canonical coverage: head={head}, revision={manifest?.ManifestRevision}.");
var dataset = new FootballDataset(manifest, stream, head, closed, fixture, people, schedule, 0);
var metadata = new GeneratedDistancePackMetadata(1, liverpool ? "liverpool-generated-replay-distance-v1" : "generated-replay-distance-v1", dataset, sourceHash, chunksHash,
    generatorHash, profile.CadenceMs,
    GeneratedDistanceLimits.ContinuityMetresPerSecond, calculator.FrameCount, calculator.ExcludedJumps);
var prefixes = calculator.Snapshot(); var bytes = PreparedGeneratedDistanceStore.Encode(metadata, prefixes);
_ = new PreparedGeneratedDistanceStore(bytes);
var outputPath = Path.GetFullPath(args[3]); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); await File.WriteAllBytesAsync(outputPath, bytes);
using var compressed = new MemoryStream(bytes); using var gzip = new GZipStream(compressed, CompressionMode.Decompress); using var expanded = new MemoryStream(); gzip.CopyTo(expanded);
Console.WriteLine(JsonSerializer.Serialize(new { sourceHash, chunksHash, metadata.GeneratorSha256, dataset.Manifest.DatasetVersion,
    dataset.Stream.StreamId, head, metadata.FrameCount, metadata.ExcludedJumps, compressedBytes = bytes.Length, expandedBytes = expanded.Length,
    packSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), datasetSha256 = GeneratedDistanceProjection.Fingerprint(dataset),
    cadenceMs = profile.CadenceMs, endCursorMs = schedule.Segments[^1].CursorEndMs,
    prefixSha256 = GeneratedDistanceProjection.Fingerprint(people.Select(p => new { p.PersonId, Samples = prefixes[p.PersonId] }).ToArray()),
    players = people.Select(p => new { p.PersonId, p.Name, samples = prefixes[p.PersonId].Length,
        cumulativeCentimetres = GeneratedDistanceProjection.Prefix(prefixes[p.PersonId], schedule.Segments[^1].CursorEndMs) }) }, FootballCodec.Json));
return 0;
