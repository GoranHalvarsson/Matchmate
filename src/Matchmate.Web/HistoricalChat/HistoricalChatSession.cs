using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Matchmate.Web.HistoricalChat;

// Scoped to one authenticated Blazor circuit, separate from replay and league conversations.
public sealed class HistoricalChatSession : IDisposable
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly HistoricalEvidence _evidence;
    private readonly IHistoricalWorkflowRouter _router;
    private readonly IHistoricalChatModel _model;
    private readonly MatchChatDiagnostics? _diagnostics;
    private readonly ILogger<HistoricalChatSession>? _logger;
    private readonly int _timeout;
    private readonly object _gate = new();
    private readonly List<HistoricalChatTurn> _turns = [];
    private HistoricalChatContext? _context;
    private HistoricalTopic? _topic;
    private string? _owner;
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;
    public HistoricalChatSession(AuthenticationStateProvider authentication, ReplayApiClient api, IHistoricalWorkflowRouter router, IHistoricalChatModel model,
        IOptions<MatchChatOptions> options, MatchChatDiagnostics? diagnostics = null, ILogger<HistoricalChatSession>? logger = null) : this(authentication, new HistoricalEvidence(api), router, model, options, diagnostics, logger) { }
    public HistoricalChatSession(AuthenticationStateProvider authentication, HistoricalEvidence evidence, IHistoricalWorkflowRouter router, IHistoricalChatModel model,
        IOptions<MatchChatOptions> options, MatchChatDiagnostics? diagnostics = null, ILogger<HistoricalChatSession>? logger = null)
    {
        _authentication = authentication; _evidence = evidence; _router = router; _model = model; _diagnostics = diagnostics; _logger = logger;
        _timeout = Math.Clamp(options.Value.TimeoutSeconds, 1, 60);
        authentication.AuthenticationStateChanged += AuthenticationChanged;
    }
    private MatchLanguage _language = MatchLanguage.English;
    public MatchLanguage Language { get { lock (_gate) return _language; } }
    public bool SetLanguage(MatchLanguage language)
    {
        lock (_gate)
        {
            if (_disposed || !Enum.IsDefined(language) || _language == language) return false;
            _language = language; _generation++; _pending?.Cancel();
            return true;
        }
    }
    public bool Configured => _router.Configured && _model.Configured;
    public IReadOnlyList<HistoricalChatTurn> Turns { get { lock (_gate) return _turns.ToArray(); } }
    public void SetContext(HistoricalChatContext? context)
    {
        lock (_gate)
        {
            if (_disposed || _context == context) return;
            CancelLocked(); ClearLocked(); _context = context;
        }
    }
    public void Cancel() { lock (_gate) { CancelLocked(); _topic = null; } }
    public void NewChat() { lock (_gate) { CancelLocked(); ClearLocked(); } }
    private void ClearLocked() { _turns.Clear(); _topic = null; }
    private void CancelLocked() { _generation++; _pending?.Cancel(); }
    private void AuthenticationChanged(Task<AuthenticationState> state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // A same-circuit reconnect can publish a new completed task for the already-bound eligible owner.
            if (_owner is not null && state is { IsCompletedSuccessfully: true } && state.Result is { } resolved && Owner(resolved.User) == _owner) return;
            CancelLocked(); ClearLocked(); _owner = null;
        }
    }
    private bool Current(long generation, HistoricalChatContext context, string? owner)
    { lock (_gate) return !_disposed && _generation == generation && _context == context && (owner is null || _owner == owner); }
    private static string? Owner(ClaimsPrincipal user)
    {
        var subject = user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier);
        return user.Identity?.IsAuthenticated == true && user.IsInRole("Tester") && subject is not null
            ? subject.Issuer + "\u001f" + (user.FindFirst("tid")?.Value ?? "") + "\u001f" + subject.Value : null;
    }
    public Task<MatchChatResult> AskAsync(HistoricalChatContext context, string question, CancellationToken cancellation = default) => AskCoreAsync(context, question, null, cancellation);
    public Task<MatchChatResult> AskSuggestionAsync(HistoricalChatContext context, string suggestionId, CancellationToken cancellation = default)
    {
        var suggestion = HistoricalWorkflows.Suggestion(suggestionId);
        if (suggestion is null) { Cancel(); return Task.FromResult(new MatchChatResult(HistoricalChatText.Get(Language, "Feedback.Source"), false, MatchChatOutcome.InvalidRequest)); }
        return AskCoreAsync(context, suggestion.Question, suggestion, cancellation);
    }
    private async Task<MatchChatResult> AskCoreAsync(HistoricalChatContext context, string question, HistoricalSuggestion? suggestion, CancellationToken cancellation)
    {
        if (!HistoricalChatContext.Valid(context) || string.IsNullOrWhiteSpace(question) || question.Length > MatchChatLimits.InputChars || HistoricalClubResolver.Normalize(question).Length == 0)
        { Cancel(); return new(HistoricalChatText.Get(Language, "Feedback.Input"), false, MatchChatOutcome.InvalidRequest); }
        question = question.Trim(); CancellationTokenSource request; long generation; MatchLanguage language;
        lock (_gate)
        {
            if (_disposed || _context != context) return Stale();
            language = _language;
            if (suggestion is not null) question = HistoricalChatPresentation.DisplayQuestion(suggestion, language);
            CancelLocked(); generation = _generation;
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellation); request.CancelAfter(TimeSpan.FromSeconds(_timeout)); _pending = request;
        }
        string? owner = null;
        var stage = HistoricalChatStage.Authentication;
        HistoricalSuggestionAnswer? source = null;
        var sourceCommit = false;
        using var trace = _diagnostics?.BeginRequest();
        using var timing = _diagnostics?.Measure(MatchChatTimingStage.SessionTotal);
        try
        {
            owner = Owner((await _authentication.GetAuthenticationStateAsync().WaitAsync(request.Token)).User);
            HistoricalRoutingInput input;
            lock (_gate)
            {
                if (!Current(generation, context, null)) return Stale();
                if (owner is null) { ClearLocked(); _owner = null; return new(HistoricalChatText.Get(Language, "Feedback.SignIn"), false, MatchChatOutcome.Disconnected); }
                if (_owner != owner) { ClearLocked(); _owner = owner; }
                if (!Configured) return new(HistoricalChatText.Get(Language, "Feedback.Disconnected"), false, MatchChatOutcome.Disconnected);
                input = new(question, context, _turns.TakeLast(MatchChatLimits.Turns).Select(t => new HistoricalQuestion(t.Question, t.ClubId)).ToImmutableArray(), _topic);
            }
            stage = HistoricalChatStage.Routing;
            var route = suggestion is null ? await _router.RouteAsync(input, request.Token).WaitAsync(request.Token)
                : new MatchRoutingResult(MatchRoutingStatus.Selected, suggestion.Workflow);
            if (!Current(generation, context, owner)) return Stale(); request.Token.ThrowIfCancellationRequested();
            if (route.Status != MatchRoutingStatus.Selected)
            {
                if (route.Status == MatchRoutingStatus.Unavailable) HistoricalChatDiagnostics.Record(_logger, stage, HistoricalChatFailureReason.RoutingFailure);
                if (route.Status == MatchRoutingStatus.InvalidResponse) HistoricalChatDiagnostics.Record(_logger, stage, HistoricalChatFailureReason.InvalidRoutingResponse);
                stage = HistoricalChatStage.Commit;
                return await CommitAsync(question, route.Status == MatchRoutingStatus.Clarify ? "I couldn’t confidently interpret that question for this match. Please rephrase it, or choose a suggested question."
                    : "I could not route that question right now. Please try again.", context, [], route.Status == MatchRoutingStatus.Clarify ? MatchChatOutcome.Clarify : MatchChatOutcome.RoutingUnavailable, null, null, generation, owner, request.Token);
            }
            var workflow = route.WorkflowId;
            if (workflow == "continue_topic") workflow = input.Topic?.Workflow;
            if (workflow == "unsupported" || !HistoricalTimeScope.SelectedFixture(suggestion?.Question ?? question, context))
            {
                stage = HistoricalChatStage.Commit;
                return await CommitAsync(question, "I can explore the result, how the teams arrived, their table positions, earlier meetings and season ground references. This result record does not contain player details, goal events or tactical evidence.",
                    context, [], MatchChatOutcome.Unsupported, null, null, generation, owner, request.Token);
            }
            if (!HistoricalWorkflows.ValidTopic(new(workflow ?? "", null)))
            {
                stage = HistoricalChatStage.Commit;
                return await CommitAsync(question, "Could you say which team or result you mean? Name the team, or add a little more detail to your question.", context, [], MatchChatOutcome.Clarify, null, null, generation, owner, request.Token);
            }
            stage = HistoricalChatStage.Retrieval;
            var loaded = await _evidence.LoadAsync(context, request.Token).WaitAsync(request.Token);
            if (!Current(generation, context, owner)) return Stale(); request.Token.ThrowIfCancellationRequested();
            stage = HistoricalChatStage.EvidencePreparation;
            var subject = HistoricalClubResolver.Resolve(loaded.Match, loaded.Clubs, suggestion?.Question ?? question, suggestion is null ? input.Topic?.ClubId : null, workflow!);
            if (subject.Clarification is not null)
            {
                stage = HistoricalChatStage.Commit;
                return await CommitAsync(question, subject.Clarification, context, [], MatchChatOutcome.Clarify, null, new(workflow!, null), generation, owner, request.Token);
            }
            var prepared = HistoricalEvidence.Prepare(loaded.Match, loaded.Clubs, workflow!, subject.ClubId);
            if (suggestion is not null)
            {
                source = HistoricalSuggestionAnswers.Prepare(suggestion, prepared, loaded.Match, language);
                if (source.EmptyForm)
                {
                    stage = HistoricalChatStage.Commit; sourceCommit = true;
                    return await CommitAsync(question, source.Answer, context, source.Evidence, MatchChatOutcome.Answered, subject.ClubId,
                        new(workflow!, subject.ClubId), generation, owner, request.Token, sourceOnly: true);
                }
            }
            var narrator = new HistoricalNarratorRequest([new("system", HistoricalGuidePrompt.Version + "\n" + HistoricalGuidePrompt.Text),
                HistoricalChatPresentation.NarratorLanguage(language),
                new("system", "Server-prepared evidence for this fixture and turn only:\n" + JsonSerializer.Serialize(prepared.Evidence)),
                new("system", "Trusted topic/subject and untrusted earlier USER intent (not facts):\n" + JsonSerializer.Serialize(new { prepared.Workflow, prepared.ClubId, input.PreviousQuestions })),
                new("user", question)], prepared.Evidence.Select(e => e.Id));
            stage = HistoricalChatStage.Narration;
            var reply = await _model.CompleteAsync(narrator, request.Token).WaitAsync(request.Token);
            if (!Current(generation, context, owner)) return Stale(); request.Token.ThrowIfCancellationRequested();
            stage = HistoricalChatStage.ReplyValidation;
            if (reply.Calls.Length != 0) throw new InvalidDataException("Historical narrator attempted a tool call.");
            var validated = LocalizedHistoricalReplyValidation.Validate(reply.Content, prepared, language);
            stage = HistoricalChatStage.Commit;
            return await CommitAsync(question, validated.Answer, context, validated.Evidence, MatchChatOutcome.Answered, subject.ClubId, new(workflow!, subject.ClubId), generation, owner, request.Token);
        }
        catch (OperationCanceledException)
        {
            HistoricalChatDiagnostics.Record(_logger, stage, request.IsCancellationRequested && !cancellation.IsCancellationRequested && Current(generation, context, owner)
                ? HistoricalChatFailureReason.Timeout : HistoricalChatFailureReason.Cancelled);
            lock (_gate) if (_generation == generation) _topic = null;
            return new(HistoricalChatText.Get(Language, "Feedback.Cancelled"), false, MatchChatOutcome.Cancelled);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException or IOException
            or System.ClientModel.ClientResultException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
        {
            var reason = HistoricalChatDiagnostics.Classify(stage, e);
            HistoricalChatDiagnostics.Record(_logger, stage, reason);
            if (sourceCommit) { lock (_gate) if (_generation == generation) _topic = null; return Stale(); }
            if (owner is not null && Current(generation, context, owner) && !request.IsCancellationRequested)
            {
                if (source is not null && stage is HistoricalChatStage.Narration or HistoricalChatStage.ReplyValidation
                    && reason != HistoricalChatFailureReason.ProviderAuthentication)
                {
                    try
                    {
                        stage = HistoricalChatStage.Commit;
                        return await CommitAsync(question, source.RecoveryAnswer, context, source.Evidence, MatchChatOutcome.NarratorUnavailable, null,
                            new(suggestion!.Workflow, null), generation, owner, request.Token, sourceOnly: true);
                    }
                    catch (OperationCanceledException) { lock (_gate) if (_generation == generation) _topic = null; return Stale(); }
                    catch (Exception commitError)
                    {
                        HistoricalChatDiagnostics.Record(_logger, HistoricalChatStage.Commit, HistoricalChatDiagnostics.Classify(HistoricalChatStage.Commit, commitError));
                        lock (_gate) if (_generation == generation) _topic = null;
                        return Stale();
                    }
                }
                try { stage = HistoricalChatStage.Commit; return await CommitAsync(question, "I could not verify a reply for this match. Please try again.", context, [], MatchChatOutcome.Unavailable, null, null, generation, owner, request.Token); }
                catch (OperationCanceledException) { return Stale(); }
            }
            return Stale();
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_pending, request)) _pending = null;
            request.Dispose();
        }
    }
    private async Task<MatchChatResult> CommitAsync(string question, string answer, HistoricalChatContext context, MatchChatEvidence[] evidence,
        MatchChatOutcome outcome, string? club, HistoricalTopic? topic, long generation, string owner, CancellationToken token, bool sourceOnly = false)
    {
        var latest = Owner((await _authentication.GetAuthenticationStateAsync().WaitAsync(token)).User);
        lock (_gate)
        {
            if (!Current(generation, context, owner) || token.IsCancellationRequested) return Stale();
            if (latest != owner) { ClearLocked(); _owner = null; return Stale(); }
            answer = outcome is MatchChatOutcome.Answered or MatchChatOutcome.NarratorUnavailable ? answer : HistoricalChatPresentation.Feedback(answer, outcome, _language);
            _turns.Add(new(question, answer, context, evidence, outcome, club, sourceOnly, _language));
            while (_turns.Count > MatchChatLimits.Turns) _turns.RemoveAt(0);
            _topic = topic;
            return new(answer, true, outcome, club);
        }
    }
    private MatchChatResult Stale() => new(HistoricalChatPresentation.Feedback("The match or signed-in session changed; reply discarded.", MatchChatOutcome.Stale, Language), false, MatchChatOutcome.Stale);
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; CancelLocked(); ClearLocked(); _owner = null; _context = null; }
        _authentication.AuthenticationStateChanged -= AuthenticationChanged;
    }
}
