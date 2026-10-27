using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

[Trait("RequiresLocalData", "true")]
public sealed class HistoricalChatCoventryReplyTests
{
    private sealed record SavedReply(string Raw, string Answer, string[] EvidenceIds, HistoricalPrepared Prepared, LeagueHistoricalMatch Fixture);
    private static async Task<SavedReply> Load(string target)
    {
        var root = LocalPaths.FindRoot(); var data = await LeagueReferenceStore.Local(root).ReadAsync(default);
        var filename = target == "story" ? "coventry-story-rejected-reply.json" : "coventry-form-rejected-reply.json";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, ".azure", "historical-chat", filename));
        Assert.Equal(target == "story" ? "f289d2bebef1fe807046353a22c3226222c12288d400cc6e5a994585d3246ab6" : "67783809016a838514e6cdcbabf96ee9061f1f9666083fb16983d2fc75669522",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var captured = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "question", "rawModelReply", "rejectionReason" }, captured.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("AggregateSubjectUnbound", captured.RootElement.GetProperty("rejectionReason").GetString());
        Assert.Equal(HistoricalWorkflows.Suggestion(target == "story" ? "result" : "form")!.Question, captured.RootElement.GetProperty("question").GetString());
        var fixture = data.Match("2026-27", "coventry:brighton");
        Assert.Equal(new HistoricalChatContext("league-reference-v2", "2026-27", "coventry:brighton", "2026-09-13"), HistoricalChatContext.From(fixture));
        var prepared = HistoricalEvidence.Prepare(fixture, data.Snapshot("2026-27", null).Clubs, target == "story" ? "overview" : "form", null);
        Assert.Null(prepared.ClubId);
        var raw = captured.RootElement.GetProperty("rawModelReply").GetString()!; using var reply = JsonDocument.Parse(raw);
        return new(raw, reply.RootElement.GetProperty("answer").GetString()!, reply.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()!).ToArray(), prepared, fixture);
    }
    private static string? Reason(string answer, SavedReply saved)
    {
        try { HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, saved.EvidenceIds), saved.Prepared); return null; }
        catch (InvalidDataException error) { return error.Message; }
    }
    private static void Report(string probe, string? reason) => Console.WriteLine("CoventryReplyProbe=" + JsonSerializer.Serialize(new { probe, accepted = reason is null, reason }));

    // Exact original RED receipts remain in the ignored phase2A audit artifacts.
    [Theory]
    [InlineData("story")][InlineData("form")]
    public async Task ExactApprovedCoventryReplyValidatesAgainstItsPairedProductionBundle(string target)
    {
        var saved = await Load(target);
        Assert.Equal(saved.Answer, HistoricalReplyValidation.Validate(saved.Raw, saved.Prepared).Answer);
    }

    [Theory]
    [InlineData("story")][InlineData("form")]
    public async Task CapturedSchemaNumbersAndActualFixtureSamplesMatchCodeFactsBeforeHistoricalPolicy(string target)
    {
        var saved = await Load(target);
        Assert.Equal(saved.Answer, MatchReplyValidation.Validate(saved.Raw, saved.Prepared.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal)).Answer);
        Assert.Equal(0, saved.Fixture.Match.HomeGoals); Assert.Equal(5, saved.Fixture.Match.AwayGoals);
        Assert.Equal(20, saved.Fixture.HomeTable.Row.Rank); Assert.Equal(0, saved.Fixture.HomeTable.Row.Points);
        Assert.Equal(4, saved.Fixture.AwayTable.Row.Rank); Assert.Equal(7, saved.Fixture.AwayTable.Row.Points);
        foreach (var home in new[] { true, false })
        {
            var club = home ? saved.Fixture.Home : saved.Fixture.Away;
            var games = (home ? saved.Fixture.HomePreMatchForm : saved.Fixture.AwayPreMatchForm)
                .Where(m => string.CompareOrdinal(m.Date, saved.Fixture.Match.Date) < 0).DistinctBy(m => m.MatchKey)
                .OrderBy(m => m.Date, StringComparer.Ordinal).ThenBy(m => m.MatchKey, StringComparer.Ordinal).TakeLast(5).ToArray();
            int Goals(LeagueMatch match) => match.Home == club.Id ? match.HomeGoals : match.AwayGoals;
            int Against(LeagueMatch match) => match.Home == club.Id ? match.AwayGoals : match.HomeGoals;
            Assert.Equal(3, games.Length); Assert.Equal(home ? 0 : 1, games.Count(m => Goals(m) > Against(m)));
            Assert.Equal(home ? 0 : 1, games.Count(m => Goals(m) == Against(m)));
            Assert.Equal(home ? 3 : 1, games.Count(m => Goals(m) < Against(m)));
            Assert.Equal(home ? 0 : 8, games.Sum(Goals)); Assert.Equal(5, games.Sum(Against));
            Assert.Equal(home ? 0 : 4, games.Sum(m => Goals(m) > Against(m) ? 3 : Goals(m) == Against(m) ? 1 : 0));
            Assert.Equal(home ? 0 : 8, saved.Prepared.GoalFacts.Single(f => f.ClubId == club.Id && f.Scope == "earlier_results").Goals);
        }
    }

    [Theory]
    [InlineData("neither", null)]
    [InlineData("zero_phrase", null)]
    [InlineData("finite_phrase", null)]
    [InlineData("both", null)]
    public async Task BothStoryGoalClausesAndTheRemainingAnswerValidate(string omit, string? expected)
    {
        var saved = await Load("story"); var answer = saved.Answer;
        if (omit is "zero_phrase" or "both") answer = Regex.Replace(answer, @"\bwithout scoring\b", "");
        if (omit is "finite_phrase" or "both") answer = Regex.Replace(answer, @"\band scored \d+ goals\b", "");
        var reason = Reason(answer, saved); Assert.Equal(expected, reason); Report("story_omit_" + omit, reason);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(2, null)]
    public async Task EachConfirmedFormGoalClauseAndItsRemainingTextValidate(int index, string? expected)
    {
        var saved = await Load("form"); var sentences = Regex.Split(saved.Answer, @"(?<=[.!?])\s+"); Assert.Equal(3, sentences.Length);
        var sentence = sentences[index]; var reason = Reason(sentence, saved); Assert.Equal(expected, reason); Report("form_sentence_" + index, reason);
        var withoutGoalPhrase = index < 2 ? Regex.Replace(sentence, @"\band scoring \d+ goals\b", "") : Regex.Replace(sentence, @"\bscored\b", "");
        var remainingReason = Reason(withoutGoalPhrase, saved); Assert.Null(remainingReason); Report("form_sentence_" + index + "_omit_goal_phrase", remainingReason);
    }
}
