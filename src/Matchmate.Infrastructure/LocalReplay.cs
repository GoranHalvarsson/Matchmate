using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public static class ReplayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
}
public static class LocalPaths
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))
                && Directory.Exists(Path.Combine(directory.FullName, "prototypes"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Run from the Matchmate workspace or configure LocalReplay paths.");
    }
    public static string Store => Path.Combine(FindRoot(), ".local", "replay");
    public static string Source => Path.Combine(FindRoot(), "prototypes", "first-experience", "assets", "tracking");
}

public sealed class SkillCornerFileSource(string directory) : IReplaySource, IObservationSource
{
    public const string SourceHash = "291fa78ef599c34dac6ee04bb8c140fe802882fb6ccca6baa95d8eac69258cd6";
    public const int MatchId = 1874553;
    public async Task<ObservationStream> OpenAsync(CancellationToken cancellationToken)
    {
        var source = await ReadAsync(cancellationToken);
        return new(source with { Document = source.Document with { Frames = [] } }, Enumerate(source.Document.Frames, cancellationToken));
    }
    private static async IAsyncEnumerable<RecordedFrame> Enumerate(RecordedFrame[] frames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        foreach (var frame in frames) { token.ThrowIfCancellationRequested(); yield return frame; }
    }
    public async Task<SourceRecording> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, "1874553-60-240.json"), 20_000_000, cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != SourceHash) throw new RecordingValidationException("Pinned tracking artifact hash mismatch; import withheld.");
        var manifest = await BoundedFile.ReadAsync(Path.Combine(directory, "manifest.json"), 64_000, cancellationToken);
        using var receipt = JsonDocument.Parse(manifest);
        if (!receipt.RootElement.GetProperty("artifacts").EnumerateArray().Any(a =>
            a.GetProperty("file").GetString() == "1874553-60-240.json"
            && a.GetProperty("sha256").GetString() == hash && a.GetProperty("bytes").GetInt64() == bytes.Length))
            throw new RecordingValidationException("Source manifest does not match the pinned artifact.");
        var licenseBytes = await BoundedFile.ReadAsync(Path.Combine(directory, "SKILLCORNER-LICENSE.txt"), 16_000, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(licenseBytes)) != "3384cc66ffd008209759af5e6328677eabded72756fb1f56f4f82e68e9b3c6eb")
            throw new RecordingValidationException("Source licence hash mismatch.");
        var document = JsonSerializer.Deserialize<SourceDocument>(bytes, ReplayJson.Options)
            ?? throw new RecordingValidationException("Missing source document.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true))
            await gzip.WriteAsync(bytes, cancellationToken);
        return new(document, hash, "real", System.Text.Encoding.UTF8.GetString(licenseBytes),
            "https://github.com/SkillCorner/opendata/tree/4340d274572876239c154c90bc507a9b3250a656", bytes.Length, compressed.Length);
    }
}

public static class BoundedFile
{
    public static async Task<byte[]> ReadAsync(string path, int cap, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > cap) throw new RecordingValidationException("Replay file exceeds its size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > cap) throw new RecordingValidationException("Replay file exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}

public sealed class AtomicReplayStore(string directory) : IReplayStore
{
    private sealed record Pointer(string Hash);
    private readonly SemaphoreSlim _readerLock = new(1, 1);
    private string? _cachedHash;
    private ReplaySnapshot? _cached;

    public async Task PublishAsync(ReplaySnapshot snapshot, CancellationToken cancellationToken)
    {
        Validate(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, ReplayJson.Options);
        if (bytes.Length > 20_000_000) throw new RecordingValidationException("Snapshot exceeds local store cap.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        // The immutable payload is durable before its pointer is published. Never modify a live payload.
        await AtomicWrite(Path.Combine(directory, hash + ".json"), bytes, cancellationToken);
        await AtomicWrite(Path.Combine(directory, "latest.json"),
            JsonSerializer.SerializeToUtf8Bytes(new Pointer(hash), ReplayJson.Options), cancellationToken);
    }

    public async Task<ReplaySnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        await _readerLock.WaitAsync(cancellationToken);
        try
        {
            var pointerPath = Path.Combine(directory, "latest.json");
            if (!File.Exists(pointerPath)) return null;
            var pointer = JsonSerializer.Deserialize<Pointer>(await BoundedFile.ReadAsync(pointerPath, 4096, cancellationToken), ReplayJson.Options);
            if (pointer?.Hash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
                throw new RecordingValidationException("Invalid publication pointer.");
            if (_cachedHash == hash) return _cached;
            var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, hash + ".json"), 20_000_000, cancellationToken);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
                throw new RecordingValidationException("Snapshot integrity check failed.");
            var snapshot = JsonSerializer.Deserialize<ReplaySnapshot>(bytes, ReplayJson.Options)
                ?? throw new RecordingValidationException("Missing published snapshot.");
            Validate(snapshot);
            _cached = snapshot; _cachedHash = hash;
            return snapshot;
        }
        finally { _readerLock.Release(); }
    }

    public static void Validate(ReplaySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Metadata is null || snapshot.Frames is null)
            throw new RecordingValidationException("Snapshot metadata or frames are missing.");
        var m = snapshot.Metadata;
        if (snapshot.ContractVersion != 1 || m.SourceHash is not { Length: 64 } || !m.SourceHash.All(Uri.IsHexDigit)
            || m.Version != $"local-replay-v1-{m.SourceHash}" || m.Gaps is null)
            throw new RecordingValidationException("Unsupported local replay snapshot/version.");
        var rebuilt = new ReplayProcessor().Process(new(new SourceDocument(1, m.Source, m.Pin, m.MatchId,
            "Recorded tracking replay", "Retrospective", m.Competition, m.DateTime, m.Home, m.Away, m.Pitch,
            m.Period, m.Start, m.End, m.Cadence, m.Players, snapshot.Frames, "Events deferred", m.Limits),
            m.SourceHash, m.Origin, m.License, m.SourceUrl, m.SourceBytes, m.GzipBytes));
        if (m.FrameCount != snapshot.Frames.Length || m.EmptyFrames != rebuilt.Metadata.EmptyFrames
            || !m.Gaps.SequenceEqual(rebuilt.Metadata.Gaps))
            throw new RecordingValidationException("Published coverage is inconsistent.");
    }

    private static async Task AtomicWrite(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Replaying the same publication must not replace an unchanged file that readers may hold.
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length
            && (await BoundedFile.ReadAsync(path, bytes.Length, cancellationToken)).AsSpan().SequenceEqual(bytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
