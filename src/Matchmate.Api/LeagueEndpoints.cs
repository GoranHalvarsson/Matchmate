using Matchmate.Core;

namespace Matchmate.Api;

public static class LeagueEndpoints
{
    public static void MapLeague(this RouteGroupBuilder api)
    {
        api.MapGet("/league/seasons", async (ILeagueReferenceStore store, CancellationToken token) =>
            await Read(store, model => model.Seasons(), token));
        api.MapGet("/league/seasons/{seasonKey}", async (string seasonKey, int? dateIndex, string? clubId, ILeagueReferenceStore store, CancellationToken token) =>
            await Read(store, model => model.Snapshot(seasonKey, dateIndex, clubId), token));
        api.MapGet("/league/seasons/{seasonKey}/matches/{matchKey}", async (string seasonKey, string matchKey, ILeagueReferenceStore store, CancellationToken token) =>
            await Read(store, model => model.Match(seasonKey, matchKey), token));
    }
    private static async Task<IResult> Read<T>(ILeagueReferenceStore store, Func<LeagueModel,T> project, CancellationToken token)
    {
        try { return Results.Ok(project(await store.ReadAsync(token))); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (LeagueCursorException exception) { return Results.Problem(exception.Message, statusCode: 400); }
        catch (Exception exception) when (exception is LeagueReferenceValidationException || Program.Unavailable(exception))
        { return Results.Problem("Historical league reference is unavailable. Retry after the versioned bundle is published.", statusCode: 503); }
    }
}
