using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Experience;
using Matchmate.Web.LeagueChat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatSourcePresentationTests
{
    [Fact]
    public async Task ProductionDateEvidenceRetainsAllTwentyClubsAndEveryDatedResult()
    {
        var evidence = await Changes(); var original = evidence.Text; var view = LeagueSourcePresentation.Read(evidence);
        var day = Assert.IsType<LeagueSourceDateChanges>(view.DateChanges);
        Assert.Equal("2026-08-28", day.Date); Assert.Equal("2026-08-21", day.PreviousDate); Assert.Equal(20, day.Changed.Count + day.Unchanged.Count); Assert.Equal(10, day.Results.Count);
        using var data = JsonDocument.Parse(evidence.Text); var facts = data.RootElement.GetProperty("facts");
        foreach (var row in facts.GetProperty("rows").EnumerateArray())
        {
            var presented = day.Changed.Concat(day.Unchanged).Single(p => p.Club == row.GetProperty("club").GetString());
            Assert.Equal(row.GetProperty("points").GetInt32(), presented.Points); Assert.Equal(row.GetProperty("previousPoints").GetInt32(), presented.PreviousPoints);
            Assert.Equal(row.GetProperty("pointsChange").GetInt32(), presented.PointsChange); Assert.Equal(row.GetProperty("played").GetInt32(), presented.Played);
            Assert.Equal(row.GetProperty("playedOnDate").GetBoolean(), presented.PlayedOnDate); Assert.Equal(row.GetProperty("shared").GetBoolean(), presented.Shared);
        }
        foreach (var match in facts.GetProperty("resultsOnSelectedDate").EnumerateArray())
        {
            var presented = day.Results.Single(p => p.Home == match.GetProperty("home").GetString() && p.Away == match.GetProperty("away").GetString());
            Assert.Equal(match.GetProperty("Date").GetString(), presented.Date); Assert.Equal(match.GetProperty("HomeGoals").GetInt32(), presented.HomeGoals); Assert.Equal(match.GetProperty("AwayGoals").GetInt32(), presented.AwayGoals);
        }
        Assert.Equal(facts.GetProperty("rule").GetString(), day.Rule); Assert.Equal(original, evidence.Text);
        var html = await Render(evidence); Assert.Equal(20, Count(html, "scope=\"row\"")); Assert.Equal(10, Count(html, "<time ")); Assert.Contains("not attributed to one fixture", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task NonplayingMovesStayVisibleAndOnlyTrulyUnchangedClubsUseDisclosure()
    {
        var model = MovementSeason(); var c = LeagueChatContext.From(model.Snapshot(2));
        var result = await new LeagueWorkflowExecutor((_, index, club, _) => Task.FromResult(model.Snapshot(index, club))).ExecuteAsync("date_changes", c, "What changed on this date?", null, default);
        var evidence = result.Evidence.Single(e => e.Id == "date_change"); var day = LeagueSourcePresentation.Read(evidence).DateChanges!;
        var arsenal = day.Changed.Single(r => r.Club == "Arsenal"); Assert.False(arsenal.PlayedOnDate); Assert.Equal(1, arsenal.PreviousPosition); Assert.Equal(2, arsenal.Position); Assert.Equal(3, arsenal.Points); Assert.Equal(3, arsenal.PreviousPoints); Assert.Equal(0, arsenal.PointsChange);
        Assert.Contains(day.Changed, r => r.Club == "Fulham" && r.PreviousShared == true && !r.Shared);
        Assert.Equal(20, day.Changed.Count + day.Unchanged.Count); Assert.NotEmpty(day.Unchanged); Assert.All(day.Unchanged, r => Assert.False(r.Changed));
        var html = await Render(evidence); var disclosure = html.IndexOf("<details", StringComparison.Ordinal);
        Assert.True(disclosure > 0); Assert.Contains("Arsenal", html[..disclosure]); Assert.Contains("Did not play", html[..disclosure]);
        Assert.Contains("Unchanged clubs (", html[disclosure..]); Assert.DoesNotContain(" open", html.Substring(disclosure, html.IndexOf('>', disclosure) - disclosure));
        Assert.Equal(20, Count(html, "scope=\"row\"")); Assert.Contains("Chelsea", html); Assert.Contains("Liverpool", html); Assert.Contains("4", html);
    }

    [Fact]
    public async Task NullAndSharedRanksStayExactAndPreseasonDoesNotRenderTwentyZeroRows()
    {
        var model = MovementSeason(); var first = LeagueChatContext.From(model.Snapshot(1));
        var executor = new LeagueWorkflowExecutor((_, index, club, _) => Task.FromResult(model.Snapshot(index, club)));
        var firstEvidence = (await executor.ExecuteAsync("date_changes", first, "What changed on this date?", null, default)).Evidence.Single(e => e.Id == "date_change");
        var firstDay = LeagueSourcePresentation.Read(firstEvidence).DateChanges!;
        Assert.All(firstDay.Changed.Concat(firstDay.Unchanged), row => Assert.Null(row.PreviousPosition));
        Assert.Contains(firstDay.Changed, r => r.Shared && r.Position == 2); var firstHtml = WebUtility.HtmlDecode(await Render(firstEvidence)); Assert.Contains("Unranked", firstHtml); Assert.Contains("Shared 2", firstHtml);
        var start = LeagueChatContext.From(model.Snapshot(0)); var zero = (await executor.ExecuteAsync("date_changes", start, "What changed on this date?", null, default)).Evidence.Single(e => e.Id == "date_change");
        Assert.True(LeagueSourcePresentation.Read(zero).DateChanges!.Preseason); var html = WebUtility.HtmlDecode(await Render(zero));
        Assert.Contains("No results have entered the table yet", html); Assert.Contains("zero points", html); Assert.DoesNotContain("<table", html); Assert.DoesNotContain("Unchanged clubs", html);
    }

    [Fact]
    public async Task RecentProductionResultsUseActualPascalCaseScoreAndDateFieldsIncludingZeroes()
    {
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(3, "everton")); var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_form", c, "Show our recent results here.", null, default);
        var evidence = result.Evidence.Single(e => e.Id == "club_results"); var view = LeagueSourcePresentation.Read(evidence); Assert.Equal(3, view.RecentResults!.Count);
        using var source = JsonDocument.Parse(evidence.Text); var raw = source.RootElement.GetProperty("facts").GetProperty("recent").EnumerateArray().ToArray();
        for (var i = 0; i < raw.Length; i++)
        {
            Assert.Equal(raw[i].GetProperty("Date").GetString(), view.RecentResults[i].Date); Assert.Equal(raw[i].GetProperty("HomeGoals").GetInt32(), view.RecentResults[i].HomeGoals); Assert.Equal(raw[i].GetProperty("AwayGoals").GetInt32(), view.RecentResults[i].AwayGoals);
        }
        Assert.Contains(view.RecentResults, r => r.HomeGoals == 0 || r.AwayGoals == 0); Assert.Equal(3, Count(await Render(evidence), "<time "));
    }

    [Theory]
    [InlineData("points")] [InlineData("previousPoints")] [InlineData("pointsChange")] [InlineData("played")]
    [InlineData("playedOnDate")] [InlineData("shared")] [InlineData("position")] [InlineData("previousPosition")] [InlineData("previousShared")]
    public async Task MissingRequiredMovementFieldsFallBackInsteadOfInventingZeroOrFalse(string field)
    {
        var evidence = await Changes(); var data = JsonNode.Parse(evidence.Text)!; data["facts"]!["rows"]![0]!.AsObject().Remove(field);
        var changed = evidence with { Text = data.ToJsonString() }; var view = LeagueSourcePresentation.Read(changed);
        Assert.Null(view.DateChanges); Assert.Equal(data["sourceAnswer"]!.GetValue<string>(), view.Text); Assert.DoesNotContain("<table", await Render(changed));
    }

    [Theory]
    [InlineData("HomeGoals")] [InlineData("AwayGoals")] [InlineData("Date")] [InlineData("home")] [InlineData("away")]
    public async Task MissingResultFieldsUseTheSafeOriginalText(string field)
    {
        var evidence = await Changes(); var data = JsonNode.Parse(evidence.Text)!; data["facts"]!["resultsOnSelectedDate"]![0]!.AsObject().Remove(field);
        var view = LeagueSourcePresentation.Read(evidence with { Text = data.ToJsonString() }); Assert.Null(view.DateChanges); Assert.Equal(data["sourceAnswer"]!.GetValue<string>(), view.Text);
    }

    [Fact]
    public async Task MalformedUnknownAndOversizedEvidenceNeverBecomesRawJsonOrThrowsRendering()
    {
        foreach (var text in new[] { "{bad json", "[]", "{\"sourceAnswer\":42}", new string('x', 12001), "{\"facts\":" + new string('[', 20) + new string(']', 20) + "}" })
        {
            var evidence = new MatchChatEvidence("date_change", "date_change", text, []); var view = LeagueSourcePresentation.Read(evidence, "Original safe answer.");
            Assert.Null(view.DateChanges); Assert.Equal("Original safe answer.", view.Text); var html = await Render(evidence, "Original safe answer."); Assert.Contains("Original safe answer.", html); Assert.DoesNotContain(text, html);
        }
        var original = await Changes(); var unknown = original with { Id = "future_fact" }; var source = LeagueSourcePresentation.Read(original).Text;
        Assert.Equal(source, LeagueSourcePresentation.Read(unknown).Text); Assert.Null(LeagueSourcePresentation.Read(unknown).DateChanges); Assert.DoesNotContain("<table", await Render(unknown));
        var oversized = JsonNode.Parse(original.Text)!; var rows = oversized["facts"]!["rows"]!.AsArray(); rows.Add(rows[0]!.DeepClone()); Assert.Null(LeagueSourcePresentation.Read(original with { Text = oversized.ToJsonString() }).DateChanges);
    }

    [Fact]
    public async Task MissingOrWrongBoolAndNumberTypesDoNotSilentlyProduceRows()
    {
        var evidence = await Changes();
        foreach (var (property, wrong) in new (string, JsonNode?)[] { ("points", JsonValue.Create("0")), ("shared", JsonValue.Create("false")), ("playedOnDate", null), ("played", JsonValue.Create(1000)) })
        {
            var data = JsonNode.Parse(evidence.Text)!; data["facts"]!["rows"]![0]![property] = wrong;
            Assert.Null(LeagueSourcePresentation.Read(evidence with { Text = data.ToJsonString() }).DateChanges);
        }
    }

    [Fact]
    public async Task RazorEncodesAllPreparedNamesRulesAndFallbackText()
    {
        var evidence = await Changes(); var data = JsonNode.Parse(evidence.Text)!;
        data["facts"]!["rows"]![0]!["club"] = "<img src=x onerror=alert(1)>"; data["facts"]!["rule"] = "<script>alert('rule')</script>";
        data["facts"]!["resultsOnSelectedDate"]![0]!["home"] = "<svg onload=alert(2)>";
        var html = await Render(evidence with { Text = data.ToJsonString() }); Assert.Contains("&lt;img", html); Assert.Contains("&lt;svg", html); Assert.Contains("&lt;script", html); Assert.DoesNotContain("<img", html); Assert.DoesNotContain("<svg", html); Assert.DoesNotContain("<script", html);
        var bad = await Render(new("unknown", "unknown", "not json", []), "<script>fallback</script>"); Assert.Contains("&lt;script&gt;", bad); Assert.DoesNotContain("<script>", bad);
    }

    [Fact]
    public async Task MainSourceFilteringMatchesProductionAndRenderingDoesNotReadOrCallModels()
    {
        var reads = 0; var router = new LeagueChatFixtures.Router { Configured = false }; var model = new LeagueChatFixtures.Model { Configured = false };
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, _, _) => reads++)); var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, null)); session.SetContext(c);
        await session.AskSourceAsync(c, "changes"); var turn = Assert.Single(session.Turns); var answer = turn.Answer; var texts = turn.Evidence.Select(e => e.Text).ToArray();
        Assert.Equal(2, reads); Assert.Equal(new[] { "date_change" }, LeagueSourcePresentation.MainEvidence(turn.Evidence).Select(e => e.Id));
        var html = await Conversation(session, c); var main = html[html.IndexOf("league-source-answer", StringComparison.Ordinal)..html.IndexOf("league-supporting-facts", StringComparison.Ordinal)];
        Assert.Contains("Source answer", main); Assert.Contains("league-source-movements", main); Assert.DoesNotContain("Coverage", main); Assert.DoesNotContain(WebUtility.HtmlEncode(answer), html);
        Assert.Contains("Supporting facts (2)", html); Assert.Contains("Coverage", html); Assert.Equal(2, reads); Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Equal(answer, turn.Answer); Assert.Equal(texts, turn.Evidence.Select(e => e.Text));
        var coverage = turn.Evidence.Single(e => e.Id == "league_coverage"); Assert.Same(coverage, Assert.Single(LeagueSourcePresentation.MainEvidence(new[] { coverage })));
    }

    [Fact]
    public async Task AiParagraphAndFeedbackLabelsKeepTheirExistingPaths()
    {
        using var session = LeagueChatFixtures.Session(); var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); session.SetContext(c); await session.AskAsync(c, "Where do we stand?");
        var answer = Assert.Single(session.Turns).Answer; var html = WebUtility.HtmlDecode(await Conversation(session, c)); Assert.Contains("Matchmate · AI", html); Assert.Contains(answer, html); Assert.Contains("<p class=\"league-answer\"", html); Assert.DoesNotContain("league-source-answer", html);
        var router = new LeagueChatFixtures.Router { Workflow = "clarify" }; using var clarification = LeagueChatFixtures.Session(router: router); clarification.SetContext(c); await clarification.AskAsync(c, "What do you mean?");
        var feedback = await Conversation(clarification, c); Assert.Contains("Clarification", feedback); Assert.DoesNotContain("league-source-answer", feedback);
    }

    [Fact]
    public void ResponsiveLayoutContainsOnlyLocalTableOverflowAndNativeDisclosure()
    {
        var css = File.ReadAllText(Path.Combine(HostingTestPaths.Root(), "src", "Matchmate.Web", "Components", "Experience", "LeagueSourceFacts.razor.css"));
        Assert.Contains("overflow-x:auto", css); Assert.Contains("max-width:100%", css); Assert.Contains("min-width:0", css); Assert.Contains("min-height:44px", css); Assert.Contains("@media(max-width:600px)", css);
    }

    private static async Task<MatchChatEvidence> Changes()
    {
        var c = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, null)); return (await LeagueChatFixtures.Executor().ExecuteAsync("date_changes", c, "What changed on this date?", null, default)).Evidence.Single(e => e.Id == "date_change");
    }
    private static LeagueSeasonModel MovementSeason()
    {
        var clubs = LeagueChatFixtures.Snapshot().Clubs;
        LeagueMatch[] matches = [new("arsenal:everton", "2026-08-21", "arsenal", "everton", 1, 0, "Synthetic presentation test"), new("chelsea:fulham", "2026-08-21", "chelsea", "fulham", 0, 0, "Synthetic presentation test"), new("chelsea:liverpool", "2026-08-22", "chelsea", "liverpool", 4, 0, "Synthetic presentation test")];
        return new(new("2026-27", "2026/27", clubs, matches, "ongoing", 380, "2026-08-22", "2026-10-23T12:00:00Z"), sources: [new("synthetic-presentation", "Synthetic presentation tests only", new string('a', 64), "Test only")], version: LeagueReferenceCodec.CurrentVersion);
    }
    private static int Count(string text, string token) => text.Split(token, StringSplitOptions.None).Length - 1;
    private static async Task<string> Render(MatchChatEvidence evidence, string? fallback = null)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider(); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LeagueSourceFacts>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LeagueSourceFacts.Evidence)] = evidence, [nameof(LeagueSourceFacts.Fallback)] = fallback }))).ToHtmlString());
    }
    private static async Task<string> Conversation(LeagueChatSession session, LeagueChatContext context)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton(session).AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider(); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LeagueConversation>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LeagueConversation.Context)] = context, [nameof(LeagueConversation.SeasonLabel)] = "2026/27" }))).ToHtmlString());
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static source rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Static source rendering must not invoke JavaScript.");
    }
}
