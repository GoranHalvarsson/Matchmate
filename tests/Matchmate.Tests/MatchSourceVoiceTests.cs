using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

// Direct deterministic-formatter contracts; real session controls remain in MatchConversationTests.
public sealed class MatchSourceVoiceTests
{
    [Theory] [InlineData(null, null)] [InlineData(0, 0)] [InlineData(2, 1)] [InlineData(1, 3)]
    public void SummaryDistinguishesUnknownLevelAndLeadingTalliesWithoutInventingDetails(int? home, int? away)
    {
        var summary = Item("match_summary", new { home = "Home", away = "Away", homeReportedGoals = home, awayReportedGoals = away,
            clock = "SECRET_CLOCK", period = "SECRET_PERIOD", eligibleReportedEvents = 999, scoreMeaning = "SECRET_DIAGNOSTIC" });
        var bundle = Bundle("current_match_summary", summary, Coverage());
        var result = Format(bundle); AssertEvidenceUnchanged(bundle, result.Evidence);
        Assert.DoesNotContain("SECRET", result.Answer); Assert.DoesNotContain("999", result.Answer);
        if (home is null)
        {
            Assert.DoesNotContain("0–0", result.Answer); Assert.Contains("No score", result.Answer);
        }
        else
        {
            Assert.Contains("Home", result.Answer); Assert.Contains("Away", result.Answer);
            Assert.Contains("reported", result.Answer);
            if (home == away) { Assert.Contains($"{home}–{away}", result.Answer); Assert.Contains("level", result.Answer); }
            else { Assert.Contains(home > away ? "Home lead Away" : "Away lead Home", result.Answer); Assert.Contains($"{Math.Max(home.Value, away!.Value)}–{Math.Min(home.Value, away.Value)}", result.Answer); }
        }
        Assert.DoesNotContain("scored", result.Answer); Assert.DoesNotContain("all the action", result.Answer);
    }

    [Theory]
    [InlineData(true, "present", "observed", false, "ball has an observed")]
    [InlineData(false, "present", "estimated", false, "ball position is an estimate")]
    [InlineData(false, "present", "generated", false, "ball position is illustrated")]
    [InlineData(true, "present", "unknown", false, "its method is not specified")]
    [InlineData(false, "present", "observed", false, "ball has an observed")]
    [InlineData(false, "missing", "observed", true, "No ball position")]
    [InlineData(true, "unknown", "generated", true, "No ball position")]
    public void PositionsKeepPlayerAndBallMethodsIndependentAndDoNotCarryGaps(bool generated, string availability, string method, bool gap, string ballMeaning)
    {
        var bundle = Bundle("coverage_and_limits", Coverage(generated, availability, method, gap));
        var result = Format(bundle); AssertEvidenceUnchanged(bundle, result.Evidence);
        Assert.Contains(generated ? "illustration" : "recorded observations and estimates", result.Answer);
        Assert.Contains(ballMeaning, result.Answer); Assert.Contains("missing", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(gap ? "rather than being carried forward" : "does not carry them forward", result.Answer);
        Assert.Contains("cannot establish", result.Answer); Assert.Contains("tactical assignments", result.Answer);
        if (gap) Assert.Contains("gap", result.Answer);
        Assert.DoesNotContain("ballAvailability", result.Answer); Assert.DoesNotContain("SECRET", result.Answer);
    }

    [Fact]
    public void UnknownPlayerSourceAndUnknownBallMethodStayUnknown()
    {
        var coverage = Item("coverage", new { origin = "Unspecified provenance", gap = false, ballAvailability = "present", ballMethod = "other-method" });
        var result = Format(Bundle("coverage_and_limits", coverage));
        Assert.Contains("does not establish how", result.Answer); Assert.Contains("method is not specified", result.Answer);
        Assert.DoesNotContain("recorded observations", result.Answer); Assert.DoesNotContain("other-method", result.Answer);
    }

    [Theory] [InlineData("generated")] [InlineData("observed")] [InlineData("estimated")] [InlineData("unknown")]
    public void PlayerIdentityAndPointMethodDoNotBecomeMovementOrPerformance(string method)
    {
        var bundle = Bundle("selected_player_context", Player(method), Involvement([]), Coverage());
        var result = Format(bundle); AssertEvidenceUnchanged(bundle, result.Evidence);
        Assert.StartsWith("Selected Player", result.Answer); Assert.Contains("Home", result.Answer); Assert.Contains("17", result.Answer);
        Assert.DoesNotContain("1.25", result.Answer); Assert.DoesNotContain("2.75", result.Answer);
        Assert.Contains("not a full-match assessment", result.Answer);
        Assert.Contains(method switch { "generated" => "illustration", "observed" => "single point", "estimated" => "estimate", _ => "method is not specified" }, result.Answer);
        Assert.DoesNotContain("played badly", result.Answer); Assert.DoesNotContain("did not play", result.Answer);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void MissingPlayerCoordinateDoesNotInferParticipationFromActiveFlag(bool active)
    {
        var result = Format(Bundle("selected_player_context", Player("observed", false, active), Involvement([]), Coverage(gap: true)));
        Assert.Contains("No current position", result.Answer); Assert.Contains("nothing about whether they played", result.Answer);
        Assert.DoesNotContain("on the bench", result.Answer); Assert.DoesNotContain("did not play", result.Answer);
    }

    [Fact]
    public void PlayerUsesOneWholeQualifiedPassageAndIgnoresUnselectedOrFutureInventory()
    {
        const string text = "The report credits a layoff that led to a saved shot.";
        const string qualification = "An attributed contribution, not verified contacts, a goal assist or an overall rating.";
        var bundle = Bundle("selected_player_context", Player("generated"), Involvement([
            new { text, qualification }, new { text = "SECOND_PASSAGE_MUST_NOT_APPEAR", qualification = "SECOND_LIMIT" }
        ]), Coverage());
        var result = Format(bundle); AssertEvidenceUnchanged(bundle, result.Evidence);
        Assert.StartsWith("Selected Player", result.Answer); Assert.Contains(text, result.Answer); Assert.Contains(qualification, result.Answer);
        Assert.DoesNotContain("SECOND_", result.Answer); Assert.DoesNotContain("SECRET_", result.Answer);
    }

    [Fact]
    public void QualificationIsNeverTruncatedToFitTheAnswerBudget()
    {
        var qualification = string.Concat(Enumerable.Repeat("Point-scoped evidence only. ", 22)) + "END_QUALIFICATION_NO_CONTACT_INFERENCE";
        var result = Format(Bundle("selected_player_context", Player("estimated"), Involvement([new { text = "A point observation places the player centrally.", qualification }]), Coverage()));
        Assert.Contains(qualification, result.Answer); Assert.EndsWith("END_QUALIFICATION_NO_CONTACT_INFERENCE", qualification);
        Assert.InRange(result.Answer.Length, 1, MatchChatLimits.AnswerChars);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnqualifiedOrOverbudgetPassageIsOmittedWhole(bool overbudget)
    {
        var text = overbudget ? "OVERSIZE_PASSAGE " + new string('x', MatchChatLimits.AnswerChars) : "UNQUALIFIED_PASSAGE";
        var qualification = overbudget ? "REQUIRED_QUALIFICATION" : "";
        var result = Format(Bundle("selected_player_context", Player("observed", false), Involvement([new { text, qualification }]), Coverage()));
        Assert.StartsWith("Selected Player", result.Answer); Assert.Contains("No current position", result.Answer);
        Assert.DoesNotContain("PASSAGE", result.Answer); Assert.DoesNotContain("REQUIRED_QUALIFICATION", result.Answer);
        Assert.InRange(result.Answer.Length, 1, MatchChatLimits.AnswerChars);
    }

    [Fact]
    public void ForeignPlayerBriefCannotEnterSelectedPlayerAnswer()
    {
        var result = Format(Bundle("selected_player_context", Player("observed"), Involvement([
            new { text = "FOREIGN_PLAYER_ACTION", qualification = "FOREIGN_LIMIT" }
        ], "person-two", "Other Player"), Coverage()));
        Assert.StartsWith("Selected Player", result.Answer); Assert.DoesNotContain("FOREIGN", result.Answer); Assert.DoesNotContain("Other Player", result.Answer);
    }

    private static MatchWorkflowItem Item(string tool, object data) => new(tool, tool, JsonSerializer.Serialize(data), ["17"]);
    private static MatchWorkflowItem Coverage(bool generated = true, string availability = "present", string method = "generated", bool gap = false) => Item("coverage", new {
        origin = generated ? "Generated illustration from the existing authored model. Positions are not observed movement." : "Recorded source observations and estimates. Missing positions stay missing.",
        gap, ballAvailability = availability, ballMethod = method, coverage = "SECRET_COVERAGE_INVENTORY", unsupported = "SECRET_LIMIT_INVENTORY"
    });
    private static MatchWorkflowItem Player(string method, bool present = true, bool active = true) => Item("current_player", new {
        selectedPlayer = "Selected Player", shirt = 17, activeInSample = active, availability = present ? "present" : "missing", method,
        quality = "unknown", xMetres = present ? "1.25" : null, yMetres = present ? "2.75" : null, limitation = "SECRET_TECHNICAL_LIMIT"
    });
    private static MatchWorkflowItem Involvement(object[] observations, string playerId = "person-one", string player = "Selected Player") => Item("player_involvement", new {
        playerId, player, team = "Home", reviewedBrief = new { observations }, shown = new[] { "SECRET_FUTURE_EVENT", "SECRET_OTHER_PLAYER" }
    });
    private static MatchWorkflowBundle Bundle(string workflow, params MatchWorkflowItem[] items)
    {
        var fixture = SharedFootballTests.Fixture(); var query = SharedFootballTests.Query(fixture, 0) with { SubjectId = "person-one" };
        return new(workflow, MatchWorkflowRegistry.Version, new(query, 1), MatchWorkflowTemporalMode.ReplayAtCursor, "replay", items.ToImmutableArray(), [], [], [], []);
    }
    private static (string Answer, MatchChatEvidence[] Evidence) Format(MatchWorkflowBundle bundle)
        => ((string, MatchChatEvidence[]))typeof(MatchSourceQuestions).GetMethod("Format", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bundle])!;
    private static void AssertEvidenceUnchanged(MatchWorkflowBundle bundle, MatchChatEvidence[] evidence)
    {
        Assert.Equal(bundle.Items.Length, evidence.Length);
        for (var i = 0; i < evidence.Length; i++)
        {
            Assert.Equal(bundle.Items[i].Id, evidence[i].Id); Assert.Equal(bundle.Items[i].Tool, evidence[i].Tool);
            Assert.Equal(bundle.Items[i].Text, evidence[i].Text); Assert.Equal(bundle.Items[i].NumericTokens, evidence[i].NumericTokens);
        }
    }
}
