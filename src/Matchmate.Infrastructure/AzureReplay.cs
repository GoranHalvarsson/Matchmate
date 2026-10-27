using System.Net;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Matchmate.Core;
using Microsoft.Azure.Cosmos;

namespace Matchmate.Infrastructure;

public sealed record AzureReplaySettings(string BlobEndpoint, string CosmosEndpoint, string Database, string Container, string ManagedIdentityClientId)
{
    public void Validate()
    {
        if (!Uri.TryCreate(BlobEndpoint, UriKind.Absolute, out var blob) || blob.Scheme != "https" || !string.IsNullOrEmpty(blob.Query)
            || !Uri.TryCreate(CosmosEndpoint, UriKind.Absolute, out var cosmos) || cosmos.Scheme != "https" || !string.IsNullOrEmpty(cosmos.Query)
            || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Container) || !Guid.TryParse(ManagedIdentityClientId, out _))
            throw new InvalidOperationException("Hosted replay requires explicit HTTPS Blob/Cosmos endpoints, database/container and runtime identity.");
    }
}
public sealed class AzureReplayResources : IDisposable
{
    public TokenCredential Credential { get; }
    public BlobServiceClient Blobs { get; }
    public CosmosClient Cosmos { get; }
    public IReplayBlobStore Recordings { get; }
    public IReplayCatalog Catalog { get; }
    public AzureReplayResources(AzureReplaySettings settings)
    {
        settings.Validate();
        Credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
        Blobs = new BlobServiceClient(new Uri(settings.BlobEndpoint), Credential);
        Cosmos = new CosmosClient(settings.CosmosEndpoint, Credential, new CosmosClientOptions
        {
            ApplicationName = "Matchmate.Replay", ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }
        });
        Recordings = new AzureReplayBlobs(Blobs.GetBlobContainerClient("recordings"));
        Catalog = new CosmosReplayCatalog(Cosmos.GetContainer(settings.Database, settings.Container));
    }
    public void Dispose() => Cosmos.Dispose();
}
public sealed class AzureReplayBlobs(BlobContainerClient container) : IReplayBlobStore
{
    public async Task PutImmutableAsync(string path, byte[] bytes, CancellationToken token)
    {
        var blob = container.GetBlobClient(path);
        try
        {
            await blob.UploadAsync(new BinaryData(bytes), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, token);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var stored = await ReadAsync(path, Math.Max(bytes.Length, 1), token);
            if (stored is null || !stored.AsSpan().SequenceEqual(bytes)) throw new RecordingValidationException("Immutable replay blob content conflict.");
        }
    }
    public async Task<byte[]?> ReadAsync(string path, int cap, CancellationToken token)
    {
        try
        {
            var download = await container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: token);
            using var stream = download.Value.Content;
            if (download.Value.Details.ContentLength > cap) throw new RecordingValidationException("Blob exceeds requested cap.");
            return await ReplayBatchCodec.ReadBoundedAsync(stream, cap, token);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
public sealed class CosmosReplayCatalog(Container container) : IReplayCatalog
{
    public async Task<VersionedReplayPointer?> ReadAsync(string datasetId, CancellationToken token)
    {
        try
        {
            var response = await container.ReadItemAsync<ReplayPointer>(datasetId, new PartitionKey(datasetId), cancellationToken: token);
            return new(response.Resource, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<IReadOnlyList<ReplayPointer>> ListAsync(CancellationToken token)
    {
        using var query = container.GetItemQueryIterator<ReplayPointer>("SELECT TOP 32 * FROM c", requestOptions: new QueryRequestOptions { MaxItemCount = 32 });
        var result = new List<ReplayPointer>();
        while (query.HasMoreResults && result.Count < 32) result.AddRange(await query.ReadNextAsync(token));
        return result;
    }
    public async Task<bool> CompareExchangeAsync(ReplayPointer candidate, string? expectedETag, CancellationToken token)
    {
        try
        {
            if (expectedETag is null) await container.CreateItemAsync(candidate, new PartitionKey(candidate.DatasetId), cancellationToken: token);
            else await container.ReplaceItemAsync(candidate, candidate.DatasetId, new PartitionKey(candidate.DatasetId),
                new ItemRequestOptions { IfMatchEtag = expectedETag }, token);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) { return false; }
    }
}

public static class PartitionCheckpoint
{
    // Hold this partition's callback on every failure. Returning and checkpointing a later event would lose work.
    public static async Task ProcessAsync(Func<CancellationToken, Task> process, Func<CancellationToken, Task> checkpoint,
        Action<Exception> failed, CancellationToken token, TimeSpan? retryDelay = null)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { await process(token); await checkpoint(token); return; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { failed(exception); await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(5), token); }
        }
    }
}
