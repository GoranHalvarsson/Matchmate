using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

// Same paged processor as Azure, with atomic local roots/chunks. Separate namespace preserves old sparse journals.
public sealed class ChunkedLocalFootballStore : IFootballStore, IPreparedFootballStore
{
    public AzureFootballStore Journal { get; }
    public ChunkedLocalFootballStore(string directory) => Journal = new(new FileFootballBlobs(Path.Combine(directory, "chunks")), new FileFootballIndexes(Path.Combine(directory, "indexes")));
    public Task<FootballReadPreparation> PrepareReadViewsAsync(CancellationToken token) => Journal.PrepareReadViewsAsync(token);
    public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => Journal.AppendAsync(batch, token);
    public Task<FootballLedger> ReadAsync(CancellationToken token) => Journal.ReadAsync(token);
    public Task<FootballDataset[]> CatalogAsync(CancellationToken token) => Journal.CatalogAsync(token);
    public Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token) => Journal.QueryAsync(query, token);
}
public sealed class FileFootballBlobs(string directory) : IReplayBlobStore
{
    private string Resolve(string path)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new RecordingValidationException("Invalid chunk path."); return target;
    }
    public async Task<byte[]?> ReadAsync(string path, int cap, CancellationToken token)
    {
        var target = Resolve(path); return File.Exists(target) ? await BoundedFile.ReadAsync(target, cap, token) : null;
    }
    public async Task PutImmutableAsync(string path, byte[] bytes, CancellationToken token)
    {
        var target = Resolve(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) { if (!(await BoundedFile.ReadAsync(target, bytes.Length + 1, token)).SequenceEqual(bytes)) throw new RecordingValidationException("Immutable file content conflict."); return; }
        try { await FootballAtomicFile.WriteAsync(target, bytes, false, token); }
        catch (IOException) when (File.Exists(target)) { if (!(await BoundedFile.ReadAsync(target, bytes.Length + 1, token)).SequenceEqual(bytes)) throw new RecordingValidationException("Immutable file content conflict."); }
    }
}
public sealed class FileFootballIndexes(string directory) : IFootballIndexStore
{
    private string PathFor(string dataset)
    {
        if (!DatasetIdentity.IsValid(dataset)) throw new RecordingValidationException("Invalid dataset identity."); return Path.Combine(directory, AzureFootballStore.IndexId(dataset) + ".json");
    }
    public async Task<VersionedFootballIndex?> ReadAsync(string datasetId, CancellationToken token)
    {
        var path = PathFor(datasetId); if (!File.Exists(path)) return null;
        var bytes = await BoundedFile.ReadAsync(path, AzureFootballStore.MaxIndexBytes, token);
        try
        {
            var index = JsonSerializer.Deserialize<FootballDatasetIndex>(bytes, FootballCodec.Json) ?? throw new RecordingValidationException("Missing local index.");
            var result = new VersionedFootballIndex(index, ReplayBatchCodec.Hash(bytes)); AzureFootballStore.ValidateIndex(result);
            if (index.DatasetId != datasetId) throw new RecordingValidationException("Local root identity mismatch."); return result;
        }
        catch (JsonException) { throw new RecordingValidationException("Corrupt local root."); }
    }
    public async Task<IReadOnlyList<VersionedFootballIndex>> ListAsync(CancellationToken token)
    {
        if (!Directory.Exists(directory)) return [];
        var files = Directory.GetFiles(directory, "football-paged-*.json");
        if (files.Length > AzureFootballStore.MaxDatasets) throw new RecordingValidationException("Local dataset cap exceeded.");
        var result = new List<VersionedFootballIndex>();
        foreach (var file in files) result.Add((await ReadAsync(Path.GetFileNameWithoutExtension(file)["football-paged-".Length..], token))!); return result;
    }
    public async Task<bool> CompareExchangeAsync(FootballDatasetIndex candidate, string? expectedETag, CancellationToken token)
    {
        AzureFootballStore.ValidateIndex(new(candidate, "candidate")); Directory.CreateDirectory(directory);
        await using var writer = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = await ReadAsync(candidate.DatasetId, token); if (current?.ETag != expectedETag) return false;
        await FootballAtomicFile.WriteAsync(PathFor(candidate.DatasetId), JsonSerializer.SerializeToUtf8Bytes(candidate, FootballCodec.Json), true, token); return true;
    }
}
internal static class FootballAtomicFile
{
    public static async Task WriteAsync(string destination, byte[] bytes, bool overwrite, CancellationToken token)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
