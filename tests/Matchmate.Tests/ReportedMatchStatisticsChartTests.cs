using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect the real component, without a substitute browser host.
public sealed class ReportedMatchStatisticsChartTests
{
    [Fact]
    public void ReportedSnapshotSuppliesOnlyReportCountsAndRejectsOtherMetrics()
    {
        var bundle = Bundle(); var chart = Assert.IsType<MatchStatisticsChartSnapshot>(Capture(bundle));
        Assert.Equal(MatchStatisticsChartEvidenceKind.ReportedEvents, chart.EvidenceKind);
        Assert.Equal(new[] { MatchStatisticsChartMetric.Goals, MatchStatisticsChartMetric.YellowCards, MatchStatisticsChartMetric.RedCards, MatchStatisticsChartMetric.Substitutions }, chart.AvailableMetrics);
        Assert.Equal(bundle.Context, chart.Context); Assert.Equal("109:00", chart.ReplayClock);
        Assert.Equal(MatchStatisticsChartMetric.Goals, chart.InitialMetric); Assert.False(chart.InitiallyExpanded); Assert.True(chart.CanChooseMetric);
        Assert.All(chart.Teams, team => { Assert.Null(team.PassAttempts); Assert.Null(team.PassCompleted); Assert.Null(team.PassAccuracy); Assert.Null(team.Shots); });
        Assert.Equal("50%", chart.BarWidth(chart.Teams[0], MatchStatisticsChartMetric.Goals));
        Assert.Equal("100%", chart.BarWidth(chart.Teams[1], MatchStatisticsChartMetric.Goals));
        foreach (var metric in new[] { MatchStatisticsChartMetric.PassAttempts, MatchStatisticsChartMetric.PassAccuracy, MatchStatisticsChartMetric.Shots, (MatchStatisticsChartMetric)99 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => chart.Value(chart.Teams[0], metric));
            Assert.Throws<ArgumentOutOfRangeException>(() => chart.Maximum(metric));
            Assert.Throws<ArgumentOutOfRangeException>(() => chart.BarWidth(chart.Teams[0], metric));
        }
    }

    [Theory]
    [InlineData("scope")] [InlineData("clock")] [InlineData("negative")] [InlineData("fraction")]
    [InlineData("missing-count")] [InlineData("null-count")] [InlineData("report-count")] [InlineData("negative-report-count")]
    [InlineData("duplicate-team")] [InlineData("blank-team")] [InlineData("one-team")] [InlineData("missing-teams")]
    [InlineData("not-json")] [InlineData("duplicate-item")]
    public void InvalidReportedEvidenceCannotCreateAChart(string change)
    {
        var bundle = Bundle(); var data = JsonNode.Parse(bundle.Items[0].Text)!;
        switch (change)
        {
            case "scope": data["scope"] = "generated_replay_statistics"; break;
            case "clock": data["replayClock"] = "57:00"; break;
            case "negative": data["teams"]![0]!["goals"] = -1; break;
            case "fraction": data["teams"]![0]!["yellowCards"] = 1.5m; break;
            case "missing-count": data["teams"]![0]!.AsObject().Remove("redCards"); break;
            case "null-count": data["teams"]![0]!["substitutions"] = null; break;
            case "report-count": data["eligibleReportCount"] = 20; break;
            case "negative-report-count": data["eligibleReportCount"] = -1; break;
            case "duplicate-team": data["teams"]![1]!["teamId"] = "brentford"; break;
            case "blank-team": data["teams"]![1]!["name"] = " "; break;
            case "one-team": data["teams"]!.AsArray().RemoveAt(1); break;
            case "missing-teams": data.AsObject().Remove("teams"); break;
        }
        bundle = bundle with { Items = [bundle.Items[0] with { Text = change == "not-json" ? "{" : data.ToJsonString() }] };
        if (change == "duplicate-item") bundle = bundle with { Items = [bundle.Items[0], bundle.Items[0]] };
        Assert.Throws<InvalidDataException>(() => Capture(bundle));
    }

    [Theory]
    [InlineData("version")] [InlineData("mode")] [InlineData("delivery")] [InlineData("knowledge")]
    [InlineData("selection")] [InlineData("cursor")] [InlineData("head")] [InlineData("identity")]
    public void ReportedChartRequiresCurrentValidatedReplayContext(string change)
    {
        var bundle = Bundle();
        bundle = change switch
        {
            "version" => bundle with { Version = "wrong-version" },
            "mode" => bundle with { TemporalMode = MatchWorkflowTemporalMode.SelectedGoalRetrospective },
            "delivery" => bundle with { DeliveryMode = "live" },
            "knowledge" => bundle with { Context = bundle.Context with { Query = bundle.Context.Query with { KnowledgeMode = "known_then" } } },
            "selection" => bundle with { Context = bundle.Context with { SelectedEvent = new("future-goal", 6600000, "90+8") } },
            "cursor" => bundle with { Context = bundle.Context with { Query = bundle.Context.Query with { CursorMs = 3420000 } } },
            "head" => bundle with { Context = bundle.Context with { HeadSequence = 0 } },
            _ => bundle with { Context = bundle.Context with { Query = bundle.Context.Query with { FixtureId = "" } } }
        };
        Assert.Throws<InvalidDataException>(() => Capture(bundle));
    }

    [Theory] [InlineData("workflow")] [InlineData("item")] [InlineData("tool")] [InlineData("not-supplied")]
    public void MissingReportedCapabilityOrWrongWorkflowItemDoesNotProduceChart(string change)
    {
        var bundle = Bundle();
        if (change == "workflow") bundle = bundle with { WorkflowId = "current_match_summary" };
        if (change == "item") bundle = bundle with { Items = [bundle.Items[0] with { Id = "other" }] };
        if (change == "tool") bundle = bundle with { Items = [bundle.Items[0] with { Tool = "other" }] };
        if (change == "not-supplied")
        {
            var data = JsonNode.Parse(bundle.Items[0].Text)!; data["reportsSupplied"] = false; data["eligibleReportCount"] = null; data["teams"] = new JsonArray();
            bundle = bundle with { Items = [bundle.Items[0] with { Text = data.ToJsonString() }] };
        }
        Assert.Null(Capture(bundle));
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task ReportedMarkupLocalizesLabelsCoverageValuesAndSupportedControls(MatchLanguage language)
    {
        var chart = Capture(Bundle(), expanded: true)!; var id = Guid.NewGuid();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<MatchStatisticsChart>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(MatchStatisticsChart.Chart)] = chart, [nameof(MatchStatisticsChart.Language)] = language, [nameof(MatchStatisticsChart.TurnId)] = id }));
            var html = WebUtility.HtmlDecode(root.ToHtmlString());
            Assert.Contains(ChatText.Get(language, "Chart.Reported"), html); Assert.Contains(ChatText.Get(language, "Chart.ReportedNote"), html);
            foreach (var metric in chart.AvailableMetrics) Assert.Contains(ChatText.Get(language, "Chart." + metric), html);
            foreach (var metric in new[] { "Generated", "PassAttempts", "PassAccuracy", "Shots", "NoAttempts" }) Assert.DoesNotContain(ChatText.Get(language, "Chart." + metric), html);
            Assert.Contains("Brentford", html); Assert.Contains("Liverpool", html); Assert.Contains("109:00", html);
            Assert.Contains("width:50%", html); Assert.Contains("width:100%", html); Assert.Contains("aria-expanded=\"true\"", html);
            Assert.Contains("statistics-chart-" + id.ToString("N"), html); Assert.Contains("lang=\"" + MatchLanguages.Code(language) + "\"", html);
        });
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void ReportedToggleAndMetricChoicesStayLocalAndRejectGeneratedMetrics(MatchLanguage language)
    {
        var chart = Capture(Bundle())!; var component = new ChartComponent();
        Set(component, nameof(MatchStatisticsChart.Chart), chart); Set(component, nameof(MatchStatisticsChart.Language), language); component.Apply();
        Assert.DoesNotContain(Render(component), frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "figure");
        Invoke(component, "Toggle");
        foreach (var metric in chart.AvailableMetrics)
        {
            Invoke(component, "Choose", metric); component.Apply();
            var frames = Render(component); var text = string.Concat(frames.Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent));
            Assert.Contains(ChatText.Get(language, "Chart." + metric), text);
            Assert.Equal(4, frames.Count(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-pressed"));
            Assert.Equal(metric, typeof(MatchStatisticsChart).GetField("_metric", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component));
        }
        Invoke(component, "Choose", MatchStatisticsChartMetric.Shots); Invoke(component, "Choose", (MatchStatisticsChartMetric)99);
        Assert.Equal(MatchStatisticsChartMetric.Substitutions, typeof(MatchStatisticsChart).GetField("_metric", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component));
        Invoke(component, "Toggle");
        Assert.DoesNotContain(Render(component), frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "figure");
    }

    internal static MatchStatisticsChartSnapshot? Capture(MatchWorkflowBundle bundle, bool expanded = false)
    {
        try { return (MatchStatisticsChartSnapshot?)typeof(MatchStatisticsChartSnapshot).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [bundle, "Compare teams", MatchLanguage.English, expanded]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }

    // Synthetic unit evidence; real Liverpool release/cutoff tests live in MatchSourceShortcutTests.
    private static MatchWorkflowBundle Bundle() => new(MatchSourceQuestions.CompareTeamsWorkflow, MatchWorkflowRegistry.Version,
        new(SharedFootballTests.Query(SharedFootballTests.Fixture(), 6540000) with { ApplicationSequence = 1 }, 1), MatchWorkflowTemporalMode.ReplayAtCursor, "replay",
        [new("reported_comparison", "reported_comparison", JsonSerializer.Serialize(new
        {
            scope = "reported_events_so_far", replayClock = "109:00", reportsSupplied = true, eligibleReportCount = 23,
            teams = new[] { new { teamId = "brentford", name = "Brentford", goals = 1, yellowCards = 3, redCards = 2, substitutions = 5 },
                new { teamId = "liverpool", name = "Liverpool", goals = 2, yellowCards = 3, redCards = 0, substitutions = 5 } }
        }), [])], [], [], [], []);

    private static void Set(object component, string property, object? value) => typeof(MatchStatisticsChart).GetProperty(property)!.SetValue(component, value);
    private static void Invoke(object component, string method, params object?[] args) => typeof(MatchStatisticsChart).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args);
    private static RenderTreeFrame[] Render(ChartComponent component)
    {
        using var tree = new RenderTreeBuilder(); component.Render(tree); return tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    }
    private sealed class ChartComponent : MatchStatisticsChart { public void Apply() => OnParametersSet(); public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
}
#pragma warning restore BL0006
