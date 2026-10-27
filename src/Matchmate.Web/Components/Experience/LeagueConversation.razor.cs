using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Matchmate.Web.Components.Experience;

public partial class LeagueConversation
{
    [Inject] private LeagueChatSession Session { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] public MatchLanguage Language { get; set; }
    private string T(string key, params object?[] args) => ConversationText.Get(Language, key, args);
    [Parameter] public LeagueChatContext? Context { get; set; }
    [Parameter] public LeagueClub? Club { get; set; }
    [Parameter] public string SeasonLabel { get; set; } = "";
    [Parameter] public bool Ongoing { get; set; }
    [Parameter] public string? ResultsThrough { get; set; }
    [Parameter] public bool Updating { get; set; }
    [Parameter] public EventCallback OnAsking { get; set; }
    [CascadingParameter] public Task<AuthenticationState>? Authentication { get; set; }
    private readonly string _inputId = "league-question-" + Guid.NewGuid().ToString("N");
    private readonly string _panelId = "league-conversation-" + Guid.NewGuid().ToString("N");
    private ElementReference _input, _thread;
    private ThreadScrollInterop? _threadScroll;
    private (long Intent, Guid TurnId)? _revealTurn;
    private bool _expanded, _focusComposer;
    private LeagueChatContext? _last;
    private Task<AuthenticationState>? _lastAuthentication;
    private CancellationTokenSource? _request;
    private string _question = "", _notice = "";
    private string? _noticeKey;
    private bool _languageCancelledPending;
    private MatchLanguage _lastLanguage;
    private string Notice => _noticeKey == "LanguageChanged" ? T(_noticeKey, MatchLanguages.Name(Language), T(_languageCancelledPending ? "PendingCancelledDraft" : "EarlierLanguageKept"))
        : _noticeKey is { } key ? T(key) : _notice;
    private bool _pending, _disposed, _lastUpdating;
    private long _intent;
    private bool Blocked => _pending || Updating || Context is null;
    protected override void OnParametersSet()
    {
        if (_last != Context || Updating && !_lastUpdating || _lastAuthentication != Authentication)
        {
            _intent++; _request?.Cancel(); _pending = false; _notice = ""; _noticeKey = null;
            if (_last?.SeasonKey != Context?.SeasonKey || _lastAuthentication != Authentication) _question = "";
        }
        _last = Context; _lastUpdating = Updating; _lastAuthentication = Authentication;
        Session.SetContext(Updating ? null : Context);
        var language = MatchLanguages.Normalize(Language);
        if (_lastLanguage != language)
        {
            _languageCancelledPending = _pending;
            // Preserve the session's last accepted reference before cancelling the linked UI token.
            Session.SetLanguage(language);
            _intent++; _request?.Cancel(); _pending = false; _notice = ""; _noticeKey = "LanguageChanged";
            _lastLanguage = language;
        }
        Session.SetLanguage(language);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _threadScroll ??= new(JS);
        if (!await _threadScroll.InitializeAsync() || _disposed) return;
        string? reveal = null;
        if (_revealTurn is { } turn)
        {
            if (turn.Intent != _intent) _revealTurn = null;
            else if (_expanded) { reveal = turn.TurnId.ToString("N"); _revealTurn = null; }
        }
        await _threadScroll.UpdateAsync(_thread, _intent, _pending, reveal);
        if (_disposed || !_focusComposer) return;
        _focusComposer = false;
        if (!Blocked) await _input.FocusAsync(preventScroll: true);
    }
    private void ToggleConversation()
    {
        _expanded = !_expanded;
        _focusComposer = _expanded;
    }
    private Task SubmitAsync() => SendAsync(null);
    private async Task SendAsync(string? source)
    {
        if (Blocked || _disposed || Context is not { } context || source is null && string.IsNullOrWhiteSpace(_question)) return;
        _expanded = true;
        var question = _question; var language = MatchLanguages.Normalize(Language); var intent = ++_intent; _pending = true; _notice = ""; _noticeKey = null;
        _request?.Cancel(); _request?.Dispose(); _request = new(); var token = _request.Token;
        try
        {
            await OnAsking.InvokeAsync();
            if (_disposed || Updating || intent != _intent || Context != context || language != MatchLanguages.Normalize(Language)) return;
            Session.SetLanguage(language);
            var result = source is null ? await Session.AskAsync(context, question, token) : await Session.AskSourceAsync(context, source, token);
            if (_disposed || Updating || intent != _intent || Context != context || language != MatchLanguages.Normalize(Language)) return;
            if (result.Accepted)
            {
                _question = "";
                if (Session.Turns.LastOrDefault() is { } turn) _revealTurn = (intent, turn.Id);
            }
            else _notice = result.Message;
        }
        finally { if (!_disposed && intent == _intent) _pending = false; }
    }
    private void Cancel() { _intent++; _request?.Cancel(); Session.Cancel(); _pending = false; _notice = ""; _noticeKey = "ReplyCancelled"; }
    private void Reset() { Cancel(); Session.NewChat(); _question = ""; _noticeKey = "NewChatNotice"; _expanded = false; _focusComposer = true; }
    private static string Label(LeagueChatTurn turn) => turn.Outcome switch
    { MatchChatOutcome.Answered => ConversationText.Get(turn.Language, turn.SourceOnly ? "SourceAnswer" : "MatchmateAi"), MatchChatOutcome.Clarify => ConversationText.Get(turn.Language, "Clarification"), MatchChatOutcome.Unsupported => ConversationText.Get(turn.Language, "Unsupported"), _ => ConversationText.Get(turn.Language, "Unavailable") };
    private static string FactLabel(string id, MatchLanguage language) => ConversationText.Get(language, id switch { "table_snapshot" => "Table", "club_standing" => "ClubStanding", "club_results" => "RecentResults", "date_change" => "DateChanges", "ground_reference" => "GroundReference", _ => "Coverage" });
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _intent++; _request?.Cancel(); _request?.Dispose(); Session.NewChat(); Session.SetContext(null);
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_threadScroll is not null) await _threadScroll.DisposeAsync();
    }
    private sealed class ThreadScrollInterop(IJSRuntime js) : IAsyncDisposable
    {
        private const string ModulePath = "./Components/Experience/LeagueConversation.razor.js";
        private const string ImportMethod = "import", UpdateMethod = "updateThread";
        private Task<IJSObjectReference>? _module;
        public async Task<bool> InitializeAsync()
        {
            try { await (_module ??= js.InvokeAsync<IJSObjectReference>(ImportMethod, ModulePath).AsTask()); return true; }
            catch (JSDisconnectedException) { return false; }
        }
        public async ValueTask UpdateAsync(ElementReference thread, long intent, bool pending, string? revealTurn)
        {
            try { if (_module is not null) await (await _module).InvokeVoidAsync(UpdateMethod, thread, intent, pending, revealTurn); }
            catch (JSDisconnectedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            if (_module is null) return;
            try { await (await _module).DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
