using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

public sealed class PlayerEvidenceVoiceTests
{
    private static readonly string Root = LocalPaths.FindRoot();
    private static readonly byte[] Source = File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json"));
    private static readonly PreparedPlayerEvidencePack Pack = JsonSerializer.Deserialize<PreparedPlayerEvidencePack>(Source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private static PlayerEvidenceObservation Observation(string id) => Pack.Observations.Single(o => o.Id == id);

    [Fact]
    public void CompleteExistingPackUsesThirtyExactReviewedPairsWithoutChangingSourceOrInventingNumbers()
    {
        Assert.Equal(57, Pack.Observations.Length); Assert.Equal(30, Pack.Observations.Select(o => (o.Text, o.Qualification)).Distinct().Count());
        Assert.Equal(27, Pack.Observations.Select(o => o.PersonId).Distinct().Count());
        var before = JsonSerializer.Serialize(Pack);
        foreach (var original in Pack.Observations)
        {
            var note = PlayerEvidenceVoice.Display(original.Text, original.Qualification);
            Assert.NotEqual(original.Qualification, note.Qualification); // A fallback would silently leave a row unreviewed.
            Assert.False(string.IsNullOrWhiteSpace(note.Text)); Assert.False(string.IsNullOrWhiteSpace(note.Qualification));
            foreach (var internalPhrase in new[] { "Do not ", "wording retained", "preserve that", "preserve attributed", "ASR", "existing exact observation", "remains unchanged" })
                Assert.DoesNotContain(internalPhrase, note.Text + " " + note.Qualification, StringComparison.OrdinalIgnoreCase);
            var supplied = MatchReplyValidation.Numbers(original.Text + " " + original.Qualification).ToHashSet(StringComparer.Ordinal);
            Assert.All(MatchReplyValidation.Numbers(note.Text + " " + note.Qualification), number => Assert.Contains(number, supplied));
        }
        Assert.Equal(before, JsonSerializer.Serialize(Pack));
        Assert.Equal(Source, File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json")));
    }

    [Fact]
    public void AllFiftySevenNotesFitWholeInTheSourceAnswerAndKeepRawEvidenceExact()
    {
        foreach (var original in Pack.Observations)
        {
            var person = Pack.Roster.Single(p => p.PersonId == original.PersonId);
            var bundle = Bundle(person.PersonId, person.Name, original.Text, original.Qualification);
            var before = JsonSerializer.Serialize(bundle.Items); var result = Format(bundle);
            var display = PlayerEvidenceVoice.Display(original.Text, original.Qualification);
            Assert.StartsWith(person.Name, result.Answer); Assert.Contains(display.Text, result.Answer); Assert.Contains(display.Qualification, result.Answer);
            Assert.InRange(result.Answer.Length, 1, MatchChatLimits.AnswerChars); Assert.Equal(before, JsonSerializer.Serialize(bundle.Items));
            Assert.Equal(bundle.Items.Select(i => i.Text), result.Evidence.Select(e => e.Text));
            using var evidence = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "player_involvement").Text);
            var raw = Assert.Single(evidence.RootElement.GetProperty("reviewedBrief").GetProperty("observations").EnumerateArray());
            Assert.Equal(original.Text, raw.GetProperty("text").GetString()); Assert.Equal(original.Qualification, raw.GetProperty("qualification").GetString());
        }
    }

    [Theory] [InlineData("text")] [InlineData("qualification")] [InlineData("text-space")] [InlineData("qualification-space")]
    [InlineData("case")] [InlineData("mixed-known-pair")] [InlineData("both-unknown")]
    public void ChangedOrUnknownPairReturnsBothOriginalInputsVerbatim(string change)
    {
        foreach (var original in Pack.Observations.DistinctBy(o => (o.Text, o.Qualification)))
        {
            var text = original.Text; var qualification = original.Qualification;
            switch (change)
            {
                case "text": text += " Changed."; break;
                case "qualification": qualification += " Changed."; break;
                case "text-space": text += " "; break;
                case "qualification-space": qualification += " "; break;
                case "case": text = text.ToUpperInvariant(); break;
                case "mixed-known-pair": qualification = Pack.Observations.First(o => o.Qualification != original.Qualification).Qualification; break;
                default: text = "Unknown observation."; qualification = "Unknown caveat."; break;
            }
            var result = PlayerEvidenceVoice.Display(text, qualification);
            Assert.Equal(text, result.Text); Assert.Equal(qualification, result.Qualification);
        }
    }

    [Fact]
    public void ParticipationOnlyExistingContextAndUnusedBenchPlayersDoNotGainNotes()
    {
        using var review = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data/match-enrichment/brentford-chelsea-2026-09-18-player-review-v2.json")));
        var participants = review.RootElement.GetProperty("participants").EnumerateArray().Select(p => p.GetProperty("personId").GetString()!).ToArray();
        var bench = review.RootElement.GetProperty("unusedBench").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Equal(31, participants.Length); Assert.Equal(9, bench.Length);
        var withoutNotes = participants.Where(id => !Pack.Observations.Any(o => o.PersonId == id)).ToArray(); Assert.Equal(4, withoutNotes.Length);
        Assert.Contains("person-pl-reference-brentford-ajer", withoutNotes); Assert.Contains("person-pl-reference-brentford-hickey", withoutNotes);
        Assert.Contains("person-pl-reference-brentford-carvalho", withoutNotes); Assert.Contains("person-pl-reference-chelsea-acheampong", withoutNotes);
        foreach (var id in withoutNotes.Concat(bench))
        {
            Assert.DoesNotContain(Pack.Observations, o => o.PersonId == id);
            var person = Pack.Roster.Single(p => p.PersonId == id); var result = Format(Bundle(id, person.Name));
            Assert.StartsWith(person.Name, result.Answer); Assert.DoesNotContain("One reviewed passage", result.Answer);
            Assert.Contains("nothing about whether they played", result.Answer);
        }
    }

    [Fact]
    public void RecognizedNoteStillCannotLeakFromForeignPlayerOrASecondNote()
    {
        var original = Observation("dw-chance-01"); var display = PlayerEvidenceVoice.Display(original.Text, original.Qualification);
        var foreign = Format(Bundle("selected-player", "Selected Player", original.Text, original.Qualification, "foreign-player"));
        Assert.DoesNotContain(display.Text, foreign.Answer); Assert.DoesNotContain(display.Qualification, foreign.Answer);
        var first = Format(Bundle(original.PersonId, "Danny Welbeck", original.Text, original.Qualification, second: Observation("dw-run-point-01")));
        Assert.Contains(display.Text, first.Answer); Assert.Contains(display.Qualification, first.Answer);
        var second = PlayerEvidenceVoice.Display(Observation("dw-run-point-01").Text, Observation("dw-run-point-01").Qualification);
        Assert.DoesNotContain(second.Text, first.Answer); Assert.DoesNotContain(second.Qualification, first.Answer);
    }

    [Fact]
    public void ChangedOverbudgetCaveatCannotBorrowRecognizedShortCopyOrBeSevered()
    {
        var original = Observation("dw-chance-01"); var qualification = original.Qualification + new string('x', MatchChatLimits.AnswerChars);
        var result = Format(Bundle(original.PersonId, "Danny Welbeck", original.Text, qualification));
        Assert.StartsWith("Danny Welbeck", result.Answer); Assert.InRange(result.Answer.Length, 1, MatchChatLimits.AnswerChars);
        Assert.DoesNotContain(original.Text, result.Answer); Assert.DoesNotContain("One reviewed passage", result.Answer);
        Assert.DoesNotContain(PlayerEvidenceVoice.Display(original.Text, original.Qualification).Text, result.Answer);
        using var raw = JsonDocument.Parse(result.Evidence.Single(e => e.Id == "player_involvement").Text);
        Assert.Equal(qualification, raw.RootElement.GetProperty("reviewedBrief").GetProperty("observations")[0].GetProperty("qualification").GetString());
    }

    [Fact]
    public void MaterialCaveatsRemainReaderFacingAndExplicit()
    {
        var cases = new Dictionary<string, string[]> {
            ["dw-chance-01"] = ["ninth or tenth minute", "one chance", "not a credited goal assist", "tally of chances", "performance verdict", "don't confirm every contact"],
            ["dw-run-point-01"] = ["one instant", "run and offside are unconfirmed", "doesn't prove a successful carry or shot", "how fast or far", "repeat"],
            ["dw-attacking-presence-01"] = ["one instant", "who a pass was meant for", "whether a pass was missed", "tactical job", "opportunity was lost", "repeated view"],
            ["rp-13-chelsea-martinez"] = ["Chelsea names Chavarria", "Brentford names Neto", "same move", "can't safely be counted as separate attempts", "isn't independently settled", "no intended role or measured pass length", "transcript isn't independent confirmation"],
            ["rp-12-chelsea-neto"] = ["Brentford names Neto", "involving Chavarria", "same attack", "doesn't establish two attempts", "independently settle"],
            ["rp-08-brentford-janelt"] = ["doesn't name who blocked"],
            ["rp-17-brentford-sangare"] = ["nearby opponent", "doesn't say who deflected"],
            ["rp-16-brentford-schuster"] = ["can't be sure", "same corner", "differently worded, untimed", "reaction speed"],
            ["rp-22-chelsea-martinez"] = ["Chelsea's account", "doesn't independently confirm", "illustrated replay cannot verify"],
            ["vp-quenda-01"] = ["camera cut doesn't link Quenda", "preceding wide delivery"],
            ["vp-estevao-01"] = ["not who took a late corner", "earlier contact"],
            ["vp-lacroix-01"] = ["one instant", "without establishing the preceding header, contact or outcome", "+5 marker refers to added time, not the time of a contact"]
        };
        foreach (var (id, required) in cases)
        {
            var original = Observation(id); var note = PlayerEvidenceVoice.Display(original.Text, original.Qualification);
            foreach (var phrase in required) Assert.Contains(phrase, note.Qualification, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static MatchWorkflowBundle Bundle(string id, string name, string? text = null, string? qualification = null, string? involvementId = null, PlayerEvidenceObservation? second = null)
    {
        var observations = new List<object>();
        if (text is not null) observations.Add(new { text, qualification });
        if (second is not null) observations.Add(new { text = second.Text, qualification = second.Qualification });
        MatchWorkflowItem Item(string tool, object data) => new(tool, tool, JsonSerializer.Serialize(data), []);
        MatchWorkflowItem[] items = [
            Item("current_player", new { selectedPlayer = name, availability = "missing", method = "unknown", xMetres = (string?)null, yMetres = (string?)null }),
            Item("player_involvement", new { playerId = involvementId ?? id, player = name, team = "Reported Team", reviewedBrief = new { observations } }),
            Item("coverage", new { origin = "Recorded source observations and estimates.", gap = true })
        ];
        var fixture = SharedFootballTests.Fixture(); var query = SharedFootballTests.Query(fixture, 0) with { SubjectId = id };
        return new("selected_player_context", MatchWorkflowRegistry.Version, new(query, 1), MatchWorkflowTemporalMode.ReplayAtCursor, "replay", items.ToImmutableArray(), [], [], [], []);
    }
    private static (string Answer, MatchChatEvidence[] Evidence) Format(MatchWorkflowBundle bundle)
        => ((string, MatchChatEvidence[]))typeof(MatchSourceQuestions).GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bundle])!;
}
