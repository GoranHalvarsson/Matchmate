using Matchmate.Contracts;
using Microsoft.AspNetCore.Components;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Components.Pages;

public partial class HistoricalMatch
{
    [Inject] private MatchLanguageState? LanguageState { get; set; }
    private MatchLanguage Language => LanguageState?.Language ?? MatchLanguage.English;
    private string T(string key, params object?[] args) => HistoricalText.Get(Language, key, args);
    private string _statusKey = "StatusLoading";
    private string DisplayStatus => Language == MatchLanguage.English ? _status : T(_statusKey);
    private void SetStatus(string key) { _statusKey = key; _status = HistoricalText.Get(MatchLanguage.English, key); }
    protected override void OnInitialized() { if (LanguageState is not null) LanguageState.Changed += LanguageChanged; }
    private async void LanguageChanged()
    {
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (Exception exception) { await DispatchExceptionAsync(exception); }
    }
    [Inject] private ReplayApiClient Api { get; set; } = default!;
    [Parameter] public string SeasonKey { get; set; } = "";
    [Parameter] public string MatchKey { get; set; } = "";
    [Parameter, SupplyParameterFromQuery(Name = "date")] public int? DateIndex { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "club")] public string? Club { get; set; }
    private LeagueHistoricalMatch? _match; private LeagueClub[] _clubs = [];
    private string _status = "Loading source-backed result context…";
    private long _generation; private bool _disposed, _loading;
    private string? _route;
    private CancellationTokenSource? _request;
    protected override Task OnParametersSetAsync()
    {
        var route = $"{SeasonKey}/{MatchKey}";
        if (_route == route) return Task.CompletedTask;
        _route = route;
        return LoadAsync();
    }
    private async Task LoadAsync()
    {
        if (_disposed) return;
        var generation = ++_generation; _request?.Cancel(); _request?.Dispose(); _request = new(); var token = _request.Token;
        var season = SeasonKey; var match = MatchKey; _match = null; _loading = true; SetStatus("StatusLoading");
        try { var result = await Api.LeagueMatchAsync(season, match, token); var snapshot = await Api.LeagueSnapshotAsync(season, null, null, token);
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            if (result.Match.MatchKey != match || result.Season.SeasonKey != season || snapshot.Season.SeasonKey != season || snapshot.Version != result.Version)
                throw new InvalidDataException("Historical snapshot changed.");
            _clubs = snapshot.Clubs; _match = result;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException)
        { if (!_disposed && generation == _generation && !token.IsCancellationRequested) SetStatus("StatusUnavailable"); }
        finally { if (!_disposed && generation == _generation) _loading = false; }
    }
    public ValueTask DisposeAsync() { _disposed = true; if (LanguageState is not null) LanguageState.Changed -= LanguageChanged; _request?.Cancel(); _request?.Dispose(); return ValueTask.CompletedTask; }
}
