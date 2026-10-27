using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;
using Xunit;

namespace Matchmate.Tests;

public sealed partial class MatchConversationTests
{
    private static readonly Lazy<Task<FootballLedger>> ShortcutChelsea = new(() => ReadShortcutSource("chelsea"));
    private static readonly Lazy<Task<FootballLedger>> ShortcutLiverpool = new(() => ReadShortcutSource("liverpool"));
    public static IEnumerable<object[]> ShortcutMatchesAndLanguages => from match in new[] { "chelsea", "liverpool" }
        from language in MatchLanguages.Supported select new object[] { match, language };

    [Theory, MemberData(nameof(ShortcutMatchesAndLanguages))]
    public async Task CompareAndCatchupUseBothRealSourcesWithoutModelsAndPreserveChelseaChart(string match, MatchLanguage language)
    {
        var ledger = await (match == "chelsea" ? ShortcutChelsea : ShortcutLiverpool).Value;
        var dataset = Assert.Single(ledger.Catalog()); var view = ledger.Query(FullFootballSourceTests.Query(dataset, match == "chelsea" ? 5880000 : 6540000));
        using var h = new Harness(actual: view); h.Api.View = view; h.Session.SetLanguage(language);
        h.Router.Configured = false; h.Model.Configured = false;
        var before = JsonSerializer.Serialize(view);
        Assert.True((await h.Session.AskStatisticsChartAsync(h.Context)).Accepted);
        var comparison = Assert.Single(h.Session.Turns);
        Assert.Equal(language, comparison.Language); Assert.True(comparison.EvidenceOnly);
        if (match == "chelsea")
        {
            var chart = Assert.IsType<MatchStatisticsChartSnapshot>(comparison.StatisticsChart);
            Assert.True(chart.InitiallyExpanded); Assert.Equal(361, chart.Teams[0].PassAttempts); Assert.Equal(367, chart.Teams[1].PassAttempts);
            Assert.Contains(comparison.Evidence, item => item.Tool == "illustration_statistics");
            Assert.DoesNotContain(comparison.Evidence, item => item.Tool == "reported_comparison");
        }
        else
        {
            var chart = Assert.IsType<MatchStatisticsChartSnapshot>(comparison.StatisticsChart);
            Assert.True(chart.InitiallyExpanded); Assert.True(chart.CanChooseMetric);
            Assert.Equal(MatchStatisticsChartEvidenceKind.ReportedEvents, chart.EvidenceKind); Assert.Equal(MatchStatisticsChartMetric.Goals, chart.InitialMetric);
            Assert.Equal((1, 3, 2, 5), (chart.Teams[0].Goals, chart.Teams[0].YellowCards, chart.Teams[0].RedCards, chart.Teams[0].Substitutions));
            Assert.Equal((2, 3, 0, 5), (chart.Teams[1].Goals, chart.Teams[1].YellowCards, chart.Teams[1].RedCards, chart.Teams[1].Substitutions));
            using var document = JsonDocument.Parse(comparison.Evidence.Single(item => item.Tool == "reported_comparison").Text);
            AssertLiverpoolCounts(document.RootElement);
            Assert.Contains(ChatText.Get(language, "Source.Reported.Team", "Brentford", 1, 3, 2, 5), comparison.Answer);
            Assert.Contains(ChatText.Get(language, "Source.Reported.Team", "Liverpool", 2, 3, 0, 5), comparison.Answer);
        }
        Assert.True((await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.Summary)).Accepted);
        var summary = h.Session.Turns[^1]; Assert.True(summary.EvidenceOnly); Assert.Null(summary.StatisticsChart);
        using var summaryDocument = JsonDocument.Parse(summary.Evidence.Single(item => item.Tool == "match_summary").Text);
        var reports = summaryDocument.RootElement.GetProperty("reportedEvents");
        Assert.Equal(match == "chelsea" ? 14 : 23, reports.GetProperty("eligibleReportCount").GetInt32());
        var recent = reports.GetProperty("recent").EnumerateArray().ToArray(); Assert.Equal(3, recent.Length);
        Assert.Contains(ChatText.Get(language, "Source.Reported.ReportCount", match == "chelsea" ? 14 : 23), summary.Answer);
        Assert.Contains(ChatText.Get(language, "Source.Reported.Limits"), summary.Answer);
        foreach (var incident in recent)
            if (incident.GetProperty("reportedClock").GetString() is { } clock) Assert.Contains(clock + "′", summary.Answer);
        Assert.InRange(summary.Answer.Length, 1, 1800); Assert.Equal(before, JsonSerializer.Serialize(view));
        Assert.Equal(2, h.Api.Queries.Count); Assert.All(h.Api.Queries, query => Assert.Equal(view.Context.CursorMs, query.CursorMs));
        Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    [Fact]
    public async Task LiverpoolAll23ReportsAndEveryReleaseBoundaryKeepCountsRecentProvenanceAndRewindFenced()
    {
        var ledger = await ShortcutLiverpool.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 6540000)); Assert.Equal(23, end.Events.Length);
        var selected = end.Events.First(record => record.Payload is MatchEventPayload { EventKind: "goal" });
        var selectedRelease = dataset.Schedule!.Releases.Single(release => release.RecordId == selected.RecordId).CursorMs;
        var reads = new List<FootballQuery>();
        var executor = new MatchWorkflowExecutor((query, _) => { reads.Add(query); return Task.FromResult(ledger.Query(query)); });
        var reportIds = end.Events.Select(record => record.RecordId).ToHashSet(StringComparer.Ordinal);
        var cursors = dataset.Schedule.Releases.Where(release => reportIds.Contains(release.RecordId))
            .SelectMany(release => new[] { release.CursorMs - 1, release.CursorMs, release.CursorMs - 1 }).Append(6540000).Append(0);
        foreach (var cursor in cursors)
        {
            var view = ledger.Query(FullFootballSourceTests.Query(dataset, cursor));
            var context = new MatchChatContext(view.Context, dataset.HeadSequence, new(selected.RecordId, selectedRelease, selected.FootballTime!.ReportedClock!));
            foreach (var workflow in new[] { MatchSourceQuestions.CompareTeamsWorkflow, "current_match_summary" })
            {
                var request = new MatchWorkflowRequest(workflow, MatchWorkflowRegistry.Version, new("unit-owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor));
                var count = reads.Count; var result = await executor.ExecuteAsync(request, _ => true);
                Assert.True(result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial, result.Message);
                var bundle = Assert.IsType<MatchWorkflowBundle>(result.Bundle); Assert.Equal(count + 1, reads.Count);
                Assert.Equal(cursor, reads[^1].CursorMs); Assert.Null(bundle.Context.SelectedEvent);
                var dataItem = bundle.Items.Single(item => item.Tool == (workflow == "current_match_summary" ? "match_summary" : "reported_comparison"));
                using var data = JsonDocument.Parse(dataItem.Text);
                var reports = workflow == "current_match_summary" ? data.RootElement.GetProperty("reportedEvents") : data.RootElement;
                Assert.Equal(view.Events.Length, reports.GetProperty("eligibleReportCount").GetInt32());
                foreach (var team in reports.GetProperty("teams").EnumerateArray())
                {
                    var id = team.GetProperty("teamId").GetString();
                    foreach (var (kind, property) in new[] { ("goal", "goals"), ("yellow_card", "yellowCards"), ("red_card", "redCards"), ("substitution", "substitutions") })
                        Assert.Equal(view.Events.Count(record => record.Payload is MatchEventPayload e && e.TeamId == id && e.EventKind == kind && (kind != "goal" || e.Decision != "disallowed")), team.GetProperty(property).GetInt32());
                }
                var chart = ReportedMatchStatisticsChartTests.Capture(bundle);
                if (workflow == "current_match_summary") Assert.Null(chart);
                else
                {
                    Assert.NotNull(chart); Assert.Equal(bundle.Context, chart.Context); Assert.Equal(cursor, chart.Context.Query.CursorMs);
                    foreach (var team in chart.Teams)
                        foreach (var (kind, metric) in new[] { ("goal", MatchStatisticsChartMetric.Goals), ("yellow_card", MatchStatisticsChartMetric.YellowCards),
                            ("red_card", MatchStatisticsChartMetric.RedCards), ("substitution", MatchStatisticsChartMetric.Substitutions) })
                            Assert.Equal(view.Events.Count(record => record.Payload is MatchEventPayload e && e.TeamId == team.TeamId && e.EventKind == kind
                                && (kind != "goal" || e.Decision != "disallowed")), chart.Value(team, metric));
                }
                var recent = reports.GetProperty("recent").EnumerateArray().ToArray(); Assert.True(recent.Length <= 3);
                foreach (var incident in recent) Assert.Contains(bundle.EventProvenance, evidence => evidence.RecordId == incident.GetProperty("recordId").GetString());
                foreach (var future in end.Events.Where(record => !view.Events.Any(current => current.RecordId == record.RecordId)))
                    Assert.DoesNotContain(bundle.EventProvenance, evidence => evidence.RecordId == future.RecordId);
                if (cursor == 6540000)
                {
                    AssertLiverpoolCounts(reports); Assert.Contains("event_provenance_truncated", bundle.Missingness);
                    Assert.Equal(20, bundle.EventProvenance.Select(e => e.RecordId).Distinct().Count());
                    foreach (var late in end.Events.Skip(20)) Assert.Contains(recent, incident => incident.GetProperty("recordId").GetString() == late.RecordId);
                }
                Assert.InRange(JsonSerializer.Serialize(bundle).Length, 1, MatchWorkflowLimits.BundleChars);
            }
        }
        var strict = await executor.ExecuteAsync(new("current_replay_statistics", MatchWorkflowRegistry.Version,
            new("unit-owner", 1, new(end.Context, dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor)), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, strict.Status); Assert.NotNull(strict.Bundle);
        var generated = Assert.Single(strict.Bundle.Items, item => item.Tool == "illustration_statistics");
        using var generatedData = JsonDocument.Parse(generated.Text);
        Assert.Equal("generated_replay_statistics", generatedData.RootElement.GetProperty("scope").GetString());
        Assert.DoesNotContain(strict.Bundle.Items, item => item.Tool == "reported_comparison");
        Assert.Contains(strict.Bundle.Sources, source => source.SourceId == "generated-model-actions"
            && source.ArtifactHash == dataset.Manifest.Sources.Single(s => s.SourceId == "generated-model-actions").ArtifactHash);
    }

    [Theory, InlineData(MatchLanguage.English), InlineData(MatchLanguage.Swedish), InlineData(MatchLanguage.Spanish)]
    public async Task MissingReportFeedDiffersFromZeroReportsAndComparisonUnavailableIsLocalized(MatchLanguage language)
    {
        var ledger = await ShortcutLiverpool.Value; var dataset = Assert.Single(ledger.Catalog()); var view = ledger.Query(FullFootballSourceTests.Query(dataset, 0));
        using var zero = new Harness(actual: view); zero.Api.View = view; zero.Session.SetLanguage(language);
        Assert.True((await zero.Session.AskStatisticsChartAsync(zero.Context)).Accepted);
        Assert.Contains(ChatText.Get(language, "Source.Reported.None"), Assert.Single(zero.Session.Turns).Answer);
        var chart = Assert.IsType<MatchStatisticsChartSnapshot>(Assert.Single(zero.Session.Turns).StatisticsChart);
        Assert.All(chart.AvailableMetrics, metric => { Assert.Equal(1m, chart.Maximum(metric)); Assert.All(chart.Teams, team => Assert.Equal("0%", chart.BarWidth(team, metric))); });
        var missing = view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = [] } } };
        using var unavailable = new Harness(actual: missing); unavailable.Api.View = missing; unavailable.Session.SetLanguage(language);
        var result = await unavailable.Session.AskStatisticsChartAsync(unavailable.Context);
        Assert.Equal(MatchChatOutcome.Unavailable, result.Outcome);
        Assert.Equal(language == MatchLanguage.English ? "This recording does not supply reported match events or generated replay statistics." : ChatText.Get(language, "Feedback.ReportComparisonUnavailable"), result.Message);
        Assert.All(unavailable.Session.Turns, turn => Assert.Null(turn.StatisticsChart)); Assert.Empty(unavailable.Router.Inputs); Assert.Empty(unavailable.Model.Messages);
    }

    [Fact]
    public async Task LiverpoolReportedChartAt57MinutesStaysAtAskedClockAndLanguageUntilRewind()
    {
        var ledger = await ShortcutLiverpool.Value; var dataset = Assert.Single(ledger.Catalog());
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, 3420000));
        using var h = new Harness(actual: view); h.Api.View = view; h.Session.SetLanguage(MatchLanguage.Swedish);
        h.Router.Configured = false; h.Model.Configured = false;
        Assert.True((await h.Session.AskStatisticsChartAsync(h.Context)).Accepted);
        var turn = Assert.Single(h.Session.Turns); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(turn.StatisticsChart);
        Assert.Equal("57:00", chart.ReplayClock);
        Assert.Equal((1, 0, 1, 2), (chart.Teams[0].Goals, chart.Teams[0].YellowCards, chart.Teams[0].RedCards, chart.Teams[0].Substitutions));
        Assert.Equal((0, 1, 0, 2), (chart.Teams[1].Goals, chart.Teams[1].YellowCards, chart.Teams[1].RedCards, chart.Teams[1].Substitutions));
        h.Session.SetLanguage(MatchLanguage.Spanish); h.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 6540000 } });
        Assert.Same(chart, Assert.Single(h.Session.Turns).StatisticsChart); Assert.Equal(MatchLanguage.Swedish, turn.Language);
        Assert.Equal(3420000, chart.Context.Query.CursorMs); Assert.Equal("57:00", chart.ReplayClock);
        h.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 0 } }); Assert.Empty(h.Session.Turns);
        Assert.Equal(1, chart.Teams[0].RedCards); Assert.Single(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    [Fact]
    public async Task SummaryExcludesDisallowedGoalAndDoesNotInferYellowFromSecondYellowOrCountVarAsCard()
    {
        using var h = new Harness(); var first = h.Api.View;
        FootballRecord Incident(string id, string kind, string? decision = null, string team = "team-home") => SharedFootballTests.Goal(SharedFootballTests.Fixture()) with
        { RecordId = id, Payload = new MatchEventPayload(kind, team, kind == "var_decision" ? null : "person-one", Decision: decision), FootballTime = new("period-one", 60000, 60000, "source_exact", true, ReportedClock: "1") };
        var view = first with { Context = first.Context with { CursorMs = 80000 }, Events = [Incident("valid-goal", "goal", "confirmed"), Incident("no-goal", "goal", "disallowed"),
            Incident("yellow", "yellow_card"), Incident("red", "red_card", "second_yellow"), Incident("review", "var_decision", "card_upgraded", "unassigned-team")] };
        h.Api.View = view; h.SetContext(new(view.Context, view.Dataset.HeadSequence));
        Assert.True((await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.Summary)).Accepted);
        var turn = Assert.Single(h.Session.Turns); using var doc = JsonDocument.Parse(turn.Evidence.Single(item => item.Tool == "match_summary").Text);
        var team = doc.RootElement.GetProperty("reportedEvents").GetProperty("teams")[0];
        Assert.Equal(1, team.GetProperty("goals").GetInt32()); Assert.Equal(1, team.GetProperty("yellowCards").GetInt32()); Assert.Equal(1, team.GetProperty("redCards").GetInt32());
        Assert.Contains(ChatText.Get(MatchLanguage.English, "Source.Reported.TeamUnknown"), turn.Answer);
        Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    [Theory, InlineData("cancel"), InlineData("rewind"), InlineData("auth"), InlineData("head"), InlineData("binding")]
    public async Task ReportComparisonCannotCommitAfterOwnerContextOrCancellationChanges(string change)
    {
        using var h = new Harness(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Api.Wait = (_, _) => { entered.SetResult(); return gate.Task; }; h.Api.WrongBinding = change == "binding";
        var pending = h.Session.AskStatisticsChartAsync(h.Context); await entered.Task;
        if (change == "cancel") h.Session.Cancel();
        if (change == "rewind") h.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 1 } });
        if (change == "auth") h.Auth.Change(null);
        if (change == "head") h.SetContext(h.Context with { HeadSequence = h.Context.HeadSequence + 1 });
        gate.SetResult(); Assert.False((await pending).Accepted); Assert.Empty(h.Session.Turns); Assert.Empty(h.Model.Messages); Assert.Empty(h.Router.Inputs);
    }

    [Fact]
    public async Task ModelCannotSelectCodeOwnedCompareActionAndUnauthenticatedActionCannotRead()
    {
        using var h = new Harness(); h.Router.Route = MatchSourceQuestions.CompareTeamsWorkflow;
        Assert.Equal(MatchChatOutcome.RoutingUnavailable, (await h.Ask("Compare the teams please")).Outcome);
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Model.Messages);
        h.Auth.Change(null); Assert.Equal(MatchChatOutcome.Disconnected, (await h.Session.AskStatisticsChartAsync(h.Context)).Outcome); Assert.Empty(h.Api.Queries);
        Assert.DoesNotContain(MatchSourceQuestions.CompareTeamsWorkflow, DecisionWorkflowRouter.Criteria.Keys);
    }

    private static void AssertLiverpoolCounts(JsonElement reports)
    {
        Assert.Equal(23, reports.GetProperty("eligibleReportCount").GetInt32());
        var home = reports.GetProperty("teams")[0]; var away = reports.GetProperty("teams")[1];
        Assert.Equal((1, 3, 2, 5), (home.GetProperty("goals").GetInt32(), home.GetProperty("yellowCards").GetInt32(), home.GetProperty("redCards").GetInt32(), home.GetProperty("substitutions").GetInt32()));
        Assert.Equal((2, 3, 0, 5), (away.GetProperty("goals").GetInt32(), away.GetProperty("yellowCards").GetInt32(), away.GetProperty("redCards").GetInt32(), away.GetProperty("substitutions").GetInt32()));
    }

    private static async Task<FootballLedger> ReadShortcutSource(string match)
    {
        IStreamingFootballSource source = match == "chelsea" ? new FullBrentfordFootballSource(Path.Combine(LocalPaths.FindRoot(), ".local/shared-inputs/brentford-full-v5/manifest.json"))
            : new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output/brentford-liverpool-generated/dataset-v2/manifest.json"));
        FootballManifest? manifest = null; FootballStream? stream = null; var records = new List<FootballRecord>();
        await foreach (var batch in source.ReadBatchesAsync(default))
        { manifest ??= batch.Manifest; stream ??= batch.Stream; records.AddRange(batch.Records.Where(record => record.Payload is not TrackingPayload)); }
        var ledger = new FootballLedger(); foreach (var batch in FootballCodec.Batches(manifest!, stream!, records)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
        return ledger;
    }
}
