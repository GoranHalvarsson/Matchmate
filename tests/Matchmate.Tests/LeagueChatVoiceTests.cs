using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatVoiceTests
{
    [Theory]
    [InlineData("table", false)] [InlineData("changes", false)] [InlineData("coverage", false)]
    [InlineData("standing", true)] [InlineData("changes", true)] [InlineData("ground", true)]
    public async Task SourceSuggestionsKeepFreshComputedFactsAndUseNeitherRouterNorModel(string id, bool club)
    {
        var reads = new List<int?>(); var router = new LeagueChatFixtures.Router { Configured = false }; var model = new LeagueChatFixtures.Model { Configured = false };
        var snapshot = LeagueChatFixtures.Snapshot(2, club ? "everton" : null); var context = LeagueChatContext.From(snapshot);
        using var session = LeagueChatFixtures.Session(router: router, model: model, executor: LeagueChatFixtures.Executor((_, index, _) => reads.Add(index)));
        session.SetContext(context); Assert.True((await session.AskSourceAsync(context, id)).Accepted);
        var turn = Assert.Single(session.Turns); Assert.True(turn.SourceOnly); Assert.Equal(context, turn.Context); Assert.NotEmpty(turn.Evidence);
        Assert.Empty(router.Inputs); Assert.Empty(model.Requests); Assert.Equal(id == "changes" ? 2 : 1, reads.Count); Assert.Equal(2, reads[0]);
        if (id == "standing")
        {
            using var evidence = JsonDocument.Parse(turn.Evidence.Single(e => e.Id == "club_standing").Text);
            var facts = evidence.RootElement.GetProperty("facts"); var row = snapshot.SelectedClub!.Row;
            Assert.Equal(row.Points, facts.GetProperty("Points").GetInt32()); Assert.Equal(row.Played, facts.GetProperty("Played").GetInt32());
            Assert.Contains($"{row.Points} {(row.Points == 1 ? "point" : "points")} from {row.Played} {(row.Played == 1 ? "match" : "matches")}", turn.Answer); Assert.StartsWith("Everton", turn.Answer);
        }
        if (id == "table")
        {
            using var evidence = JsonDocument.Parse(turn.Evidence.Single(e => e.Id == "table_snapshot").Text);
            Assert.Equal(snapshot.Completed, evidence.RootElement.GetProperty("facts").GetProperty("Completed").GetInt32());
            Assert.Contains($"{snapshot.Completed} completed league matches", turn.Answer);
        }
        if (id == "ground")
        {
            Assert.Contains(snapshot.SelectedClub!.Club.Ground, turn.Answer); Assert.Contains(snapshot.SelectedClub.Club.Town, turn.Answer);
            Assert.Contains("for this season", turn.Answer); Assert.Contains("doesn't confirm a particular match venue", turn.Answer);
        }
        if (id == "coverage")
        {
            Assert.Contains($"{snapshot.Completed} results from 380 scheduled fixtures", turn.Answer);
            Assert.Contains("isn't a live feed", turn.Answer); Assert.Contains("doesn't update automatically", turn.Answer);
            using var evidence = JsonDocument.Parse(turn.Evidence.Single(e => e.Id == "league_coverage").Text);
            Assert.Equal("ongoing", evidence.RootElement.GetProperty("facts").GetProperty("Status").GetString());
        }
        Assert.DoesNotContain("END OF PLAYED DATE", turn.Answer); Assert.DoesNotContain("snapshot", turn.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("league_context", "What does the whole league table look like?", "league_context", null)]
    [InlineData("named_club_context", "How are Arsenal doing?", "club_context", "arsenal")]
    [InlineData("club_context", "How are we doing?", "club_context", "everton")]
    [InlineData("named_ground_context", "Tell me about Arsenal's ground.", "ground_context", "arsenal")]
    public async Task NarratorVoiceUsesTrustedCanonicalSubjectRatherThanUiSelection(string route, string question, string canonical, string? subject)
    {
        var router = new LeagueChatFixtures.Router { Workflow = route }; var model = new LeagueChatFixtures.Model();
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2, "everton"));
        using var session = LeagueChatFixtures.Session(router: router, model: model); session.SetContext(context);
        Assert.True((await session.AskAsync(context, question)).Accepted);
        var turn = Assert.Single(session.Turns); Assert.False(turn.SourceOnly); Assert.Equal(subject, turn.ResolvedClubId); Assert.Equal("everton", context.SelectedClubId);
        Assert.Single(router.Inputs); var request = Assert.Single(model.Requests); Assert.Equal(question, Assert.Single(request.Messages, m => m.Role == "user").Content);
        Assert.StartsWith("league-guide-v2\n", request.Messages[0].Content);
        var semantic = request.Messages.Single(m => m.Content.StartsWith("Trusted semantic", StringComparison.Ordinal)).Content;
        using var trusted = JsonDocument.Parse(semantic[(semantic.IndexOf('\n') + 1)..]);
        Assert.Equal(canonical, trusted.RootElement.GetProperty("CanonicalWorkflow").GetString());
        Assert.Equal(subject, trusted.RootElement.GetProperty("ResolvedClubId").GetString());
        Assert.Equal(subject is null ? null : subject == "arsenal" ? "Arsenal" : "Everton", trusted.RootElement.GetProperty("SubjectName").GetString());
        var instruction = request.Messages[0].Content;
        Assert.Contains("never from the UI selection alone", instruction); Assert.Contains("Whole-league topics", instruction); Assert.Contains("informed fan", instruction);
        foreach (var retainedGuard in new[] { "have not watched footage", "do not represent a club", "untrusted context", "premise or sentiment is not evidence",
            "Missing data is unknown, never zero", "No future/later-date results", "TOP-LEVEL evidence IDs", "no Markdown", "No tools, tool calls, HTML" })
            Assert.Contains(retainedGuard, instruction);
        Assert.DoesNotContain("assistant", request.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task NaturalAuthoredClubReplyCanCorrectPremiseUsingOnlyResolvedClubFacts()
    {
        // Tests the real request/validation/session contract, not live model tone or semantic-truth classification.
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_standing" }; var model = new LeagueChatFixtures.Model(); string? answer = null;
        model.Completing = (request, _) =>
        {
            var prepared = request.Messages.Single(m => m.Content.StartsWith("Prepared evidence", StringComparison.Ordinal)).Content;
            using var bundle = JsonDocument.Parse(prepared[(prepared.IndexOf('\n') + 1)..]);
            using var standing = JsonDocument.Parse(bundle.RootElement.EnumerateArray().Single(e => e.GetProperty("Id").GetString() == "club_standing").GetProperty("Text").GetString()!);
            var facts = standing.RootElement.GetProperty("facts"); Assert.Equal("Arsenal", facts.GetProperty("club").GetString()); Assert.True(facts.GetProperty("Lost").GetInt32() > 0);
            var lost = facts.GetProperty("Lost").GetInt32(); var points = facts.GetProperty("Points").GetInt32(); var played = facts.GetProperty("Played").GetInt32();
            answer = $"Arsenal aren't unbeaten: the record has {lost} {(lost == 1 ? "loss" : "losses")} and {points} {(points == 1 ? "point" : "points")} from {played} {(played == 1 ? "match" : "matches")}. It's a short run, so I'd keep expectations measured.";
            return Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "club_standing" } }), []));
        };
        using var session = LeagueChatFixtures.Session(router: router, model: model); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); session.SetContext(context);
        Assert.True((await session.AskAsync(context, "Why are Arsenal unbeaten?")).Accepted);
        var turn = Assert.Single(session.Turns); Assert.Equal(answer, turn.Answer); Assert.Equal("arsenal", turn.ResolvedClubId); Assert.Equal("everton", context.SelectedClubId);
        Assert.Single(turn.Evidence); Assert.Equal("club_standing", turn.Evidence[0].Id); Assert.Single(model.Requests);
    }

    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task SharedAndPreseasonRanksStayAccurateInTableStandingAndDateCopy(int index)
    {
        var model = SharedScenario(); var snapshot = model.Snapshot(index, "arsenal"); var context = LeagueChatContext.From(snapshot);
        var executor = new LeagueWorkflowExecutor((_, i, club, _) => Task.FromResult(model.Snapshot(i, club)));
        var standing = await executor.ExecuteAsync("club_standing", context, "Where do we stand?", null, default);
        var table = await executor.ExecuteAsync("league_table", context, "Who leads the whole league table?", null, default);
        var changes = await executor.ExecuteAsync("date_changes", context, "What changed on this date?", null, default);
        using var raw = JsonDocument.Parse(standing.Evidence.Single(e => e.Id == "club_standing").Text); var facts = raw.RootElement.GetProperty("facts");
        if (index == 0)
        {
            Assert.Equal(JsonValueKind.Null, facts.GetProperty("Rank").ValueKind); Assert.Contains("no table position", LeagueWorkflowExecutor.SourceAnswer(standing.Evidence));
            Assert.Contains("no ranking", LeagueWorkflowExecutor.SourceAnswer(table.Evidence)); Assert.Contains("All clubs are unranked", LeagueWorkflowExecutor.SourceAnswer(changes.Evidence));
        }
        else
        {
            Assert.Equal(1, facts.GetProperty("Rank").GetInt32()); Assert.True(facts.GetProperty("Shared").GetBoolean());
            Assert.Contains("share position 1", LeagueWorkflowExecutor.SourceAnswer(standing.Evidence)); Assert.Contains("share the lead", LeagueWorkflowExecutor.SourceAnswer(table.Evidence));
            Assert.Contains("shared position 1", LeagueWorkflowExecutor.SourceAnswer(changes.Evidence)); Assert.Contains("there was no previous ranking", LeagueWorkflowExecutor.SourceAnswer(changes.Evidence));
        }
    }

    [Fact]
    public async Task DayMovementKeepsNoGameAndAllDayCaveatWithoutChangingComputedValues()
    {
        var clubs = LeagueChatFixtures.Snapshot().Clubs;
        var model = new LeagueSeasonModel(new("2026-27", "2026/27", clubs, [
            new("arsenal:everton", "2026-08-21", "arsenal", "everton", 2, 0, "Synthetic voice fixture"),
            new("manchester-city:chelsea", "2026-08-21", "manchester-city", "chelsea", 1, 0, "Synthetic voice fixture"),
            new("chelsea:brentford", "2026-08-28", "chelsea", "brentford", 4, 0, "Synthetic voice fixture")
        ], "ongoing", 380, "2026-08-28", "2026-10-23T12:15:59Z"), version: LeagueReferenceCodec.CurrentVersion);
        var context = LeagueChatContext.From(model.Snapshot(2, "arsenal")); var reads = 0;
        var executor = new LeagueWorkflowExecutor((_, i, club, _) => { reads++; return Task.FromResult(model.Snapshot(i, club)); });
        var result = await executor.ExecuteAsync("date_changes", context, "What changed on this date?", null, default);
        using var raw = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text); var facts = raw.RootElement.GetProperty("facts"); var row = facts.GetProperty("rows")[0];
        Assert.False(row.GetProperty("playedOnDate").GetBoolean()); Assert.Equal(0, row.GetProperty("pointsChange").GetInt32());
        Assert.Equal(1, row.GetProperty("previousPosition").GetInt32()); Assert.Equal(2, row.GetProperty("position").GetInt32());
        var answer = LeagueWorkflowExecutor.SourceAnswer(result.Evidence); Assert.Contains("Arsenal did not play", answer); Assert.Contains("3 from 1 match", answer);
        Assert.Contains("Moved from position 1 to position 2", answer); Assert.Contains("don't single out one fixture", answer); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task RecentResultsKeepExactTeamsScoresDatesAndShortRunBoundary()
    {
        var snapshot = LeagueChatFixtures.Snapshot(3, "arsenal"); var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(3, "everton"));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("named_club_form", context, "Show Arsenal recent form here.", null, default);
        Assert.Equal("arsenal", result.ResolvedClubId); var answer = LeagueWorkflowExecutor.SourceAnswer(result.Evidence);
        foreach (var match in snapshot.SelectedClub!.Recent)
            Assert.Contains($"{match.Date}: {snapshot.Clubs.Single(c => c.Id == match.Home).Name} {match.HomeGoals}–{match.AwayGoals} {snapshot.Clubs.Single(c => c.Id == match.Away).Name}.", answer);
        Assert.DoesNotContain("2026-09-20", answer); Assert.Contains("Five matches cannot establish a full-season verdict", LeagueGuidePrompt.Text);
    }

    [Theory] [InlineData(1, 0, "arsenal", "1 win")] [InlineData(1, 0, "everton", "1 loss")] [InlineData(1, 1, "arsenal", "1 draw")]
    public async Task SingleMatchCopyUsesSingularNounsAndRetainsEveryComputedCount(int homeGoals, int awayGoals, string club, string outcome)
    {
        var model = new LeagueSeasonModel(new("2026-27", "2026/27", LeagueChatFixtures.Snapshot().Clubs, [
            new("arsenal:everton", "2026-08-21", "arsenal", "everton", homeGoals, awayGoals, "Synthetic singular voice fixture")
        ], "ongoing", 380, "2026-08-21", "2026-10-23T12:15:59Z"), version: LeagueReferenceCodec.CurrentVersion);
        var snapshot = model.Snapshot(1, club); var context = LeagueChatContext.From(snapshot);
        var executor = new LeagueWorkflowExecutor((_, index, selected, _) => Task.FromResult(model.Snapshot(index, selected)));
        var standing = await executor.ExecuteAsync("club_standing", context, "Where do we stand?", null, default);
        using var raw = JsonDocument.Parse(standing.Evidence.Single(e => e.Id == "club_standing").Text); var facts = raw.RootElement.GetProperty("facts"); var expected = snapshot.SelectedClub!.Row;
        Assert.Equal(expected.Played, facts.GetProperty("Played").GetInt32()); Assert.Equal(expected.Won, facts.GetProperty("Won").GetInt32());
        Assert.Equal(expected.Drawn, facts.GetProperty("Drawn").GetInt32()); Assert.Equal(expected.Lost, facts.GetProperty("Lost").GetInt32()); Assert.Equal(expected.Points, facts.GetProperty("Points").GetInt32());
        var answer = LeagueWorkflowExecutor.SourceAnswer(standing.Evidence); Assert.Contains("1 match:", answer); Assert.Contains(outcome, answer);
        foreach (var wrong in new[] { "1 matches", "1 wins", "1 draws", "1 losses", "1 points" }) Assert.DoesNotContain(wrong, answer);
        var table = await executor.ExecuteAsync("league_table", context, "Who leads the whole league table?", null, default);
        Assert.Contains("There has been 1 completed league match so far", LeagueWorkflowExecutor.SourceAnswer(table.Evidence));
        var changes = await executor.ExecuteAsync("date_changes", context, "What changed on this date?", null, default);
        Assert.Contains("1 match.", LeagueWorkflowExecutor.SourceAnswer(changes.Evidence));
        if (homeGoals == awayGoals) { Assert.Contains("1 point from 1 match", answer); Assert.Contains("Added 1 point", LeagueWorkflowExecutor.SourceAnswer(changes.Evidence)); }
    }

    private static LeagueSeasonModel SharedScenario() => new(new("2026-27", "2026/27", LeagueChatFixtures.Snapshot().Clubs, [
        new("arsenal:everton", "2026-08-21", "arsenal", "everton", 1, 0, "Synthetic shared voice fixture"),
        new("manchester-city:chelsea", "2026-08-21", "manchester-city", "chelsea", 1, 0, "Synthetic shared voice fixture")
    ], "ongoing", 380, "2026-08-21", "2026-10-23T12:15:59Z"), version: LeagueReferenceCodec.CurrentVersion);
}
