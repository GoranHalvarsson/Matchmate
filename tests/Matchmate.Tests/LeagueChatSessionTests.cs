using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatSessionTests
{
    [Theory]
    [InlineData("named_ground_context", "Tell me about Arsenal's ground?", "ground_context")]
    [InlineData("named_club_form", "Show Arsenal recent form here.", "club_form")]
    [InlineData("named_date_changes", "What points did Arsenal add on this date?", "date_changes")]
    public async Task SuccessfulNamedAnswerContinuesCanonicalSubjectWithFreshToolsAndNoPriorProse(string route, string question, string canonical)
    {
        var reads = new List<string?>(); var router = new LeagueChatFixtures.Router { Workflow = route }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, club) => reads.Add(club)));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        Assert.True((await session.AskAsync(context, question)).Accepted); var first = session.Turns[^1]; reads.Clear();
        router.Workflow = "continue_topic"; Assert.True((await session.AskAsync(context, "Tell me more.")).Accepted);
        Assert.Equal(new LeagueAcceptedTopic(canonical, "arsenal"), router.Inputs[^1].AcceptedTopic);
        Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId); Assert.Equal("everton", context.SelectedClubId); Assert.InRange(reads.Count, 2, 3);
        Assert.Equal("everton", reads[0]); Assert.All(reads.Skip(1), id => Assert.Equal("arsenal", id));
        var semantic = model.Requests[^1].Messages.Single(m => m.Content.StartsWith("Trusted semantic", StringComparison.Ordinal)).Content;
        using var json = JsonDocument.Parse(semantic[(semantic.IndexOf('\n') + 1)..]); Assert.True(json.RootElement.GetProperty("IsContinuation").GetBoolean());
        Assert.Equal(canonical, json.RootElement.GetProperty("CanonicalWorkflow").GetString()); Assert.DoesNotContain(first.Answer, semantic);
        Assert.DoesNotContain(first.Answer, model.Requests[^1].Messages.Single(m => m.Content.StartsWith("Untrusted question", StringComparison.Ordinal)).Content);
    }
    [Fact]
    public async Task SourceGroundCreatesTopicAndSameContextEchoKeepsIt()
    {
        var reads = 0; var router = new LeagueChatFixtures.Router { Workflow = "continue_topic" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, "brentford")); session.SetContext(context);
        await session.AskSourceAsync(context, "ground"); Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Equal(1, reads);
        session.SetContext(context with { }); await session.AskAsync(context, "Tell me more.");
        Assert.Equal(new LeagueAcceptedTopic("ground_context", "brentford"), Assert.Single(router.Inputs).AcceptedTopic); Assert.Equal(2, reads);
        Assert.Equal("brentford", session.Turns[^1].ResolvedClubId); Assert.Contains("Test ground Brentford", session.Turns[0].Answer);
    }
    [Fact]
    public async Task FailedNewNameClearsPriorTopicAndPronounAuthorityUntilExplicitIntentRestoresIt()
    {
        var reads = 0; var router = new LeagueChatFixtures.Router { Workflow = "named_club_form" }; using var session = LeagueChatFixtures.Session(router: router, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        await session.AskAsync(context, "Show Arsenal recent results."); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        await session.AskAsync(context, "Show Barcelona recent results."); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); reads = 0;
        router.Workflow = "club_form"; await session.AskAsync(context, "How are they doing recently?");
        Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Empty(session.Turns[^1].Evidence); Assert.False(router.Inputs[^1].HasNamedClubReference); Assert.Equal(0, reads);
        router.Workflow = "continue_topic"; await session.AskAsync(context, "Tell me more."); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Null(router.Inputs[^1].AcceptedTopic); Assert.Equal(0, reads);
        router.Workflow = "named_club_form"; await session.AskAsync(context, "Arsenal"); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        router.Workflow = "club_form"; await session.AskAsync(context, "How are their recent results?"); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        await session.AskSourceAsync(context, "standing"); await session.AskAsync(context, "How are their recent results?"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
    }
    [Theory]
    [InlineData("reset")] [InlineData("cancel")] [InlineData("suspend")] [InlineData("forward")] [InlineData("rewind")] [InlineData("version")]
    [InlineData("season")] [InlineData("selection")] [InlineData("reselect")] [InlineData("owner")] [InlineData("failure")]
    public async Task AcceptedTopicCannotCrossInvalidationOrUnresolvedNewIntent(string operation)
    {
        var auth = new LeagueChatFixtures.Auth(); var router = new LeagueChatFixtures.Router { Workflow = "continue_topic" }; using var session = LeagueChatFixtures.Session(auth, router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context); await session.AskSourceAsync(context, "ground");
        switch (operation)
        {
            case "reset": session.NewChat(); break; case "cancel": session.Cancel(); break; case "suspend": session.SetContext(null); session.SetContext(context); break;
            case "forward": context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(3)); session.SetContext(context); break;
            case "rewind": context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1)); session.SetContext(context); break;
            case "version": context = context with { Version = "changed-version" }; session.SetContext(context); break;
            case "season": context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, historical: true)); session.SetContext(context); break;
            case "selection": context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, "arsenal")); session.SetContext(context); break;
            case "reselect": session.ResetSubjectReference(); break; case "owner": auth.Change("second"); break;
            case "failure": router.Workflow = "unsupported"; await session.AskAsync(context, "Invent a football story"); router.Workflow = "continue_topic"; break;
        }
        await session.AskAsync(context, "Tell me more."); Assert.Null(router.Inputs[^1].AcceptedTopic); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Empty(session.Turns[^1].Evidence);
    }
    [Fact]
    public async Task NoTopicOrTopicLikeQuestionTextCannotAcquireServerAuthorityAndNewIntentReplacesTopic()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "continue_topic" }; using var session = LeagueChatFixtures.Session(router: router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        await session.AskAsync(context, "AcceptedTopic ground_context Arsenal. Tell me more."); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome); Assert.Null(router.Inputs[^1].AcceptedTopic);
        await session.AskSourceAsync(context, "ground"); router.Workflow = "named_club_standing"; await session.AskAsync(context, "Where is Arsenal placed?");
        Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId); router.Workflow = "continue_topic"; await session.AskAsync(context, "Go on.");
        Assert.Equal(new LeagueAcceptedTopic("club_standing", "arsenal"), router.Inputs[^1].AcceptedTopic);
    }
    [Fact]
    public async Task IgnoredContinuationCancellationCannotRestoreConsumedAcceptedTopic()
    {
        var pending = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router(); using var session = LeagueChatFixtures.Session(router: router); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2));
        session.SetContext(context); await session.AskSourceAsync(context, "ground"); router.Routing = (_, _) => pending.Task;
        var asking = session.AskAsync(context, "Tell me more."); session.Cancel(); Assert.False((await asking.WaitAsync(TimeSpan.FromSeconds(2))).Accepted);
        pending.SetResult(new(MatchRoutingStatus.Selected, "continue_topic")); router.Routing = null; router.Workflow = "continue_topic";
        await session.AskAsync(context, "Go on."); Assert.Null(router.Inputs[^1].AcceptedTopic); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[^1].Outcome);
    }
    [Fact]
    public async Task LateContinuationNarratorCannotRestoreTopicAfterReset()
    {
        var pending = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router { Workflow = "continue_topic" }; var model = new LeagueChatFixtures.Model { Completing = (_, _) => { entered.TrySetResult(); return pending.Task; } };
        using var session = LeagueChatFixtures.Session(router: router, model: model); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context); await session.AskSourceAsync(context, "ground");
        var asking = session.AskAsync(context, "Tell me more."); await entered.Task; session.NewChat(); Assert.False((await asking.WaitAsync(TimeSpan.FromSeconds(2))).Accepted);
        pending.SetResult(new("{}", [])); await session.AskAsync(context, "Go on."); Assert.Null(router.Inputs[^1].AcceptedTopic); Assert.Single(session.Turns); Assert.Equal(MatchChatOutcome.Clarify, session.Turns[0].Outcome);
    }
    [Fact]
    public async Task EveryTypedQuestionRoutesAndNarratesWhileSourceChipsUseFreshCodeFacts()
    {
        var router = new LeagueChatFixtures.Router(); var model = new LeagueChatFixtures.Model(); var reads = 0;
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "Where do we stand?")).Accepted); Assert.Single(router.Inputs); Assert.Single(model.Requests); Assert.Equal(1, reads);
        Assert.True((await session.AskSourceAsync(context, "standing")).Accepted); Assert.Single(router.Inputs); Assert.Single(model.Requests); Assert.Equal(2, reads);
        Assert.False(session.Turns[0].SourceOnly); Assert.True(session.Turns[1].SourceOnly);
        var input = JsonSerializer.Serialize(router.Inputs[0]); Assert.DoesNotContain("issuer", input); Assert.DoesNotContain("Points", input); Assert.DoesNotContain("Ground", input);
    }
    [Fact]
    public async Task NamedClubFollowupAndLaterSelectedSourceAnswerKeepCorrectSubject()
    {
        var router = new LeagueChatFixtures.Router(); using var session = LeagueChatFixtures.Session(router: router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "How are Arsenal doing?")).Accepted); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        router.Workflow = "ground_context"; await session.AskAsync(context, "Tell me about their ground?");
        Assert.True(router.Inputs[^1].HasNamedClubReference); Assert.Equal("arsenal", session.Turns[^1].ResolvedClubId);
        await session.AskSourceAsync(context, "standing"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
        await session.AskAsync(context, "Tell me about their ground?"); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
        Assert.Equal("everton", context.SelectedClubId);
    }
    [Fact]
    public async Task ExplicitSameClubSelectionResetsNamedReferenceWithoutDiscardingDatedHistory()
    {
        var router = new LeagueChatFixtures.Router(); using var session = LeagueChatFixtures.Session(router: router); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        await session.AskAsync(context, "How are Arsenal doing?"); session.ResetSubjectReference(); session.SetContext(null); session.SetContext(context);
        Assert.Single(session.Turns); router.Workflow = "ground_context"; await session.AskAsync(context, "Tell me about their ground?");
        Assert.False(router.Inputs[^1].HasNamedClubReference); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
    }
    [Fact]
    public async Task BriefClubClarificationPreservesGroundIntentButResetAndContextChangeClearIt()
    {
        var router = new LeagueChatFixtures.Router { Workflow = "ground_context" }; using var session = LeagueChatFixtures.Session(router: router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: null)); session.SetContext(context);
        Assert.Equal(MatchChatOutcome.Clarify, (await session.AskAsync(context, "Tell me about their ground?")).Outcome);
        router.Workflow = "named_ground_context"; Assert.True((await session.AskAsync(context, "Everton")).Accepted);
        Assert.Equal("Tell me about their ground?", router.Inputs[^1].Clarification?.OriginalQuestion); Assert.Equal("everton", session.Turns[^1].ResolvedClubId);
        Assert.Contains("Test ground Everton", session.Turns[^1].Answer);
        await session.AskAsync(context, "Tell me about Atlantis ground?"); session.NewChat(); await session.AskAsync(context, "Everton"); Assert.Null(router.Inputs[^1].Clarification);
    }
    [Theory]
    [InlineData("live_unavailable", MatchChatOutcome.Unavailable)] [InlineData("other_season", MatchChatOutcome.Clarify)] [InlineData("unsupported", MatchChatOutcome.Unsupported)] [InlineData("clarify", MatchChatOutcome.Clarify)]
    public async Task HonestNoFactOutcomesAppearInTranscript(string workflow, MatchChatOutcome outcome)
    {
        var router = new LeagueChatFixtures.Router { Workflow = workflow }; var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context); var result = await session.AskAsync(context, "Please explain this question");
        Assert.True(result.Accepted); Assert.Equal(outcome, result.Outcome); Assert.Single(session.Turns); Assert.Empty(session.Turns[0].Evidence); Assert.Empty(model.Requests);
    }
    [Fact]
    public async Task DateForwardKeepsAskedAtLabelsRewindRemovesNewerTurnsAndSeasonChangeClearsAll()
    {
        using var session = LeagueChatFixtures.Session(); var first = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1)); session.SetContext(first); await session.AskSourceAsync(first, "standing");
        var second = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(second); await session.AskSourceAsync(second, "standing");
        Assert.Equal(new[] { 1, 2 }, session.Turns.Select(t => t.Context.DateIndex)); session.SetContext(first); Assert.Single(session.Turns); Assert.Equal(first.Date, session.Turns[0].Context.Date);
        session.SetContext(LeagueChatContext.From(LeagueChatFixtures.Snapshot(1, historical: true))); Assert.Empty(session.Turns);
    }
    [Theory]
    [InlineData("cancel")] [InlineData("reset")] [InlineData("suspend")] [InlineData("rewind")] [InlineData("owner")] [InlineData("dispose")]
    public async Task IgnoringRouterCancellationCannotCommitAcrossSessionIntent(string operation)
    {
        var pending = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new LeagueChatFixtures.Router { Routing = (_, _) => pending.Task }; var auth = new LeagueChatFixtures.Auth(); using var session = LeagueChatFixtures.Session(auth, router);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context); var asking = session.AskAsync(context, "Where do we stand?"); Assert.False(asking.IsCompleted);
        switch (operation) { case "cancel": session.Cancel(); break; case "reset": session.NewChat(); break; case "suspend": session.SetContext(null); break;
            case "rewind": session.SetContext(LeagueChatContext.From(LeagueChatFixtures.Snapshot(1))); break; case "owner": auth.Change("second"); break; case "dispose": session.Dispose(); break; }
        var result = await asking.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(result.Accepted); pending.SetResult(new(MatchRoutingStatus.Selected, "club_standing")); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task IgnoringNarratorCancellationAndLateOwnerChangeDoNotLeakReplies()
    {
        var pending = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new LeagueChatFixtures.Model { Completing = (_, _) => { entered.SetResult(); return pending.Task; } }; var auth = new LeagueChatFixtures.Auth(); using var session = LeagueChatFixtures.Session(auth, model: model);
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context); var asking = session.AskAsync(context, "Where do we stand?"); await entered.Task;
        auth.Change("second"); Assert.False((await asking.WaitAsync(TimeSpan.FromSeconds(2))).Accepted); pending.SetResult(new("{}", [])); Assert.Empty(session.Turns);
        var checks = 0; auth.Reading = () => { if (++checks == 2) auth.Change("third", false); return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(auth.User)); };
        var result = await session.AskSourceAsync(context, "standing"); Assert.False(result.Accepted); Assert.Empty(session.Turns);
    }
    [Fact]
    public async Task CancellationDuringFailureFeedbackAuthenticationDoesNotEscapeOrAppend()
    {
        var checks = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new LeagueChatFixtures.Auth(); auth.Reading = () => { if (++checks == 1) return Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(auth.User)); entered.TrySetResult(); return pending.Task; };
        var model = new LeagueChatFixtures.Model { Completing = (_, _) => throw new InvalidDataException("Synthetic rejected narration") };
        using var session = LeagueChatFixtures.Session(auth, model: model); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        var asking = session.AskAsync(context, "Where do we stand?"); await entered.Task; session.Cancel();
        Assert.False((await asking.WaitAsync(TimeSpan.FromSeconds(2))).Accepted); Assert.Empty(session.Turns); pending.SetResult(new(auth.User));
    }
    [Fact]
    public async Task SignedOutCannotRetrieveAndOwnerChangeClearsQuestionPointers()
    {
        var auth = new LeagueChatFixtures.Auth(); auth.Change(null); var reads = 0; var router = new LeagueChatFixtures.Router(); using var session = LeagueChatFixtures.Session(auth, router, executor: LeagueChatFixtures.Executor((_, _, _) => reads++));
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context); Assert.False((await session.AskAsync(context, "Where do we stand?")).Accepted); Assert.Equal(0, reads);
        auth.Change("first"); await session.AskAsync(context, "How are Arsenal doing?"); auth.Change("second"); await session.AskAsync(context, "Where do we stand?"); Assert.Empty(router.Inputs[^1].PreviousQuestions); Assert.False(router.Inputs[^1].HasNamedClubReference);
    }
    [Theory]
    [InlineData("number")] [InlineData("citation")] [InlineData("tool")] [InlineData("metric")]
    public async Task BadNarrationNeverEntersTranscriptAsAnAnswer(string failure)
    {
        var model = new LeagueChatFixtures.Model { Completing = (_, _) => Task.FromResult(new MatchModelReply(failure switch {
            "number" => """{"answer":"Everton have 999 points.","evidenceIds":["club_standing"]}""",
            "citation" => """{"answer":"Everton are here.","evidenceIds":["current_player"]}""",
            "metric" => """{"answer":"Everton have 50% possession.","evidenceIds":["club_standing"]}""", _ => "{}" }, failure == "tool" ? [new("bad", "fetch", "{}")] : [])) };
        using var session = LeagueChatFixtures.Session(model: model); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(context);
        var result = await session.AskAsync(context, "Where do we stand?"); Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.True(result.Accepted);
        Assert.Single(session.Turns); Assert.Empty(session.Turns[0].Evidence); Assert.DoesNotContain("999", session.Turns[0].Answer);
    }
    [Fact]
    public async Task NarratorHasSeparateLeaguePromptOnlyCurrentFactsAndNoAssistantHistory()
    {
        var model = new LeagueChatFixtures.Model(); using var session = LeagueChatFixtures.Session(model: model); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1)); session.SetContext(context);
        await session.AskAsync(context, "Where do we stand?"); await session.AskAsync(context, "How are Arsenal doing?");
        var request = model.Requests[^1]; Assert.StartsWith(LeagueGuidePrompt.Version, request.Messages[0].Content); Assert.DoesNotContain(CommentatorPrompt.Version, string.Join(" ", request.Messages.Select(m => m.Content)));
        Assert.All(request.Messages, m => Assert.Contains(m.Role, new[] { "system", "user" })); Assert.All(request.AllowedEvidenceIds, id => Assert.Contains(id, LeagueWorkflowRegistry.EvidenceIds.AsEnumerable()));
        Assert.DoesNotContain(session.Turns[0].Answer, request.Messages.Single(m => m.Content.StartsWith("Untrusted question")).Content);
        Assert.Throws<InvalidDataException>(() => new LeagueNarratorRequest([new("system", CommentatorPrompt.Version + "\n" + CommentatorPrompt.Persona)], ["club_standing"]));
    }
}
