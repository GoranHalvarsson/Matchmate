using System.Globalization;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Matchmate.Api;
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        var hosted = RuntimeMode.Validate(builder.Configuration, builder.Environment);
        var enabled = hosted && (builder.Configuration.GetValue<bool?>("Pipeline:Enabled")
            ?? throw new InvalidOperationException("Hosted API requires an explicit Pipeline:Enabled setting."));
        if (hosted) builder.Services.AddReplayServiceAuthentication();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(PreparedPlayerEvidenceStore.LoadEmbedded());
        builder.Services.AddSingleton(PreparedGeneratedDistanceStore.LoadEmbedded());
        builder.Services.AddSingleton<IReplayStore>(_ => hosted ? new AwaitingPipelineStore() : new AtomicReplayStore(
            builder.Configuration["LocalReplay:StoreDirectory"] ?? LocalPaths.Store));
        if (enabled)
        {
            var settings = new AzureReplaySettings(builder.Configuration["Pipeline:BlobEndpoint"] ?? "", builder.Configuration["Pipeline:CosmosEndpoint"] ?? "",
                builder.Configuration["Pipeline:Database"] ?? "", builder.Configuration["Pipeline:Container"] ?? "", builder.Configuration["Runtime:ManagedIdentityClientId"] ?? "");
            settings.Validate();
            builder.Services.AddSingleton(new AzureFootballResources(settings));
            builder.Services.AddSingleton<IFootballStore>(services => services.GetRequiredService<AzureFootballResources>().Store);
        }
        else builder.Services.AddSingleton<IFootballStore>(_ => hosted ? new EmptyFootballStore() : new ChunkedLocalFootballStore(
            builder.Configuration["SharedFootball:StoreDirectory"] ?? Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")));
        builder.Services.AddSingleton<ILeagueReferenceStore>(services => hosted
            ? enabled ? LeagueReferenceStore.Hosted(services.GetRequiredService<AzureFootballResources>()) : new LeagueReferenceStore(_ => Task.FromResult<byte[]?>(null))
            : LeagueReferenceStore.Local());
        builder.Services.AddSingleton<IReplayDatasetStore, EmptyReplayDatasetStore>();
        builder.Services.AddHealthChecks().AddCheck<SharedFootballHealth>("shared-football");
        var app = builder.Build();
        app.UseExceptionHandler();
        if (hosted) { app.UseAuthentication(); app.UseAuthorization(); }
        app.MapDefaultEndpoints();
        var api = app.MapGroup("/api");
        if (hosted) api.RequireAuthorization(ServiceAuthentication.ReplayPolicy);
        api.MapFootball();
        api.MapLeague();
        api.MapGet("/hosting/status", async (IFootballStore football, CancellationToken token) =>
        {
            try { return Results.Ok(new { state = (await football.CatalogAsync(token)).Length > 0 ? "ready" : "awaiting-pipeline" }); }
            catch (Exception exception) when (Unavailable(exception)) { return NotReady(); }
        });
        api.MapGet("/datasets", async (IReplayDatasetStore store, CancellationToken token) =>
        {
            try { return Results.Ok(await store.CatalogAsync(token)); }
            catch (Exception exception) when (Unavailable(exception)) { return NotReady(); }
        });
        api.MapGet("/datasets/{datasetId}/metadata", async (string datasetId, IReplayDatasetStore store, CancellationToken token) =>
            await Query(datasetId, null, null, false, store, token));
        api.MapGet("/datasets/{datasetId}/frame", async (string datasetId, string? cursor, string? version, IReplayDatasetStore store, CancellationToken token) =>
        {
            if (!TryCursor(cursor, out var time) || string.IsNullOrWhiteSpace(version) || version.Length > 100)
                return Results.Problem("Finite source cursor and dataset version are required.", statusCode: 400);
            return await Query(datasetId, time, version, false, store, token);
        });
        // Compatibility for the original local replay contract; new clients select a string dataset ID.
        api.MapGet("/matches/{matchId:int}/metadata", async (int matchId, IReplayDatasetStore store, CancellationToken token) =>
            matchId != SkillCornerFileSource.MatchId ? Results.NotFound() : await Query(DatasetIdentity.Australian, null, null, true, store, token));
        api.MapGet("/matches/{matchId:int}/frame", async (int matchId, string? cursor, IReplayDatasetStore store, CancellationToken token) =>
        {
            if (!TryCursor(cursor, out var time)) return Results.Problem("A finite cursor in source seconds is required.", statusCode: 400);
            return matchId != SkillCornerFileSource.MatchId ? Results.NotFound() : await Query(DatasetIdentity.Australian, time, null, true, store, token);
        });
        app.Run();
    }
    private static bool TryCursor(string? cursor, out double time)
    {
        time = 0;
        return cursor is { Length: > 0 and <= 40 } && double.TryParse(cursor, NumberStyles.Float, CultureInfo.InvariantCulture, out time) && double.IsFinite(time);
    }
    private static async Task<IResult> Query(string datasetId, double? time, string? version, bool legacy, IReplayDatasetStore store, CancellationToken token)
    {
        if (!DatasetIdentity.IsValid(datasetId)) return Results.Problem("Invalid dataset identity.", statusCode: 400);
        try
        {
            var descriptor = await store.DescriptorAsync(datasetId, token);
            if (descriptor is null) return datasetId == DatasetIdentity.Australian ? NotReady() : Results.NotFound();
            if (time is null) return legacy ? Results.Ok(descriptor.Metadata) : Results.Ok(descriptor);
            if (version is not null && version != descriptor.Metadata.Version) return Results.Problem("Dataset version changed; reload metadata.", statusCode: 409);
            if (time < descriptor.Metadata.Start || time > descriptor.Metadata.End) return Results.Problem("Cursor is outside this recording.", statusCode: 400);
            var snapshot = await store.SnapshotAsync(datasetId, version ?? descriptor.Metadata.Version, token);
            return snapshot is null ? NotReady() : Results.Ok(ReplayQuery.At(snapshot, time.Value));
        }
        catch (ReplayVersionChangedException) { return Results.Problem("Dataset version changed; reload metadata.", statusCode: 409); }
        catch (Exception exception) when (Unavailable(exception)) { return NotReady(); }
    }
    public static bool Unavailable(Exception exception) => exception is IOException or InvalidDataException or JsonException or Newtonsoft.Json.JsonException or RecordingValidationException or UnauthorizedAccessException
        or Azure.RequestFailedException or Microsoft.Azure.Cosmos.CosmosException or Azure.Identity.AuthenticationFailedException;
    private static IResult NotReady() => Results.Problem("The recording is not ready or its durable publication is unavailable. Retry later.", statusCode: 503);
}
public sealed class AwaitingPipelineStore : IReplayStore
{
    public Task<ReplaySnapshot?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<ReplaySnapshot?>(null);
    public Task PublishAsync(ReplaySnapshot snapshot, CancellationToken cancellationToken) => throw new InvalidOperationException("Local publication is disabled in hosted mode.");
}
public sealed class RecordingHealth(IReplayDatasetStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var datasets = await store.CatalogAsync(cancellationToken);
            return datasets.Count == 0 || await store.SnapshotAsync(datasets[0].DatasetId, datasets[0].Metadata.Version, cancellationToken) is null
                ? HealthCheckResult.Unhealthy("Awaiting durable Worker publication.") : HealthCheckResult.Healthy("Published replay integrity verified.");
        }
        catch (Exception exception) when (Program.Unavailable(exception) || exception is ReplayVersionChangedException)
        { return HealthCheckResult.Unhealthy("Published recording cannot be read."); }
    }
}
