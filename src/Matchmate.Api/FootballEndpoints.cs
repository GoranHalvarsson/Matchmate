using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Matchmate.Api;

public static class FootballEndpoints
{
    public static void MapFootball(this RouteGroupBuilder api)
    {
        api.MapGet("/football/datasets", async (IFootballStore store, CancellationToken token) =>
        {
            try { return Results.Ok(await store.CatalogAsync(token)); }
            catch (Exception exception) when (Program.Unavailable(exception)) { return Unavailable(); }
        });
        api.MapPost("/football/view", async (FootballQuery query, IFootballStore store, PreparedPlayerEvidenceStore playerEvidence, PreparedGeneratedDistanceStore generatedDistance, CancellationToken token) =>
        {
            try { return Results.Ok(generatedDistance.Attach(playerEvidence.Attach(await store.QueryAsync(query, token)))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (FootballQueryException exception) { return Results.Problem(exception.Message, statusCode: 400); }
            catch (Exception exception) when (Program.Unavailable(exception)) { return Unavailable(); }
        });
    }
    private static IResult Unavailable() => Results.Problem("Shared football publication is unavailable. Retry after the Worker has accepted a batch.", statusCode: 503);
}
public sealed class EmptyFootballStore : IFootballStore
{
    public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => throw new InvalidOperationException("Hosted common ingestion is not implemented in this local slice.");
    public Task<FootballLedger> ReadAsync(CancellationToken token) => Task.FromResult(new FootballLedger());
}
public sealed class SharedFootballHealth(IFootballStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var first = (await store.CatalogAsync(cancellationToken)).FirstOrDefault(d => d.Schedule is not null && d.Fixture is not null);
            if (first is null) return HealthCheckResult.Unhealthy("Awaiting a durable common football batch.");
            await store.QueryAsync(new(first.Manifest.DatasetId, first.Manifest.DatasetVersion, first.Stream.StreamId, first.Manifest.FixtureId,
                first.Schedule!.Segments[0].CursorStartMs, "retrospective_replay", first.Schedule.PolicyId, first.Schedule.PolicyRevision), cancellationToken);
            return HealthCheckResult.Healthy("Open or closed common football stream is queryable.");
        }
        catch (Exception exception) when (Program.Unavailable(exception) || exception is FootballQueryException)
        { return HealthCheckResult.Unhealthy("Shared publication cannot be queried."); }
    }
}
