using System.Globalization;
using Matchmate.Contracts;

namespace Matchmate.Web;

public sealed class ReplayApiClient(HttpClient client)
{
    public Task<LeagueSeasonInfo[]> LeagueSeasonsAsync(CancellationToken token) => GetAsync<LeagueSeasonInfo[]>("api/league/seasons", token);
    public Task<LeagueSeasonSnapshot> LeagueSnapshotAsync(string seasonKey, int? dateIndex, string? clubId, CancellationToken token)
    {
        var query = new List<string>();
        if (dateIndex is not null) query.Add("dateIndex=" + dateIndex.Value.ToString(CultureInfo.InvariantCulture));
        if (clubId is not null) query.Add("clubId=" + Uri.EscapeDataString(clubId));
        return GetAsync<LeagueSeasonSnapshot>("api/league/seasons/" + Uri.EscapeDataString(seasonKey) + (query.Count == 0 ? "" : "?" + string.Join('&', query)), token);
    }
    public Task<LeagueHistoricalMatch> LeagueMatchAsync(string seasonKey, string matchKey, CancellationToken token) => GetAsync<LeagueHistoricalMatch>(
        "api/league/seasons/" + Uri.EscapeDataString(seasonKey) + "/matches/" + Uri.EscapeDataString(matchKey), token);
    public Task<FootballDataset[]> FootballCatalogAsync(CancellationToken token) => GetAsync<FootballDataset[]>("api/football/datasets", token);
    public async Task<FootballView> FootballViewAsync(FootballQuery query, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("api/football/view", query, token); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FootballView>(cancellationToken: token) ?? throw new System.Text.Json.JsonException("Missing shared view.");
    }
    public Task<HostingStatus> HostingStatusAsync(CancellationToken token) => GetAsync<HostingStatus>("api/hosting/status", token);
    public Task<ReplayDataset[]> CatalogAsync(CancellationToken token) => GetAsync<ReplayDataset[]>("api/datasets", token);
    public Task<ReplayDataset> MetadataAsync(string datasetId, CancellationToken token) => GetAsync<ReplayDataset>(
        "api/datasets/" + Uri.EscapeDataString(datasetId) + "/metadata", token);
    public Task<FrameView> FrameAsync(string datasetId, string version, double cursor, CancellationToken token) => GetAsync<FrameView>(
        "api/datasets/" + Uri.EscapeDataString(datasetId) + "/frame?cursor=" + cursor.ToString("R", CultureInfo.InvariantCulture)
        + "&version=" + Uri.EscapeDataString(version), token);
    private async Task<T> GetAsync<T>(string path, CancellationToken token) =>
        await client.GetFromJsonAsync<T>(path, token) ?? throw new HttpRequestException("The replay API returned no data.");
}
public sealed record HostingStatus(string State);
