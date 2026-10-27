using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.HistoricalChat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatRecoveryTests
{
    [Fact]
    public async Task MeetingsThenEmptyFormAnswersTheLoadedSeasonLimitWithoutAnotherNarration()
    {
        var fixture = HistoricalChatFixtures.Fixture() with { HomePreMatchForm = [], AwayPreMatchForm = [] };
        var context = HistoricalChatContext.From(fixture);
        var router = new HistoricalChatSessionTests.Router("continue_topic");
        var model = new HistoricalChatSessionTests.Model();
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), HistoricalChatFixtures.Evidence(fixture), router, model, Options.Create(new MatchChatOptions()));
        session.SetContext(context);
        Assert.True((await session.AskSuggestionAsync(context, "meetings")).Accepted);

        var result = await session.AskSuggestionAsync(context, "form");

        Assert.True(result.Accepted);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Contains("no earlier", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.Match.Date, result.Message);
        Assert.Single(model.Requests);
        Assert.Empty(router.Inputs);
        Assert.Equal("form", Assert.Single(session.Turns.Last().Evidence).Id);
        Assert.True(session.Turns.Last().SourceOnly);
        Assert.DoesNotContain("verified AI reply", result.Message);
        Assert.Contains("selected season (2016-17)", result.Message);
        Assert.Contains("never played", result.Message);

        Assert.True((await session.AskAsync(context, "And Stoke?")).Accepted);
        Assert.Equal("form", Assert.Single(router.Inputs).Topic!.Workflow);
        Assert.Null(router.Inputs[0].Topic!.ClubId);
        Assert.Equal("stoke-city", session.Turns.Last().ClubId);
        Assert.Equal(2, model.Requests.Count);
        Assert.False(session.Turns.Last().SourceOnly);
    }

    [Theory]
    [InlineData("result", "fixture", false)][InlineData("form", "form", false)]
    [InlineData("table", "table", false)][InlineData("meetings", "reference", false)]
    [InlineData("result", "fixture", true)][InlineData("form", "form", true)]
    [InlineData("table", "table", true)][InlineData("meetings", "reference", true)]
    public async Task KnownSuggestionRecoversOnlyItsFreshSourceAndKeepsTheClosedFailure(string id, string evidenceId, bool narrationFailure)
    {
        using var scenario = new Scenario();
        scenario.Model.Complete = (_, _) => narrationFailure ? Task.FromException<MatchModelReply>(new HttpRequestException(Private))
            : Task.FromResult(Reply("Stoke scored through Crouch. " + Private));
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, id);
        Assert.True(result.Accepted); Assert.Equal(MatchChatOutcome.NarratorUnavailable, result.Outcome);
        Assert.StartsWith("I couldn’t provide a verified AI reply. Here are the match facts.", result.Message);
        var turn = Assert.Single(scenario.Session.Turns); Assert.True(turn.SourceOnly); Assert.Null(turn.ClubId);
        Assert.Equal(evidenceId, Assert.Single(turn.Evidence).Id);
        var prepared = HistoricalEvidence.Prepare(scenario.Fixture, HistoricalChatFixtures.Clubs, HistoricalWorkflows.Suggestion(id)!.Workflow, null);
        Assert.Equal(prepared.Evidence.Single(e => e.Id == evidenceId).Text, turn.Evidence[0].Text);
        Assert.DoesNotContain(Private, turn.Answer); Assert.DoesNotContain("Crouch", turn.Answer);
        Assert.DoesNotContain(Private, turn.Evidence[0].Text);
        Assert.Equal(1, scenario.MatchReads); Assert.Equal(1, scenario.SnapshotReads); Assert.Equal(1, scenario.Model.Calls); Assert.Empty(scenario.Router.Inputs);
        var warning = Assert.Single(scenario.Log.Entries);
        Assert.Equal(7301, warning.Event); Assert.Equal(LogLevel.Warning, warning.Level); Assert.Null(warning.Exception);
        Assert.Contains(narrationFailure ? "Narration NarrationFailure" : "ReplyValidation DetailMechanismQualifier", warning.Message);
        Assert.DoesNotContain(Private, warning.Message);

        scenario.Model.Complete = (_, _) => Task.FromResult(Reply("The result is recorded."));
        Assert.True((await scenario.Session.AskAsync(scenario.Context, "And Stoke?")).Accepted);
        var input = Assert.Single(scenario.Router.Inputs);
        Assert.Equal(HistoricalWorkflows.Suggestion(id)!.Workflow, input.Topic!.Workflow); Assert.Null(input.Topic.ClubId);
        Assert.Equal("stoke-city", scenario.Session.Turns.Last().ClubId);
        Assert.False(scenario.Session.Turns.Last().SourceOnly);
    }

    [Fact]
    public async Task EmptySamplesUseTheExistingEligibilityFilterRatherThanRawArrayLength()
    {
        var f = HistoricalChatFixtures.Fixture();
        LeagueMatch[] polluted = [f.Match, f.Match with { MatchKey = "future", Date = "2017-05-22" },
            f.Match with { MatchKey = "invalid-date", Date = "2017-02-30" }, f.Match with { MatchKey = "invalid-score", Date = "2017-05-01", HomeGoals = -1 },
            HistoricalChatFixtures.Game("chelsea", "arsenal", "2017-05-01", 1, 0)];
        using var scenario = new Scenario(f with { HomePreMatchForm = polluted, AwayPreMatchForm = polluted });
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, "form");
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.True(result.Accepted); Assert.True(Assert.Single(scenario.Session.Turns).SourceOnly);
        Assert.Contains("no earlier loaded results", result.Message); Assert.Equal(0, scenario.Model.Calls); Assert.Empty(scenario.Log.Entries);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ZeroGoalsWithEarlierGamesStillNarratesAndKeepsAIOrigin(bool bothSamples)
    {
        var f = HistoricalChatFixtures.Fixture(); var game = HistoricalChatFixtures.Game(f.Home.Id, f.Away.Id, "2017-05-01", 0, 0);
        using var scenario = new Scenario(f with { HomePreMatchForm = [game], AwayPreMatchForm = bothSamples ? [game] : [] });
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, "form");
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(1, scenario.Model.Calls);
        Assert.False(Assert.Single(scenario.Session.Turns).SourceOnly); Assert.Empty(scenario.Log.Entries);
        var html = await Render(scenario); Assert.Contains("Matchmate · AI", WebUtility.HtmlDecode(html)); Assert.DoesNotContain("Source answer", html);
    }

    [Fact]
    public async Task FormRecoveryKeepsFiveDeduplicatedStrictlyEarlierSelectedSeasonResults()
    {
        var f = HistoricalChatFixtures.Fixture();
        var games = Enumerable.Range(1, 7).Select(day => HistoricalChatFixtures.Game(f.Home.Id, f.Away.Id, $"2017-04-{day:00}", 0, 1) with { MatchKey = "earlier-" + day }).ToArray();
        using var scenario = new Scenario(f with { HomePreMatchForm = [..games, games[6], f.Match, f.Match with { Date = "2017-05-22", HomeGoals = 49 }] });
        scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, "form");
        Assert.Contains("selected season (2016-17), strictly before 2017-05-21", result.Message);
        Assert.Contains("Southampton: 5 earlier results in this sample", result.Message);
        Assert.Contains("0 goals for and 5 against in that earlier sample", result.Message);
        Assert.DoesNotContain("49", result.Message); Assert.DoesNotContain("2017-04-01", result.Message);
        Assert.Equal("form", Assert.Single(Assert.Single(scenario.Session.Turns).Evidence).Id);
    }

    [Fact]
    public async Task TableAndMeetingsRecoveryRetainDateAndCoverageBoundaries()
    {
        var f = HistoricalChatFixtures.Fixture();
        var polluted = f with { PriorMeetings = [..f.PriorMeetings, new("2016-17", f.Match, f.Home.Name, f.Away.Name),
            new("2016-17", f.Match with { Date = "2017-05-22" }, f.Home.Name, f.Away.Name),
            new("2016-17", f.Match with { Date = "invalid" }, f.Home.Name, f.Away.Name),
            new("2016-17", HistoricalChatFixtures.Game("chelsea", "arsenal", "2017-05-01", 5, 4), "Chelsea", "Arsenal")] };
        using var scenario = new Scenario(polluted); scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        var table = await scenario.Session.AskSuggestionAsync(scenario.Context, "table");
        Assert.Contains("End of 2017-05-21: all results on that date enter together", table.Message);
        Assert.Contains("not solely attributed to this fixture", table.Message);
        Assert.Contains(HistoricalEvidence.Table(f.HomeTable), table.Message);
        Assert.Contains(HistoricalEvidence.Table(f.AwayTable), table.Message);
        var meetings = await scenario.Session.AskSuggestionAsync(scenario.Context, "meetings");
        Assert.Contains("1 earlier meetings", meetings.Message); Assert.Contains("2016-12-14", meetings.Message);
        Assert.Contains("not an all-time head-to-head record", meetings.Message);
        Assert.Contains("Strictly before 2017-05-21", meetings.Message); Assert.DoesNotContain("2017-05-22", meetings.Message); Assert.DoesNotContain("Chelsea", meetings.Message);
    }

    [Fact]
    public async Task FailedTypedReplyStillClearsOlderTopicAndNeverUsesTheSourcePath()
    {
        var router = new HistoricalChatSessionTests.Router("form", "overview", "continue_topic"); using var scenario = new Scenario(router: router);
        Assert.True((await scenario.Session.AskAsync(scenario.Context, "How were Southampton doing?")).Accepted);
        scenario.Model.Complete = (_, _) => Task.FromResult(Reply("Stoke scored through Crouch. " + Private));
        var rejected = await scenario.Session.AskAsync(scenario.Context, HistoricalWorkflows.Suggestion("result")!.Question);
        Assert.Equal(MatchChatOutcome.Unavailable, rejected.Outcome); Assert.Empty(scenario.Session.Turns.Last().Evidence);
        Assert.False(scenario.Session.Turns.Last().SourceOnly); Assert.DoesNotContain(Private, rejected.Message);
        Assert.Equal(MatchChatOutcome.Clarify, (await scenario.Session.AskAsync(scenario.Context, "Go on")).Outcome);
        Assert.Null(router.Inputs.Last().Topic); Assert.Equal(2, scenario.Model.Calls);
    }

    [Theory]
    [InlineData("invalid-id")][InlineData("anonymous")][InlineData("role")][InlineData("model")][InlineData("router")]
    public async Task InvalidSuggestionsAndUnavailableAuthenticationOrConfigurationNeverReadOrRecover(string mode)
    {
        var auth = new HistoricalChatSessionTests.Auth(); if (mode == "anonymous") auth.Set(null); if (mode == "role") auth.Set("reader", tester: false);
        using var scenario = new Scenario(auth: auth); scenario.Model.Configured = mode != "model"; scenario.Router.Configured = mode != "router";
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, mode == "invalid-id" ? "TABLE" : "form");
        Assert.Equal(mode == "invalid-id" ? MatchChatOutcome.InvalidRequest : MatchChatOutcome.Disconnected, result.Outcome);
        Assert.False(result.Accepted); Assert.Empty(scenario.Session.Turns); Assert.Equal(0, scenario.MatchReads); Assert.Equal(0, scenario.SnapshotReads); Assert.Equal(0, scenario.Model.Calls);
    }

    [Theory]
    [InlineData("retrieval")][InlineData("preparation")][InlineData("version")]
    public async Task RetrievalBindingAndPreparationFailuresCannotUseSourceRecovery(string stage)
    {
        var f = HistoricalChatFixtures.Fixture();
        if (stage == "preparation") f = f with { HomePreMatchForm = [HistoricalChatFixtures.Game(f.Home.Id, "unknown", "2017-05-01", 1, 0)] };
        using var scenario = new Scenario(f); if (stage == "retrieval") scenario.ReadFailure = new HttpRequestException(Private);
        var context = stage == "version" ? scenario.Context with { Version = "changed" } : scenario.Context; scenario.Session.SetContext(context);
        var result = await scenario.Session.AskSuggestionAsync(context, "form");
        Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Equal(0, scenario.Model.Calls);
        var turn = Assert.Single(scenario.Session.Turns); Assert.False(turn.SourceOnly); Assert.Empty(turn.Evidence);
        Assert.DoesNotContain(Private, result.Message);
    }

    [Theory]
    [InlineData("cancel")][InlineData("caller")][InlineData("reset")][InlineData("context")][InlineData("version")]
    [InlineData("auth")][InlineData("silent-owner")][InlineData("dispose")][InlineData("timeout")]
    public async Task DelayedNarrationFailureCannotRecoverAfterRequestAuthorityChanges(string action)
    {
        var auth = new HistoricalChatSessionTests.Auth(); using var scenario = new Scenario(auth: auth, timeout: action == "timeout" ? 1 : 30);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reply = new TaskCompletionSource<MatchModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Model.Complete = (_, _) => { started.TrySetResult(); return reply.Task; };
        using var cancellation = new CancellationTokenSource(); var ask = scenario.Session.AskSuggestionAsync(scenario.Context, "table", cancellation.Token); await started.Task;
        switch (action)
        {
            case "cancel": scenario.Session.Cancel(); break; case "caller": cancellation.Cancel(); break; case "reset": scenario.Session.NewChat(); break;
            case "context": scenario.Session.SetContext(scenario.Context with { MatchKey = "other:fixture" }); break;
            case "version": scenario.Session.SetContext(scenario.Context with { Version = "changed" }); break;
            case "auth": auth.Set("other"); break; case "silent-owner": auth.Set("other", notify: false); break; case "dispose": scenario.Session.Dispose(); break;
        }
        if (action == "timeout") { Assert.False((await ask).Accepted); reply.SetException(new HttpRequestException(Private)); }
        else { reply.SetException(new HttpRequestException(Private)); Assert.False((await ask).Accepted); }
        Assert.Empty(scenario.Session.Turns); Assert.Equal(1, scenario.Model.Calls); Assert.Equal(1, scenario.MatchReads); Assert.Empty(scenario.Router.Inputs);
    }

    [Theory]
    [InlineData(401)][InlineData(403)][InlineData(0)]
    public async Task ProviderAuthenticationFailureRemainsUnavailable(int status)
    {
        using var scenario = new Scenario();
        Exception error = status == 0 ? new Azure.Identity.AuthenticationFailedException(Private) : new HttpRequestException(Private, null, (HttpStatusCode)status);
        scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(error);
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, "result");
        Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome); Assert.Equal(1, scenario.Model.Calls);
        Assert.False(Assert.Single(scenario.Session.Turns).SourceOnly); Assert.Empty(scenario.Session.Turns[0].Evidence);
        Assert.Contains("Narration ProviderAuthentication", Assert.Single(scenario.Log.Entries).Message);
    }

    [Theory]
    [InlineData("rate-limit", "Narration ProviderRateLimited")]
    [InlineData("incomplete", "Narration IncompleteNarratorReply")]
    [InlineData("invalid-json", "ReplyValidation InvalidReplyJson")]
    [InlineData("tool-call", "ReplyValidation UnexpectedToolCall")]
    public async Task AllowedProviderAndValidationFailuresRecoverWithoutLeakingPayloads(string mode, string diagnostic)
    {
        using var scenario = new Scenario();
        scenario.Model.Complete = (_, _) => mode switch
        {
            "rate-limit" => Task.FromException<MatchModelReply>(new HttpRequestException(Private, null, HttpStatusCode.TooManyRequests)),
            "incomplete" => Task.FromException<MatchModelReply>(new InvalidDataException("Incomplete, filtered or unexpected tool-call narrator reply.")),
            "invalid-json" => Task.FromResult(new MatchModelReply("{" + Private, [])),
            _ => Task.FromResult(new MatchModelReply(Private, [new(Private, Private, Private)]))
        };
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, "table");
        Assert.True(result.Accepted); Assert.Equal(MatchChatOutcome.NarratorUnavailable, result.Outcome);
        Assert.True(Assert.Single(scenario.Session.Turns).SourceOnly); Assert.Equal("table", Assert.Single(scenario.Session.Turns[0].Evidence).Id);
        Assert.Equal(1, scenario.Model.Calls); Assert.Equal(1, scenario.MatchReads); Assert.Equal(1, scenario.SnapshotReads);
        Assert.Contains(diagnostic, Assert.Single(scenario.Log.Entries).Message); Assert.DoesNotContain(Private, result.Message);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SourceCommitAuthenticationFailureClearsTheOlderNamedTopicWithoutRetry(bool emptyForm)
    {
        var auth = new FailingAuth(4); var f = HistoricalChatFixtures.Fixture(); if (emptyForm) f = f with { HomePreMatchForm = [], AwayPreMatchForm = [] };
        var router = new HistoricalChatSessionTests.Router("form", "continue_topic"); using var scenario = new Scenario(f, router: router, auth: auth);
        Assert.True((await scenario.Session.AskAsync(scenario.Context, "How were Southampton doing?")).Accepted);
        Assert.Equal("southampton", Assert.Single(scenario.Session.Turns).ClubId);
        scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        Assert.False((await scenario.Session.AskSuggestionAsync(scenario.Context, emptyForm ? "form" : "table")).Accepted);
        Assert.Equal(4, auth.Calls); Assert.Single(scenario.Session.Turns);
        Assert.Equal(MatchChatOutcome.Clarify, (await scenario.Session.AskAsync(scenario.Context, "Go on")).Outcome);
        Assert.Null(router.Inputs.Last().Topic); Assert.False(scenario.Session.Turns.Last().SourceOnly);
        Assert.Equal(emptyForm ? 1 : 2, scenario.Model.Calls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SourceCommitCancellationClearsOnlyItsOwnGeneration(bool newerQuestion)
    {
        var auth = new DelayedCommitAuth(); var router = new HistoricalChatSessionTests.Router("form", newerQuestion ? "table" : "continue_topic", "continue_topic");
        using var scenario = new Scenario(router: router, auth: auth);
        Assert.True((await scenario.Session.AskAsync(scenario.Context, "How were Southampton doing?")).Accepted);
        scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        using var cancellation = new CancellationTokenSource(); var old = scenario.Session.AskSuggestionAsync(scenario.Context, "result", cancellation.Token);
        await auth.Started.Task;
        if (newerQuestion)
        {
            scenario.Model.Complete = (_, _) => Task.FromResult(Reply("The result is recorded."));
            Assert.True((await scenario.Session.AskAsync(scenario.Context, "How did Stoke finish in the table?")).Accepted);
        }
        else cancellation.Cancel();
        Assert.False((await old).Accepted); auth.Pending.TrySetException(new HttpRequestException(Private));
        var followup = await scenario.Session.AskAsync(scenario.Context, "Go on");
        if (newerQuestion)
        {
            Assert.Equal(MatchChatOutcome.Answered, followup.Outcome); Assert.Equal("table", router.Inputs.Last().Topic!.Workflow);
            Assert.Equal("stoke-city", router.Inputs.Last().Topic!.ClubId);
        }
        else { Assert.Equal(MatchChatOutcome.Clarify, followup.Outcome); Assert.Null(router.Inputs.Last().Topic); }
        Assert.All(scenario.Session.Turns, turn => Assert.False(turn.SourceOnly));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FailedLatestAuthenticationOnSourceCommitIsAttemptedOnlyOnce(bool emptyForm)
    {
        var auth = new FailingAuth(2); var f = HistoricalChatFixtures.Fixture(); if (emptyForm) f = f with { HomePreMatchForm = [], AwayPreMatchForm = [] };
        using var scenario = new Scenario(f, auth: auth); scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new HttpRequestException(Private));
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, emptyForm ? "form" : "table");
        Assert.False(result.Accepted); Assert.Empty(scenario.Session.Turns); Assert.Equal(2, auth.Calls);
        Assert.Equal(emptyForm ? 0 : 1, scenario.Model.Calls); Assert.Contains("Commit CommitFailure", scenario.Log.Entries.Last().Message);
    }

    [Fact]
    public async Task InitialAuthenticationExceptionNeverReadsOrRecovers()
    {
        using var scenario = new Scenario(auth: new FailingAuth(1));
        Assert.False((await scenario.Session.AskSuggestionAsync(scenario.Context, "table")).Accepted);
        Assert.Empty(scenario.Session.Turns); Assert.Equal(0, scenario.MatchReads); Assert.Equal(0, scenario.Model.Calls);
    }

    [Theory]
    [InlineData("result")][InlineData("form")][InlineData("table")][InlineData("meetings")][InlineData("empty-form")]
    public async Task LongSourceFactsUseWholeBoundedSummariesWithoutLosingImportantQualifications(string id)
    {
        var f = HistoricalChatFixtures.Fixture(); var home = f.Home with { Name = "Home " + new string('h', 1100) }; var away = f.Away with { Name = "Away " + new string('a', 1100) };
        f = f with { Home = home, Away = away, HomeTable = f.HomeTable with { Club = home, Row = f.HomeTable.Row with { Name = home.Name } },
            AwayTable = f.AwayTable with { Club = away, Row = f.AwayTable.Row with { Name = away.Name } },
            PriorMeetings = [new("2016-17", f.PriorMeetings[0].Match, away.Name, home.Name)] };
        if (id == "empty-form") f = f with { HomePreMatchForm = [], AwayPreMatchForm = [] };
        var clubs = HistoricalChatFixtures.Clubs.Select(c => c.Id == home.Id ? home : c.Id == away.Id ? away : c).ToArray();
        using var scenario = new Scenario(f, clubs: clubs); scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        var result = await scenario.Session.AskSuggestionAsync(scenario.Context, id == "empty-form" ? "form" : id);
        Assert.True(result.Accepted); Assert.True(Assert.Single(scenario.Session.Turns).SourceOnly); Assert.InRange(result.Message.Length, 1, MatchChatLimits.AnswerChars);
        switch (id)
        {
            case "form": Assert.Contains("strictly before 2017-05-21", result.Message); Assert.Contains("earlier sample", result.Message); break;
            case "empty-form": Assert.Contains("no earlier loaded results", result.Message); Assert.Contains("never played", result.Message); break;
            case "table": Assert.Contains("All results on that date", result.Message); Assert.Contains("not solely attributed", result.Message); break;
            case "meetings": Assert.Contains("strictly before 2017-05-21", result.Message); Assert.Contains("not an all-time", result.Message); break;
            case "result": Assert.Contains("2017-05-21", result.Message); Assert.Contains("0–1", result.Message); break;
        }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SourceOriginIsRenderedBeforeOutcomeAndSourceContentIsEncoded(bool fallback)
    {
        var f = HistoricalChatFixtures.Fixture(); var home = f.Home with { Name = "Southampton <script>unsafe()</script>" };
        f = f with { Home = home, HomeTable = f.HomeTable with { Club = home }, HomePreMatchForm = [], AwayPreMatchForm = [] };
        var clubs = HistoricalChatFixtures.Clubs.Select(c => c.Id == home.Id ? home : c).ToArray();
        using var scenario = new Scenario(f, clubs: clubs); scenario.Model.Complete = (_, _) => Task.FromException<MatchModelReply>(new IOException(Private));
        Assert.True((await scenario.Session.AskSuggestionAsync(scenario.Context, fallback ? "result" : "form")).Accepted);
        var html = await Render(scenario); Assert.Contains("Matchmate · Source answer", WebUtility.HtmlDecode(html)); Assert.DoesNotContain("Matchmate · AI", WebUtility.HtmlDecode(html));
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>", html); Assert.Contains("Supporting facts", html);
    }

    private const string Private = "PRIVATE-rejected-content-and-provider-detail";
    private static MatchModelReply Reply(string answer) => new(HistoricalChatFixtures.Reply(answer, "fixture"), []);
    private static async Task<string> Render(Scenario scenario)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton(scenario.Session).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<HistoricalConversation>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(HistoricalConversation.Context)] = scenario.Context }))).ToHtmlString());
    }

    private sealed class Scenario : IDisposable
    {
        public LeagueHistoricalMatch Fixture { get; }
        public HistoricalChatContext Context { get; }
        public HistoricalChatSession Session { get; }
        public HistoricalChatSessionTests.Router Router { get; }
        public Model Model { get; } = new();
        public CaptureLog Log { get; } = new();
        public int MatchReads { get; private set; }
        public int SnapshotReads { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Scenario(LeagueHistoricalMatch? fixture = null, HistoricalChatSessionTests.Router? router = null, AuthenticationStateProvider? auth = null, int timeout = 30, LeagueClub[]? clubs = null)
        {
            Fixture = fixture ?? HistoricalChatFixtures.Fixture(); Context = HistoricalChatContext.From(Fixture); Router = router ?? new("continue_topic");
            var evidence = new HistoricalEvidence((_, _, _) => { MatchReads++; return ReadFailure is null ? Task.FromResult(Fixture) : Task.FromException<LeagueHistoricalMatch>(ReadFailure); },
                (_, _, _, _) => { SnapshotReads++; return Task.FromResult(HistoricalChatFixtures.Model().Snapshot(null) with { Clubs = clubs ?? HistoricalChatFixtures.Clubs }); });
            Session = new HistoricalChatSession(auth ?? new HistoricalChatSessionTests.Auth(), evidence, Router, Model, Options.Create(new MatchChatOptions { TimeoutSeconds = timeout }), logger: Log);
            Session.SetContext(Context);
        }
        public void Dispose() => Session.Dispose();
    }
    private sealed class Model : IHistoricalChatModel
    {
        public bool Configured { get; set; } = true;
        public int Calls { get; private set; }
        public Func<HistoricalNarratorRequest, CancellationToken, Task<MatchModelReply>> Complete { get; set; } = (_, _) => Task.FromResult(Reply("The result is recorded."));
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token) { Calls++; return Complete(request, token); }
    }
    private sealed class CaptureLog : ILogger<HistoricalChatSession>
    {
        public List<(LogLevel Level, int Event, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, eventId.Id, formatter(state, exception), exception));
    }
    private sealed class FailingAuth(int failOn) : AuthenticationStateProvider
    {
        public int Calls { get; private set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => ++Calls == failOn ? Task.FromException<AuthenticationState>(new HttpRequestException(Private))
            : Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim(ClaimTypes.Role, "Tester")], "test"))));
    }
    private sealed class DelayedCommitAuth : AuthenticationStateProvider
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AuthenticationState> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (++_calls == 4) { Started.TrySetResult(); return Pending.Task; }
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner"), new Claim(ClaimTypes.Role, "Tester")], "test"))));
        }
    }
}
