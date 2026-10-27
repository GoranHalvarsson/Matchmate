using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;

namespace Matchmate.Tests;

public sealed class GeneratedReplayChatTests(ITestOutputHelper output)
{
    private static (TestFixture Fixture, FootballLedger Ledger) Recording()
    {
        var fixture = GeneratedFixture(); fixture = fixture with { Manifest = fixture.Manifest with
        { Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "generated-actions.json", new string('a', 64))] } };
        var ledger = new FootballLedger(); Add(ledger, fixture, 1, GeneratedContext(fixture));
        Add(ledger, fixture, 2, Action(fixture, "first-pass", "pass", 1000, 7400), Action(fixture, "control", "possession", 0, 20000, outcome: "controlled"));
        return (fixture, ledger);
    }
    private static MatchWorkflowRequest Request(FootballView view, string workflow = "current_replay_statistics", MatchSelectedEvent? selected = null) => new(workflow, MatchWorkflowRegistry.Version,
        new("unit-owner", 1, new(view.Context, view.Dataset.HeadSequence, selected), MatchWorkflowTemporalMode.ReplayAtCursor));
    private static (string Answer, MatchChatEvidence[] Evidence) Format(MatchWorkflowBundle bundle) =>
        ((string, MatchChatEvidence[]))typeof(MatchSourceQuestions).GetMethod("Format", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bundle])!;

    [Fact]
    public async Task FreshLedgerCursorBoundaryRewindAndSelectedGoalNeverReadFutureStatistics()
    {
        var (fixture, ledger) = Recording(); var reads = new List<FootballQuery>();
        var executor = new MatchWorkflowExecutor((query, _) => { reads.Add(query); return Task.FromResult(ledger.Query(query)); });
        foreach (var cursor in new long[] { 7300, 7400, 7300, 20000 })
        {
            var result = await executor.ExecuteAsync(Request(ledger.Query(Query(fixture, cursor)), selected: new("future-goal", 60000, "1")), _ => true);
            Assert.Equal(MatchWorkflowStatus.Success, result.Status); var bundle = result.Bundle!;
            Assert.Null(bundle.Context.SelectedEvent); Assert.Empty(bundle.EventProvenance); Assert.Equal(cursor, reads[^1].CursorMs);
            var json = bundle.Items.Single(i => i.Tool == "illustration_statistics").Text; using var data = JsonDocument.Parse(json);
            Assert.Equal(cursor < 7400 ? 0 : 1, data.RootElement.GetProperty("teams")[0].GetProperty("passAttempts").GetInt32());
            var answer = Format(bundle).Answer; Assert.StartsWith("In this generated replay at", answer); Assert.Contains("finished control spells", answer);
            Assert.Contains(cursor < 7400 ? "0 of 0 passes completed (unavailable)" : "1 of 1 pass completed (100%)", answer);
            Assert.DoesNotContain("future-goal", JsonSerializer.Serialize(bundle)); Assert.DoesNotContain("ReportedGoals", json); Assert.InRange(answer.Length, 1, 1800);
            Assert.Single(bundle.Sources); Assert.Equal("generated-model-actions", bundle.Sources[0].SourceId);
            Assert.Contains("\"origin\":\"synthetic\"", json); Assert.Contains("\"method\":\"generated\"", json); Assert.Contains("\"claimStatus\":\"authored\"", json);
        }
        Assert.Equal(4, reads.Count);
    }

    [Fact]
    public async Task RevisionAndWithdrawalUseTheSharedFreshApplicationFence()
    {
        var (fixture, ledger) = Recording(); var original = Action(fixture, "first-pass", "pass", 1000, 7400);
        Add(ledger, fixture, 3, original with { Revision = 2, SupersedesRevision = 1, RecordState = "withdrawn", WithdrawalReason = "Withdrawn" });
        var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query)));
        foreach (var application in new long[] { 2, 3 })
        {
            var view = ledger.Query(Query(fixture, 7400) with { ApplicationSequence = application });
            var result = await executor.ExecuteAsync(Request(view), _ => true);
            Assert.Equal(MatchWorkflowStatus.Success, result.Status); Assert.Contains(application == 2 ? "1 of 1 pass completed" : "0 of 0 passes completed", Format(result.Bundle!).Answer);
        }
    }

    [Theory]
    [InlineData("no-cap")] [InlineData("no-view")]
    public async Task MissingCapabilityOrProjectionIsUnavailableNotZero(string fault)
    {
        var (fixture, ledger) = Recording(); var view = ledger.Query(Query(fixture, 7400));
        view = fault == "no-cap" ? view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = [] } } } : view with { IllustrationStatistics = null };
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(view with { Context = q })).ExecuteAsync(Request(view), _ => true);
        Assert.Equal(MatchWorkflowStatus.Unavailable, result.Status); Assert.Null(result.Bundle); Assert.DoesNotContain("0 pass", result.Message);
    }

    [Theory]
    [InlineData("team-count")] [InlineData("duplicate")] [InlineData("foreign")] [InlineData("null-team")] [InlineData("negative")]
    [InlineData("completed")] [InlineData("cap")] [InlineData("negative-control")] [InlineData("total")] [InlineData("future-control")]
    [InlineData("share")] [InlineData("zero-share")] [InlineData("overflow")] [InlineData("source-missing")] [InlineData("source-duplicate")]
    [InlineData("hash")] [InlineData("unsafe-name")]
    public async Task MalformedAggregateOrArtifactBindingFailsRetrieval(string fault)
    {
        var (fixture, ledger) = Recording(); var view = ledger.Query(Query(fixture, 20000)); var stats = view.IllustrationStatistics!; var team = stats.Teams[0];
        stats = fault switch
        {
            "team-count" => stats with { Teams = [team] }, "duplicate" => stats with { Teams = [team, team] },
            "foreign" => stats with { Teams = [team with { TeamId = "foreign" }, stats.Teams[1]] }, "null-team" => stats with { Teams = [null!, stats.Teams[1]] },
            "negative" => stats with { Teams = [team with { PassAttempts = -1 }, stats.Teams[1]] }, "completed" => stats with { Teams = [team with { PassCompleted = 2 }, stats.Teams[1]] },
            "cap" => stats with { Teams = [team with { Shots = 2001 }, stats.Teams[1]] }, "negative-control" => stats with { Teams = [team with { ControlledMs = -1 }, stats.Teams[1]] },
            "total" => stats with { TotalClassifiedControlMs = 19999 }, "future-control" => stats with { TotalClassifiedControlMs = 20001, Teams = [team with { ControlledMs = 20001 }, stats.Teams[1]] },
            "share" => stats with { Teams = [team with { ControlSharePercent = 99m }, stats.Teams[1]] },
            "zero-share" => new([team with { ControlledMs = 0, ControlSharePercent = 0m }, stats.Teams[1] with { ControlSharePercent = 0m }], 0),
            "overflow" => stats with { Teams = [team with { ControlledMs = long.MaxValue }, stats.Teams[1] with { ControlledMs = 1 }] }, _ => stats
        };
        var sources = view.Dataset.Manifest.Sources;
        sources = fault switch { "source-missing" => sources.Where(s => s.SourceId != "generated-model-actions").ToArray(), "source-duplicate" => [.. sources, sources[^1]],
            "hash" => [sources[0], sources[1] with { ArtifactHash = "not-a-hash" }], _ => sources };
        view = view with { IllustrationStatistics = stats, Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Sources = sources },
            Fixture = fault == "unsafe-name" ? view.Dataset.Fixture! with { Home = view.Dataset.Fixture.Home with { Name = "<script>" } } : view.Dataset.Fixture } };
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(view with { Context = q })).ExecuteAsync(Request(view), _ => true);
        Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle);
    }

    [Theory] [InlineData("head")] [InlineData("context")] [InlineData("cancel")] [InlineData("stale")]
    public async Task WrongBindingAndLateChangesCannotPublishStats(string fault)
    {
        var (fixture, ledger) = Recording(); var view = ledger.Query(Query(fixture, 7400)); var current = true; using var cancellation = new CancellationTokenSource();
        var executor = new MatchWorkflowExecutor((q, _) =>
        {
            if (fault == "cancel") cancellation.Cancel(); if (fault == "stale") current = false;
            return Task.FromResult(view with { Context = fault == "context" ? q with { CursorMs = 20000 } : q,
                Dataset = fault == "head" ? view.Dataset with { HeadSequence = 3 } : view.Dataset });
        });
        var result = await executor.ExecuteAsync(Request(view), _ => current, cancellation.Token);
        Assert.Equal(fault == "cancel" ? MatchWorkflowStatus.Cancelled : MatchWorkflowStatus.Stale, result.Status); Assert.Null(result.Bundle);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task SourceAndArbitraryTypedStatisticsUseOneFreshReadAndNoNarrator(bool source)
    {
        var (fixture, ledger) = Recording(); using var h = new SessionHarness(q => Task.FromResult(ledger.Query(q)));
        h.Router.Configured = h.Model.Configured = !source;
        var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        var result = source ? await h.Session.AskSourceAsync(context, MatchSourceQuestionId.ReplayStatistics)
            : await h.Session.AskAsync(context, "How many passes have these teams completed in this replay?");
        Assert.True(result.Accepted); var turn = Assert.Single(h.Session.Turns); Assert.True(turn.EvidenceOnly); Assert.Equal(0, h.Model.Calls);
        Assert.Equal(source ? 0 : 1, h.Router.Calls); Assert.Single(h.Queries); Assert.Equal(7400, h.Queries[0].CursorMs);
        Assert.Contains(source ? "1 of 1 pass completed (100%)" : "have completed a pass", turn.Answer);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task F4_SelectedStatisticsDoNotRequireAPossessionHighlight(bool selectedFutureGoal)
    {
        const string question = "Show possession statistics for both teams in this replay";
        var (fixture, ledger) = Recording(); using var h = new SessionHarness(q => Task.FromResult(ledger.Query(q)));
        var context = new MatchChatContext(Query(fixture, 7400), 2, selectedFutureGoal ? new("future-goal", 60000, "1") : null); h.Session.SetContext(context);
        var result = await h.Session.AskAsync(context, question);
        Assert.True(result.Accepted); Assert.Equal(question, h.Router.LastInput!.Question); Assert.Equal(1, h.Router.Calls);
        Assert.Equal(7400, Assert.Single(h.Queries).CursorMs); Assert.Equal(0, h.Model.Calls);
        var turn = Assert.Single(h.Session.Turns); Assert.True(turn.EvidenceOnly); Assert.Null(turn.EventVisual); Assert.False(turn.SelectedEventScope);
        Assert.Contains("illustrated control share unavailable", turn.Answer); Assert.DoesNotContain("passes", turn.Answer);
        Assert.Contains("\"passAttempts\":1", turn.Evidence.Single(item => item.Tool == "illustration_statistics").Text);
        Assert.Contains(turn.Evidence, item => item.Tool == "illustration_statistics"); Assert.DoesNotContain(turn.Evidence, item => item.Tool is "selected_goal" or "selected_event");
    }

    [Fact]
    public async Task F4_OtherWorkflowsStillRequireTheRequestedEventVisualSelection()
    {
        var (fixture, ledger) = Recording(); using var h = new SessionHarness(q => Task.FromResult(ledger.Query(q))); h.Router.Workflow = "current_match_events";
        var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        var result = await h.Session.AskAsync(context, "Show possession statistics for both teams in this replay");
        Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Contains("Choose a completed possession interval", Assert.Single(h.Session.Turns).Answer);
        Assert.Empty(h.Queries); Assert.Equal(0, h.Model.Calls);
    }

    [Fact]
    public async Task FocusedShotsKeepTheOriginalQuestionFullEvidenceAndCurrentRead()
    {
        const string question = "How many shots are in this generated replay?";
        var (fixture, ledger) = Recording(); using var h = new SessionHarness(q => Task.FromResult(ledger.Query(q)));
        var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        Assert.True((await h.Session.AskAsync(context, question)).Accepted); var turn = Assert.Single(h.Session.Turns);
        Assert.Equal(question, h.Router.LastInput!.Question); Assert.Equal(1, h.Router.Calls); Assert.Equal(7400, Assert.Single(h.Queries).CursorMs); Assert.Equal(0, h.Model.Calls);
        Assert.True(turn.EvidenceOnly); Assert.Contains("have taken no shots yet", turn.Answer); Assert.DoesNotContain("passes", turn.Answer); Assert.DoesNotContain("control", turn.Answer);
        Assert.Contains("\"passAttempts\":1", turn.Evidence.Single(item => item.Tool == "illustration_statistics").Text); Assert.Equal(2, turn.Evidence.Length);
        Assert.InRange(turn.Answer.Length, 1, 260);
    }

    [Fact]
    public async Task ExactSourcePhraseWorksDisconnectedAndLateCancelledResponseDoesNotCommit()
    {
        var (fixture, ledger) = Recording(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new SessionHarness(async q => { entered.TrySetResult(); await gate.Task; return ledger.Query(q); });
        h.Router.Configured = h.Model.Configured = false; var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        var pending = h.Session.AskAsync(context, "Show replay statistics"); await entered.Task; h.Session.SetContext(context with { Query = Query(fixture, 7300) }); gate.SetResult();
        Assert.False((await pending).Accepted); Assert.Empty(h.Session.Turns); Assert.Equal(0, h.Router.Calls); Assert.Equal(0, h.Model.Calls);
        context = context with { Query = Query(fixture, 7300) }; var accepted = await h.Session.AskAsync(context, "Show replay statistics"); Assert.True(accepted.Accepted);
        Assert.Contains("0 of 0 passes completed", Assert.Single(h.Session.Turns).Answer);
    }

    [Fact]
    public async Task ExistingSummaryStillUsesNarratorAndRejectsInventedPassStatistics()
    {
        var (fixture, ledger) = Recording(); using var h = new SessionHarness(q => Task.FromResult(ledger.Query(q))); h.Router.Workflow = "current_match_summary";
        h.Model.Answer = "Home completed 290 passes."; var context = new MatchChatContext(Query(fixture, 7400), 2); h.Session.SetContext(context);
        var result = await h.Session.AskAsync(context, "Summarize the current match"); Assert.False(result.Accepted); Assert.Equal(1, h.Model.Calls); Assert.Empty(h.Session.Turns);
        var view = ledger.Query(Query(fixture, 7400)); var summary = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(Request(view, "current_match_summary"), _ => true);
        Assert.DoesNotContain(summary.Bundle!.Items, i => i.Tool == "illustration_statistics"); Assert.DoesNotContain(summary.Bundle.Sources, s => s.SourceId == "generated-model-actions");
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ActualV5FreshPreparedReadsMatchGoldenClocksAndAllWorkflowBudgetsWithoutReimport()
    {
        var store = new ChunkedLocalFootballStore(Path.Combine(LocalPaths.FindRoot(), ".local", "football-full-v1")); var catalog = await store.CatalogAsync(default);
        var dataset = catalog.Single(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v5-", StringComparison.Ordinal)); Assert.Equal(9, dataset.Manifest.Sources.Length);
        var reads = new List<FootballQuery>(); var executor = new MatchWorkflowExecutor(async (q, t) => { reads.Add(q); return await store.QueryAsync(q, t); });
        var maximumEvidenceChars = 0; var maximumBundleChars = 0;
        foreach (var (cursor, homeAttempts, homeCompleted, awayAttempts, awayCompleted, homeControl, awayControl) in new[] {
            (7300L, 0, 0, 0, 0, 0L, 0L), (7400L, 1, 1, 0, 0, 0L, 0L), (2820000L, 177, 142, 175, 140, 1412000L, 1408000L),
            (5880000L, 361, 290, 367, 293, 2905300L, 2944700L), (7300L, 0, 0, 0, 0, 0L, 0L) })
        {
            var view = await store.QueryAsync(FullFootballSourceTests.Query(dataset, cursor), default); var result = await executor.ExecuteAsync(Request(view), _ => true);
            Assert.Equal(MatchWorkflowStatus.Success, result.Status); var bundle = result.Bundle!; Assert.Single(bundle.Sources); Assert.Empty(bundle.EventProvenance);
            using var data = JsonDocument.Parse(bundle.Items[0].Text); var teams = data.RootElement.GetProperty("teams");
            Assert.Equal(homeAttempts, teams[0].GetProperty("passAttempts").GetInt32()); Assert.Equal(homeCompleted, teams[0].GetProperty("passCompleted").GetInt32());
            Assert.Equal(awayAttempts, teams[1].GetProperty("passAttempts").GetInt32()); Assert.Equal(awayCompleted, teams[1].GetProperty("passCompleted").GetInt32());
            Assert.Equal(homeControl, teams[0].GetProperty("controlledMs").GetInt64()); Assert.Equal(awayControl, teams[1].GetProperty("controlledMs").GetInt64());
            Assert.Equal(cursor == 5880000 ? 3 : 0, teams[0].GetProperty("shots").GetInt32()); Assert.Equal(0, teams[1].GetProperty("shots").GetInt32());
            Assert.Equal("e790083f8482661a27a5f8f4999aa0b11cb4676e046d8346bd04a3c3d65b89b1", Assert.Single(bundle.Sources).ArtifactHash);
            var answer = Format(bundle).Answer; if (cursor == 5880000) { Assert.Contains("290 of 361 passes completed (80.3%)", answer); Assert.Contains("293 of 367 passes completed (79.8%)", answer); Assert.Contains("97 min 30 sec", answer); Assert.Contains("49.7%", answer); }
            maximumEvidenceChars = Math.Max(maximumEvidenceChars, bundle.Items.Sum(i => i.Text.Length)); maximumBundleChars = Math.Max(maximumBundleChars, JsonSerializer.Serialize(bundle).Length);
            Assert.InRange(maximumEvidenceChars, 1, 12000); Assert.InRange(maximumBundleChars, 1, 32000); Assert.InRange(answer.Length, 1, 1800);
        }
        foreach (var workflow in new[] { "current_match_summary", "current_match_events", "selected_player_context" })
        {
            var view = await store.QueryAsync(FullFootballSourceTests.Query(dataset, 5880000) with { SubjectId = dataset.People[0].PersonId }, default);
            var result = await executor.ExecuteAsync(Request(view, workflow), _ => true); Assert.Contains(result.Status, new[] { MatchWorkflowStatus.Success, MatchWorkflowStatus.Partial });
            Assert.Equal(8, result.Bundle!.Sources.Length); Assert.DoesNotContain(result.Bundle.Sources, s => s.SourceId == "generated-model-actions");
            Assert.InRange(JsonSerializer.Serialize(result.Bundle).Length, 1, 32000);
        }
        var unsupported = catalog.Where(d => d.Manifest.DatasetVersion.StartsWith("brent-full-v4-", StringComparison.Ordinal) || d.Manifest.DatasetId == "australian-recorded").ToArray();
        Assert.Contains(unsupported, d => d.Manifest.DatasetId == "australian-recorded"); Assert.Contains(unsupported, d => d.Manifest.DatasetVersion.StartsWith("brent-full-v4-", StringComparison.Ordinal));
        foreach (var old in unsupported)
        {
            var view = await store.QueryAsync(FullFootballSourceTests.Query(old, old.Schedule!.Segments[0].CursorStartMs), default);
            Assert.Equal(MatchWorkflowStatus.Unavailable, (await executor.ExecuteAsync(Request(view), _ => true)).Status);
        }
        var bound = await store.QueryAsync(FullFootballSourceTests.Query(dataset, 5880000), default);
        var excessive = bound with { Dataset = bound.Dataset with { Manifest = bound.Dataset.Manifest with { Sources = [.. bound.Dataset.Manifest.Sources, new("unknown-extra", "unknown", new string('b', 64))] } } };
        Assert.Equal(MatchWorkflowStatus.RetrievalFailure, (await new MatchWorkflowExecutor((q, _) => Task.FromResult(excessive with { Context = q })).ExecuteAsync(Request(excessive, "current_match_summary"), _ => true)).Status);
        output.WriteLine(JsonSerializer.Serialize(new { sources = dataset.Manifest.Sources.Length, dedicatedSources = 1, maximumNormalSources = 8, maximumEvidenceChars, maximumBundleChars, reads = reads.Count, maxReadCursor = reads.Max(q => q.CursorMs), storeMutation = false }));
    }

    private sealed class SessionHarness : IDisposable
    {
        public Routing Router { get; } = new(); public Narration Model { get; } = new(); public List<FootballQuery> Queries { get; } = [];
        private readonly HttpClient _http; public MatchChatSession Session { get; }
        public SessionHarness(Func<FootballQuery, Task<FootballView>> read)
        {
            _http = new(new Handler(async request => { var q = (await request.Content!.ReadFromJsonAsync<FootballQuery>())!; Queries.Add(q); return new(HttpStatusCode.OK) { Content = JsonContent.Create(await read(q)) }; })) { BaseAddress = new("https://unit.invalid/") };
            Session = new(new Auth(), new ReplayApiClient(_http), Router, Model, Options.Create(new MatchChatOptions()));
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
    private sealed class Routing : IMatchWorkflowRouter
    {
        public bool Configured { get; set; } = true; public int Calls; public string Workflow = "current_replay_statistics"; public MatchRoutingInput? LastInput;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Calls++; LastInput = input; return Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, Workflow)); }
    }
    private sealed class Narration : IMatchChatModel
    {
        public bool Configured { get; set; } = true; public int Calls; public string Answer = "Unexpected narrator call";
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) { Calls++; return Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer = Answer, evidenceIds = new[] { "match_summary" } }), [])); }
    }
    private sealed class Auth : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "unit-owner"), new("roles", "Tester")], "unit", "sub", "roles")))); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> read) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => read(request); }
}
