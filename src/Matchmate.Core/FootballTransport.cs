using System.Buffers.Binary;
using System.IO.Compression;
using Matchmate.Contracts;

namespace Matchmate.Core;

public sealed record FootballTransportBytes(byte[] Bytes, string ContentType);
public static class FootballTransport
{
    public const string JsonType = "application/vnd.matchmate.football-batch+json;version=0.4.0-draft.1";
    public const string GzipType = JsonType + ";encoding=gzip";
    public static FootballTransportBytes Encode(FootballBatch batch)
    {
        var raw = FootballCodec.Encode(batch); var compressed = ReplayBatchCodec.Compress(raw);
        return compressed.Length < raw.Length ? new(compressed, GzipType) : new(raw, JsonType);
    }
    public static FootballBatch Decode(byte[] bytes, string? contentType)
    {
        if (bytes.Length is < 1 or > FootballCodec.MaxBatchBytes) throw new RecordingValidationException("Compressed transport cap exceeded.");
        return contentType switch
        {
            JsonType => FootballCodec.Decode(bytes),
            GzipType => FootballCodec.Decode(Expand(bytes, FootballCodec.MaxBatchBytes)),
            _ => throw new RecordingValidationException("Unknown football transport format/version.")
        };
    }
    public static byte[] Expand(byte[] bytes, int cap)
    {
        if (bytes.Length < 18 || bytes[0] != 31 || bytes[1] != 139 || bytes[2] != 8) throw new RecordingValidationException("Invalid gzip member.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4));
        if (length > cap) throw new RecordingValidationException("Gzip expanded-size cap exceeded.");
        try
        {
            using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = gzip.Read(buffer)) > 0) { if (output.Length + read > cap) throw new RecordingValidationException("Gzip expansion exceeds cap."); output.Write(buffer, 0, read); }
            if (output.Length != length) throw new RecordingValidationException("Truncated/concatenated gzip member.");
            return output.ToArray();
        }
        catch (InvalidDataException) { throw new RecordingValidationException("Corrupt gzip member."); }
    }
}
