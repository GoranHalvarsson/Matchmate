using System.Net.Http.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.ReplayStoryTimelineTests;

namespace Matchmate.Tests;

public sealed class GeneratedPassageChatTests
{
    private static MatchChatContext Context(FootballView view, int index = 0) => new(view.Context, view.Dataset.HeadSequence,
        SelectedPassage: ReplayStory.Locator(view.Story!.Passages[index]));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TypedAndExplicitSourceUseOnlyExactPassageWithOneFreshReadAndNoModels(bool source)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 9000)); using var h = new Harness(q => Task.FromResult(ledger.Query(q)));
        var context = Context(view); h.Session.SetContext(context);
        var result = source ? await h.Session.AskSourceAsync(context, MatchSourceQuestionId.ReplayStatistics)
            : await h.Session.AskAsync(context, "How many passes did they complete in the whole match?");
        Assert.True(result.Accepted); var turn = Assert.Single(h.Session.Turns); Assert.True(turn.EvidenceOnly);
        Assert.Contains("1 of 2 passes completed", turn.Answer); Assert.DoesNotContain("2 of 3", turn.Answer);
        Assert.Contains("this passage only", turn.Answer); Assert.Equal(context.SelectedPassage, turn.Context.SelectedPassage);
        var query = Assert.Single(h.Queries); Assert.Equal(9000, query.CursorMs); Assert.Equal(view.Dataset.HeadSequence, query.ApplicationSequence);
        var evidence = Assert.Single(turn.Evidence); Assert.Equal("selected_passage", evidence.Tool); Assert.Contains("\"revision\":1", evidence.Text);
        Assert.Contains("generated-model-actions", evidence.Text); Assert.Contains("authored", evidence.Text);
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task PublicPassageReplyReportsLocalizedSuccessAndCommitsOnce(MatchLanguage language)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); using var h = new Harness(q => Task.FromResult(ledger.Query(q)));
        var context = Context(view); h.Session.SetContext(context); h.Session.SetLanguage(language);
        var result = await h.Session.AskAsync(context, "Passes?");
        Assert.True(result.Accepted); Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal(language == MatchLanguage.English ? "Reply grounded in the selected workflow evidence." : ChatText.Get(language, "Feedback.Grounded"), result.Message);
        Assert.NotEqual(ChatText.Get(language, "Feedback.Failure"), result.Message);
        var turn = Assert.Single(h.Session.Turns); Assert.True(turn.EvidenceOnly); Assert.Equal(language, turn.Language); Assert.Single(h.Queries);
    }

    [Theory] [InlineData(MatchLanguage.English, "real-match tactics")] [InlineData(MatchLanguage.Swedish, "verklig matchtaktik")] [InlineData(MatchLanguage.Spanish, "táctica del partido real")]
    public async Task UnsupportedTacticsSpeedDistanceAndRatingStayWithinLocalizedFacts(MatchLanguage language, string boundary)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); using var h = new Harness(q => Task.FromResult(ledger.Query(q)));
        var context = Context(view); h.Session.SetContext(context); h.Session.SetLanguage(language);
        var result = await h.Session.AskAsync(context, "Why did the defenders fail? What was their speed, distance and rating?");
        Assert.True(result.Accepted); var turn = Assert.Single(h.Session.Turns); Assert.Contains(boundary, turn.Answer);
        Assert.Equal(language, turn.Language); Assert.True(turn.EvidenceOnly); Assert.DoesNotContain("defenders failed", turn.Answer);
    }

    [Theory]
    [InlineData("How fast and how far did they run? speed and distance", "Speed and distance are unavailable")]
    [InlineData("What rating does the player deserve?", "cannot establish a player rating")]
    [InlineData("Show the actual match xG", "Actual match statistics are unavailable")]
    public async Task ExplicitUnavailableMetricQuestionsDoNotPretendToAnswerWithPassCounts(string question, string expected)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); using var h = new Harness(q => Task.FromResult(ledger.Query(q)));
        var context = Context(view); h.Session.SetContext(context); Assert.True((await h.Session.AskAsync(context, question)).Accepted);
        var answer = Assert.Single(h.Session.Turns).Answer; Assert.Contains(expected, answer); Assert.DoesNotContain("1 of 2", answer);
        Assert.Single(h.Queries);
    }

    [Theory] [InlineData(0L)] [InlineData(9000L)]
    public async Task ReportBackedReplayKeepsReportedCompareAndExplicitGeneratedStatisticsSeparate(long cursor)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, cursor));
        view = view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with
            { Capabilities = [.. view.Dataset.Manifest.Capabilities, "complete-generated-200ms-illustration"] } } };
        var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence);
        var snapshot = new MatchWorkflowSnapshot("unit-owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor);
        var reads = new List<FootballQuery>();
        var executor = new MatchWorkflowExecutor((query, _) => { reads.Add(query); return Task.FromResult(view with { Context = query }); });
        var reported = await executor.ExecuteAsync(new(MatchSourceQuestions.CompareTeamsWorkflow, MatchWorkflowRegistry.Version, snapshot), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, reported.Status);
        Assert.Contains(reported.Bundle!.Items, item => item.Tool == "reported_comparison");
        Assert.DoesNotContain(reported.Bundle.Items, item => item.Tool == "illustration_statistics");
        Assert.DoesNotContain(reported.Bundle.Sources, source => source.SourceId == "generated-model-actions");
        var generated = await executor.ExecuteAsync(new("current_replay_statistics", MatchWorkflowRegistry.Version, snapshot), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, generated.Status);
        var facts = Assert.Single(generated.Bundle!.Items, item => item.Tool == "illustration_statistics");
        Assert.Contains("generated_replay_statistics", facts.Text); Assert.Contains("synthetic", facts.Text); Assert.Contains("authored", facts.Text);
        Assert.DoesNotContain(generated.Bundle.Items, item => item.Tool == "reported_comparison");
        Assert.Equal("generated-model-actions", Assert.Single(generated.Bundle.Sources).SourceId);
        Assert.Equal(2, reads.Count); Assert.All(reads, query => Assert.Equal(cursor, query.CursorMs));
    }

    [Theory]
    [InlineData("fixture")] [InlineData("stream")] [InlineData("fingerprint")] [InlineData("head")] [InlineData("policy")]
    [InlineData("source")] [InlineData("membership")] [InlineData("revision")] [InlineData("missing")]
    public async Task FreshWrongBindingCorrectionAndUnsupportedProjectionCannotAnswer(string fault)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000));
        using var h = new Harness(q =>
        {
            var fresh = ledger.Query(q); var passage = fresh.Story!.Passages[0];
            return Task.FromResult(fault switch
            {
                "fixture" => fresh with { Dataset = fresh.Dataset with { Manifest = fresh.Dataset.Manifest with { FixtureId = "foreign" } } },
                "stream" => fresh with { Dataset = fresh.Dataset with { Stream = fresh.Dataset.Stream with { StreamId = "foreign" } } },
                "fingerprint" => fresh with { Story = new([passage with { Fingerprint = new string('b', 64) }]) },
                "head" => fresh with { Dataset = fresh.Dataset with { HeadSequence = 4 } },
                "policy" => fresh with { Dataset = fresh.Dataset with { Schedule = fresh.Dataset.Schedule! with { PolicyRevision = 2 } } },
                "source" => fresh with { Dataset = fresh.Dataset with { Manifest = fresh.Dataset.Manifest with { Sources = [] } } },
                "membership" => fresh with { GeneratedActionRecordIds = [] },
                "revision" => fresh with { Story = new([passage with { Evidence = [passage.Evidence[0] with { Revision = 0 }] }]) },
                _ => fresh with { Story = null }
            });
        });
        var context = Context(view); h.Session.SetContext(context); Assert.False((await h.Session.AskAsync(context, "Passes?")).Accepted); Assert.Empty(h.Session.Turns);
    }

    [Theory] [InlineData("rewind")] [InlineData("clear")] [InlineData("language")] [InlineData("cancel")]
    public async Task LateReplyCannotCommitAfterIntentChanges(string change)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(async q => { entered.SetResult(); await release.Task; return ledger.Query(q); });
        var context = Context(view); h.Session.SetContext(context); var pending = h.Session.AskAsync(context, "Passes?"); await entered.Task;
        if (change == "rewind") h.Session.SetContext(context with { Query = Query(f, 2999), SelectedPassage = null });
        if (change == "clear") h.Session.SetContext(context with { SelectedPassage = null });
        if (change == "language") h.Session.SetLanguage(MatchLanguage.Swedish);
        if (change == "cancel") h.Session.Cancel();
        release.SetResult(); Assert.False((await pending).Accepted); Assert.Empty(h.Session.Turns);
    }

    [Theory] [InlineData("before-end")] [InlineData("goal")] [InlineData("person")]
    public async Task InvalidTrustedScopeNeverRetrieves(string fault)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); using var h = new Harness(q => Task.FromResult(ledger.Query(q)));
        var context = Context(view); context = fault switch
        {
            "before-end" => context with { Query = Query(f, 2999) },
            "goal" => context with { SelectedEvent = new("future-goal", 60000, "1") },
            _ => context with { Query = context.Query with { SubjectId = "person-one" } }
        };
        h.Session.SetContext(context); Assert.False((await h.Session.AskAsync(context, "Passes?")).Accepted); Assert.Empty(h.Queries); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task ClearingPassageDropsScopedHistoryAndFailedReadAddsNothing()
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var fail = false;
        using var h = new Harness(q => fail ? throw new HttpRequestException() : Task.FromResult(ledger.Query(q)));
        var context = Context(view); h.Session.SetContext(context); Assert.True((await h.Session.AskAsync(context, "Passes?")).Accepted);
        h.Session.SetContext(context with { SelectedPassage = null }); Assert.Empty(h.Session.Turns);
        h.Session.SetContext(context); fail = true; Assert.False((await h.Session.AskAsync(context, "Passes?")).Accepted); Assert.Empty(h.Session.Turns);
    }

    private sealed class Harness : IDisposable
    {
        public List<FootballQuery> Queries { get; } = []; public MatchChatSession Session { get; } private readonly HttpClient _http;
        public Harness(Func<FootballQuery, Task<FootballView>> read)
        {
            _http = new(new Handler(q => { Queries.Add(q); return read(q); })) { BaseAddress = new("https://unit.invalid/") };
            var model = new NoModel(); Session = new(new Auth(), new ReplayApiClient(_http), model, model, Options.Create(new MatchChatOptions()));
        }
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
}
