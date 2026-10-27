using Matchmate.Core;

namespace Matchmate.Infrastructure;

// Fixed immutable reference bundle; failed/cancelled loads do not poison the cache and can be retried.
public sealed class LeagueReferenceStore(Func<CancellationToken, Task<byte[]?>> read) : ILeagueReferenceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LeagueModel? _cached;
    public static LeagueReferenceStore Local(string? root = null) => Versioned(async (version, token) =>
    {
        var path = Path.Combine(root ?? LocalPaths.FindRoot(), ".local", version == LeagueReferenceCodec.CurrentVersion ? "league-reference-v2" : "league-reference-v1", "league-reference.json");
        try { return await BoundedFile.ReadAsync(path, LeagueReferenceCodec.MaxBytes, token); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    });
    // Only genuine absence permits fallback. Invalid content and IO/auth failures remain visible.
    public static LeagueReferenceStore Versioned(Func<string, CancellationToken, Task<byte[]?>> readVersion) => new(async token =>
        await readVersion(LeagueReferenceCodec.CurrentVersion, token) ?? await readVersion(LeagueReferenceCodec.Version, token));
    public static LeagueReferenceStore Hosted(AzureFootballResources resources)
    {
        var blobs = new AzureReplayBlobs(resources.Blobs.GetBlobContainerClient("recordings"));
        return Versioned((version, token) => blobs.ReadAsync(version == LeagueReferenceCodec.CurrentVersion ? LeagueReferenceCodec.CurrentBlobPath : LeagueReferenceCodec.BlobPath, LeagueReferenceCodec.MaxBytes, token));
    }
    public async Task<LeagueModel> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (_cached is not null) return _cached;
        await _gate.WaitAsync(token);
        try
        {
            if (_cached is not null) return _cached;
            var bytes = await read(token) ?? throw new LeagueReferenceValidationException("The league reference bundle is unavailable.");
            var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes)); token.ThrowIfCancellationRequested(); _cached = model; return model;
        }
        finally { _gate.Release(); }
    }
}
