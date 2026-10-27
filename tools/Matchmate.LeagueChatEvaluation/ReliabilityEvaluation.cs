using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;

namespace Matchmate.LeagueChatEvaluation;

public sealed record ReliabilityExpected([property: JsonRequired] string Workflow, [property: JsonRequired] string Family,
    [property: JsonRequired] string Outcome, [property: JsonRequired] string? SubjectId, [property: JsonRequired] string[] EvidenceIds,
    [property: JsonRequired] string Binding, [property: JsonRequired] string TimeScope, string? TimeIntent = null);
public sealed record ReliabilityCase([property: JsonRequired] string Id, [property: JsonRequired] string Question,
    [property: JsonRequired] LeagueRoutingContext Context, [property: JsonRequired] ReliabilityExpected Expected,
    string? ReferenceClubId = null, LeagueAcceptedTopic? AcceptedTopic = null, LeagueClarificationContext? Clarification = null);
public sealed record ReliabilityFixture([property: JsonRequired] int SchemaVersion, [property: JsonRequired] ReliabilityCase[] Cases);
public sealed record ReliabilityObservation(MatchRoutingResult Route, LeagueWorkflowResult? Execution, int Reads, bool ExactWorkflow,
    bool FamilyCorrect, bool OutcomeCorrect, bool SubjectCorrect, bool EvidenceCorrect, bool? BindingCorrect, bool TimeScopeCorrect, string ResolvedTimeScope, bool? TimeIntentCorrect,
    bool Usable, bool UnsafeSubject, bool UnsafeTime, bool UnsafeUnexpectedAnswer);

// Direct read-only domain execution. This is not an app/auth/browser replacement.
public sealed class ReliabilityRunner(string version, Func<string, int?, string?, LeagueSeasonSnapshot> snapshot)
{
    public static ReliabilityRunner ReadReference(string path, string expectedHash)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > LeagueReferenceCodec.MaxBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("Reference SHA-256 mismatch or size cap exceeded; no inference performed.");
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        return new(model.Version, model.Snapshot);
    }
    public LeagueChatContext Context(ReliabilityCase item)
    {
        var requested = item.Context;
        var current = snapshot(requested.SeasonKey, requested.DateIndex, requested.SelectedClubId);
        var context = LeagueChatContext.From(current);
        if (context.Date != requested.Date || context.SelectedClubName != requested.SelectedClubName || context.Version != version
            || item.ReferenceClubId is not null && !current.Clubs.Any(c => c.Id == item.ReferenceClubId)
            || item.Expected.SubjectId is not null && !current.Clubs.Any(c => c.Id == item.Expected.SubjectId)
            || item.AcceptedTopic is not null && (!LeagueWorkflowRegistry.ValidTopic(item.AcceptedTopic)
                || item.AcceptedTopic.SubjectId is not null && !current.Clubs.Any(c => c.Id == item.AcceptedTopic.SubjectId)))
            throw new InvalidDataException("Fixture context/topic is inconsistent with the pinned reference.");
        return context;
    }
    public LeagueRoutingInput Input(ReliabilityCase item)
    {
        var c = Context(item);
        return new(item.Question, new(c.SeasonKey, c.DateIndex, c.Date, c.SelectedClubId, c.SelectedClubName), [],
            item.ReferenceClubId is not null, item.Clarification, item.AcceptedTopic);
    }
    public async Task<ReliabilityObservation> ExecuteAsync(ReliabilityCase item, MatchRoutingResult route,
        EvaluationDecisionDiagnostics? decision = null, CancellationToken token = default)
        => await ExecuteCoreAsync(item, route, decision, null, token);
    // Independent expected intent is used only to verify an offline expected plan.
    // Live scoring has no gold-input argument and never calls this entry point.
    public Task<ReliabilityObservation> ExecuteExpectedPlanAsync(ReliabilityCase item, CancellationToken token = default)
        => ExecuteCoreAsync(item, new(MatchRoutingStatus.Selected, item.Expected.Workflow), null, item.Expected.TimeIntent, token);
    private async Task<ReliabilityObservation> ExecuteCoreAsync(ReliabilityCase item, MatchRoutingResult route,
        EvaluationDecisionDiagnostics? decision, string? offlineExpectedIntent, CancellationToken token)
    {
        var context = Context(item); var reads = 0; LeagueWorkflowResult? result = null;
        var original = LeagueQuestionProjection.BriefIdentityReply(item.Question) ? item.Clarification?.OriginalQuestion : null;
        var semantic = route.WorkflowId is "coverage_limits" or "clarify" or "unsupported" ? LeagueTimeResolution.MatchesSelection
            : LeagueTimeScope.Resolve(context.SeasonKey, context.Date, original ?? item.Question, (decision?.TimeIntent.Choice ?? offlineExpectedIntent) == "explicit_time_reference");
        var resolvedScope = LeagueTimeScope.Semantic(semantic);
        var executor = new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            if (++reads > 3) throw new InvalidDataException("Fresh read budget exceeded.");
            return Task.FromResult(snapshot(season, index, club));
        });
        if (route.Status == MatchRoutingStatus.Selected && route.WorkflowId is { } workflow)
        {
            result = workflow == "continue_topic" && item.AcceptedTopic is not null
                ? await executor.ExecuteContinuationAsync(context, item.Question, item.AcceptedTopic, token)
                : await executor.ExecuteAsync(workflow, context, item.Question, item.ReferenceClubId, token, original);
            if (result.Evidence.Length > 4) throw new InvalidDataException("Evidence budget exceeded.");
        }
        var e = item.Expected;
        var selected = route.Status == MatchRoutingStatus.Selected;
        var exact = selected && route.WorkflowId == e.Workflow;
        var family = selected && LeagueWorkflowRegistry.Family(route.WorkflowId!) == e.Family;
        var outcome = result?.Outcome.ToString() == e.Outcome;
        var subject = result is not null && result.ResolvedClubId == e.SubjectId;
        var evidence = result is not null && result.Evidence.Select(x => x.Id).Order(StringComparer.Ordinal).SequenceEqual(e.EvidenceIds.Order(StringComparer.Ordinal));
        var unsafeSubject = result?.Outcome == MatchChatOutcome.Answered && result.ResolvedClubId != e.SubjectId;
        var scopeCorrect = resolvedScope == e.TimeScope;
        var unsafeTime = result?.Outcome == MatchChatOutcome.Answered && (e.TimeScope != "selected_snapshot" || resolvedScope != "selected_snapshot");
        var unexpectedAnswer = result?.Outcome == MatchChatOutcome.Answered && e.Outcome != "Answered";
        return new(route, result, reads, exact, family, outcome, subject, evidence,
            decision is null ? null : decision.ClubBinding.Choice == e.Binding,
            scopeCorrect, resolvedScope, decision is null || e.TimeIntent is null ? null : decision.TimeIntent.Choice == e.TimeIntent,
            family && outcome && subject && evidence && scopeCorrect && !unsafeSubject && !unsafeTime && !unexpectedAnswer, unsafeSubject, unsafeTime, unexpectedAnswer);
    }
}
public static class ReliabilityFixtures
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static ReliabilityFixture Read(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 100000) throw new InvalidDataException("Invalid reliability fixture size.");
        ReliabilityFixture fixture;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            bool Duplicate(JsonElement value) => value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count() || value.EnumerateObject().Any(p => Duplicate(p.Value))
                : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(Duplicate);
            if (Duplicate(doc.RootElement)) throw new InvalidDataException("Duplicate fixture properties.");
            fixture = JsonSerializer.Deserialize<ReliabilityFixture>(bytes, Json) ?? throw new InvalidDataException("Missing fixture.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid strict reliability fixture schema.", exception); }
        if (fixture.SchemaVersion != 2 || fixture.Cases is not { Length: >= 1 and <= 60 }
            || fixture.Cases.Any(c => c is null || c.Context is null || c.Expected is null || string.IsNullOrWhiteSpace(c.Id)
                || string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > 1000 || c.Clarification?.OriginalQuestion.Length > 1000
                || !LeagueWorkflowExecutor.ValidContext(new("evaluation", c.Context.SeasonKey, c.Context.DateIndex, c.Context.Date, c.Context.SelectedClubId, c.Context.SelectedClubName))
                || c.ReferenceClubId is not null && !LeagueSeasonModel.ValidId(c.ReferenceClubId)
                || c.AcceptedTopic is not null && !LeagueWorkflowRegistry.ValidTopic(c.AcceptedTopic)
                || !LeagueWorkflowRegistry.Criteria.ContainsKey(c.Expected.Workflow) || c.Expected.Family != LeagueWorkflowRegistry.Family(c.Expected.Workflow)
                || c.Expected.Outcome is not ("Answered" or "Clarify" or "Unavailable" or "Unsupported")
                || c.Expected.SubjectId is not null && !LeagueSeasonModel.ValidId(c.Expected.SubjectId)
                || c.Expected.EvidenceIds is not { Length: <= 4 } || c.Expected.EvidenceIds.Distinct().Count() != c.Expected.EvidenceIds.Length
                || c.Expected.EvidenceIds.Any(id => !LeagueWorkflowRegistry.EvidenceIds.Contains(id))
                || (c.Expected.Outcome == "Answered" ? c.Expected.EvidenceIds.Length == 0 : c.Expected.EvidenceIds.Length != 0 || c.Expected.SubjectId is not null)
                || !LeagueWorkflowRegistry.BindingCriteria.ContainsKey(c.Expected.Binding) || !LeagueWorkflowRegistry.SemanticTimeScopes.Contains(c.Expected.TimeScope)
                || c.Expected.TimeIntent is not null && !LeagueWorkflowRegistry.TimeIntentCriteria.ContainsKey(c.Expected.TimeIntent))
            || fixture.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != fixture.Cases.Length
            || fixture.Cases.Select(c => LeagueClubResolver.Normalize(c.Question)).Distinct(StringComparer.Ordinal).Count() != fixture.Cases.Length)
            throw new InvalidDataException("Invalid bounded reliability plan/context/expectations.");
        return fixture;
    }
}
public sealed class ReliabilityScore(int fixtureCases, int limit = 60)
{
    private int _consecutiveFailures;
    public int Total { get; private set; }
    public int Usable { get; private set; }
    public int RawExact { get; private set; }
    public int Supported { get; private set; }
    public int SupportedUsable { get; private set; }
    public int UnsafeAnswers { get; private set; }
    public int ProviderFailures { get; private set; }
    public int InvalidResponses { get; private set; }
    public int UnavailableResponses { get; private set; }
    public int SemanticErrors { get; private set; }
    public bool Stopped { get; private set; }
    public int FixtureCases { get; } = fixtureCases is >= 1 and <= 60 ? fixtureCases : throw new ArgumentOutOfRangeException(nameof(fixtureCases));
    public int Requested { get; } = limit is >= 1 and <= 60 ? Math.Min(limit, fixtureCases) : throw new ArgumentOutOfRangeException(nameof(limit));
    public bool ExecutedRequested => !Stopped && Total == Requested;
    public bool Completed => !Stopped && Total == FixtureCases;
    public string ResultKind => !Completed ? "partial_diagnostic" : FixtureCases == 60 ? "full_fresh_gate_candidate" : "complete_calibration";
    public bool SampleQualityMet => ExecutedRequested && Usable >= Total * .95 && Supported > 0 && SupportedUsable >= Supported * .95 && UnsafeAnswers == 0;
    public bool CalibrationQualityMet => Completed && FixtureCases != 60 && SampleQualityMet;
    public bool FreshGateComposition => Completed && FixtureCases == 60 && Supported >= 40;
    // Acceptance requires the entire frozen fixture, not a successful --limit subset.
    public bool MeetsTarget => FreshGateComposition && SampleQualityMet;
    public void Register(ReliabilityCase item, ReliabilityObservation result)
    {
        if (Stopped || Total >= Requested) throw new InvalidOperationException("Evaluation already stopped.");
        Total++; if (result.Usable) Usable++; if (result.ExactWorkflow) RawExact++;
        if (item.Expected.Outcome == "Answered") { Supported++; if (result.Usable) SupportedUsable++; }
        if (result.UnsafeSubject || result.UnsafeTime || result.UnsafeUnexpectedAnswer) UnsafeAnswers++;
        var failure = result.Route.Status is MatchRoutingStatus.Unavailable or MatchRoutingStatus.InvalidResponse;
        if (failure) ProviderFailures++; else if (!result.Usable) SemanticErrors++;
        if (result.Route.Status == MatchRoutingStatus.InvalidResponse) InvalidResponses++;
        if (result.Route.Status == MatchRoutingStatus.Unavailable) UnavailableResponses++;
        _consecutiveFailures = failure ? _consecutiveFailures + 1 : 0; Stopped = _consecutiveFailures >= 3;
    }
}
