using System.Security.Claims;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatSessionTests
{
    private static readonly HistoricalChatContext Context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
    private static HistoricalChatSession Session(Auth auth, Router router, Model model, HistoricalEvidence? evidence = null, int timeout = 30)
    {
        var session = new HistoricalChatSession(auth, evidence ?? HistoricalChatFixtures.Evidence(), router, model, Options.Create(new MatchChatOptions { TimeoutSeconds = timeout }));
        session.SetContext(Context); return session;
    }
    [Fact]
    public async Task FollowupKeepsTrustedTopicAndSwitchesFixtureClubWithoutAssistantAuthority()
    {
        var router = new Router("form", "continue_topic", "continue_topic"); var model = new Model();
        using var session = Session(new(), router, model);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(Context, "How were Southampton doing before this game?")).Outcome);
        Assert.Equal("southampton", session.Turns[0].ClubId);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(Context, "And Stoke?")).Outcome);
        Assert.Equal("stoke-city", session.Turns[1].ClubId);
        Assert.Equal("form", router.Inputs[1].Topic!.Workflow); Assert.Equal("southampton", router.Inputs[1].Topic!.ClubId);
        Assert.Single(router.Inputs[1].PreviousQuestions); Assert.Equal("How were Southampton doing before this game?", router.Inputs[1].PreviousQuestions[0].Question);
        Assert.DoesNotContain("The result is recorded.", string.Join(" ", model.Requests[1].Messages.Select(m => m.Content)));
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(Context, "Tell me more about them")).Outcome);
        Assert.Equal("stoke-city", session.Turns[2].ClubId);
    }
    [Fact]
    public async Task AmbiguousReferenceClarifiesThenNamedReplyRecoversTheTopic()
    {
        var router = new Router("form", "continue_topic"); var model = new Model(); using var session = Session(new(), router, model);
        Assert.Equal(MatchChatOutcome.Clarify, (await session.AskAsync(Context, "How were they doing?")).Outcome); Assert.Empty(model.Requests);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(Context, "Stoke")).Outcome); Assert.Equal("stoke-city", session.Turns[1].ClubId);
    }
    [Fact]
    public async Task UnsupportedAndMissingContinuationNeverInvokeNarrator()
    {
        var router = new Router("unsupported", "continue_topic"); var model = new Model(); using var session = Session(new(), router, model);
        Assert.Equal(MatchChatOutcome.Unsupported, (await session.AskAsync(Context, "Who scored?")).Outcome);
        Assert.Equal(MatchChatOutcome.Clarify, (await session.AskAsync(Context, "Go on")).Outcome); Assert.Empty(model.Requests);
    }
    [Theory]
    [InlineData("cancel")][InlineData("reset")][InlineData("navigate")][InlineData("version")][InlineData("auth")][InlineData("dispose")]
    public async Task LateNonCooperativeNarratorCannotCommitAfterStateChanges(string action)
    {
        var auth = new Auth(); var router = new Router("overview"); var model = new Model { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = Session(auth, router, model); var ask = session.AskAsync(Context, "What happened?"); await model.Started.Task;
        switch (action)
        {
            case "cancel": session.Cancel(); break; case "reset": session.NewChat(); break;
            case "navigate": session.SetContext(Context with { MatchKey = "stoke-city:southampton" }); break;
            case "version": session.SetContext(Context with { Version = "new-version" }); break;
            case "auth": auth.Set("other"); break; case "dispose": session.Dispose(); break;
        }
        model.Delayed.SetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []));
        Assert.False((await ask).Accepted); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task SilentOwnerChangeIsCheckedAgainBeforeCommit()
    {
        var auth = new Auth(); var model = new Model { BeforeReply = () => auth.Set("other", notify: false) }; using var session = Session(auth, new("overview"), model);
        Assert.False((await session.AskAsync(Context, "What happened?")).Accepted); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task FailedValidationCannotBecomeTrustedFollowupTopic()
    {
        var model = new Model { Answer = "A player scored a penalty." }; var router = new Router("overview", "continue_topic"); using var session = Session(new(), router, model);
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskAsync(Context, "Give me the result")).Outcome);
        Assert.Equal(MatchChatOutcome.Clarify, (await session.AskAsync(Context, "Go on")).Outcome); Assert.Null(router.Inputs[1].Topic);
        Assert.All(session.Turns, t => Assert.Empty(t.Evidence));
    }
    [Fact]
    public async Task HistoryIsBoundedAndResetDoesNotLeakAcrossFixtures()
    {
        var router = new Router("overview"); using var session = Session(new(), router, new());
        for (var i = 0; i < 11; i++) await session.AskAsync(Context, "What happened?");
        Assert.Equal(MatchChatLimits.Turns, session.Turns.Count); Assert.All(router.Inputs, input => Assert.InRange(input.PreviousQuestions.Length, 0, MatchChatLimits.Turns));
        session.NewChat(); await session.AskAsync(Context, "What happened?"); Assert.Empty(router.Inputs[^1].PreviousQuestions);
        session.SetContext(Context with { Date = "2017-05-20" }); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task DisabledModelsAnonymousAndNonTesterNeverRetrieveOrInfer()
    {
        foreach (var mode in new[] { "model", "router", "anonymous", "role" })
        {
            var auth = new Auth(); var router = new Router("overview") { Configured = mode != "router" }; var model = new Model { Configured = mode != "model" };
            if (mode == "anonymous") auth.Set(null); if (mode == "role") auth.Set("user", tester: false);
            var evidence = new HistoricalEvidence((_, _, _) => throw new InvalidOperationException("No retrieval"), (_, _, _, _) => throw new InvalidOperationException("No retrieval"));
            using var session = Session(auth, router, model, evidence);
            Assert.Equal(MatchChatOutcome.Disconnected, (await session.AskAsync(Context, "What happened?")).Outcome); Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Empty(session.Turns);
        }
    }
    [Fact]
    public async Task TimeoutAndCallerCancellationCannotAddALateReply()
    {
        var model = new Model { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var session = Session(new(), new("overview"), model, timeout: 1);
        Assert.Equal(MatchChatOutcome.Cancelled, (await session.AskAsync(Context, "What happened?")).Outcome); Assert.Empty(session.Turns);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Equal(MatchChatOutcome.Cancelled, (await session.AskAsync(Context, "What happened?", cancelled.Token)).Outcome);
    }
    [Fact]
    public async Task ChangedSnapshotOrMalformedRoutingDoesNotNarrate()
    {
        var f = HistoricalChatFixtures.Fixture() with { Version = "changed" }; var model = new Model();
        using var session = Session(new(), new("overview", "arbitrary-tool"), model, HistoricalChatFixtures.Evidence(f));
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskAsync(Context, "What happened?")).Outcome);
        Assert.Equal(MatchChatOutcome.Clarify, (await session.AskAsync(Context, "Try again")).Outcome); Assert.Empty(model.Requests);
    }
    internal sealed class Auth : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = User("owner", true);
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
        public void Set(string? owner, bool tester = true, bool notify = true) { _user = User(owner, tester); if (notify) NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_user))); }
        private static ClaimsPrincipal User(string? owner, bool tester) => owner is null ? new(new ClaimsIdentity())
            : new(new ClaimsIdentity(new[] { new Claim("sub", owner), new Claim(ClaimTypes.Role, tester ? "Tester" : "Reader") }, "test"));
    }
    internal sealed class Router(params string[] workflows) : IHistoricalWorkflowRouter
    {
        public bool Configured { get; set; } = true;
        public List<HistoricalRoutingInput> Inputs { get; } = [];
        public Task<MatchRoutingResult> RouteAsync(HistoricalRoutingInput input, CancellationToken token)
        { Inputs.Add(input); return Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, workflows[Math.Min(Inputs.Count - 1, workflows.Length - 1)])); }
    }
    internal sealed class Model : IHistoricalChatModel
    {
        public bool Configured { get; set; } = true;
        public string Answer { get; set; } = "The result is recorded.";
        public List<HistoricalNarratorRequest> Requests { get; } = [];
        public TaskCompletionSource<MatchModelReply>? Delayed { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? BeforeReply { get; set; }
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token)
        { Requests.Add(request); Started.TrySetResult(); BeforeReply?.Invoke(); return Delayed?.Task ?? Task.FromResult(new MatchModelReply(HistoricalChatFixtures.Reply(Answer, "fixture"), [])); }
    }
}
