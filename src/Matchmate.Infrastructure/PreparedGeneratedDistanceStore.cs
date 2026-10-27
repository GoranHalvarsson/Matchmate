using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public sealed class PreparedGeneratedDistanceStore
{
    private static readonly byte[] Magic = "MMGD0001"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24 };
    private readonly GeneratedDistanceProjection[] _projections;
    private PreparedGeneratedDistanceStore(GeneratedDistanceProjection[] projections) => _projections = projections;
    public PreparedGeneratedDistanceStore(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length is < 1 or > GeneratedDistanceLimits.CompressedBytes) throw new InvalidDataException("Invalid generated distance compressed size.");
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = gzip.Read(buffer)) != 0)
            {
                if (expanded.Length + count > GeneratedDistanceLimits.ExpandedBytes) throw new InvalidDataException("Generated distance expansion exceeds limit.");
                expanded.Write(buffer, 0, count);
            }
            expanded.Position = 0;
            using var reader = new BinaryReader(expanded, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Invalid generated distance pack version.");
            var headerLength = reader.ReadInt32();
            if (headerLength is < 1 or > GeneratedDistanceLimits.HeaderBytes || headerLength > expanded.Length - expanded.Position)
                throw new InvalidDataException("Invalid generated distance header size.");
            var metadata = JsonSerializer.Deserialize<GeneratedDistancePackMetadata>(reader.ReadBytes(headerLength), Json)
                ?? throw new InvalidDataException("Missing generated distance metadata.");
            GeneratedDistanceProjection.ValidateMetadata(metadata);
            var players = new Dictionary<string, GeneratedDistanceSample[]>(StringComparer.Ordinal); var total = 0;
            foreach (var person in metadata.Dataset.People)
            {
                var length = reader.ReadInt32();
                if (length < 0 || length > metadata.FrameCount || total + length > GeneratedDistanceLimits.Samples
                    || (long)length * 2 > expanded.Length - expanded.Position) throw new InvalidDataException("Invalid generated distance sample count.");
                total += length; var samples = new GeneratedDistanceSample[length];
                var cursorUnits = 0; var centimetres = 0;
                for (var i = 0; i < length; i++)
                {
                    var timeDelta = reader.Read7BitEncodedInt(); var distanceDelta = reader.Read7BitEncodedInt();
                    if (timeDelta < 0 || distanceDelta < 0) throw new InvalidDataException("Negative generated distance delta.");
                    cursorUnits = checked(cursorUnits + timeDelta); centimetres = checked(centimetres + distanceDelta);
                    samples[i] = new(checked(cursorUnits * metadata.CadenceMs), centimetres);
                }
                players.Add(person.PersonId, samples);
            }
            if (expanded.Position != expanded.Length) throw new InvalidDataException("Unexpected generated distance pack tail.");
            _projections = [new(metadata, players, Convert.ToHexStringLower(SHA256.HashData(compressed)))];
        }
        catch (Exception e) when (e is JsonException or EndOfStreamException or ArgumentException or OverflowException or FormatException)
        { throw new InvalidDataException("Invalid generated distance pack.", e); }
    }
    public static PreparedGeneratedDistanceStore LoadEmbedded()
    {
        var projections = new List<GeneratedDistanceProjection>();
        foreach (var name in new[] { "Matchmate.GeneratedDistance.v1.gz", "Matchmate.GeneratedDistance.Liverpool.v1.gz" })
        {
            using var stream = typeof(PreparedGeneratedDistanceStore).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Missing embedded generated distance pack.");
            if (stream.Length is < 1 or > GeneratedDistanceLimits.CompressedBytes) throw new InvalidDataException("Invalid embedded generated distance size.");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            projections.Add(new PreparedGeneratedDistanceStore(bytes)._projections[0]);
        }
        return new(projections.ToArray());
    }
    public FootballView Attach(FootballView view)
    {
        foreach (var projection in _projections)
        {
            var attached = projection.Attach(view);
            if (attached.GeneratedDistance is not null) return attached;
        }
        return view with { GeneratedDistance = null };
    }
    // Reproducible offline writer, also used by bounded synthetic unit fixtures. Never invoked by a host.
    public static byte[] Encode(GeneratedDistancePackMetadata metadata, IReadOnlyDictionary<string, GeneratedDistanceSample[]> players)
    {
        _ = new GeneratedDistanceProjection(metadata, players, new string('0', 64));
        var header = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        if (header.Length > GeneratedDistanceLimits.HeaderBytes) throw new InvalidDataException("Generated distance header exceeds limit.");
        using var expanded = new MemoryStream();
        using (var writer = new BinaryWriter(expanded, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic); writer.Write(header.Length); writer.Write(header);
            foreach (var person in metadata.Dataset.People)
            {
                var samples = players[person.PersonId]; writer.Write(samples.Length);
                var previousUnits = 0; var previousCentimetres = 0;
                foreach (var sample in samples)
                {
                    var units = sample.CursorMs / metadata.CadenceMs;
                    writer.Write7BitEncodedInt(units - previousUnits); writer.Write7BitEncodedInt(sample.CumulativeCentimetres - previousCentimetres);
                    previousUnits = units; previousCentimetres = sample.CumulativeCentimetres;
                }
            }
        }
        if (expanded.Length > GeneratedDistanceLimits.ExpandedBytes) throw new InvalidDataException("Generated distance pack exceeds expanded limit.");
        using var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.SmallestSize, leaveOpen: true)) expanded.WriteTo(gzip);
        if (result.Length > GeneratedDistanceLimits.CompressedBytes) throw new InvalidDataException("Generated distance pack exceeds compressed limit.");
        return result.ToArray();
    }
}
