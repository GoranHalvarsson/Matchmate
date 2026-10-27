using System.Text.Json;
using Matchmate.Web.HistoricalChat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Matchmate.Web.Components.Experience;

public partial class HistoricalConversation
{
    [Inject] private HistoricalChatSession Session { get; set; } = default!;
    [CascadingParameter] public MatchLanguage Language { get; set; }
    private string T(string key, params object?[] args) => ConversationText.Get(Language, key, args);
    [Parameter, EditorRequired] public HistoricalChatContext? Context { get; set; }
    [CascadingParameter] public Task<AuthenticationState>? Authentication { get; set; }
    private static IReadOnlyList<HistoricalSuggestion> Suggestions => HistoricalWorkflows.Suggestions;
    private readonly string _inputId = "historical-question-" + Guid.NewGuid().ToString("N");
    private HistoricalChatContext? _last;
    private Task<AuthenticationState>? _lastAuthentication;
    private CancellationTokenSource? _request;
    private string _question = "", _notice = "", _pendingQuestion = "";
    private string? _noticeKey;
    private bool _languageCancelledPending;
    private MatchLanguage _lastLanguage;
    private string Notice => _noticeKey == "LanguageChanged" ? T(_noticeKey, MatchLanguages.Name(Language), T(_languageCancelledPending ? "PendingCancelledDraft" : "EarlierLanguageKept"))
        : _noticeKey is { } key ? T(key) : _notice;
    private bool _pending, _disposed;
    private long _intent;
    private bool Blocked => _pending || Context is null || !Session.Configured;
    protected override void OnParametersSet()
    {
        if (_last != Context || _lastAuthentication != Authentication)
        {
            _intent++; _request?.Cancel(); _pending = false; _notice = ""; _noticeKey = null; _question = ""; _pendingQuestion = "";
        }
        _last = Context; _lastAuthentication = Authentication;
        Session.SetContext(Context);
        var language = MatchLanguages.Normalize(Language);
        if (_lastLanguage != language)
        {
            _languageCancelledPending = _pending;
            Session.SetLanguage(language);
            _intent++; _request?.Cancel(); _pending = false; _pendingQuestion = ""; _notice = ""; _noticeKey = "LanguageChanged";
            _lastLanguage = language;
        }
        Session.SetLanguage(language);
    }
    private Task SubmitAsync() => SendAsync(_question);
    private Task SendSuggestionAsync(string id)
    {
        var suggestion = HistoricalWorkflows.Suggestion(id);
        return suggestion is null ? Task.CompletedTask : SendAsync(HistoricalChatPresentation.DisplayQuestion(suggestion, Language), id);
    }
    private async Task SendAsync(string question, string? suggestionId = null)
    {
        if (Blocked || _disposed || Context is not { } context || string.IsNullOrWhiteSpace(question)) return;
        var language = MatchLanguages.Normalize(Language); var intent = ++_intent; _pending = true; _notice = ""; _noticeKey = null; _pendingQuestion = question;
        _request?.Cancel(); _request?.Dispose(); _request = new(); var token = _request.Token;
        try
        {
            Session.SetLanguage(language);
            var result = suggestionId is null ? await Session.AskAsync(context, question, token) : await Session.AskSuggestionAsync(context, suggestionId, token);
            if (_disposed || intent != _intent || Context != context || language != MatchLanguages.Normalize(Language)) return;
            if (result.Accepted) _question = ""; else _notice = result.Message;
        }
        finally { if (!_disposed && intent == _intent) { _pending = false; _pendingQuestion = ""; } }
    }
    private void Cancel() { _intent++; _request?.Cancel(); Session.Cancel(); _pending = false; _pendingQuestion = ""; _notice = ""; _noticeKey = "ReplyCancelled"; }
    private void Reset() { Cancel(); Session.NewChat(); _question = ""; _noticeKey = "NewChatNotice"; }
    private static string FactLabel(string id, MatchLanguage language) => ConversationText.Get(language, id switch { "fixture" => "Result", "form" => "BeforeMatch", "table" => "EndDateStandings", _ => "MeetingsGround" });
    private static string Supporting(Matchmate.Web.Chat.MatchChatEvidence fact)
    { using var doc = JsonDocument.Parse(fact.Text); return doc.RootElement.GetProperty("sourceAnswer").GetString() ?? ""; }
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true; _intent++; _request?.Cancel(); _request?.Dispose(); Session.SetContext(null);
        return ValueTask.CompletedTask;
    }
}
