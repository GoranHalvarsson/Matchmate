using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var hosted = RuntimeMode.Validate(builder.Configuration, builder.Environment);
var importEnabled = RuntimeMode.RecordedImportEnabled(builder.Configuration, hosted);
var pipelineEnabled = hosted && (builder.Configuration.GetValue<bool?>("Pipeline:Enabled")
    ?? throw new InvalidOperationException("Hosted Worker requires an explicit Pipeline:Enabled setting."));
if (pipelineEnabled)
{
    var settings = new AzureReplaySettings(builder.Configuration["Pipeline:BlobEndpoint"] ?? "", builder.Configuration["Pipeline:CosmosEndpoint"] ?? "",
        builder.Configuration["Pipeline:Database"] ?? "", builder.Configuration["Pipeline:Container"] ?? "", builder.Configuration["Runtime:ManagedIdentityClientId"] ?? "");
    settings.Validate();
    builder.Services.AddSingleton(new AzureFootballResources(settings));
    builder.Services.AddSingleton<IFootballStore>(services => services.GetRequiredService<AzureFootballResources>().Store);
    builder.Services.AddSingleton<PipelineState>();
    builder.Services.AddHostedService<AzureObservationWorker>();
    builder.Services.AddHealthChecks().AddCheck<PipelineHealth>("azure-replay-pipeline");
}
if (importEnabled)
{
    builder.Services.AddSingleton<IFootballStore>(_ => new ChunkedLocalFootballStore(builder.Configuration["SharedFootball:StoreDirectory"] ?? Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")));
    builder.Services.AddHostedService<SharedFootballImportWorker>();
}
builder.Services.AddSingleton(new ImportState { Disabled = !importEnabled });
if (!pipelineEnabled) builder.Services.AddHealthChecks().AddCheck<ImportHealth>("recorded-import");
var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

public sealed class ImportState { public volatile bool Ready; public volatile bool Failed; public bool Disabled; }
public sealed class SharedFootballImportWorker(IFootballStore store, IConfiguration configuration, ImportState state, ILogger<SharedFootballImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (store is IPreparedFootballStore prepared) await prepared.PrepareReadViewsAsync(stoppingToken);
            var root = LocalPaths.FindRoot();
            IStreamingFootballSource[] sources = [new FullAustralianFootballSource(configuration["LocalReplay:SourceDirectory"] ?? LocalPaths.Source,
                configuration["SharedFootball:PossessionsPath"] ?? Path.Combine(LocalPaths.Source, "1874553-completed-events.json")),
                new FullBrentfordFootballSource(configuration["SharedFootball:BrentfordExport"] ?? Path.Combine(root, ".local", "shared-inputs", "brentford-full-v5", "manifest.json")),
                new LiverpoolFootballSource(configuration["SharedFootball:LiverpoolExport"] ?? Path.Combine(root, "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json"))];
            foreach (var source in sources)
            {
                await foreach (var batch in source.ReadBatchesAsync(stoppingToken))
                {
                    var decoded = FootballCodec.Decode(FootballCodec.Encode(batch));
                    var changed = await store.AppendAsync(decoded, stoppingToken);
                    state.Ready = true;
                    logger.LogInformation("Common football batch {Stream}/{Sequence} durable, {Records} records, closed {Closed}, changed {Changed}.", batch.Stream.StreamId, batch.Sequence, batch.Records.Length, batch.CloseStream, changed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { state.Failed = true; logger.LogError(exception, "Shared local import failed; durable prior batches remain available. For missing Brentford export run node tools/export-brentford-full.cjs."); }
    }
}
public sealed class ImportHealth(ImportState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Disabled ? HealthCheckResult.Unhealthy("Azure pipeline pending; local recorded import is disabled.")
            : state.Ready && !state.Failed ? HealthCheckResult.Healthy("Shared football batches published.")
            : HealthCheckResult.Unhealthy(state.Failed ? "Recorded import failed; inspect Worker logs." : "Import in progress."));
}
public sealed class RecordedImportWorker(IObservationSource source, IReplayStore store, ImportState state,
    ILogger<RecordedImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stream = await source.OpenAsync(stoppingToken);
            var processor = new RecordingAccumulator(stream.Header);
            await foreach (var frame in stream.Frames.WithCancellation(stoppingToken)) processor.Accept(frame);
            var snapshot = processor.Complete();
            await store.PublishAsync(snapshot, stoppingToken);
            state.Ready = true;
            logger.LogInformation("Accelerated recorded import published {Version}: {Frames} frames, {Empty} empty. No wall-clock live feed.",
                snapshot.Metadata.Version, snapshot.Metadata.FrameCount, snapshot.Metadata.EmptyFrames);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            state.Failed = true;
            logger.LogCritical(exception, "Recorded import failed. Previous valid publication has not been replaced.");
        }
    }
}
