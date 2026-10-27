using System.Text.Json;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

// Developer-authored examples exercise policy, not live model quality or semantic truth.
public sealed class MatchExpertAnalysisTests
{
    private static readonly Dictionary<string, MatchChatEvidence> Supplied = new() {
        ["current_events"] = new("current_events", "current_events", "Unit event evidence; digits represent clocks/scores, not metrics.", ["0", "1", "2", "3", "4", "9", "61", "80", "83", "90", "100", "-1.25"])
    };
    public static TheoryData<string> Accepted => new() {
        "The report describes a headed pass across goal. Football interpretation: defending the second ball matters here; it does not establish individual blame.",
        "Damsgaard passes to Schade in the report. A possible reading is that the rebound creates another defensive problem; the contacts are not independently verified.",
        "Football interpretation: a compact formation can help protect the far side after a cross. No observed formation is established by the generated layout.",
        "The reported score is 2-1. Speed is unavailable.",
        "We cannot measure player speed or distance from these positions.",
        "The evidence doesn't provide xG, speed or distance measurements.",
        "Speed, distance and expected goals are unavailable in the supplied evidence.",
        "No reliable speed or distance data is supplied; the reported pass still describes an action.",
        "Player speeds aren't measured here, and possession percentage is unknown.",
        "We don't have data on sprint counts. Pass count cannot be established from generated positions.",
        "Expected-goals data isn't provided. We cannot infer measured pressure or an observed tactical assignment.",
        "At x -1.25 m, this is a generated coordinate, not measured movement."
    };
    public static TheoryData<string> RejectedMeasurements => new() {
        "His speed was 61.", "He reached 61 km/h.", "He ran at 2 mph.", "He reached 1 m/s.",
        "He ran at 61 kilometres per hour.", "He covered 61 metres.", "His distance was 61.",
        "He covered 2 miles.", "He ran two yards.", "He travelled 61 feet.", "He made a two-yard pass.",
        "The xG was 2.", "Expected-goals data: 1.", "The pass count was 61.", "He completed 2 short passes.",
        "They completed two accurate passes.", "They built a two-pass move.", "The team had 61% possession.",
        "The team had 61 percent possession.", "Possession was 61.", "They made 2 sprints.", "Their formation was 4-3-3.",
        "They set up in 4-4-2.", "Speed is unavailable, but his speed was 61.",
        "Speed is unavailable. His xG was 1.", "No speed or distance data is supplied; he completed 2 passes.",
        "The pass count is unknown and he made 2 sprints.", "The xG is unavailable, but it was 1.",
        "No speed data is supplied. The value was 61.", "Distance isn't unavailable: it was 61.",
        "Speed is not unknown; he reached 2 mph.", "No more than 2 passes were needed.",
        "Speed is unavailable, although he sprinted past the defender.", "The formation is unknown, but it was 4-3-3."
    };

    [Theory] [MemberData(nameof(Accepted))]
    public void SourceBackedInterpretationAndLocalMetricCaveatsPass(string answer)
        => Assert.Equal(answer, MatchReplyValidation.Validate(Reply(answer), Supplied).Answer);

    [Theory] [MemberData(nameof(RejectedMeasurements))]
    public void PositiveMetricsCannotBorrowScoreDigitsOrAnotherClausesAbstention(string answer)
    {
        MatchReplyRejection? reason = null;
        Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(answer), Supplied, category => reason = category));
        Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
    }

    public static TheoryData<string> ReviewedCaveats => new() {
        "The evidence does not tell us the speed or distance covered",
        "We do not have the tracking data to assess speed or distance",
        "The evidence doesn't tell us the player's speed or the distance covered",
        "The recording cannot show us the players' speed or distance",
        "We don't have enough tracking data to determine speed or distance",
        "We do not have the tracking measurements needed to quantify player speed or distance",
        "There is no tracking data to assess speed or distance",
        "We cannot assess the speed or distance from these generated positions"
    };

    [Theory] [MemberData(nameof(ReviewedCaveats))]
    public void F001_LocalCaveatsPassButCannotExcuseLaterMetricOrDistanceAssertions(string caveat)
    {
        Assert.Equal(caveat, MatchReplyValidation.Validate(Reply(caveat), Supplied).Answer);
        foreach (var assertion in new[] { ", but he made a 2-metre pass.", "; he made a two metre pass.", ". His speed was 2.", "; the xG was 1." })
        {
            MatchReplyRejection? reason = null;
            Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(caveat + assertion), Supplied, category => reason = category));
            Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
        }
    }

    [Theory]
    [InlineData("m")] [InlineData("metre")] [InlineData("metres")] [InlineData("meter")] [InlineData("meters")]
    [InlineData("km")] [InlineData("kilometre")] [InlineData("kilometres")] [InlineData("kilometer")] [InlineData("kilometers")]
    [InlineData("mi")] [InlineData("mile")] [InlineData("miles")] [InlineData("yd")] [InlineData("yard")] [InlineData("yards")]
    [InlineData("ft")] [InlineData("foot")] [InlineData("feet")]
    public void F002_QuantifiedPassDistanceNeverBorrowsScoreClockNumbersOrCaveats(string unit)
    {
        foreach (var quantity in new[] { "2", "two" }) foreach (var separator in new[] { "-", " ", "" })
        {
            var assertion = $"He made a {quantity}{separator}{unit} pass.";
            foreach (var prefix in new[] { "", "Speed is unavailable, but " })
            {
                MatchReplyRejection? reason = null;
                Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(prefix + assertion), Supplied, category => reason = category));
                Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
            }
        }
    }

    [Theory] [InlineData("x 2 m")] [InlineData("x 2 metres")] [InlineData("y 1 meters")] [InlineData("x -1.25 m")]
    public void F002_SuppliedCoordinatesRemainCoordinates(string coordinate)
    {
        var answer = $"The current generated coordinate is {coordinate}. This is illustration, not an observed trajectory.";
        Assert.Equal(answer, MatchReplyValidation.Validate(Reply(answer), Supplied).Answer);
    }

    [Theory] [InlineData("passes")] [InlineData("crosses")] [InlineData("shots")] [InlineData("runs")] [InlineData("sprints")]
    [InlineData("carries")] [InlineData("headers")] [InlineData("finishes")] [InlineData("deliveries")] [InlineData("clearances")]
    public void F002_PluralDistanceActionsCannotBypassTheSameGuard(string action)
    {
        foreach (var quantity in new[] { "2", "two" }) foreach (var separator in new[] { "-", " ", "" })
        {
            var assertion = $"He sent {quantity}{separator}metre {action}.";
            foreach (var prefix in new[] { "", "Speed is unavailable, but " })
            {
                MatchReplyRejection? reason = null;
                Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(prefix + assertion), Supplied, category => reason = category));
                Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
            }
        }
    }

    [Theory] [InlineData("The pass was 2 metres long.")] [InlineData("The pass was two metres long.")]
    [InlineData("The passes were 2 metres long.")] [InlineData("The crosses are two metres long.")]
    public void F002_PostposedDistanceAssertionsCannotBorrowScoreDigits(string assertion)
    {
        foreach (var prefix in new[] { "", "Speed is unavailable, but " })
        {
            MatchReplyRejection? reason = null;
            Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(prefix + assertion), Supplied, category => reason = category));
            Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
        }
    }

    [Fact]
    public void ExistingStructureReferencesNumbersHtmlAndBudgetsStillRejectWithClosedCategories()
    {
        Reject("not JSON PRIVATE_PAYLOAD", MatchReplyRejection.MalformedJson, json: true);
        Reject("{\"answer\":\"A reply\",\"evidenceIds\":[\"current_events\"],\"override\":true}", MatchReplyRejection.InvalidStructure);
        Reject("{\"answer\":\"A reply\",\"answer\":\"A duplicate\",\"evidenceIds\":[\"current_events\"]}", MatchReplyRejection.InvalidStructure);
        Reject(Reply("A reply", "invented"), MatchReplyRejection.UnknownEvidence);
        Reject(Reply("There were 999 goals."), MatchReplyRejection.UnsupportedNumber);
        Reject(Reply("<b>A reply</b>"), MatchReplyRejection.AnswerContent);
        Reject(Reply(new string('a', MatchChatLimits.AnswerChars + 1)), MatchReplyRejection.AnswerContent);
        Reject(new string('a', MatchChatLimits.ReplyChars + 1), MatchReplyRejection.ReplyBudget);
        static void Reject(string value, MatchReplyRejection expected, bool json = false)
        {
            MatchReplyRejection? reason = null;
            var exception = Record.Exception(() => MatchReplyValidation.Validate(value, Supplied, category => reason = category));
            if (json) Assert.IsAssignableFrom<JsonException>(exception); else Assert.IsType<InvalidDataException>(exception);
            Assert.Equal(expected, reason);
        }
    }

    internal static string Reply(string answer, string evidenceId = "current_events") => JsonSerializer.Serialize(new { answer, evidenceIds = new[] { evidenceId } });
}
