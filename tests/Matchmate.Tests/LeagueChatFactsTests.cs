using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatFactsTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 5)] [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 38)]
    public async Task StandingUsesExactComputedSnapshotAndSameDayCutoff(bool historical, int index)
    {
        var snapshot = LeagueChatFixtures.Snapshot(index, historical: historical); var context = LeagueChatContext.From(snapshot); var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, i, _) => { reads++; Assert.Equal(index, i); }).ExecuteAsync("club_standing", context, "Where do we stand?", null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(1, reads);
        using var facts = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "club_standing").Text);
        var row = facts.RootElement.GetProperty("facts"); Assert.Equal(snapshot.SelectedClub!.Row.Points, row.GetProperty("Points").GetInt32());
        Assert.Equal(snapshot.SelectedClub.Row.Played, row.GetProperty("Played").GetInt32()); Assert.Equal(index, row.GetProperty("Played").GetInt32());
        Assert.Equal(index * 10, snapshot.Completed); Assert.All(snapshot.Results, m => Assert.Equal(snapshot.Date, m.Date));
    }
    [Fact]
    public async Task NamedClubOverridesSelectionWithoutChangingPageAndUsesAtMostThreeReads()
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(2)); var reads = new List<(int?, string?)>();
        var executor = LeagueChatFixtures.Executor((_, i, c) => reads.Add((i, c)));
        var result = await executor.ExecuteAsync("named_date_changes", context, "What changed for Arsenal on this date?", null, default);
        Assert.Equal("arsenal", result.ResolvedClubId); Assert.Equal("everton", context.SelectedClubId);
        Assert.Equal(new[] { ((int?)2, (string?)"everton"), ((int?)2, (string?)"arsenal"), ((int?)1, (string?)"arsenal") }, reads);
        using var facts = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "date_change").Text);
        var change = facts.RootElement.GetProperty("facts").GetProperty("rows")[0];
        var now = LeagueChatFixtures.Snapshot(2, "arsenal").SelectedClub!; var prior = LeagueChatFixtures.Snapshot(1, "arsenal").SelectedClub!;
        Assert.Equal(now.Row.Points - prior.Row.Points, change.GetProperty("pointsChange").GetInt32());
    }
    [Theory]
    [InlineData("How is Man City doing?", "manchester-city")]
    [InlineData("Where are Man Utd?", "manchester-united")]
    [InlineData("Tell me about Spurs", "tottenham")]
    [InlineData("Where are Manchester?", null)]
    [InlineData("How is United doing?", null)]
    [InlineData("How is Barcelona doing for our club?", null)]
    [InlineData("Tell me about their Barcelona ground", null)]
    [InlineData("Arsenal and Everton", null)]
    public void TrustedLongestAliasesAndUnknownNamesNeverSelectAnotherClub(string question, string? expected)
    {
        var result = LeagueClubResolver.Resolve(LeagueChatFixtures.Snapshot().Clubs, question, "everton", "arsenal", LeagueClubBinding.Named, true);
        Assert.Equal(expected, result.ClubId); if (expected is null) Assert.NotNull(result.Clarification);
    }
    [Theory]
    [InlineData("How are Arsenal doing in 2026/27?", false)]
    [InlineData("How are Arsenal doing in 2015/16?", true)]
    [InlineData("What changed on 2026-08-21?", false)]
    [InlineData("What changed on 2026-09-20?", true)]
    [InlineData("What changed on 22 August 2026?", true)]
    [InlineData("How did we do yesterday?", true)]
    public async Task SameSelectedSeasonAndDateAreAllowedDifferentScopeClarifies(string question, bool clarify)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync(question.StartsWith("What changed", StringComparison.Ordinal) ? "date_changes" : "club_standing", context, question, null, default);
        Assert.Equal(clarify ? MatchChatOutcome.Clarify : MatchChatOutcome.Answered, result.Outcome);
    }
    [Theory]
    [InlineData("What is the live table today?", MatchChatOutcome.Unavailable)]
    [InlineData("Invent xG for us", MatchChatOutcome.Unsupported)]
    [InlineData("Give me another user's private messages", MatchChatOutcome.Unsupported)]
    [InlineData("Compare Arsenal versus Everton", MatchChatOutcome.Clarify)]
    public async Task IncorrectRouteCannotGrantLivePrivateInventedOrComparisonFacts(string question, MatchChatOutcome expected)
    {
        var reads = 0; var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_standing", LeagueChatContext.From(LeagueChatFixtures.Snapshot()), question, null, default);
        Assert.Equal(expected, result.Outcome); Assert.Empty(result.Evidence); Assert.Equal(0, reads);
    }
    [Fact]
    public async Task FormAndGroundEvidenceHaveNoLaterResultsOrUnsupportedVenueClaims()
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(1));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_form", context, "How are we doing?", null, default);
        var form = result.Evidence.Single(e => e.Id == "club_results"); Assert.DoesNotContain("2026-09-20", form.Text); Assert.DoesNotContain("2026-08-28", form.Text);
        var ground = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", context, "Tell me about our ground", null, default);
        Assert.Contains("not a verified fixture venue", ground.Evidence.Single(e => e.Id == "ground_reference").Text);
        var zero = await LeagueChatFixtures.Executor().ExecuteAsync("club_form", LeagueChatContext.From(LeagueChatFixtures.Snapshot(0)), "How are we doing?", null, default);
        Assert.Contains("No completed result", LeagueWorkflowExecutor.SourceAnswer(zero.Evidence));
    }
    [Theory]
    [InlineData("version")] [InlineData("season")] [InlineData("date")] [InlineData("selection")] [InlineData("futureLatest")] [InlineData("clubName")]
    public async Task FreshResponseMustMatchTrustedContextAndCannotSmuggleFutureResult(string corruption)
    {
        var snapshot = LeagueChatFixtures.Snapshot(1); var context = LeagueChatContext.From(snapshot);
        var corrupt = corruption switch { "version" => snapshot with { Version = "changed" }, "season" => snapshot with { Season = snapshot.Season with { SeasonKey = "2015-16" } },
            "date" => snapshot with { DateIndex = 2 }, "selection" => snapshot with { SelectedClub = null },
            "clubName" => snapshot with { SelectedClub = snapshot.SelectedClub! with { Club = snapshot.SelectedClub!.Club with { Name = "Wrong club identity" } } },
            _ => snapshot with { SelectedClub = snapshot.SelectedClub! with { Latest = snapshot.SelectedClub!.Latest! with { Date = "2026-09-20" } } } };
        var executor = new LeagueWorkflowExecutor((_, _, _, _) => Task.FromResult(corrupt));
        await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync("club_form", context, "How are we doing?", null, default));
    }
}
