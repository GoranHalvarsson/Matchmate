using System.Security.Claims;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatContinuityTests
{
    private static readonly HistoricalChatContext Context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
    private static AuthenticationState State(string change = "same")
    {
        var claims = new List<Claim> { new("tid", change == "tenant" ? "other-tenant" : "tenant"), new(ClaimTypes.Role, change == "role" ? "Reader" : "Tester") };
        if (change != "missing-subject") claims.Add(new("sub", change == "subject" ? "other-owner" : "owner", ClaimValueTypes.String, change == "issuer" ? "other-issuer" : "issuer"));
        return new(new ClaimsPrincipal(new ClaimsIdentity(claims, change == "anonymous" ? null : "test")));
    }
    private static ServerAuthenticationStateProvider Authentication()
    {
        var provider = new ServerAuthenticationStateProvider();
        provider.SetAuthenticationState(Task.FromResult(State()));
        return provider;
    }
    private static HistoricalChatSession Session(ServerAuthenticationStateProvider auth, HistoricalChatSessionTests.Router router, IHistoricalChatModel? model = null)
    {
        var session = new HistoricalChatSession(auth, HistoricalChatFixtures.Evidence(), router, model ?? new HistoricalChatSessionTests.Model(), Options.Create(new MatchChatOptions()));
        session.SetContext(Context); return session;
    }
    private static async Task Seed(HistoricalChatSession session)
    {
        Assert.True((await session.AskAsync(Context, "How were Southampton doing before the match?")).Accepted);
        Assert.True((await session.AskAsync(Context, "And Stoke?")).Accepted);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal("stoke-city", session.Turns[1].ClubId);
    }

    // REV-HIST-CONT-01 RED: real framework notification, fresh task and principal with identical owner claims.
    [Fact, Trait("ContinuityRed", "true")]
    public async Task CompletedSameOwnerFrameworkNotificationRetainsTurnIdsTopicAndNextPronounHistory()
    {
        var auth = Authentication(); var router = new HistoricalChatSessionTests.Router("overview", "continue_topic");
        using var session = Session(auth, router); await Seed(session);
        var ids = session.Turns.Select(turn => turn.Id).ToArray();
        auth.SetAuthenticationState(Task.FromResult(State()));
        Assert.Equal(ids, session.Turns.Select(turn => turn.Id));
        var followup = await session.AskAsync(Context, "How were they doing before the match?");
        Assert.True(followup.Accepted); Assert.Equal("stoke-city", followup.ResolvedSubjectId);
        Assert.Equal(new HistoricalTopic("overview", "stoke-city"), router.Inputs[2].Topic);
        Assert.Equal(new[] { "How were Southampton doing before the match?", "And Stoke?" }, router.Inputs[2].PreviousQuestions.Select(question => question.Question));
        Assert.Equal(new[] { "southampton", "stoke-city" }, router.Inputs[2].PreviousQuestions.Select(question => question.ClubId));
        Assert.Equal(ids, session.Turns.Take(2).Select(turn => turn.Id));
    }

    [Fact, Trait("ContinuityRed", "true")]
    public async Task CompletedSameOwnerNotificationDoesNotCancelTheSessionModelRequest()
    {
        var auth = Authentication(); var model = new WaitingModel();
        using var session = Session(auth, new("overview"), model);
        var ask = session.AskAsync(Context, "What happened?"); await model.Started.Task;
        try
        {
            auth.SetAuthenticationState(Task.FromResult(State()));
            Assert.False(model.RequestToken.IsCancellationRequested);
        }
        finally { model.Reply.TrySetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), [])); }
        Assert.True((await ask).Accepted); Assert.Single(session.Turns);
    }

    [Theory]
    [InlineData("issuer")][InlineData("tenant")][InlineData("subject")]
    [InlineData("role")][InlineData("anonymous")][InlineData("missing-subject")]
    public async Task ChangedOwnerOrEligibilityImmediatelyClearsHistoryAndCannotRestoreIt(string change)
    {
        var auth = Authentication(); var router = new HistoricalChatSessionTests.Router("overview", "continue_topic", "overview");
        using var session = Session(auth, router); await Seed(session);
        auth.SetAuthenticationState(Task.FromResult(State(change)));
        Assert.Empty(session.Turns);
        auth.SetAuthenticationState(Task.FromResult(State()));
        Assert.Empty(session.Turns);
        Assert.True((await session.AskAsync(Context, "What happened?")).Accepted);
        Assert.Empty(router.Inputs[2].PreviousQuestions); Assert.Null(router.Inputs[2].Topic);
    }

    [Theory]
    [InlineData("pending")][InlineData("cancelled")][InlineData("faulted")][InlineData("missing-state")]
    public async Task UnprovenNotificationClearsImmediatelyAndLaterCompletionCannotResurrectHistory(string status)
    {
        var auth = Authentication(); var router = new HistoricalChatSessionTests.Router("overview", "continue_topic", "overview");
        using var session = Session(auth, router); await Seed(session);
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = status switch
        {
            "pending" => pending.Task,
            "cancelled" => Task.FromCanceled<AuthenticationState>(new CancellationToken(true)),
            "faulted" => Task.FromException<AuthenticationState>(new InvalidOperationException("Test auth state unavailable.")),
            _ => Task.FromResult<AuthenticationState>(null!)
        };
        auth.SetAuthenticationState(notification);
        Assert.Empty(session.Turns);
        if (status == "pending") { pending.SetResult(State()); await notification; }
        if (status == "faulted") _ = notification.Exception;
        Assert.Empty(session.Turns);
        auth.SetAuthenticationState(Task.FromResult(State()));
        Assert.True((await session.AskAsync(Context, "What happened?")).Accepted);
        Assert.Empty(router.Inputs[2].PreviousQuestions); Assert.Null(router.Inputs[2].Topic);
    }

    [Theory]
    [InlineData("subject")][InlineData("role")]
    public async Task ARealOwnerOrEligibilityChangeCancelsAndRejectsANonCooperativeLateModel(string change)
    {
        var auth = Authentication(); var model = new WaitingModel();
        using var session = Session(auth, new("overview"), model);
        var ask = session.AskAsync(Context, "What happened?"); await model.Started.Task;
        auth.SetAuthenticationState(Task.FromResult(State(change)));
        Assert.True(model.RequestToken.IsCancellationRequested); Assert.Empty(session.Turns);
        auth.SetAuthenticationState(Task.FromResult(State()));
        model.Reply.SetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []));
        Assert.False((await ask).Accepted); Assert.Empty(session.Turns);
    }

    private sealed class WaitingModel : IHistoricalChatModel
    {
        public bool Configured => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MatchModelReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token)
        { RequestToken = token; Started.TrySetResult(); return Reply.Task; }
    }
}
