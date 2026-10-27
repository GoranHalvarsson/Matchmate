using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Matchmate.Web.Components.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private MatchLanguageState? LanguageState { get; set; }
    private MatchLanguage Language => LanguageState?.Language ?? MatchLanguage.English;
    private string T(string key, params object?[] args) => HistoricalText.Get(Language, key, args);
    private string _statusKey = "LegacyStatusWaiting";
    private string? _errorKey;
    private string DisplayStatus => Language == MatchLanguage.English ? _status : T(_statusKey);
    private string? DisplayError => _errorKey is { } key ? T(key) : null;
    private void SetStatus(string key) { _statusKey = key; _status = HistoricalText.Get(MatchLanguage.English, key); }
    private void SetError(string? key) { _errorKey = key; _error = key is null ? null : HistoricalText.Get(MatchLanguage.English, key); }
    private string Quality(string? quality) => quality switch
    {
        null or "loading" => T("LoadingValue"), "detected" => T("Detected"), "unavailable" => T("Unavailable"),
        _ => ReplayText.Value(Language, quality)
    };
    private string LocalDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("g", MatchLanguages.Culture(Language)) + " " + date.ToString("zzz", CultureInfo.InvariantCulture) : value;
    protected override void OnInitialized() { if (LanguageState is not null) LanguageState.Changed += LanguageChanged; }
    private async void LanguageChanged()
    {
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (Exception exception) { await DispatchExceptionAsync(exception); }
    }
    [Inject] private ReplayApiClient Api { get; set; } = default!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DatasetSelection _selection = new();
    private CancellationTokenSource? _playCancellation, _frameCancellation, _catalogCancellation;
    private Task? _playTask;
    private ReplayDataset[] _catalog = [];
    private string _activeDatasetId = "";
    private ReplayMetadata? _metadata;
    private ViewerCursor? _cursor;
    private FrameView? _frame;
    private int? _selected;
    private int _generation, _playGeneration;
    private bool _loading, _catalogLoading, _disposed;
    private string _status = "Waiting for a complete recorded dataset…";
    private string? _error;
    private PlayerView? Selected => _frame?.Players.FirstOrDefault(p => p.Player.Id == _selected);
    private double PitchLength => _metadata?.Pitch.Length ?? 105;
    private double PitchWidth => _metadata?.Pitch.Width ?? 68;
    private string ViewBox => $"0 0 {InvariantNumber(PitchLength + 12)} {InvariantNumber(PitchWidth + 12)}";
    private string PenaltyMarkings => FormattableString.Invariant($"M6 {PitchWidth / 2 + 6 - 20.16}H22.5V{PitchWidth / 2 + 6 + 20.16}H6 M{PitchLength + 6} {PitchWidth / 2 + 6 - 20.16}H{PitchLength - 10.5}V{PitchWidth / 2 + 6 + 20.16}H{PitchLength + 6} M6 {PitchWidth / 2 + 6 - 9.16}H11.5V{PitchWidth / 2 + 6 + 9.16}H6 M{PitchLength + 6} {PitchWidth / 2 + 6 - 9.16}H{PitchLength + .5}V{PitchWidth / 2 + 6 + 9.16}H{PitchLength + 6}");
    protected override Task OnInitializedAsync() => LoadAsync();
    private async Task LoadAsync()
    {
        if (_catalogLoading || _disposed) return;
        _catalogLoading = true; _loading = true; Pause();
        _catalogCancellation?.Dispose(); _catalogCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _catalogCancellation.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var catalog = await Api.CatalogAsync(token);
                    if (_disposed || token.IsCancellationRequested) return;
                    if (catalog.Length > 0) { _catalog = catalog; break; }
                    if (attempt >= 11) { SetStatus("LegacyStatusNoRecording"); return; }
                }
                catch (HttpRequestException) when (attempt < 11) { }
                SetStatus("LegacyStatusWorkerWaiting");
                await InvokeAsync(StateHasChanged);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            var datasetId = _catalog.Any(item => item.DatasetId == _activeDatasetId) ? _activeDatasetId : _catalog[0].DatasetId;
            await SwitchDatasetAsync(datasetId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        { if (!_disposed) SetStatus("LegacyStatusCatalogUnavailable"); }
        finally { _catalogLoading = false; if (_activeDatasetId.Length == 0) _loading = false; }
    }
    private Task SelectDatasetAsync(ChangeEventArgs args) => args.Value is string datasetId
        && _catalog.Any(item => item.DatasetId == datasetId) ? SwitchDatasetAsync(datasetId) : Task.CompletedTask;
    private async Task SwitchDatasetAsync(string datasetId)
    {
        Pause(); _generation++; _frameCancellation?.Cancel(); _catalogCancellation?.Cancel();
        var request = _selection.Change(datasetId);
        _activeDatasetId = datasetId; _metadata = null; _cursor = null; _frame = null; _selected = null; SetError(null);
        _loading = true; SetStatus("LegacyStatusLoadingRecording");
        try
        {
            var dataset = await Api.MetadataAsync(datasetId, request.Token);
            if (!_selection.AcceptMetadata(request, dataset)) return;
            _metadata = dataset.Metadata; _cursor = new ViewerCursor(_metadata); _selected = _metadata.Players.FirstOrDefault()?.Id;
            await FetchFrameAsync();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        { if (_selection.IsCurrent(request)) SetStatus("LegacyStatusNotReady"); }
        finally { if (_selection.IsCurrent(request)) _loading = false; }
    }
    private async Task FetchFrameAsync()
    {
        if (_cursor is null || _metadata is null || _disposed) return;
        var request = _selection.Current; var generation = ++_generation; var cursor = _cursor.Time; var version = _metadata.Version;
        _frameCancellation?.Cancel(); _frameCancellation?.Dispose();
        _frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, request.Token);
        var token = _frameCancellation.Token; _frame = null; SetError(null);
        try
        {
            var frame = await Api.FrameAsync(request.DatasetId, version, cursor, token);
            if (_disposed || generation != _generation || !_selection.IsCurrent(request)) return;
            if (!_selection.AcceptFrame(request, frame)) { Pause(); SetError("LegacyErrorVersion"); return; }
            _frame = frame;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        { if (generation == _generation && _selection.IsCurrent(request)) { Pause(); SetError("LegacyErrorMoment"); } }
    }
    private void Select(int id) => _selected = id;
    private void SelectKey(KeyboardEventArgs e, int id) { if (e.Key is "Enter" or " ") Select(id); }
    private void Pause() { _playGeneration++; _cursor?.Pause(); _playCancellation?.Cancel(); }
    private async Task SeekAsync(double time) { Pause(); _cursor?.Seek(time); await FetchFrameAsync(); }
    private Task ScrubAsync(ChangeEventArgs e) => double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? SeekAsync(time) : Task.CompletedTask;
    private Task AdjacentAsync(bool forward) => _cursor?.Adjacent(forward) is { } time ? SeekAsync(time) : Task.CompletedTask;
    private Task TogglePlayAsync() { if (_cursor?.Playing == true) { Pause(); return Task.CompletedTask; } return StartPlaybackAsync(false); }
    private Task ContinueAsync() => StartPlaybackAsync(true);
    private async Task StartPlaybackAsync(bool continueGap)
    {
        Pause(); var generation = _playGeneration;
        if (_playTask is not null) await _playTask;
        if (_disposed || _cursor is null || generation != _playGeneration) return;
        _playCancellation?.Dispose(); _playCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _selection.Current.Token);
        if (continueGap) _cursor.ContinueGap(); else _cursor.Play();
        _playTask = PlaybackAsync(_playCancellation.Token); await _playTask;
    }
    private async Task PlaybackAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); var previous = clock.Elapsed.TotalSeconds;
        try
        {
            while (_cursor?.Playing == true)
            {
                await Task.Delay(100, token); token.ThrowIfCancellationRequested();
                var now = clock.Elapsed.TotalSeconds; _cursor.Advance(now - previous); previous = now;
                await FetchFrameAsync();
                if (!_disposed) await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static string Clock(double time) => $"{(int)time / 60:00}:{(int)time % 60:00}";
    private static string InvariantNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private double X(Observation position) => position.X!.Value + PitchLength / 2 + 6;
    private double Y(Observation position) => PitchWidth / 2 + 6 - position.Y!.Value;
    private string MarkerClass(PlayerView player) => $"marker {(player.Player.TeamId == _metadata?.Home.Id ? "home" : "away")} {player.Position.Quality} {(_selected == player.Player.Id ? "focused" : "")}";
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; if (LanguageState is not null) LanguageState.Changed -= LanguageChanged; _lifetime.Cancel(); _selection.Dispose(); _catalogCancellation?.Cancel(); _playCancellation?.Cancel(); _frameCancellation?.Cancel();
        if (_playTask is not null) await _playTask;
        _catalogCancellation?.Dispose(); _frameCancellation?.Dispose(); _playCancellation?.Dispose(); _lifetime.Dispose();
    }
}
