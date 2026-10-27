using Matchmate.Contracts;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Matchmate.Web.LeagueChat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Matchmate.Web.Components.Pages;

public partial class LeagueExplorer
{
    [Inject] private MatchLanguageState? LanguageState { get; set; }
    private MatchLanguage Language => LanguageState?.Language ?? MatchLanguage.English;
    private string T(string key, params object?[] args) => LeagueText.Get(Language, key, args);
    private string _statusKey = "StatusLoadingCatalog";
    private object?[] _statusArgs = [];
    private string DisplayStatus => Language == MatchLanguage.English ? _status : T(_statusKey, LocalizedStatusArgs());
    private object?[] LocalizedStatusArgs() => _statusKey switch
    {
        "StatusShowing" => [_statusArgs[0], ExperiencePresentation.Date(_statusArgs[1] as string, Language), _statusArgs[2] is string club ? " · " + club : T("ChooseClubSuffix")],
        "StatusUpdating" => [_statusArgs[0], ExperiencePresentation.Date(_statusArgs[1] as string, Language)],
        "StatusUpdateFailed" => [_statusArgs[0], ExperiencePresentation.Date(_statusArgs[1] as string, Language), _statusArgs[2] is string club ? " · " + club + "." : "."],
        _ => _statusArgs
    };
    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key; _statusArgs = args;
        _status = LeagueText.Get(MatchLanguage.English, key, key switch
        {
            "StatusShowing" => [args[0], ExperiencePresentation.Date(args[1] as string), args[2] is string club ? " · " + club : LeagueText.Get(MatchLanguage.English, "ChooseClubSuffix")],
            "StatusUpdating" => [args[0], ExperiencePresentation.Date(args[1] as string)],
            "StatusUpdateFailed" => [args[0], ExperiencePresentation.Date(args[1] as string), args[2] is string club ? " · " + club + "." : "."],
            _ => args
        });
    }
    protected override void OnInitialized() { if (LanguageState is not null) LanguageState.Changed += LanguageChanged; }
    private async void LanguageChanged()
    {
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (Exception exception) { await DispatchExceptionAsync(exception); }
    }
    [Inject] private ReplayApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private LeagueChatSession? ConversationSession { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "season")] public string? Season { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "date")] public int? DateIndex { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "club")] public string? Club { get; set; }
    private sealed record SelectionRequest(string Season, int? Index, string? Club);
    private LeagueSeasonInfo[] _seasons = [];
    private LeagueSeasonSnapshot? _snapshot;
    // These fields describe the committed view, never the outstanding request.
    private string _season = "2026-27", _status = "Loading the private reference catalog…", _route = "";
    private string? _club;
    private int? _index; private int _speed = 1;
    private bool _loading, _loadError, _playing, _disposed, _showMap, _navigatingAway, _playbackLoading;
    private long _generation, _intent;
    private SelectionRequest? _pending;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request, _playback;
    private Task? _playTask;
    private IDisposable? _locationChanging;
    private string DisplayedSeason => _snapshot?.Season.SeasonKey ?? (_pending is { Season.Length: > 0 } pending ? pending.Season : _season);
    private LeagueChatContext? ConversationContext => !_disposed && !_loading && !_loadError && !_navigatingAway && _snapshot is { } shown ? LeagueChatContext.From(shown) : null;
    private string FullDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var date) ? date.ToString("d MMMM yyyy", MatchLanguages.Culture(Language)) : T("UnknownDate");
    private string Coverage(LeagueSeasonInfo season) => season.Status == "ongoing"
        ? T("ResultsThrough", FullDate(season.ResultsThrough)) : T("CompleteResults", season.Label);
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && !_disposed) _locationChanging = Navigation.RegisterLocationChangingHandler(LocationChanging);
    }
    private ValueTask LocationChanging(LocationChangingContext context)
    {
        _navigatingAway = !IsLeagueLocation(context.TargetLocation);
        if (_navigatingAway) BeginIntent();
        return ValueTask.CompletedTask;
    }
    private bool IsLeagueLocation(string location)
    {
        // F-CURRENT-001: server-side NavigateTo handlers receive the original relative URI.
        try { return Navigation.ToBaseRelativePath(Navigation.ToAbsoluteUri(location).AbsoluteUri).Split('?', '#')[0].Length == 0; }
        catch (ArgumentException) { return false; }
    }
    protected override async Task OnParametersSetAsync()
    {
        // F-SMTH-002: query parameters can be cleared on the outgoing component before disposal.
        if (_navigatingAway || !IsLeagueLocation(Navigation.Uri)) return;
        var route = $"{Season}/{DateIndex}/{Club}";
        if (_route == route) return;
        _route = route;
        var intent = BeginIntent();
        await LoadAsync(new(Season ?? "", DateIndex, Club), intent);
    }
    private long BeginIntent()
    {
        if (_disposed) return _intent;
        ConversationSession?.SetContext(null); _playbackLoading = false;
        _intent++; Pause(); _request?.Cancel(); _loading = false; _loadError = false; _pending = null;
        if (_snapshot is { } shown) SetStatus("StatusShowing", shown.Season.Label, shown.Date, shown.SelectedClub?.Club.Name);
        return _intent;
    }
    private bool Current(long intent, long generation, CancellationToken token) => !_disposed && !_navigatingAway && IsLeagueLocation(Navigation.Uri)
        && intent == _intent && generation == _generation && !token.IsCancellationRequested;
    private async Task<bool> LoadAsync(SelectionRequest desired, long intent)
    {
        if (_disposed || _navigatingAway || !IsLeagueLocation(Navigation.Uri) || intent != _intent) return false;
        var generation = ++_generation; _request?.Cancel(); _request?.Dispose(); _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ConversationSession?.SetContext(null);
        var token = _request.Token; var club = desired.Club;
        // A same-season update keeps the keyed workspace, focus and committed facts mounted.
        if (_snapshot?.Season.SeasonKey != desired.Season) _snapshot = null;
        _pending = desired; _loading = true; _loadError = false;
        if (_snapshot is { } shown) SetStatus("StatusUpdating", shown.Season.Label, shown.Date);
        else SetStatus("StatusLoadingSeason");
        try
        {
            if (_seasons.Length == 0)
            {
                var seasons = await Api.LeagueSeasonsAsync(token);
                if (!Current(intent, generation, token)) return false;
                _seasons = seasons.OrderByDescending(s => s.SeasonKey, StringComparer.Ordinal).ToArray();
            }
            // Resolve a fresh root from the available catalog, including genuine legacy fallback.
            if (desired.Season.Length == 0)
            {
                desired = desired with { Season = _seasons.FirstOrDefault()?.SeasonKey ?? _season };
                _pending = desired;
            }
            LeagueSeasonSnapshot snapshot;
            try { snapshot = await Api.LeagueSnapshotAsync(desired.Season, desired.Index, club, token); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound && club is not null && _seasons.Any(s => s.SeasonKey == desired.Season))
            {
                if (!Current(intent, generation, token)) return false;
                snapshot = await Api.LeagueSnapshotAsync(desired.Season, desired.Index, null, token); club = null;
            }
            if (!Current(intent, generation, token)) return false;
            if (snapshot.Season.SeasonKey != desired.Season || (desired.Index is not null && snapshot.DateIndex != desired.Index)
                || snapshot.SelectedClub?.Club.Id != club) throw new System.Text.Json.JsonException("The returned league context does not match the request.");
            // Commit selection, facts and URL in one synchronous step, only for the latest intent.
            _snapshot = snapshot; _season = snapshot.Season.SeasonKey; _index = snapshot.DateIndex; _club = club;
            _pending = null; _loading = false; _loadError = false;
            ConversationSession?.SetContext(ConversationContext);
            SetStatus("StatusShowing", snapshot.Season.Label, snapshot.Date, snapshot.SelectedClub?.Club.Name);
            Remember(); return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (Current(intent, generation, token))
            {
                Pause(); _loadError = true;
                if (_snapshot is { } retained) SetStatus("StatusUpdateFailed", retained.Season.Label, retained.Date, retained.SelectedClub?.Club.Name);
                else SetStatus("StatusUnavailable");
            }
            return false;
        }
        finally { if (Current(intent, generation, token)) _loading = false; }
    }
    private async Task RetryAsync() { var desired = _pending ?? new(_season, _index, _club); await LoadAsync(desired, BeginIntent()); }
    private async Task ChangeSeasonAsync(string season) { var desired = new SelectionRequest(season, null, _club); await LoadAsync(desired, BeginIntent()); }
    private async Task SelectClubAsync(string club)
    {
        if (_snapshot is not { } shown) return;
        ConversationSession?.ResetSubjectReference();
        await LoadAsync(new(shown.Season.SeasonKey, shown.DateIndex, club), BeginIntent());
    }
    private async Task SeekAsync(int index)
    {
        if (_snapshot is not { } shown) return;
        await LoadAsync(new(shown.Season.SeasonKey, index, _club), BeginIntent());
    }
    private void SetSpeed(int value) { if (value is 1 or 2 or 4) _speed = value; }
    private void Remember()
    {
        if (_snapshot is null || _navigatingAway || !IsLeagueLocation(Navigation.Uri)) return;
        _route = $"{_season}/{_index}/{_club}";
        Navigation.NavigateTo(ExperiencePresentation.LeagueUrl(_season, _index, _club), forceLoad: false, replace: true);
    }
    private void Pause() { _playing = false; _playback?.Cancel(); }
    private void PauseForConversation()
    {
        if (_disposed || _navigatingAway || _loadError || _loading && !_playbackLoading) return;
        _intent++; Pause(); _request?.Cancel();
        if (_playbackLoading) { _playbackLoading = false; _loading = false; _pending = null; }
        if (_snapshot is { } shown) SetStatus("StatusShowing", shown.Season.Label, shown.Date, shown.SelectedClub?.Club.Name);
        ConversationSession?.SetContext(ConversationContext);
    }
    private async Task ToggleAsync()
    {
        var wasPlaying = _playing; var intent = BeginIntent();
        if (wasPlaying || _snapshot is null) return;
        if (_playTask is not null) await _playTask;
        if (_disposed || intent != _intent || _snapshot is null) return;
        if (_snapshot.DateIndex == _snapshot.Season.PlayedDates.Length)
        {
            if (!await LoadAsync(new(_season, 0, _club), intent)) return;
            if (_disposed || intent != _intent || _snapshot is null) return;
        }
        _playback?.Dispose(); _playback = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _playing = true;
        _playTask = PlayAsync(intent, _playback.Token); await _playTask;
    }
    private async Task PlayAsync(long intent, CancellationToken token)
    {
        try
        {
            while (_playing && !_disposed && intent == _intent && _snapshot is not null)
            {
                await Task.Delay(1600 / _speed, token); token.ThrowIfCancellationRequested();
                if (_disposed || intent != _intent || !_playing || _snapshot is not { } shown) return;
                _playbackLoading = true;
                var loaded = await LoadAsync(new(shown.Season.SeasonKey, shown.DateIndex + 1, _club), intent);
                if (intent == _intent) _playbackLoading = false;
                if (_disposed || intent != _intent || token.IsCancellationRequested) return;
                if (!loaded || _snapshot is null || _snapshot.DateIndex >= _snapshot.Season.PlayedDates.Length) Pause();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static SourcePrompt[] Prompts(LeagueSeasonSnapshot snapshot)
    {
        if (snapshot.SelectedClub is not { } f) return [new("What does this table include?", $"{snapshot.Completed} results through {ExperiencePresentation.Date(snapshot.Date)}. All results on a played date enter together; this is a retrospective corrected table.")];
        return [new("Where do we stand?", $"{f.Club.Name}: position {ExperiencePresentation.Rank(f.Row)}, {f.Row.Points} points from {f.Row.Played} matches at this cutoff."),
            new("What changed on this date?", $"{f.PointsChange} points added. " + (f.PositionChange is null ? "No earlier played-date rank is available." : $"Position change versus the previous played date: {f.PositionChange}. Other clubs' results also affect the table.")),
            new("Tell me about our ground?", $"The {snapshot.Season.Label} reference places {f.Club.Name} at {f.Club.Ground}, {f.Club.Town}. The map is an illustration, not a surveyed location or a source for stadium history.")];
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; if (LanguageState is not null) LanguageState.Changed -= LanguageChanged; _locationChanging?.Dispose(); _intent++; _lifetime.Cancel(); _request?.Cancel(); Pause();
        ConversationSession?.NewChat(); ConversationSession?.SetContext(null);
        if (_playTask is not null) await _playTask;
        _request?.Dispose(); _playback?.Dispose(); _lifetime.Dispose();
    }
}
