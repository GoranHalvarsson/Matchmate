using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatCoventryGrammarTests
{
    // REV-COV-01: the current two replies, not replacements or later model attempts.
    private static async Task<(string Raw, HistoricalPrepared Prepared)> Captured(string target)
    {
        var root = LocalPaths.FindRoot();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, ".azure", "historical-coventry-fix-20261024", target + "-rejected.json"));
        Assert.Equal(target == "newstory" ? "234c598d316208a4c94e1ebddf4074efdbdc291fc4b05c745c2fb0682ff00455" : "b97d9d4f3051a110b32e3df1b2c3264f244c4c056577aeace7968e37eeeddd35",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var saved = JsonDocument.Parse(bytes);
        Assert.Equal("AggregateSubjectUnbound", saved.RootElement.GetProperty("closedRejectionReason").GetString());
        var model = await LeagueReferenceStore.Local(root).ReadAsync(default);
        var fixture = model.Match("2026-27", "coventry:brighton");
        Assert.Equal("2026-09-13", fixture.Match.Date);
        return (saved.RootElement.GetProperty("rawModelReply").GetString()!,
            HistoricalEvidence.Prepare(fixture, model.Snapshot("2026-27", null).Clubs, target == "newstory" ? "overview" : "form", null));
    }

    [Theory, Trait("RequiresLocalData", "true"), Trait("CoventryRed", "true")]
    [InlineData("newstory")][InlineData("form")]
    public async Task BothExactCurrentRepliesValidateUnchanged(string target)
    {
        var (raw, prepared) = await Captured(target);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(document.RootElement.GetProperty("answer").GetString(), HistoricalReplyValidation.Validate(raw, prepared).Answer);
    }

    [Theory, Trait("RequiresLocalData", "true"), Trait("CoventryRed", "true")]
    [InlineData("newstory", 1)][InlineData("newstory", 2)][InlineData("newstory", 4)]
    [InlineData("form", 0)][InlineData("form", 1)][InlineData("form", 3)]
    public async Task EachOfTheSixActualClausesValidatesWithItsOriginalCitations(string target, int index)
    {
        var (raw, prepared) = await Captured(target);
        using var document = JsonDocument.Parse(raw);
        var sentence = Regex.Split(document.RootElement.GetProperty("answer").GetString()!, @"(?<=[.!?])\s+|\n")[index];
        var ids = document.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Equal(sentence, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(sentence, ids), prepared).Answer);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData(false)][InlineData(true)]
    public async Task TheIndependentTableTailNeedsItsCitedTeamTupleEvenWhenEveryNumberIsAvailable(bool omitTable)
    {
        var (raw, prepared) = await Captured("newstory");
        using var document = JsonDocument.Parse(raw);
        var sentence = Regex.Split(document.RootElement.GetProperty("answer").GetString()!, @"(?<=[.!?])\s+|\n")[2];
        // The wrong rank belongs to the other selected club and is therefore still an allowed numeric token.
        if (!omitTable) sentence = sentence.Replace("4th with", "20th with", StringComparison.Ordinal);
        var ids = omitTable ? new[] { "fixture", "form" } : ["fixture", "form", "table"];
        var probe = HistoricalChatFixtures.Reply(sentence, ids);
        if (!omitTable) MatchReplyValidation.Validate(probe, prepared.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal));
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(probe, prepared));
    }

    private static HistoricalPrepared Prepare(bool swap = false, bool equal = false, string? selected = null)
    {
        var fixture = HistoricalChatFixtures.Fixture();
        LeagueMatch[] Form(string club, bool zero) => [
            HistoricalChatFixtures.Game(club, "chelsea", "2017-05-01", zero ? 0 : 4, zero ? 1 : 0),
            HistoricalChatFixtures.Game("arsenal", club, "2017-05-08", zero ? 2 : 4, zero ? 0 : 3),
            HistoricalChatFixtures.Game(club, "arsenal", "2017-05-15", zero ? 0 : 1, zero ? 2 : 1)];
        fixture = fixture with { HomePreMatchForm = Form(fixture.Home.Id, !swap || equal), AwayPreMatchForm = Form(fixture.Away.Id, swap || equal) };
        return HistoricalEvidence.Prepare(fixture, HistoricalChatFixtures.Clubs, "overview", selected);
    }
    private static string Words(string text) => text.Replace("HOME", HistoricalChatFixtures.Home.Name, StringComparison.Ordinal).Replace("AWAY", HistoricalChatFixtures.Away.Name, StringComparison.Ordinal);
    private static void Accept(string text, HistoricalPrepared? prepared = null, params string[] ids)
        => Assert.Equal(Words(text), HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(Words(text), ids.Length == 0 ? ["form"] : ids), prepared ?? Prepare()).Answer);
    private static void Reject(string text, HistoricalPrepared? prepared = null, params string[] ids)
        => Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(Words(text), ids.Length == 0 ? ["form"] : ids), prepared ?? Prepare()));

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ClosedStatisticalSubjectsWorkForEitherSelectedTeam(bool swap)
    {
        var prepared = Prepare(swap); var zero = swap ? "AWAY" : "HOME"; var eight = swap ? "HOME" : "AWAY";
        Accept($"The result extended a difficult start for {zero}: in their 3 earlier sampled results, they had 0 wins and scored 0 goals.", prepared);
        Accept($"Before the match, {eight} had the stronger recent form: in the 3 earlier results in the sample, they recorded 1 win, 1 draw and 1 loss, earning 4 points and scoring 8 goals.", prepared);
        Accept($"{zero} lost all 3 of their earlier results, earned 0 points and scored 0 goals.", prepared);
        Accept($"{eight} had been more productive before the match, scoring 8 goals in their 3 earlier sampled results.", prepared);
        Accept($"That contrast makes {eight}’s better results and greater scoring output stand out, though the samples cover only those earlier matches.", prepared);
    }

    [Theory]
    [InlineData("The result extended a difficult start for HOME: in their 3 earlier sampled results, they had 0 wins and scored 8 goals.")]
    [InlineData("Before the match, HOME had the stronger recent form: in the 3 earlier results in the sample, they recorded 1 win, 1 draw and 1 loss, earning 4 points and scoring 8 goals.")]
    [InlineData("HOME lost all 3 of their earlier results, earned 0 points and scored 5 goals.")]
    [InlineData("AWAY had been more productive before the match, scoring 8 goals in this fixture.")]
    [InlineData("HOME lost all 5 of their earlier results, earned 0 points and scored 0 goals.")]
    [InlineData("AWAY had been more productive before the match, scoring 8 goals in their 5 earlier sampled results.")]
    public void NewGrammarKeepsTheExactTeamCountRecordSampleAndScope(string text) => Reject(text);

    [Theory]
    [InlineData("fixture")][InlineData("table")]
    public void TheNewStatisticalGrammarStillNeedsItsOwnFormCitation(string id)
        => Reject("HOME lost all 3 of their earlier results, earned 0 points and scored 0 goals.", null, id);

    [Theory]
    [InlineData("The result mentioned HOME: in their 3 earlier sampled results, they had 0 wins and scored 0 goals.")]
    [InlineData("The result extended a difficult start for HOME's striker: in their 3 earlier sampled results, they had 0 wins and scored 0 goals.")]
    [InlineData("The result extended a difficult start for HOME and AWAY: in their 3 earlier sampled results, they had 0 wins and scored 0 goals.")]
    [InlineData("Before the match, HOME's Crouch had the stronger recent form: in the 3 earlier results in the sample, they recorded 1 win, 1 draw and 1 loss, earning 4 points and scoring 8 goals.")]
    [InlineData("Before the match, HOME and AWAY had the stronger recent form: in the 3 earlier results in the sample, they recorded 1 win, 1 draw and 1 loss, earning 4 points and scoring 8 goals.")]
    [InlineData("Before the match, HOME had the stronger recent form.\n\nThey had 0 wins and scored 0 goals in their earlier results.")]
    [InlineData("HOME had earlier results. They had 0 wins and scored 0 goals in their earlier results.")]
    [InlineData("HOME lost all 3 of their earlier results, celebrated 0 points and scored 0 goals.")]
    [InlineData("HOME lost all 3 of their earlier results, earned 0 points and their striker scored 0 goals.")]
    public void ArbitraryHeadingsPlayersPossessivesAndParagraphContextCannotEstablishTheSubject(string text) => Reject(text);

    [Theory]
    [InlineData(" through Crouch")][InlineData(" by Crouch")][InlineData(" from corners")]
    [InlineData(", according to Crouch")][InlineData(", and everyone celebrated")]
    [InlineData(", and this win came alongside a strong end-of-date table position: 1st with 4 points from 3 matches")]
    public void AProvedGoalPrefixDoesNotAuthorizeAnArbitraryTail(string tail)
        => Reject("AWAY had been more productive before the match, scoring 8 goals in their 3 earlier sampled results" + tail + ".");

    [Theory]
    [InlineData("HOME")][InlineData("AWAY's striker")][InlineData("Crouch")][InlineData("HOME and AWAY")]
    public void GreaterOutputNeedsTheCorrectUnambiguousTeam(string subject)
        => Reject($"That contrast makes {subject}'s better results and greater scoring output stand out, though the samples cover only those earlier matches.");

    [Fact]
    public void GreaterOutputNeedsBothCitedFactsAndStrictGreaterThan()
    {
        const string answer = "That contrast makes AWAY's better results and greater scoring output stand out, though the samples cover only those earlier matches.";
        Reject(answer, Prepare(equal: true));
        Reject(answer, Prepare(selected: HistoricalChatFixtures.Away.Id));
        Reject(answer, null, "fixture");
        Reject(answer.Replace("greater", "lower", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("The available evidence does not say how the goals were scored.")]
    [InlineData("The supplied evidence cannot identify who scored.")]
    public void HonestEvidenceLimitsAreAllowedButLeavePositiveUnsupportedClaimsVisible(string limit)
    {
        Accept(limit);
        Reject(limit[..^1] + ", but Crouch scored 1 goal.", null, "form", "fixture");
        Reject(limit + " HOME pressed high.");
    }
}
