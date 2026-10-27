using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatSuggestionTests
{
    private static readonly HistoricalChatContext Context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
    private static HistoricalChatSession Session(HistoricalChatSessionTests.Auth auth, HistoricalChatSessionTests.Router router,
        HistoricalChatSessionTests.Model model, HistoricalEvidence? evidence = null)
    {
        var session = new HistoricalChatSession(auth, evidence ?? HistoricalChatFixtures.Evidence(), router, model, Options.Create(new MatchChatOptions()));
        session.SetContext(Context); return session;
    }

    [Theory]
    [InlineData("result", "fixture")]
    [InlineData("form", "form")]
    [InlineData("table", "table")]
    [InlineData("meetings", "reference")]
    public async Task ExplicitSuggestionUsesTrustedWorkflowAndNarratesWithoutClassifier(string id, string evidenceId)
    {
        var router = new HistoricalChatSessionTests.Router("unsupported"); var model = new HistoricalChatSessionTests.Model();
        using var session = Session(new(), router, model);
        var result = await session.AskSuggestionAsync(Context, id);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.True(result.Accepted); Assert.Empty(router.Inputs);
        var request = Assert.Single(model.Requests); Assert.Contains(evidenceId, request.AllowedEvidenceIds);
        Assert.Equal(HistoricalWorkflows.Suggestion(id)!.Question, request.Messages.Last().Content);
        Assert.Equal(HistoricalWorkflows.Suggestion(id)!.Question, Assert.Single(session.Turns).Question);
    }

    [Fact]
    public async Task TypedEquivalentStillUsesDecisionRouter()
    {
        var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalChatSessionTests.Model();
        using var session = Session(new(), router, model);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(Context, HistoricalWorkflows.Suggestion("table")!.Question)).Outcome);
        Assert.Equal("What changed in the table?", Assert.Single(router.Inputs).Question); Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData("")][InlineData("TABLE")][InlineData("table ")][InlineData("table;unsupported")][InlineData("fetch-other-fixture")]
    public async Task UnknownSuggestionCannotRetrieveInferOrCommit(string id)
    {
        var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalChatSessionTests.Model();
        var evidence = new HistoricalEvidence((_, _, _) => throw new InvalidOperationException("Must not retrieve"), (_, _, _, _) => throw new InvalidOperationException("Must not retrieve"));
        using var session = Session(new(), router, model, evidence);
        Assert.Equal(MatchChatOutcome.InvalidRequest, (await session.AskSuggestionAsync(Context, id)).Outcome);
        Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Empty(session.Turns);
    }

    [Theory]
    [InlineData("anonymous")][InlineData("role")][InlineData("model")][InlineData("router")]
    public async Task SuggestionKeepsAuthenticationAndConfiguredProviderGate(string mode)
    {
        var auth = new HistoricalChatSessionTests.Auth(); var router = new HistoricalChatSessionTests.Router("overview") { Configured = mode != "router" };
        var model = new HistoricalChatSessionTests.Model { Configured = mode != "model" };
        if (mode == "anonymous") auth.Set(null); if (mode == "role") auth.Set("reader", tester: false);
        var evidence = new HistoricalEvidence((_, _, _) => throw new InvalidOperationException("Must not retrieve"), (_, _, _, _) => throw new InvalidOperationException("Must not retrieve"));
        using var session = Session(auth, router, model, evidence);
        Assert.Equal(MatchChatOutcome.Disconnected, (await session.AskSuggestionAsync(Context, "table")).Outcome);
        Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Empty(session.Turns);
    }

    [Theory]
    [InlineData("date")][InlineData("version")][InlineData("fixture")]
    public async Task SuggestionCannotChangeTheCodeBoundFixture(string changed)
    {
        var context = changed switch { "date" => Context with { Date = "2017-05-20" }, "version" => Context with { Version = "different" }, _ => Context with { MatchKey = "stoke-city:southampton" } };
        var router = new HistoricalChatSessionTests.Router("overview"); var model = new HistoricalChatSessionTests.Model(); using var session = Session(new(), router, model);
        session.SetContext(context);
        Assert.Equal(MatchChatOutcome.Unavailable, (await session.AskSuggestionAsync(context, "table")).Outcome);
        Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.All(session.Turns, turn => Assert.Empty(turn.Evidence));
    }

    [Fact]
    public async Task FixtureSuggestionResetsEarlierNamedSubjectButKeepsUserIntentHistory()
    {
        var router = new HistoricalChatSessionTests.Router("form"); var model = new HistoricalChatSessionTests.Model(); using var session = Session(new(), router, model);
        await session.AskAsync(Context, "How were Southampton doing before this game?");
        Assert.Equal("southampton", session.Turns.Last().ClubId);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskSuggestionAsync(Context, "table")).Outcome);
        Assert.Null(session.Turns.Last().ClubId); Assert.Single(router.Inputs);
        Assert.Contains("How were Southampton doing before this game?", string.Join(" ", model.Requests.Last().Messages.Select(m => m.Content)));
        Assert.DoesNotContain("The result is recorded.", string.Join(" ", model.Requests.Last().Messages.Select(m => m.Content)));
    }

    [Theory]
    [InlineData("cancel")][InlineData("reset")][InlineData("navigate")][InlineData("auth")]
    public async Task SuggestionCannotCommitAfterCancellationOrContextOwnershipChanges(string action)
    {
        var auth = new HistoricalChatSessionTests.Auth(); var router = new HistoricalChatSessionTests.Router("overview");
        var model = new HistoricalChatSessionTests.Model { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var session = Session(auth, router, model);
        var ask = session.AskSuggestionAsync(Context, "table"); await model.Started.Task;
        switch (action)
        {
            case "cancel": session.Cancel(); break; case "reset": session.NewChat(); break;
            case "navigate": session.SetContext(Context with { Version = "new-version" }); break; case "auth": auth.Set("other-owner"); break;
        }
        model.Delayed.SetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []));
        Assert.False((await ask).Accepted); Assert.Empty(session.Turns); Assert.Empty(router.Inputs);
    }

    [Fact]
    public async Task SuggestionStillRejectsUnsupportedNarratorClaims()
    {
        var model = new HistoricalChatSessionTests.Model { Answer = "Stoke scored through Crouch, not Arnautovic." };
        var router = new HistoricalChatSessionTests.Router("overview"); using var session = Session(new(), router, model);
        var result = await session.AskSuggestionAsync(Context, "result");
        Assert.Equal(MatchChatOutcome.NarratorUnavailable, result.Outcome); Assert.True(result.Accepted);
        Assert.Empty(router.Inputs); Assert.Single(model.Requests);
        var turn = Assert.Single(session.Turns); Assert.True(turn.SourceOnly); Assert.Equal("fixture", Assert.Single(turn.Evidence).Id);
        Assert.DoesNotContain("Crouch", result.Message); Assert.DoesNotContain("Arnautovic", result.Message);
        Assert.DoesNotContain(model.Answer, turn.Answer); Assert.DoesNotContain(model.Answer, string.Join(" ", turn.Evidence.Select(e => e.Text)));
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(model.Answer, "fixture"),
            HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", null)));
    }
}
