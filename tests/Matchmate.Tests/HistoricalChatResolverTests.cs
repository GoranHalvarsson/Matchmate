using System.Text.Json;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Xunit;

namespace Matchmate.Tests;

// REV-CHAT-2: F-HIST08 explicit fixture references and F-HIST09 sourced aggregate gerunds.
public sealed class HistoricalChatResolverTests
{
    [Theory]
    [InlineData("What had happened in their earlier meetings?", null, null)]
    [InlineData("What had happened in their earlier meetings?", "southampton", null)]
    [InlineData("Tell me about their previous meetings.", "stoke-city", null)]
    [InlineData("What were their prior meetings like?", null, null)]
    [InlineData("What about their head-to-head meetings?", "southampton", null)]
    [InlineData("Which ground is listed for the home club?", null, "southampton")]
    [InlineData("What ground is listed for the home club?", "stoke-city", "southampton")]
    [InlineData("Which ground is listed for the away club?", null, "stoke-city")]
    [InlineData("And Stoke?", "southampton", "stoke-city")]
    [InlineData("Tell me more about them.", "southampton", "southampton")]
    public void ExplicitPairAndHomeReferencesDoNotLoseTheirSelectedFixture(string question, string? prior, string? club)
    {
        var resolved = HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, prior, "overview");
        Assert.Null(resolved.Clarification); Assert.Equal(club, resolved.ClubId);
    }

    [Theory]
    [InlineData("Dynamo ground?")]
    [InlineData("Which ground is listed for Dynamo?")]
    [InlineData("What ground is listed for Chelsea?")]
    [InlineData("What had happened in their earlier meetings with Chelsea?")]
    [InlineData("What were their previous meetings with Barcelona like?")]
    [InlineData("How were they doing?")]
    public void ExplicitReferenceHandlingPreservesForeignUnknownAndUnboundClubGuards(string question) => Assert.NotNull(
        HistoricalClubResolver.Resolve(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, question, null, "overview").Clarification);

    [Theory]
    [InlineData("Southampton had 2 earlier results in this sample, scoring 3 goals and conceding 2.", "form", "southampton")]
    [InlineData("Southampton had earlier results in this sample. They had 1 win and 1 draw, scoring 3 goals and conceding 2.", "form", "southampton")]
    [InlineData("Stoke City won this fixture, scoring 1 goal.", "fixture", null)]
    [InlineData("Southampton were unbeaten in their earlier results, scoring 3 goals.", "form", "southampton")]
    [InlineData("They had earlier results in this sample, scoring 3 goals.", "form", "southampton")]
    public void TrailingGerundRequiresOneBoundTeamAndItsExactCitedTotal(string answer, string citation, string? club)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", club);
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, citation), prepared).Answer);
    }

    [Theory]
    [InlineData("Southampton had earlier results, scoring 2 goals.", "form")]
    [InlineData("Southampton had earlier results; in this fixture, scoring 3 goals.", "form")]
    [InlineData("Southampton had earlier results, scoring 3 goals.", "fixture")]
    [InlineData("Stoke City had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton and Stoke City had earlier results, scoring 3 goals.", "form")]
    [InlineData("Crouch had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton had earlier results, scoring 3 goals from corners.", "form")]
    [InlineData("Southampton had earlier results, scoring 3 goals via set pieces.", "form")]
    [InlineData("Southampton had earlier results, scoring 3 goals through Crouch, not Arnautovic.", "form")]
    [InlineData("No scorers are supplied, but Crouch scored 1 goal.", "fixture")]
    [InlineData("Southampton pressed high without letting Stoke settle.", "fixture")]
    [InlineData("Southampton’s striker had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton's striker had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton’s Crouch had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton's Crouch had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton striker had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton Crouch had earlier results, scoring 3 goals.", "form")]
    [InlineData("Southampton player had earlier results, scoring 3 goals.", "form")]
    [InlineData("They striker had earlier results, scoring 3 goals.", "form")]
    public void GerundAllowanceDoesNotAuthorizeWrongTotalsScopesClubsPlayersOrMechanisms(string answer, string citation)
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, citation), prepared));
    }

    [Theory]
    [InlineData("Brighton & Hove Albion")]
    [InlineData("St John's & Town")]
    public void EscapedClubIdentityMustAppearInDecodedCitedSourceAnswer(string name)
    {
        var clubs = HistoricalChatFixtures.Clubs.Select(c => c.Id == "chelsea" ? c with { Name = name } : c).ToArray();
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), clubs, "overview", "southampton");
        Assert.Contains("\\u0026", prepared.Evidence.Single(e => e.Id == "form").Text);
        var answer = $"The earlier results include {name}.";
        Assert.Equal(answer, HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "form"), prepared).Answer);
        var uncited = Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply(answer, "fixture"), prepared));
        Assert.Equal("Uncited club identity.", uncited.Message);
    }

    [Fact]
    public void EvidenceMetadataCannotAuthorizeAnOtherwiseUncitedClub()
    {
        var prepared = HistoricalEvidence.Prepare(HistoricalChatFixtures.Fixture(), HistoricalChatFixtures.Clubs, "overview", "southampton");
        var fixture = new MatchChatEvidence("fixture", "fixture", JsonSerializer.Serialize(new { sourceAnswer = "The result is recorded.", detail = new { club = "Arsenal" } }), []);
        prepared = prepared with { Evidence = [fixture] };
        var failure = Assert.Throws<InvalidDataException>(() => HistoricalReplyValidation.Validate(HistoricalChatFixtures.Reply("Arsenal appear in this record.", "fixture"), prepared));
        Assert.Equal("Uncited club identity.", failure.Message);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("evaluation-interaction-final-sessions.json", 6)]
    [InlineData("evaluation-interaction-rev2-sessions.json", 5)]
    public async Task SavedFinalNarratorRepliesValidateIncludingTheBrentfordGerundAndEscapedChelseaOpponent(string filename, int expected)
    {
        var root = LocalPaths.FindRoot(); var model = await LeagueReferenceStore.Local(root).ReadAsync(default);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".azure", "historical-chat", filename)));
        var replies = capture.RootElement.GetProperty("outcomes").EnumerateArray().Where(item => item.GetProperty("rawNarratorReply").ValueKind == JsonValueKind.String).ToArray();
        Assert.Equal(expected, replies.Length);
        HistoricalChatContext? previous = null; string? prior = null;
        foreach (var item in replies)
        {
            var context = JsonSerializer.Deserialize<HistoricalChatContext>(item.GetProperty("context").GetRawText())!;
            if (previous != context) prior = null;
            var fixture = model.Match(context.SeasonKey, context.MatchKey); var clubs = model.Snapshot(context.SeasonKey, null).Clubs;
            Assert.Equal(context, HistoricalChatContext.From(fixture));
            var subject = HistoricalClubResolver.Resolve(fixture, clubs, item.GetProperty("question").GetString()!, prior, "overview");
            Assert.Null(subject.Clarification);
            var raw = item.GetProperty("rawNarratorReply").GetString()!;
            var validated = HistoricalReplyValidation.Validate(raw, HistoricalEvidence.Prepare(fixture, clubs, "overview", subject.ClubId));
            using var reply = JsonDocument.Parse(raw);
            Assert.Equal(reply.RootElement.GetProperty("answer").GetString(), validated.Answer);
            Assert.Equal(reply.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(id => id.GetString()), validated.Evidence.Select(e => e.Id));
            if (item.GetProperty("id").GetString() == "switch_o") { Assert.Equal("brentford", subject.ClubId); Assert.Contains("scoring 7 goals", validated.Answer); }
            Console.WriteLine($"Offline saved reply validated: {item.GetProperty("id").GetString()} / {subject.ClubId ?? "pair"}");
            previous = context; prior = subject.ClubId;
        }
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task SavedChelseaReplyCommitsItsTrustedTopicForTheNextClubSwitch()
    {
        var root = LocalPaths.FindRoot(); var data = await LeagueReferenceStore.Local(root).ReadAsync(default);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".azure", "historical-chat", "evaluation-interaction-rev2-sessions.json")));
        var saved = capture.RootElement.GetProperty("outcomes").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "form_o");
        var context = JsonSerializer.Deserialize<HistoricalChatContext>(saved.GetProperty("context").GetRawText())!;
        var router = new HistoricalChatSessionTests.Router("overview", "unsupported");
        var evidence = new HistoricalEvidence((s, m, _) => Task.FromResult(data.Match(s, m)), (s, i, c, _) => Task.FromResult(data.Snapshot(s, i, c)));
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), evidence, router, new SavedReply(saved.GetProperty("rawNarratorReply").GetString()!),
            Microsoft.Extensions.Options.Options.Create(new MatchChatOptions()));
        session.SetContext(context);
        Assert.Equal(MatchChatOutcome.Answered, (await session.AskAsync(context, saved.GetProperty("question").GetString()!)).Outcome);
        Assert.Equal("chelsea", Assert.Single(session.Turns).ClubId);
        await session.AskAsync(context, "And Brentford?");
        Assert.Equal("chelsea", router.Inputs[1].Topic!.ClubId);
        Assert.Equal(saved.GetProperty("question").GetString(), Assert.Single(router.Inputs[1].PreviousQuestions).Question);
    }

    private sealed class SavedReply(string reply) : IHistoricalChatModel
    {
        public bool Configured => true;
        public Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token) => Task.FromResult(new MatchModelReply(reply, []));
    }
}
