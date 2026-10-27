using System.Reflection;
using System.Text.Json;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

// Pure display checks over the already-validated statistics shape; fresh ledger/store authority is tested separately.
public sealed class FocusedStatisticsPresentationTests
{
    private static string Answer(string question, int homeAttempts = 361, int homeCompleted = 290, int homeShots = 3, int awayShots = 0)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            replayClock = "98:00", totalClassifiedControlMs = 5850000,
            teams = new[] {
                new { name = "Brentford", passAttempts = homeAttempts, passCompleted = homeCompleted,
                    passCompletionPercent = homeAttempts == 0 ? (decimal?)null : 100m * homeCompleted / homeAttempts,
                    shots = homeShots, controlledMs = 2905300, illustratedControlSharePercent = 100m * 2905300 / 5850000 },
                new { name = "Chelsea", passAttempts = 367, passCompleted = 293, passCompletionPercent = (decimal?)(100m * 293 / 367),
                    shots = awayShots, controlledMs = 2944700, illustratedControlSharePercent = 100m * 2944700 / 5850000 }
            }
        }));
        return (string)typeof(MatchSourceQuestions).Assembly.GetType("Matchmate.Web.Chat.MatchStatisticsPresentation")!
            .GetMethod("Format", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [document.RootElement, question])!;
    }

    [Theory]
    [InlineData("How many shots are in this generated replay?", "Brentford have taken 3 shots; Chelsea have taken none.", "passes")]
    [InlineData("How many shots has Chelsea taken?", "Chelsea have taken no shots yet.", "Brentford")]
    [InlineData("How many passes has Chelsea made in this replay?", "Chelsea have made 367 pass attempts.", "Brentford")]
    [InlineData("How many pass attempts have Brentford made?", "Brentford have made 361 pass attempts.", "completed")]
    [InlineData("How many passes have Brentford completed?", "Brentford have completed 290 passes.", "shots")]
    [InlineData("What is Brentford pass completion rate?", "Brentford have completed 290 of 361 passes (80.3%).", "Chelsea")]
    [InlineData("What is Chelsea pass accuracy?", "Chelsea have completed 293 of 367 passes (79.8%).", "Brentford")]
    [InlineData("Which team has taken more shots?", "Brentford have taken 3 shots; Chelsea have taken none.", "control")]
    [InlineData("Show Chelsea shots compared with the opponent", "Brentford have taken 3 shots; Chelsea have taken none.", "passes")]
    [InlineData("Please show Chelsea shots", "Chelsea have taken no shots yet.", "Brentford")]
    [InlineData("What is Chelsea’s pass accuracy?", "Chelsea have completed 293 of 367 passes (79.8%).", "Brentford")]
    [InlineData("What is Chelsea's pass accuracy?", "Chelsea have completed 293 of 367 passes (79.8%).", "Brentford")]
    [InlineData("Could you show Chelsea’s shots?", "Chelsea have taken no shots yet.", "Brentford")]
    [InlineData("Would you show Chelsea's pass attempts?", "Chelsea have made 367 pass attempts.", "Brentford")]
    public void OrdinarySingleMetricIsShortAndContainsOnlyItsRequestedMetric(string question, string expected, string absent)
    {
        var answer = Answer(question); Assert.Equal("In this generated replay at 98:00, " + expected, answer);
        Assert.DoesNotContain(absent, answer); Assert.InRange(answer.Length, 1, 260);
    }

    [Theory]
    [InlineData("Show possession statistics for both teams in this replay", "Brentford: illustrated control share 49.7%; Chelsea: illustrated control share 50.3%", "passes")]
    [InlineData("What is Chelsea illustrated control share?", "Chelsea: illustrated control share 50.3%", "Brentford")]
    [InlineData("How much control time has Chelsea had?", "Chelsea have 49 min 4 sec of illustrated control", "%")]
    public void ControlAnswersUseHumanDurationsAndExplainOnlyTheirDenominator(string question, string expected, string absent)
    {
        var answer = Answer(question); Assert.StartsWith("In this generated replay at 98:00, ", answer); Assert.Contains(expected, answer);
        Assert.Contains("finished", answer); Assert.Contains("Pauses, restarts, unknown time and unfinished spells are excluded", answer);
        Assert.DoesNotContain(absent, answer); Assert.InRange(answer.Length, 1, 430);
    }

    [Theory]
    [InlineData("Show replay statistics")]
    [InlineData("How many shots have they taken?")]
    [InlineData("How many shots has Arsenal taken?")]
    [InlineData("Show Brentford shots and completed passes")]
    [InlineData("Show shots not passes")]
    [InlineData("Could you please discuss the statistics?")]
    [InlineData("Please show Arsenal’s shots")]
    [InlineData("Kindly show Chelsea shots")]
    [InlineData("Please show their shots")]
    [InlineData("Please show Chelsea shots not passes")]
    [InlineData("Please show Chelsea shots and completed passes")]
    public void BroadAmbiguousUnknownNegatedAndCompoundRequestsKeepBothTeamsOverview(string question)
    {
        var answer = Answer(question); Assert.StartsWith("In this generated replay at 98:00, ", answer);
        Assert.Contains("Brentford: 3 shots", answer); Assert.Contains("Chelsea: 0 shots", answer); Assert.Contains("290 of 361 passes completed (80.3%)", answer);
        Assert.Contains("49.7% share", answer); Assert.Contains("97 min 30 sec", answer); Assert.InRange(answer.Length, 1, 650);
    }

    [Theory]
    [InlineData("How many shots on target has Chelsea taken?", "shots-on-target")]
    [InlineData("How many actual shots have Chelsea taken?", "actual match statistics")]
    [InlineData("What is Chelsea xG?", "xG")]
    [InlineData("How many shots has this player taken?", "individual-player")]
    [InlineData("How many passes to Thiago?", "pass-recipient")]
    [InlineData("How many shots in the first half?", "time window")]
    [InlineData("How many shots in the last 10 minutes?", "time window")]
    public void UnsupportedQualifiersNeverBecomeUnqualifiedTeamMetricClaims(string question, string qualification)
    {
        var answer = Answer(question); Assert.StartsWith("In this generated replay at 98:00, ", answer); Assert.Contains(qualification, answer);
        Assert.Contains("aren't available", answer.Replace("isn't available", "aren't available"));
        Assert.DoesNotContain("3 shots", answer); Assert.DoesNotContain("293", answer); Assert.InRange(answer.Length, 1, 260);
    }

    [Fact]
    public void ZeroSingularAndEmptyDenominatorsHavePlainGrammar()
    {
        Assert.Contains("Brentford have taken a shot; Chelsea have taken none", Answer("Show shots for both teams", homeShots: 1));
        Assert.Contains("have taken no shots yet", Answer("Show shots for both teams", homeShots: 0));
        Assert.Contains("made one pass attempt", Answer("How many passes has Brentford made?", 1, 1));
        Assert.Contains("completed a pass", Answer("How many passes has Brentford completed?", 1, 1));
        Assert.Contains("completed 1 of 1 pass (100%)", Answer("What is Brentford pass completion rate?", 1, 1));
        Assert.Contains("1 of 1 pass completed (100%)", Answer("Show replay statistics", 1, 1, 1));
        var empty = Answer("What is Brentford pass completion rate?", 0, 0); Assert.Contains("no finished pass attempts yet", empty); Assert.Contains("rate is unavailable", empty); Assert.DoesNotContain("0%", empty);
        Assert.Contains("completed 0 of 10 passes (0%)", Answer("What is Brentford pass completion rate?", 10, 0));
    }
}
