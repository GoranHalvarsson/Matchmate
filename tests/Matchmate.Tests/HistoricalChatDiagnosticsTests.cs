using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatDiagnosticsTests
{
    private const string Private = "PRIVATE-prompt-reply-exception-user-path-config";
    private static readonly HistoricalChatContext Context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
    private static HistoricalChatSession Session(CaptureLog log, IHistoricalChatModel model, HistoricalEvidence? evidence = null,
        AuthenticationStateProvider? auth = null, IHistoricalWorkflowRouter? router = null, int timeout = 30)
    {
        var session = new HistoricalChatSession(auth ?? new HistoricalChatSessionTests.Auth(), evidence ?? HistoricalChatFixtures.Evidence(),
            router ?? new Router(), model, Options.Create(new MatchChatOptions { TimeoutSeconds = timeout }), logger: log);
        session.SetContext(Context); return session;
    }
    private static MatchModelReply Reply(string answer, params string[] ids) => new(HistoricalChatFixtures.Reply(answer, ids), []);

    [Theory]
    [InlineData(HistoricalChatFailureReason.UnsupportedDetail)]
    [InlineData(HistoricalChatFailureReason.Venue)]
    [InlineData(HistoricalChatFailureReason.UncitedIdentity)]
    [InlineData(HistoricalChatFailureReason.UnsupportedNumber)]
    [InlineData(HistoricalChatFailureReason.UnknownEvidence)]
    [InlineData(HistoricalChatFailureReason.InvalidReplyJson)]
    [InlineData(HistoricalChatFailureReason.InvalidReplyStructure)]
    [InlineData(HistoricalChatFailureReason.ReplyBudget)]
    [InlineData(HistoricalChatFailureReason.InvalidAnswerContent)]
    [InlineData(HistoricalChatFailureReason.UnsupportedMeasurement)]
    [InlineData(HistoricalChatFailureReason.UnexpectedToolCall)]
    public async Task RejectionsExposeOnlyTheirClosedValidationCode(HistoricalChatFailureReason reason)
    {
        var reply = reason switch
        {
            HistoricalChatFailureReason.UnsupportedDetail => Reply("Stoke scored through Crouch, not Arnautovic. " + Private, "fixture"),
            HistoricalChatFailureReason.Venue => Reply("Southampton lost at St Mary's Stadium. " + Private, "fixture"),
            HistoricalChatFailureReason.UncitedIdentity => Reply("Arsenal appear here. " + Private, "fixture"),
            HistoricalChatFailureReason.UnsupportedNumber => Reply("Southampton had 999 points. " + Private, "fixture"),
            HistoricalChatFailureReason.UnknownEvidence => Reply(Private, "private-evidence"),
            HistoricalChatFailureReason.InvalidReplyJson => new(Private + "{", []),
            HistoricalChatFailureReason.InvalidReplyStructure => new(JsonSerializer.Serialize(new { secret = Private }), []),
            HistoricalChatFailureReason.ReplyBudget => new(Private + new string('x', MatchChatLimits.ReplyChars), []),
            HistoricalChatFailureReason.InvalidAnswerContent => Reply("<b>" + Private + "</b>", "fixture"),
            HistoricalChatFailureReason.UnsupportedMeasurement => Reply("Southampton had 60% possession. " + Private, "fixture"),
            _ => new(Private, [new(Private, Private, Private)])
        };
        var log = new CaptureLog(); using var session = Session(log, new Model(_ => Task.FromResult(reply)));
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskAsync(Context, "Give me the result " + Private)).Outcome);
        AssertSafe(Assert.Single(log.Records), HistoricalChatStage.ReplyValidation,
            reason == HistoricalChatFailureReason.UnsupportedDetail ? HistoricalChatFailureReason.DetailMechanismQualifier : reason);
        Assert.All(session.Turns, turn => Assert.Empty(turn.Evidence));
    }

    [Theory]
    [InlineData("Stoke scored through Crouch, not Arnautovic.", "fixture", "overview", HistoricalChatFailureReason.DetailMechanismQualifier)]
    [InlineData("Southampton scored 3 goals from corners in their earlier results.", "form", "overview", HistoricalChatFailureReason.DetailForbiddenNoun)]
    [InlineData("Southampton pressed high without letting Stoke settle.", "fixture", "overview", HistoricalChatFailureReason.DetailForbiddenAction)]
    [InlineData("Crouch scored 3 goals in the earlier sample.", "form", "overview", HistoricalChatFailureReason.AggregateSubjectUnbound)]
    [InlineData("Stoke City scored 1 goal in the earlier sample.", "fixture", "ground", HistoricalChatFailureReason.AggregateScopeMissing)]
    [InlineData("Southampton scored 0 goals in this fixture.", "form", "overview", HistoricalChatFailureReason.AggregateCitationMissing)]
    [InlineData("Southampton's striker had earlier results, scoring 3 goals.", "form", "overview", HistoricalChatFailureReason.AggregateTeamPredicateMismatch)]
    [InlineData("Southampton scored 2 goals in the earlier sample.", "form", "overview", HistoricalChatFailureReason.AggregateValueMismatch)]
    [InlineData("Southampton had earlier results scoring 3 goals.", "form", "overview", HistoricalChatFailureReason.AggregateGerundWithoutComma)]
    [InlineData("Southampton scored a total of 3 goals in the earlier sample.", "form", "overview", HistoricalChatFailureReason.AggregateFiniteTotalPhrase)]
    [InlineData("Southampton have scored 3 goals in the earlier sample.", "form", "overview", HistoricalChatFailureReason.AggregateFiniteGrammarMismatch)]
    [InlineData("Southampton had earlier results, scoring three goals.", "form", "overview", HistoricalChatFailureReason.AggregateGerundGrammarMismatch)]
    public async Task DetailSubcodesRemainRejectedAndExposeOnlyClosedDiagnosticValues(string answer, string citation, string workflow, HistoricalChatFailureReason reason)
    {
        var log = new CaptureLog(); using var session = Session(log, new Model(_ => Task.FromResult(Reply(answer + " " + Private, citation))), router: new Router { Workflow = workflow });
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskAsync(Context, "Give me the result " + Private)).Outcome);
        AssertSafe(Assert.Single(log.Records), HistoricalChatStage.ReplyValidation, reason);
        Assert.All(session.Turns, turn => Assert.Empty(turn.Evidence));
    }

    [Theory]
    [InlineData("Southampton scored 3 goals in their earlier results.", "form")]
    [InlineData("Southampton had earlier results, scoring 3 goals.", "form")]
    [InlineData("No scorers or lineups are supplied.", "fixture")]
    public async Task DiagnosticRefinementLeavesAcceptedAnswersAndWarningsUnchanged(string answer, string citation)
    {
        var log = new CaptureLog(); using var session = Session(log, new Model(_ => Task.FromResult(Reply(answer, citation))));
        var result = await session.AskAsync(Context, "Give me the result");
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(answer, result.Message);
        Assert.Equal(answer, Assert.Single(session.Turns).Answer); Assert.Empty(log.Records);
    }

    [Theory]
    [InlineData(HistoricalChatFailureReason.ProviderRateLimited)]
    [InlineData(HistoricalChatFailureReason.ProviderAuthentication)]
    [InlineData(HistoricalChatFailureReason.NarrationFailure)]
    [InlineData(HistoricalChatFailureReason.IncompleteNarratorReply)]
    [InlineData(HistoricalChatFailureReason.Unknown)]
    public async Task ProviderAndUnknownExceptionsNeverExposeMessagesOrStatusValues(HistoricalChatFailureReason reason)
    {
        Exception failure = reason switch
        {
            HistoricalChatFailureReason.ProviderRateLimited => new HttpRequestException(Private, null, HttpStatusCode.TooManyRequests),
            HistoricalChatFailureReason.ProviderAuthentication => new HttpRequestException(Private, null, HttpStatusCode.Unauthorized),
            HistoricalChatFailureReason.NarrationFailure => new HttpRequestException(Private),
            HistoricalChatFailureReason.IncompleteNarratorReply => new InvalidDataException("Incomplete, filtered or unexpected tool-call narrator reply."),
            _ => new InvalidOperationException(Private)
        };
        var log = new CaptureLog(); using var session = Session(log, new Model(_ => Task.FromException<MatchModelReply>(failure)));
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskAsync(Context, "Give me the result " + Private)).Outcome);
        AssertSafe(Assert.Single(log.Records), HistoricalChatStage.Narration, reason);
    }

    [Theory]
    [InlineData(HistoricalChatStage.Authentication, HistoricalChatFailureReason.AuthenticationFailure)]
    [InlineData(HistoricalChatStage.Routing, HistoricalChatFailureReason.RoutingFailure)]
    [InlineData(HistoricalChatStage.Retrieval, HistoricalChatFailureReason.RetrievalFailure)]
    [InlineData(HistoricalChatStage.EvidencePreparation, HistoricalChatFailureReason.EvidencePreparationFailure)]
    [InlineData(HistoricalChatStage.Commit, HistoricalChatFailureReason.CommitFailure)]
    public async Task RequestLocalStageIdentifiesFailuresOutsideTheNarrator(HistoricalChatStage stage, HistoricalChatFailureReason reason)
    {
        var auth = new Auth { FailOn = stage == HistoricalChatStage.Authentication ? 1 : stage == HistoricalChatStage.Commit ? 2 : 0 };
        var router = new Router { Failure = stage == HistoricalChatStage.Routing ? new HttpRequestException(Private) : null };
        var fixture = HistoricalChatFixtures.Fixture();
        if (stage == HistoricalChatStage.EvidencePreparation) fixture = fixture with { HomePreMatchForm = [HistoricalChatFixtures.Game("southampton", "unknown", "2017-05-01", 1, 0)] };
        var evidence = stage == HistoricalChatStage.Retrieval
            ? new HistoricalEvidence((_, _, _) => Task.FromException<Matchmate.Contracts.LeagueHistoricalMatch>(new HttpRequestException(Private)), (_, _, _, _) => throw new InvalidOperationException(Private))
            : HistoricalChatFixtures.Evidence(fixture);
        var log = new CaptureLog(); using var session = Session(log, new Model(_ => Task.FromResult(Reply("The result is recorded.", "fixture"))), evidence, auth, router);
        var result = await session.AskAsync(Context, "Give me the result " + Private);
        Assert.Equal(stage == HistoricalChatStage.Authentication ? MatchChatOutcome.Stale : MatchChatOutcome.Unavailable, result.Outcome);
        AssertSafe(Assert.Single(log.Records), stage, reason);
    }

    [Theory]
    [InlineData(MatchRoutingStatus.Unavailable, HistoricalChatFailureReason.RoutingFailure)]
    [InlineData(MatchRoutingStatus.InvalidResponse, HistoricalChatFailureReason.InvalidRoutingResponse)]
    public async Task ReturnedRouterFailuresAlsoHaveClosedCodes(MatchRoutingStatus status, HistoricalChatFailureReason reason)
    {
        var log = new CaptureLog(); var router = new Router { Status = status };
        using var session = Session(log, new Model(_ => throw new InvalidOperationException("Must not narrate")), router: router);
        Assert.Equal(MatchChatOutcome.RoutingUnavailable, (await session.AskAsync(Context, "Give me the result " + Private)).Outcome);
        AssertSafe(Assert.Single(log.Records), HistoricalChatStage.Routing, reason);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AThrowingLoggerCannotChangeTheExistingOutcome(bool rejected)
    {
        var log = new CaptureLog { Throws = true };
        using var session = Session(log, new Model(_ => Task.FromResult(Reply(rejected ? "Stoke scored through Crouch." : "The result is recorded.", "fixture"))));
        Assert.Equal(rejected ? MatchChatOutcome.Unavailable : MatchChatOutcome.Answered, (await session.AskAsync(Context, "Give me the result")).Outcome);
    }

    [Theory]
    [InlineData("cancel")][InlineData("context")][InlineData("auth")][InlineData("timeout")]
    public async Task DiagnosticsDoNotLetCancelledOrStaleRequestsCommit(string action)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new HistoricalChatSessionTests.Auth(); var log = new CaptureLog();
        using var session = Session(log, new Model(_ => { started.TrySetResult(); return delayed.Task; }), auth: auth, timeout: 1);
        var ask = session.AskAsync(Context, "Give me the result " + Private); await started.Task;
        if (action == "cancel") session.Cancel();
        if (action == "context") session.SetContext(Context with { Version = Private });
        if (action == "auth") auth.Set(Private);
        Assert.Equal(MatchChatOutcome.Cancelled, (await ask).Outcome); Assert.Empty(session.Turns);
        AssertSafe(Assert.Single(log.Records), HistoricalChatStage.Narration, action == "timeout" ? HistoricalChatFailureReason.Timeout : HistoricalChatFailureReason.Cancelled);
        delayed.TrySetResult(Reply(Private, "fixture")); Assert.Empty(session.Turns);
    }

    private static void AssertSafe(Entry entry, HistoricalChatStage stage, HistoricalChatFailureReason reason)
    {
        Assert.Equal(LogLevel.Warning, entry.Level); Assert.Equal(7301, entry.Event.Id); Assert.Null(entry.Exception);
        Assert.Equal(new[] { "Stage", "Reason", "{OriginalFormat}" }, entry.State.Select(p => p.Key));
        Assert.Equal(stage, Assert.IsType<HistoricalChatStage>(entry.State[0].Value));
        Assert.Equal(reason, Assert.IsType<HistoricalChatFailureReason>(entry.State[1].Value));
        Assert.True(Enum.IsDefined(stage)); Assert.True(Enum.IsDefined(reason));
        Assert.Equal("Historical chat failure {Stage} {Reason}", entry.State[2].Value);
        Assert.Equal($"Historical chat failure {stage} {reason}", entry.Message);
        Assert.DoesNotContain(Private, entry.Message); Assert.DoesNotContain(Private, string.Join(" ", entry.State.Select(p => p.Value)));
    }
    private sealed record Entry(LogLevel Level, EventId Event, KeyValuePair<string, object?>[] State, Exception? Exception, string Message);
    private sealed class CaptureLog : ILogger<HistoricalChatSession>
    {
        public bool Throws { get; init; }
        public List<Entry> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throws) throw new IOException(Private);
            Records.Add(new(level, eventId, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToArray(), exception, formatter(state, exception)));
        }
    }
    private sealed class Router : IHistoricalWorkflowRouter
    {
        public bool Configured => true;
        public MatchRoutingStatus Status { get; init; } = MatchRoutingStatus.Selected;
        public string Workflow { get; init; } = "overview";
        public Exception? Failure { get; init; }
        public Task<MatchRoutingResult> RouteAsync(HistoricalRoutingInput input, CancellationToken token) => Failure is null
            ? Task.FromResult(new MatchRoutingResult(Status, Workflow)) : Task.FromException<MatchRoutingResult>(Failure);
    }
    private sealed class Model(Func<CancellationToken, Task<MatchModelReply>> complete) : IHistoricalChatModel
    {
        public bool Configured => true;
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token) => complete(token);
    }
    private sealed class Auth : AuthenticationStateProvider
    {
        public int FailOn { get; init; }
        private int _calls;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => ++_calls == FailOn
            ? Task.FromException<AuthenticationState>(new HttpRequestException(Private))
            : Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Private), new Claim(ClaimTypes.Role, "Tester")], "test"))));
    }
}
