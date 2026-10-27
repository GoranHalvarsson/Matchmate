using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;
using Matchmate.Web.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Components.Pages;

public partial class FootballReplay
{
    [Inject] private ReplayApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private MatchChatSession? ChatSession { get; set; }
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private MatchLanguageState? LanguageState { get; set; }
    private MatchLanguage Language => LanguageState?.Language ?? MatchLanguage.English;
    private string T(string key, params object?[] args) => ReplayText.Get(Language, key, args);
    protected override void OnInitialized()
    {
        if (LanguageState is not null) LanguageState.Changed += LanguageChanged;
        ChatSession?.SetLanguage(Language);
    }
    private async void LanguageChanged()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;
                _intentGeneration++;
                ChatSession?.SetLanguage(Language);
                _eventPresentationContext = null; _eventPresentationId = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = "";
                StateHasChanged();
            });
        }
        catch (Exception exception) { await DispatchExceptionAsync(exception); }
    }
    private ReplayPitchInterop? _pitchInterop;
    private ElementReference _pitchElement;
    private MatchChatContext? _eventPresentationContext;
    [Parameter] public string? DatasetId { get; set; }
    private FootballDataset[] _catalog = [];
    private FootballView? _view;
    private FootballView? _insightView;
    private ReplayInsight? _insight;
    private MatchAudienceMode _audienceMode, _insightAudienceMode;
    private MatchLanguage _insightLanguage;
    private ReplayInsightPreparation? _preparedInsight;
    private FootballView? _recapEvidence;
    private (FootballDataset Dataset, ReplayRecapBoundary Boundary, long Generation, bool Completed)? _recapAttempt;
    private ReplayRecapPreparation? _preparedRecap;
    private ReplayRecap? CurrentRecap => !_disposed && !InteractionBlocked && _selectedEvent is null
        && _view is { } view && view.Context.StreamId == _selectedStream
        ? ReplayRecap.Project(view, _recapEvidence, _audienceMode, Language) : null;
    private async Task UpdateRecapAsync(FootballView accepted, long generation, CancellationToken token)
    {
        var boundary = ReplayRecap.BoundaryFor(accepted);
        if (_recapEvidence is { } prior && !ReplayRecap.SameAuthority(prior.Dataset, accepted.Dataset)) _recapEvidence = null;
        if (boundary is null || _recapAttempt is { } attempt && attempt.Boundary == boundary && ReplayRecap.SameAuthority(attempt.Dataset, accepted.Dataset)
            && (attempt.Completed || attempt.Generation == generation)) return;
        _recapAttempt = (accepted.Dataset, boundary, generation, false);
        var query = accepted.Context with { CursorMs = boundary.EvidenceCursorMs };
        try
        {
            var evidence = query == accepted.Context ? accepted : await Api.FootballViewAsync(query, token).WaitAsync(token);
            if (_disposed || token.IsCancellationRequested || generation != _generation || !ReferenceEquals(_view, accepted)
                || _selectedStream != accepted.Context.StreamId || evidence.Context != query || !ReplayRecap.SameAuthority(accepted.Dataset, evidence.Dataset)) return;
            _recapEvidence = evidence;
            _recapAttempt = (accepted.Dataset, boundary, generation, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            // A failed/canceled read stays incomplete so a later accepted request can retry. Never clear a newer attempt.
        }
    }
    private async Task PrepareRecapQuestionAsync(ReplayRecapAction action)
    {
        if (_disposed || InteractionBlocked || CurrentRecap?.Action != action || _view is not { } accepted) return;
        var selection = _selectionGeneration;
        Pause(); _personId = null; _selectedEvent = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = "";
        // Chat reads the very same cutoff as this recap, including when halftime is viewed later in the match.
        var seeking = SeekAsync(action.Query.CursorMs); var intent = _intentGeneration; await seeking;
        if (_disposed || intent != _intentGeneration || selection != _selectionGeneration || InteractionBlocked || _view is not { } current
            || !ReplayRecap.SameAuthority(accepted.Dataset, current.Dataset) || !ReplayRecap.MatchesEvidence(action, current)
            || ReadyConversationContext is not { } context) return;
        _preparedRecap = new(Guid.NewGuid(), action, context);
    }
    private ReplayInsight? CurrentInsight
    {
        get
        {
            if (_eventPresentationContext is not null || _view is not { } view || view.Context.StreamId != _selectedStream) return null;
            if (!ReferenceEquals(_insightView, view) || _insightAudienceMode != _audienceMode || _insightLanguage != Language)
            { _insightView = view; _insightAudienceMode = _audienceMode; _insightLanguage = Language; _insight = ReplayInsight.Project(view, _audienceMode, Language); }
            return _insight;
        }
    }
    private void ChangeAudience(MatchAudienceMode mode)
    {
        if (_disposed || !Enum.IsDefined(mode) || _audienceMode == mode) return;
        _audienceMode = mode;
        ChatSession?.SetAudienceMode(mode);
    }
    private Task PrepareInsightQuestionAsync(ReplayInsightAction action)
    {
        if (_disposed || InteractionBlocked || CurrentInsight?.Action != action || !ReplayInsight.Matches(action, _view)) return Task.CompletedTask;
        Pause(); _selectedPassage = null; _preparedPassage = null; _storyNotice = "";
        if (ReadyConversationContext is { } context) _preparedInsight = new(Guid.NewGuid(), action, context);
        return Task.CompletedTask;
    }
    private ReplayHighlight[] _highlights = [];
    private FootballRecord[] _sequenceIndex = [];
    private FootballRecord[] _possessionIndex = [];
    private FootballRecord[] PresentationIndex => [.. _sequenceIndex, .. _possessionIndex];
    private Guid? _eventPresentationId;
    private string _selectedStream = "", _status = "Waiting for shared football batches…", _highlightStatus = "Loading the retrospective index…";
    private string? _personId, _routeDataset;
    private MatchSelectedEvent? _selectedEvent;
    private long _eventSelectionVersion;
    private long _cursorMs, _generation, _selectionGeneration, _intentGeneration;
    private long _motionEpoch, _playbackGeneration;
    private long? _playbackRequestGeneration;
    private long _highlightHead;
    private long? _stopAt;
    private int _speed = 1;
    private bool _playing, _disposed, _continueGap, _updating, _contextUnavailable, _catalogBusy, _indexBusy, _indexLoaded;
    private bool HighlightCountsKnown => _indexLoaded && (_highlights.Length > 0 || CurrentDataset?.Closed == true);
    private bool PositionsLoading => _view is { Dataset.Closed: false } view && (view.Frame is null || view.TrackingGap);
    private static string ReplayStatus(FootballView view) => !view.Dataset.Closed && (view.Frame is null || view.TrackingGap) ? "Preparing this replay moment…" : "Replay ready.";
    private bool InteractionBlocked => _updating || _contextUnavailable || _eventPresentationContext is not null;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request, _playback, _catalogRequest, _highlightRequest;
    private Task? _playTask;
    private CancellationTokenSource? _indexRetry;
    private Task? _indexRetryTask;
    private const int IndexRetryAttempts = 60;
    private FootballDataset? CurrentDataset => _view?.Dataset ?? _catalog.FirstOrDefault(d => d.Stream.StreamId == _selectedStream);
    // Local presentation placement only; the shared catalog, identities and source evidence remain intact.
    private static bool IsTechnicalSample(string? datasetId) => datasetId is "australian-recorded" or "paced-generated-demo";
    private FootballDataset[] MainRecordings => _catalog.Where(d => !IsTechnicalSample(d.Manifest.DatasetId)).ToArray();
    private FootballDataset[] TechnicalSamples => _catalog.Where(d => IsTechnicalSample(d.Manifest.DatasetId)).ToArray();
    private bool ViewingTechnicalSample => IsTechnicalSample(DatasetId) || IsTechnicalSample(CurrentDataset?.Manifest.DatasetId);
    private MatchChatContext? ConversationContext => _eventPresentationContext ?? ReadyConversationContext;
    private MatchChatContext? ReadyConversationContext => _view is { } view ? new(view.Context with { SubjectId = _personId }, view.Dataset.HeadSequence, _selectedEvent, _selectedPassage) : null;
    private string ConversationLabel => _view is { } view ? T("ConversationClock", ExperiencePresentation.PeriodLabel(view, Language), ExperiencePresentation.Clock(ExperiencePresentation.MatchClock(view)), ExperiencePresentation.Clock(view.Context.CursorMs)) : T("ChooseReadyMoment");
    private bool SequenceIndexAvailable => _view is not { } view || MatchPitchScene.IndexAvailable(view, _highlightHead, _sequenceIndex);
    private static bool CompleteReportedSequenceIndex(FootballView view, SchedulePayload schedule, FootballRecord[] index)
        => MatchPitchScene.CompleteReportedSequenceIndex(view, schedule, index);
    private MatchSequenceProjection[] PitchSequences => _view is { } view
        ? MatchPitchScene.Select(view, _selectedEvent, _highlightHead, _sequenceIndex) : [];
    private bool MaskGoalWindow => _view is { } view && MatchPitchScene.Mask(view, _highlightHead, _sequenceIndex, PitchSequences);
    protected override Task OnParametersSetAsync()
    {
        if (_routeDataset == DatasetId && _catalog.Length > 0) return Task.CompletedTask;
        if (_routeDataset != DatasetId)
        {
            // F-UI-001: the route owns the visible selection, even before its catalog request succeeds.
            Pause(); _selectionGeneration++; _generation++; _intentGeneration++;
            _request?.Cancel(); _highlightRequest?.Cancel(); _catalogRequest?.Cancel(); _indexRetry?.Cancel();
            _view = null; _recapEvidence = null; _recapAttempt = null; _preparedRecap = null; _highlights = []; _sequenceIndex = []; _possessionIndex = []; _highlightHead = 0; _indexLoaded = false; _indexBusy = false; _selectedEvent = null; _personId = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; _selectedStream = ""; _cursorMs = 0;
            _continueGap = false; _stopAt = null; _status = "Loading this recording…";
            _highlightStatus = "Loading the retrospective index…";
        }
        _routeDataset = DatasetId; return RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        _recapAttempt = null;
        Pause(); _selectedEvent = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; _catalogRequest?.Cancel(); _catalogRequest?.Dispose(); _catalogRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var catalogRequest = _catalogRequest; var catalogToken = catalogRequest.Token; _catalogBusy = true;
        try
        {
            await StopIndexRetryAsync();
            if (catalogToken.IsCancellationRequested || _disposed) return;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var catalog = await Api.FootballCatalogAsync(catalogToken); if (_disposed || catalogToken.IsCancellationRequested) return;
                _catalog = catalog.Where(d => d.Fixture is not null && d.Schedule is not null).GroupBy(d => d.Manifest.DatasetId, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(d => d.Manifest.ManifestRevision).ThenBy(d => d.Stream.StreamId, StringComparer.Ordinal).First()).ToArray();
                if (_catalog.Length > 0)
                {
                    var selected = DatasetId is not null ? _catalog.FirstOrDefault(d => d.Manifest.DatasetId == DatasetId)
                        : MainRecordings.FirstOrDefault(d => d.Stream.StreamId == _selectedStream) ?? MainRecordings.FirstOrDefault();
                    if (selected is null)
                    {
                        _view = null; _selectedStream = "";
                        _status = DatasetId is null ? "No match recordings are available. Technical samples are listed below."
                            : "This recording is not in the current catalog. Choose an available recording.";
                        return;
                    }
                    if (_selectedStream != selected.Stream.StreamId) await SwitchAsync(selected);
                    else { await FetchAsync(selected); await LoadHighlightsAsync(selected, _selectionGeneration); }
                    return;
                }
                await Task.Delay(1000, catalogToken);
            }
            _status = "No common batch is queryable yet. The first full local import can take about three minutes; refresh after the Worker is ready.";
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { if (!_disposed && !catalogToken.IsCancellationRequested) _status = "The shared API is unavailable. Refresh after the Worker is ready."; }
        finally { if (ReferenceEquals(_catalogRequest, catalogRequest)) _catalogBusy = false; }
    }
    private Task ChangeAsync(ChangeEventArgs args)
    {
        if (MainRecordings.FirstOrDefault(d => d.Stream.StreamId == args.Value?.ToString()) is { } dataset)
            Navigation.NavigateTo("/recordings/" + Uri.EscapeDataString(dataset.Manifest.DatasetId));
        return Task.CompletedTask;
    }
    private async Task SwitchAsync(FootballDataset dataset)
    {
        Pause(); var selection = ++_selectionGeneration; _generation++; _intentGeneration++; _request?.Cancel(); _highlightRequest?.Cancel(); _indexRetry?.Cancel();
        _view = null; _recapEvidence = null; _recapAttempt = null; _preparedRecap = null; _highlights = []; _sequenceIndex = []; _possessionIndex = []; _highlightHead = 0; _indexLoaded = false; _indexBusy = false; _selectedEvent = null; _personId = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; _continueGap = false; _stopAt = null; _selectedStream = dataset.Stream.StreamId;
        _cursorMs = dataset.Schedule!.Segments[0].CursorStartMs; _status = "Loading shared stream…"; _indexBusy = true;
        await FetchAsync(dataset); if (selection == _selectionGeneration && !_disposed) await LoadHighlightsAsync(dataset, selection);
    }
    private static FootballQuery Query(FootballDataset d, long cursor) => new(d.Manifest.DatasetId, d.Manifest.DatasetVersion, d.Stream.StreamId, d.Manifest.FixtureId, cursor,
        "retrospective_replay", d.Schedule!.PolicyId, d.Schedule.PolicyRevision);
    private async Task LoadHighlightsAsync(FootballDataset dataset, long selection)
    {
        await StopIndexRetryAsync();
        if (!IndexSelectionCurrent(dataset, selection)) return;
        _highlightRequest?.Cancel(); _highlightRequest?.Dispose(); _highlightRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); var token = _highlightRequest.Token;
        _indexBusy = true;
        _highlightStatus = "";
        var query = Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs);
        var retry = !dataset.Closed;
        try { var end = await Api.FootballViewAsync(query, token).WaitAsync(token);
            if (token.IsCancellationRequested || !IndexSelectionCurrent(dataset, selection)) return;
            if (IndexViewBound(end, dataset, query) && _view is { } current && IndexViewBound(current, dataset, Query(dataset, _cursorMs)))
            { _highlights = ExperiencePresentation.Highlights(end); _sequenceIndex = end.Events; _possessionIndex = end.Possessions.Where(r => MatchSequenceProjector.Possession(end.Dataset, r, end.Context.CursorMs) is not null).ToArray(); _highlightHead = dataset.HeadSequence; _indexLoaded = true; _highlightStatus = IndexStatus(dataset); }
            else { retry = true; _highlightStatus = "The recording advanced while the index loaded. Retrying automatically; the replay remains available."; }
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { if (token.IsCancellationRequested || !IndexSelectionCurrent(dataset, selection)) return;
            retry = true; _highlightStatus = "The retrospective index is unavailable. Retrying automatically; use Refresh to retry manually."; }
        if (retry && !token.IsCancellationRequested && IndexSelectionCurrent(dataset, selection))
        {
            var lifetime = _indexRetry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(5));
            _indexRetryTask = RetryIndexAsync(dataset, selection, lifetime);
        }
        else if (IndexSelectionCurrent(dataset, selection) && _highlightRequest?.Token == token) _indexBusy = false;
    }
    private bool IndexSelectionCurrent(FootballDataset dataset, long selection) => !_disposed && selection == _selectionGeneration
        && _selectedStream == dataset.Stream.StreamId;
    private static bool SameIndexDataset(FootballDataset a, FootballDataset b) => a.Manifest.DatasetId == b.Manifest.DatasetId
        && a.Manifest.DatasetVersion == b.Manifest.DatasetVersion && a.Manifest.FixtureId == b.Manifest.FixtureId
        && a.Stream == b.Stream && a.Schedule?.PolicyId == b.Schedule?.PolicyId && a.Schedule?.PolicyRevision == b.Schedule?.PolicyRevision;
    private static bool IndexViewBound(FootballView view, FootballDataset dataset, FootballQuery query) => view.Context == query
        && SameIndexDataset(view.Dataset, dataset) && view.Dataset.HeadSequence == dataset.HeadSequence && view.Dataset.Closed == dataset.Closed;
    private string IndexStatus(FootballDataset dataset) => !dataset.Closed
        ? "This recording is importing. The retrospective index reflects committed batches and will update automatically."
        : _highlights.Length == 0 ? "No reported events are present in this recording." : "";
    protected virtual Task WaitForIndexRetryAsync(CancellationToken token) => Task.Delay(TimeSpan.FromSeconds(5), token);
    protected virtual Task RenderIndexRetryAsync() => InvokeAsync(StateHasChanged);
    private async Task StopIndexRetryAsync()
    {
        var lifetime = _indexRetry; _indexRetry = null; lifetime?.Cancel();
        var pending = _indexRetryTask;
        if (pending is not null) await pending;
        if (ReferenceEquals(pending, _indexRetryTask)) { _indexRetryTask = null; _indexBusy = false; }
    }
    private async Task RetryIndexAsync(FootballDataset original, long selection, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token; var limitReached = false;
        try
        {
            for (var attempt = 0; attempt < IndexRetryAttempts; attempt++)
            {
                await WaitForIndexRetryAsync(token);
                if (!IndexSelectionCurrent(original, selection) || !ReferenceEquals(_indexRetry, lifetime)) return;
                var intent = _intentGeneration; var generation = _generation; var cursor = _cursorMs; var previous = _view;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token); request.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var catalog = await Api.FootballCatalogAsync(request.Token).WaitAsync(request.Token);
                    if (!IndexSelectionCurrent(original, selection) || token.IsCancellationRequested) return;
                    var updated = catalog.FirstOrDefault(d => SameIndexDataset(d, original) && d.HeadSequence >= original.HeadSequence);
                    if (updated is null || previous is null || updated.HeadSequence < previous.Dataset.HeadSequence)
                    { _highlightStatus = "Waiting for the selected recording's committed index. Retrying automatically."; }
                    else if (!updated.Closed && updated.HeadSequence == _highlightHead && previous.Dataset.HeadSequence == _highlightHead)
                    { _highlightStatus = IndexStatus(updated); }
                    else
                    {
                        var currentQuery = Query(updated, cursor); var endQuery = Query(updated, updated.Schedule!.Segments[^1].CursorEndMs);
                        var current = await Api.FootballViewAsync(currentQuery, request.Token).WaitAsync(request.Token);
                        var end = await Api.FootballViewAsync(endQuery, request.Token).WaitAsync(request.Token);
                        if (!IndexSelectionCurrent(original, selection) || token.IsCancellationRequested) return;
                        if (intent != _intentGeneration || generation != _generation || cursor != _cursorMs || !ReferenceEquals(previous, _view))
                        { _highlightStatus = "The replay changed while the index refreshed. Retrying at your current moment."; }
                        else if (!IndexViewBound(current, updated, currentQuery) || !IndexViewBound(end, updated, endQuery))
                        { _highlightStatus = "This recording is still being prepared. Highlights will retry automatically."; }
                        else
                        {
                            _catalog = _catalog.Select(d => d.Stream.StreamId == updated.Stream.StreamId ? updated : d).ToArray();
                            if (previous.Dataset.HeadSequence != updated.HeadSequence) { _selectedEvent = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; }
                            _view = current; ReconcileStorySelection(current); _highlights = ExperiencePresentation.Highlights(end); _sequenceIndex = end.Events; _possessionIndex = end.Possessions.Where(r => MatchSequenceProjector.Possession(end.Dataset, r, end.Context.CursorMs) is not null).ToArray(); _highlightHead = updated.HeadSequence; _indexLoaded = true;
                            _highlightStatus = IndexStatus(updated); _status = ReplayStatus(current);
                            await RenderIndexRetryAsync();
                            if (updated.Closed) return;
                            continue;
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    if (!IndexSelectionCurrent(original, selection)) return;
                    _highlightStatus = "The index refresh is unavailable. Retrying automatically; use Refresh to retry manually.";
                }
                if (!IndexSelectionCurrent(original, selection) || !ReferenceEquals(_indexRetry, lifetime)) return;
                await RenderIndexRetryAsync();
            }
            limitReached = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { limitReached = true; }
        finally
        {
            if (limitReached && IndexSelectionCurrent(original, selection) && ReferenceEquals(_indexRetry, lifetime))
            { _highlightStatus = "Automatic index refresh paused after its retry limit. The import may still be running; use Refresh to retry."; await RenderIndexRetryAsync(); }
            if (ReferenceEquals(_indexRetry, lifetime))
            {
                _indexRetry = null; _indexBusy = false;
                if (IndexSelectionCurrent(original, selection)) await RenderIndexRetryAsync();
            }
            lifetime.Dispose();
        }
    }
    private Task FetchAsync(FootballDataset? selected = null) => FetchCoreAsync(selected);
    private Task FetchCoreAsync(FootballDataset? selected = null, Guid? presentationId = null) => FetchContextAsync(selected, presentationId);
    private async Task FetchContextAsync(FootballDataset? selected, Guid? presentationId, CancellationToken playbackToken = default, long? playbackGeneration = null)
    {
        var dataset = selected ?? _catalog.FirstOrDefault(d => d.Stream.StreamId == _selectedStream); if (dataset?.Schedule is null) return;
        if (presentationId is { } id && ChatSession?.BeginEventPresentationFetch(id) != true)
        {
            if (!_updating && _view is { } preserved)
            {
                _cursorMs = preserved.Context.CursorMs;
                if (_eventPresentationContext is { } original) _selectedEvent = original.SelectedEvent;
                _status = ReplayStatus(preserved);
            }
            return;
        }
        var generation = ++_generation; _request?.Cancel(); _request?.Dispose(); _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, playbackToken); var token = _request.Token;
        _playbackRequestGeneration = playbackGeneration;
        var context = Query(dataset, _cursorMs);
        // Keep the accepted scene mounted. Its own clock remains visible; requested context cannot be used until commit.
        _updating = true; _contextUnavailable = true;
        if (presentationId is null) ChatSession?.Cancel(); // Invalidate pending replies before the next component render, retaining history.
        _status = _view is { } accepted ? $"Updating… Showing accepted replay {ExperiencePresentation.Clock(accepted.Context.CursorMs)} until the new moment is ready." : "Loading this replay context…";
        try
        {
            var response = Api.FootballViewAsync(context, token);
            var view = playbackGeneration is null ? await response : await response.WaitAsync(token);
            if (_disposed || token.IsCancellationRequested || generation != _generation || _selectedStream != context.StreamId || view.Context != context
                || playbackGeneration is { } owner && (!_playing || owner != _playbackGeneration)
                || view.Dataset.Manifest.DatasetId != context.DatasetId || view.Dataset.Manifest.DatasetVersion != context.DatasetVersion || view.Dataset.Stream.StreamId != context.StreamId) return;
            if (_selectedEvent is not null && _catalog.FirstOrDefault(d => d.Stream.StreamId == context.StreamId) is { } prior
                && (view.Dataset.HeadSequence != prior.HeadSequence || view.Dataset.Manifest.DatasetVersion != prior.Manifest.DatasetVersion)) { _selectedEvent = null; _highlights = []; _sequenceIndex = []; _possessionIndex = []; _highlightHead = 0; _indexLoaded = false; }
            _view = view; ReconcileStorySelection(view); _contextUnavailable = false; _status = ReplayStatus(view);
            await UpdateRecapAsync(view, generation, token);
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            if (view.TrackingGap && !_continueGap) Pause(); else if (!view.TrackingGap) _continueGap = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        { if (!_disposed && generation == _generation) { Pause(); _status = "This context cannot be loaded. Playback is paused; refresh if its version changed."; } }
        finally { if (generation == _generation) { _updating = false; _playbackRequestGeneration = null; } }
    }
    private Task SeekAsync(long cursor) => SeekCoreAsync(cursor, preserveSelectedGoal: false);
    private async Task SeekCoreAsync(long cursor, bool preserveSelectedGoal, Guid? presentationId = null)
    {
        if (!preserveSelectedGoal) _selectedEvent = null;
        _preparedPassage = null;
        if (_selectedPassage is { } passage && cursor < passage.EndCursorMs) _selectedPassage = null;
        var dataset = CurrentDataset; _intentGeneration++; Pause(); _stopAt = null;
        if (dataset?.Schedule is null) return;
        _cursorMs = Math.Clamp(cursor, dataset.Schedule.Segments[0].CursorStartMs, dataset.Schedule.Segments[^1].CursorEndMs);
        await FetchCoreAsync(dataset, presentationId);
    }
    private void SelectPerson(string id) { if (!InteractionBlocked) { _intentGeneration++; _personId = id; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; } }
    private void SetSpeed(int speed) { if (speed is 1 or 4 or 12 && speed != _speed) { _speed = speed; _motionEpoch++; } }
    private void Pause()
    {
        var playbackFetch = _playbackRequestGeneration == _playbackGeneration;
        _playing = false; _motionEpoch++; _playbackGeneration++; _playback?.Cancel();
        if (playbackFetch)
        {
            // Pause owns only its playback fetch; independent seeks/catalog work keep their existing lifecycle.
            _generation++; _request?.Cancel(); _playbackRequestGeneration = null; _updating = false;
            if (_view is { } accepted) { _cursorMs = accepted.Context.CursorMs; _contextUnavailable = false; _status = ReplayStatus(accepted); }
        }
    }
    private void ClearSelectedEvent() { if (!InteractionBlocked) _selectedEvent = null; }
    private async Task ToggleAsync()
    {
        var intent = ++_intentGeneration;
        if (_playing) { Pause(); return; } if (InteractionBlocked || CurrentDataset is not { Schedule: { } schedule } dataset || _view is null) return;
        var selection = _selectionGeneration;
        if (_playTask is not null) await _playTask;
        if (!AcceptedIntent(dataset, selection, intent)) return;
        if (_cursorMs >= schedule.Segments[^1].CursorEndMs)
        {
            _selectedEvent = null; _selectedPassage = null; _preparedPassage = null; _storyNotice = ""; _cursorMs = schedule.Segments[0].CursorStartMs; _stopAt = null; await FetchAsync(dataset);
            if (!AcceptedIntent(dataset, selection, intent)) return;
        }
        _playback?.Dispose(); _playback = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _playing = true;
        _playTask = PlayAsync(_playback.Token); await _playTask;
    }
    private async Task PlayAsync(CancellationToken token)
    {
        var playbackGeneration = _playbackGeneration;
        try
        {
            while (_playing && CurrentDataset?.Schedule is { } schedule)
            {
                await Task.Delay(100, token); token.ThrowIfCancellationRequested();
                var previous = _cursorMs; var end = _stopAt ?? schedule.Segments[^1].CursorEndMs;
                var next = Math.Min(end, previous + 100 * _speed);
                var half = schedule.Segments.Skip(1).FirstOrDefault(s => s.CursorStartMs > previous && s.CursorStartMs <= next);
                _cursorMs = half?.CursorStartMs ?? next;
                await FetchContextAsync(null, null, token, playbackGeneration); token.ThrowIfCancellationRequested();
                if (half is not null) { Pause(); _status = "Half-time / period boundary. Resume when you are ready."; }
                if (_cursorMs >= end) { Pause(); _stopAt = null; }
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private Task ContinueAsync() { _continueGap = true; return ToggleAsync(); }
    private Task NextAsync() => CurrentDataset?.Schedule?.Milestones.FirstOrDefault(m => m.CursorMs > _cursorMs) is { } next ? SeekAsync(next.CursorMs) : Task.CompletedTask;
    private Task PreviousAsync() => CurrentDataset?.Schedule?.Milestones.LastOrDefault(m => m.CursorMs < _cursorMs) is { } previous ? SeekAsync(previous.CursorMs) : Task.CompletedTask;
    private Task HighlightAsync(ReplayHighlight highlight) => HighlightCoreAsync(highlight);
    private async Task ShowEventOnPitchAsync(MatchEventVisualAction action)
    {
        if (InteractionBlocked || ChatSession is null || ConversationContext is not { } context
            || CurrentDataset is not { } dataset || !SequenceIndexAvailable || _highlightHead != dataset.HeadSequence) return;
        var intent = _intentGeneration;
        var transition = await ChatSession.BeginEventPresentationAsync(action, context, dataset, PresentationIndex);
        if (transition is null) { _status = "That event diagram is no longer current. Ask again from a ready replay moment."; return; }
        try
        {
            if (_disposed || intent != _intentGeneration || ConversationContext != context || InteractionBlocked) return;
            var highlight = _highlights.SingleOrDefault(h => h.RecordId == transition.Selection.RecordId && h.Kind == transition.Selection.Kind
                && h.CursorMs == transition.Selection.ReleaseCursorMs && h.ReportedClock == transition.Selection.ReportedClock);
            if (highlight is null) return;
            _eventPresentationContext = context; _eventPresentationId = transition.Id;
            await HighlightCoreAsync(highlight, transition);
        }
        finally { if (_eventPresentationId == transition.Id) { _eventPresentationContext = null; _eventPresentationId = null; } ChatSession.CancelEventPresentation(transition.Id); }
    }
    private async Task HighlightCoreAsync(ReplayHighlight highlight, MatchEventPresentationTransition? presentation = null)
    {
        if (CurrentDataset is not { Schedule: { } schedule } dataset) return;
        if (_highlightHead != dataset.HeadSequence) { _highlightStatus = "The recording changed. Its index is refreshing; use Refresh to retry manually."; return; }
        var released = highlight.Kind == "completed_possession"
            ? _possessionIndex.Count(r => r.RecordId == highlight.RecordId && MatchSequenceProjector.Possession(dataset, r, schedule.Segments[^1].CursorEndMs) is { } p
                && p.ReleaseCursorMs == highlight.CursorMs && MatchEventVisual.IntervalLabel(p) == highlight.ReportedClock) == 1
            : schedule.Releases.Any(release => release.RecordId == highlight.RecordId && release.CursorMs == highlight.CursorMs);
        if (!_highlights.Contains(highlight) || !released) return;
        var selection = _selectionGeneration;
        _selectedPassage = null; _preparedPassage = null; _storyNotice = "";
        _selectedEvent = MatchEventVisual.SupportedKind(highlight.Kind) ? new(highlight.RecordId, highlight.CursorMs, highlight.ReportedClock, highlight.Kind) : null;
        var target = Math.Clamp(highlight.Kind == "goal" ? highlight.CursorMs - 15000 : highlight.CursorMs, schedule.Segments[0].CursorStartMs, schedule.Segments[^1].CursorEndMs);
        var seeking = SeekCoreAsync(target, preserveSelectedGoal: true, presentation?.Id); var intent = _intentGeneration; await seeking;
        // F-UI-003: a completed old seek cannot start another recording or supersede a newer seek.
        if (!AcceptedIntent(dataset, selection, intent) || _contextUnavailable || _view!.Context.CursorMs != target) return;
        if (presentation is not null)
        {
            if (ChatSession is null || ReadyConversationContext is not { } context
                || !await ChatSession.CompleteEventPresentationAsync(presentation.Id, context, _view.Dataset, PresentationIndex)) return;
            if (_eventPresentationId == presentation.Id) { _eventPresentationContext = null; _eventPresentationId = null; }
            if (!AcceptedIntent(dataset, selection, intent)) return;
            _pitchInterop ??= new ReplayPitchInterop(JS);
            await _pitchInterop.FocusAsync(_pitchElement);
            if (!AcceptedIntent(dataset, selection, intent)) return;
        }
        _eventSelectionVersion++;
        if (highlight.Kind == "goal") { _speed = 1; _stopAt = Math.Min(schedule.Segments[^1].CursorEndMs, highlight.CursorMs + 5000); await ToggleAsync(); }
    }
    private bool AcceptedIntent(FootballDataset dataset, long selection, long intent) => !_disposed && selection == _selectionGeneration
        && intent == _intentGeneration && _selectedStream == dataset.Stream.StreamId && _view?.Context == Query(dataset, _cursorMs)
        && _view.Dataset.Manifest.DatasetId == dataset.Manifest.DatasetId && _view.Dataset.Manifest.DatasetVersion == dataset.Manifest.DatasetVersion;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; if (LanguageState is not null) LanguageState.Changed -= LanguageChanged; _lifetime.Cancel(); _request?.Cancel(); _catalogRequest?.Cancel(); _highlightRequest?.Cancel(); Pause();
        await StopIndexRetryAsync();
        if (_pitchInterop is not null) await _pitchInterop.DisposeAsync();
        if (_playTask is not null) await _playTask; _request?.Dispose(); _playback?.Dispose(); _catalogRequest?.Dispose(); _highlightRequest?.Dispose(); _lifetime.Dispose();
    }
}
