using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Messaging.EventHubs.Consumer;
using System.Collections.Concurrent;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class PipelineState
{
    public volatile bool Published;
    public readonly ConcurrentDictionary<string, byte> BlockedPartitions = new();
    public volatile bool SdkFailed;
}
public sealed class PipelineHealth(PipelineState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.SdkFailed || !state.BlockedPartitions.IsEmpty ? HealthCheckResult.Unhealthy("Processing blocked; the partition is held without advancing its checkpoint.")
            : state.Published ? HealthCheckResult.Healthy("Open or closed canonical stream durably queryable.")
            : HealthCheckResult.Unhealthy("Consumer awaiting a durable canonical batch."));
}
public sealed class AzureObservationWorker(AzureFootballResources resources, IFootballStore processing,
    IConfiguration configuration, PipelineState state, ILogger<AzureObservationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = configuration["Pipeline:EventHubHost"] ?? throw new InvalidOperationException("Pipeline:EventHubHost is required.");
        var hub = configuration["Pipeline:EventHubName"] ?? throw new InvalidOperationException("Pipeline:EventHubName is required.");
        var group = configuration["Pipeline:ConsumerGroup"] ?? throw new InvalidOperationException("Pipeline:ConsumerGroup is required.");
        // Checkpoints may already be at the head after a restart. Durable publication remains ready without another event.
        try
        {
            if (processing is IPreparedFootballStore prepared) await prepared.PrepareReadViewsAsync(stoppingToken);
            state.Published = (await processing.CatalogAsync(stoppingToken)).Length > 0;
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        { logger.LogWarning("Initial publication readiness lookup failed with {FailureType}; consumer recovery will continue.", exception.GetType().Name); }
        var consumer = new EventProcessorClient(resources.Blobs.GetBlobContainerClient("eventhub-checkpoints"), group,
            host, hub, resources.Credential);
        consumer.PartitionInitializingAsync += args => { args.DefaultStartingPosition = EventPosition.Earliest; return Task.CompletedTask; };
        consumer.ProcessEventAsync += async args =>
        {
            if (!args.HasEvent) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, args.CancellationToken);
            await PartitionCheckpoint.ProcessAsync(async token =>
            {
                var batch = FootballTransport.Decode(args.Data.EventBody.ToArray(), args.Data.ContentType);
                if (args.Data.PartitionKey != batch.Stream.DatasetId) throw new RecordingValidationException("Event partition key does not match the bound dataset.");
                var changed = await processing.AppendAsync(batch, token);
                state.BlockedPartitions.TryRemove(args.Partition.PartitionId, out _); state.SdkFailed = false;
                state.Published = true;
                logger.LogInformation("Canonical replay {Dataset}/{Stream} batch {Sequence} committed, changed {Changed}, digest {Digest}. Recorded replay, not live.",
                    batch.Stream.DatasetId, batch.Stream.StreamId, batch.Sequence, changed, batch.Digest);
            }, token => args.UpdateCheckpointAsync(token), exception =>
            {
                state.BlockedPartitions[args.Partition.PartitionId] = 0;
                logger.LogError("Replay partition {Partition} held before checkpoint; failure {FailureType}. Retrying same event.",
                    args.Partition.PartitionId, exception.GetType().Name);
            }, cancellation.Token);
        };
        consumer.ProcessErrorAsync += args =>
        {
            state.SdkFailed = true;
            logger.LogError("Event Hubs operation {Operation} failed with {FailureType}; checkpoint recovery remains active.", args.Operation, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        await consumer.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await consumer.StopProcessingAsync(); }
    }
}
