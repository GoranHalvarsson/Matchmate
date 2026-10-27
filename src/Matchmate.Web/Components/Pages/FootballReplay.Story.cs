using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Components.Pages;

public partial class FootballReplay
{
    private MatchSelectedPassage? _selectedPassage;
    private ReplayStoryPreparation? _preparedPassage;
    private string _storyNotice = "";
    private ReplayStoryItem[]? CurrentStory => _eventPresentationContext is null && _view?.Context.StreamId == _selectedStream ? ReplayStory.Project(_view, Language) : null;
    private Task SelectStoryPassageAsync(ReplayStoryAction action) => SelectStoryPassageCoreAsync(action);
    private Task PrepareStoryQuestionAsync(ReplayStoryAction action)
    {
        if (_disposed || InteractionBlocked || _view is not { } accepted || !ReplayStory.Matches(action, accepted)
            || _cursorMs != accepted.Context.CursorMs || CurrentDataset is not { } dataset
            || _selectedStream != dataset.Stream.StreamId || accepted.Context != Query(dataset, _cursorMs)
            || !ReplayRecap.SameAuthority(accepted.Dataset, dataset)
            || _catalog.FirstOrDefault(item => item.Stream.StreamId == _selectedStream) is not { } catalog
            || !ReplayRecap.SameAuthority(catalog, dataset)) return Task.CompletedTask;
        if (_selectedPassage != action.Passage || _personId is not null || _selectedEvent is not null) _intentGeneration++;
        Pause(); _stopAt = null; _personId = null; _selectedEvent = null; _selectedPassage = action.Passage; _storyNotice = "";
        if (ReadyConversationContext is { } context) _preparedPassage = new(Guid.NewGuid(), action, context);
        return Task.CompletedTask;
    }
    private async Task SelectStoryPassageCoreAsync(ReplayStoryAction action)
    {
        if (_disposed || InteractionBlocked || _view is not { } accepted || !ReplayStory.Matches(action, accepted)) return;
        var selection = _selectionGeneration; _storyNotice = "";
        Pause();
        // Selecting this accepted end again keeps history and any in-flight question.
        if (_selectedPassage == action.Passage && _personId is null && _selectedEvent is null
            && accepted.Context.CursorMs == action.Passage.EndCursorMs && _cursorMs == accepted.Context.CursorMs
            && CurrentDataset is { } dataset && _selectedStream == dataset.Stream.StreamId
            && accepted.Context == Query(dataset, _cursorMs) && ReplayRecap.SameAuthority(accepted.Dataset, dataset)
            && _catalog.FirstOrDefault(item => item.Stream.StreamId == _selectedStream) is { } catalog
            && ReplayRecap.SameAuthority(catalog, dataset)) return;
        _personId = null; _selectedEvent = null; _selectedPassage = null; _preparedPassage = null;
        var seeking = SeekAsync(action.Passage.EndCursorMs); var intent = _intentGeneration; await seeking;
        if (_disposed || intent != _intentGeneration || selection != _selectionGeneration) return;
        if (InteractionBlocked || _view is not { } current
            || current.Context != (action.Query with { CursorMs = action.Passage.EndCursorMs })
            || !ReplayRecap.SameAuthority(accepted.Dataset, current.Dataset) || ReplayStory.Find(current, action.Passage, Language) is null)
        { _storyNotice = T("StoryUnavailable"); return; }
        _selectedPassage = action.Passage;
    }
    private void ClearSelectedPassage()
    {
        if (InteractionBlocked) return;
        _intentGeneration++; _selectedPassage = null; _preparedPassage = null; _storyNotice = "";
    }
    private void ReconcileStorySelection(FootballView accepted)
    {
        if (_selectedPassage is not null && ReplayStory.Find(accepted, _selectedPassage) is null)
        { _selectedPassage = null; _preparedPassage = null; }
    }
}
