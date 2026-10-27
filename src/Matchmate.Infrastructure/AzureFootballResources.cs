using System.Net;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Matchmate.Core;
using Microsoft.Azure.Cosmos;

namespace Matchmate.Infrastructure;

public sealed class AzureFootballResources : IDisposable
{
    public TokenCredential Credential { get; }
    public BlobServiceClient Blobs { get; }
    public IFootballIndexStore Indexes { get; }
    public IFootballStore Store { get; }
    private readonly CosmosClient _cosmos;
    public AzureFootballResources(AzureReplaySettings settings) : this(Validated(settings), settings.CosmosEndpoint, settings.Database, settings.Container,
        new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId))) { }
    public AzureFootballResources(string blobEndpoint, string cosmosEndpoint, string database, string container, TokenCredential credential)
    {
        // Operator callers supply AzureCliCredential explicitly; hosted callers use only their runtime UAMI above.
        new AzureReplaySettings(blobEndpoint, cosmosEndpoint, database, container, Guid.Empty.ToString()).Validate();
        Credential = credential; Blobs = new(new Uri(blobEndpoint), credential);
        _cosmos = new(cosmosEndpoint, credential, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, ApplicationName = "Matchmate.CommonFootball",
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase } });
        Indexes = new CosmosFootballIndexes(_cosmos.GetContainer(database, container));
        Store = new AzureFootballStore(new AzureReplayBlobs(Blobs.GetBlobContainerClient("recordings")), Indexes);
    }
    private static string Validated(AzureReplaySettings settings) { settings.Validate(); return settings.BlobEndpoint; }
    public void Dispose() => _cosmos.Dispose();
}
public sealed class CosmosFootballIndexes(Container container) : IFootballIndexStore
{
    public async Task<VersionedFootballIndex?> ReadAsync(string datasetId, CancellationToken token)
    {
        try
        {
            var response = await container.ReadItemAsync<FootballDatasetIndex>(AzureFootballStore.IndexId(datasetId), new PartitionKey(datasetId), cancellationToken: token);
            var result = new VersionedFootballIndex(response.Resource, response.ETag); AzureFootballStore.ValidateIndex(result);
            if (result.Value.DatasetId != datasetId) throw new RecordingValidationException("Cloud dataset point-read identity mismatch.");
            return result;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<IReadOnlyList<VersionedFootballIndex>> ListAsync(CancellationToken token)
    {
        var query = new QueryDefinition($"SELECT TOP {AzureFootballStore.MaxDatasets + 1} VALUE {{\"value\": c, \"eTag\": c._etag}} FROM c WHERE c.discriminator = @kind")
            .WithParameter("@kind", AzureFootballStore.Discriminator);
        using var iterator = container.GetItemQueryIterator<VersionedFootballIndex>(query, requestOptions: new QueryRequestOptions { MaxItemCount = AzureFootballStore.MaxDatasets + 1 });
        var result = new List<VersionedFootballIndex>();
        while (iterator.HasMoreResults && result.Count <= AzureFootballStore.MaxDatasets) result.AddRange(await iterator.ReadNextAsync(token));
        if (result.Count > AzureFootballStore.MaxDatasets) throw new RecordingValidationException("Cloud dataset count exceeds safe cap.");
        return result;
    }
    public async Task<bool> CompareExchangeAsync(FootballDatasetIndex candidate, string? expectedETag, CancellationToken token)
    {
        AzureFootballStore.ValidateIndex(new(candidate, "candidate"));
        try
        {
            if (expectedETag is null) await container.CreateItemAsync(candidate, new PartitionKey(candidate.DatasetId), cancellationToken: token);
            else await container.ReplaceItemAsync(candidate, candidate.Id, new PartitionKey(candidate.DatasetId), new ItemRequestOptions { IfMatchEtag = expectedETag }, token);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) { return false; }
    }
}
