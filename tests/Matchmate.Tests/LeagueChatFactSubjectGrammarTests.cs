using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatFactSubjectGrammarTests
{
    private const string F010 = "Before the first results arrive, what points total and league placing does our club have?";

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F010PreseasonCompoundFactsKeepCoventryAndZeroUnrankedEvidence()
    {
        var model = RawModel();
        var context = LeagueChatContext.From(model.Snapshot("2026-27", 0, "coventry"));
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync("club_context", context, F010, null, default);
        Evidence(result, context, model.Snapshot("2026-27", 0, "coventry"));
        Assert.Equal(1, reads);
        using var data = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal(0, data.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("facts").GetProperty("Rank").ValueKind);
    }

    public static IEnumerable<object[]> CompositionalFacts()
    {
        foreach (var facts in new[]
        {
            "points total and league placing", "games played and points tally", "wins and draws",
            "league position and recent form", "goal difference and goals for", "number of matches and total points",
            "played record and results", "current performance and league standing", "points and win draw loss record",
            "match count and goals against", "points total or league placing", "recent results and home record"
        }) yield return [facts];
    }

    [Theory, MemberData(nameof(CompositionalFacts))]
    public async Task CompleteFactConstituentsRemainSeparateFromExplicitAuxiliarySubjects(string facts)
    {
        var question = $"What {facts} does our club have here?";
        await Answer(question, Context(), "everton");
        await Answer($"What {facts} does Everton have here?", Context("bournemouth"), "everton", "named_club_context");
    }

    [Theory]
    [InlineData("Could you show me how many points and wins our club earned here?")]
    [InlineData("Tell me what games played and points total our club have here.")]
    [InlineData("Show what recent form and league position our club have here.")]
    [InlineData("Could you show me how many matches and points we have here?")]
    [InlineData("Tell me what wins and draws our club earned here.")]
    [InlineData("What points Arsenal earned here?")]
    [InlineData("Tell me what points Arsenal have earned here.")]
    [InlineData("Tell me what points our club has earned here.")]
    public Task LegacyEmbeddedFactQuestionsUseTheSameCompleteFactGrammar(string question)
        => Answer(question, Context(), question.Contains("Arsenal", StringComparison.Ordinal) ? "arsenal" : "everton",
            question.Contains("Arsenal", StringComparison.Ordinal) ? "named_club_context" : "club_context");

    [Theory]
    [InlineData("What does our points and played record look like here?")]
    [InlineData("What does our recent form and league position show here?")]
    [InlineData("What has our recent record meant for our standing here?")]
    [InlineData("What are our points total and league placing here?")]
    [InlineData("What's our points total and league placing here?")]
    [InlineData("Which is our recent form here?")]
    [InlineData("What is our recent results record at this cutoff?")]
    [InlineData("Which Everton league record is listed here?")]
    [InlineData("What position is our club in this table?")]
    public Task OwnedNominalsYieldTheirOwnerOnlyAfterEveryFactIsConsumed(string question)
        => Answer(question, Context(), "everton");

    [Theory]
    [InlineData("What does Everton's points tally show here?")]
    [InlineData("What does Everton'S points tally show here?")]
    [InlineData("What does Everton’s points tally show here?")]
    [InlineData("What does Everton’S points tally show here?")]
    public async Task F_LR11_001PossessiveCasingPreservesNamedOwnerAndTypedPayload(string question)
    {
        await Answer(question, Context("bournemouth"), "everton", "named_club_context");
        var router = new LeagueChatFixtures.Router { Workflow = "named_club_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = Context("bournemouth"); session.SetContext(context);
        await session.AskAsync(context, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question));
        Assert.Equal(question, Assert.Single(session.Turns).Question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome); Assert.Equal("everton", Assert.Single(session.Turns).ResolvedClubId);
        var request = Assert.Single(model.Requests);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(request.Messages.Where(message => message.Role == "user")).Content));
    }

    [Theory]
    [InlineData("What does Barcelona's points tally show here?")]
    [InlineData("What does Barcelona'S points tally show here?")]
    [InlineData("What does Barcelona’s points tally show here?")]
    [InlineData("What does Barcelona’S points tally show here?")]
    [InlineData("What does supportedclub'S points tally show here?")]
    [InlineData("What does contextclub’S points tally show here?")]
    [InlineData("What does Everton POSSESSIVE points tally show here?")]
    [InlineData("What does Everton'S POSSESSIVE points tally show here?")]
    [InlineData("What does Everton’S internalownershipstamp points tally show here?")]
    [InlineData("What does INTERNALOWNERSHIPSTAMP'S points tally show here?")]
    public Task F_LR11_001UnknownOrRawMarkerOwnershipCannotAcquireAuthority(string question)
        => CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity(question);

    [Theory]
    [InlineData("What points total does the club I have selected have here?")]
    [InlineData("What the club I have selected's points total does our club have here?")]
    public Task SelectorRelativeHaveKeepsItsLocalRoleInsideCompleteOwnersAndSubjects(string question)
        => Answer(question, Context(), "everton");

    [Theory]
    [InlineData("How many matches has our club completed here?")]
    [InlineData("How many points has our club collected from the games counted on this page?")]
    [InlineData("What have our last few results looked like by this stage?")]
    [InlineData("What is the points total attached to our row here?")]
    [InlineData("How many wins, draws and defeats are on our record here?")]
    [InlineData("Which results brought our team to the position shown on this page?")]
    [InlineData("What's our record here, including points rather than just our place?")]
    [InlineData("What results have our club put together by this stage of the season?")]
    [InlineData("Which outcome is our most recent result in this view?")]
    public Task ClosedPredicateAndComplementRolesKeepEveryClubOwner(string question)
        => Answer(question, Context(), "everton");

    [Theory]
    [InlineData("What Barcelona's points total and league placing does our club have here?")]
    [InlineData("What Barcelona points total does our club have here?")]
    [InlineData("What points Barcelona total does our club have here?")]
    [InlineData("What Arsenal's points and Barcelona's record does our club have here?")]
    [InlineData("What Arsenal's points and Everton's record does our club have here?")]
    [InlineData("What points total does Barcelona have here?")]
    [InlineData("What points total does our Barcelona club have here?")]
    [InlineData("What points total does our club have Barcelona?")]
    [InlineData("What points total does our club have and Barcelona?")]
    [InlineData("What points total does our club have here, and what form does Barcelona have here?")]
    [InlineData("What does Barcelona's points and played record look like here?")]
    [InlineData("What does our Barcelona club's recent form look like here?")]
    [InlineData("Tell me what points total Barcelona have here.")]
    [InlineData("Tell me what points Barcelona total our club have here.")]
    [InlineData("What points and league placing Barcelona does our club have here?")]
    [InlineData("What points total does Arsenal and Everton have here?")]
    [InlineData("What points total does Arsenal Town have here?")]
    [InlineData("What points total does Arsenal record have here?")]
    [InlineData("What points total does our Arsenal Town have here?")]
    [InlineData("What Arsenal's points total does our club have here, and has Bayern Munich won here?")]
    [InlineData("Tell me what points Barcelona have earned here.")]
    [InlineData("What the Barcelona club I have selected's points total does our club have here?")]
    [InlineData("What points total does the club I have Barcelona selected have here?")]
    [InlineData("What Barcelona league record is listed here?")]
    [InlineData("What Everton Barcelona league record is listed here?")]
    [InlineData("What has our recent record meant for Barcelona's standing here?")]
    [InlineData("What position is our Barcelona club in this table?")]
    [InlineData("What is our Barcelona recent results record at this cutoff?")]
    [InlineData("How many points has our club collected from Barcelona's games counted on this page?")]
    [InlineData("What have Barcelona's last few results looked like by this stage?")]
    [InlineData("What is the points total attached to our Barcelona row here?")]
    [InlineData("How many wins, draws and defeats are on our Barcelona record here?")]
    [InlineData("Which results brought Barcelona to the position shown on this page?")]
    [InlineData("What's our record here, including Barcelona points rather than just our place?")]
    [InlineData("Which outcome is our Barcelona club's most recent result in this view?")]
    [InlineData("Show our ground name and Barcelona town here.")]
    [InlineData("Show our Barcelona ground name and town here.")]
    [InlineData("Show our ground name and recent Barcelona town here.")]
    [InlineData("Show our ground name Barcelona and town here.")]
    [InlineData("Give me the ground name and town recorded as home for the Barcelona club I've selected.")]
    public async Task CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity(string question)
    {
        foreach (var workflow in new[] { "club_context", "named_club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync(workflow, Context(), question, "arsenal", default);
            Refused(result);
            Assert.Equal(1, reads);
        }
        var accepted = await LeagueChatFixtures.Executor().ExecuteContinuationAsync(Context("brighton"), question, new("club_context", "everton"), default);
        Refused(accepted);
    }

    [Theory]
    [InlineData("What points total does supportedclub have here?")]
    [InlineData("What supportedclub points total does our club have here?")]
    [InlineData("What supportedclub's points total does our club have here?")]
    [InlineData("How are supportedclub doing here?")]
    [InlineData("What points does contextclub have here?")]
    [InlineData("What possessive points total does our club have here?")]
    [InlineData("What points total does our club have possessive here?")]
    [InlineData("What Arsenal supportedclub points does our club have here?")]
    public Task RawPrivateMarkersNeverAcquireIdentityOrOwnerAuthority(string question)
        => CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity(question);

    [Theory]
    [InlineData("What points total and league placing does our club have here?")]
    [InlineData("Tell me what games played and points tally our club have here.")]
    [InlineData("What does our points and played record look like here?")]
    [InlineData("What points total does it have here?")]
    public async Task SharedFactAnalysisKeepsSelectedReferenceMissingAndAcceptedGlobalAuthority(string question)
    {
        await Answer(question, Context(), "everton");
        await Answer(question, Context(null), "arsenal", reference: "arsenal");
        var context = Context("brighton");
        var accepted = await LeagueChatFixtures.Executor().ExecuteContinuationAsync(context, question, new("club_context", "everton"), default);
        Evidence(accepted, context, LeagueChatFixtures.Snapshot(club: "everton"));
        foreach (var workflow in new[] { "club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, Context(null), question, null, default);
            Refused(result); Assert.Equal(0, reads);
        }
        var globalReads = 0;
        var global = await LeagueChatFixtures.Executor((_, _, _) => globalReads++)
            .ExecuteContinuationAsync(Context(), question, new("league_context", null), default);
        Refused(global); Assert.Equal(0, globalReads);
    }

    [Theory]
    [InlineData("Give me the ground name and town recorded as home for the club I've selected.", "everton")]
    [InlineData("Please show our ground name and town here.", "everton")]
    [InlineData("Tell me their stadium name and town here.", "arsenal")]
    [InlineData("Show Everton's ground name and town here.", "everton")]
    public async Task CompletePlaceFieldFacetsRetainTheirFullContextOrNamedOwner(string question, string subject)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", Context(), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(subject, result.ResolvedClubId);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        Assert.Contains("Test ground " + LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Club.Name, result.Evidence[0].Text);
        Assert.Contains("Test town", result.Evidence[0].Text);
    }

    [Theory]
    [InlineData("What points total and league placing does our club have at this moment?", MatchChatOutcome.Unavailable)]
    [InlineData("What points total and league placing does our club have in March?", MatchChatOutcome.Clarify)]
    [InlineData("What points total and league placing does our club have on 19 September 2026?", MatchChatOutcome.Clarify)]
    public async Task FactGrammarCannotOverrideTimeAuthority(string question, MatchChatOutcome outcome)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", Context(), question, null, default);
        Refused(result, outcome); Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ParsedFactsNeverReplaceTheOriginalTypedPayload()
    {
        const string question = "What points total and league placing does our club have here?";
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" };
        var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = Context(); session.SetContext(context);
        await session.AskAsync(context, question);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question));
        Assert.Equal(question, Assert.Single(session.Turns).Question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome);
        Assert.Single(model.Requests);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task R3ExactWrappedOwnerCannotReleaseSelectedFactsForAnUnknownClub()
    {
        var model = RawModel(); var context = LeagueChatContext.From(model.Snapshot("2026-27", 17, "bournemouth"));
        const string unknown = "Now, show me, for Barcelona in this selected season, what points and results have accumulated?";
        const string known = "Now, show me, for Everton in this selected season, what points and results have accumulated?";
        var reads = 0;
        LeagueWorkflowExecutor Executor() => new((season, index, club, _) => { reads++; return Task.FromResult(model.Snapshot(season, index, club)); });
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, unknown));
        Refused(await Executor().ExecuteAsync("club_context", context, unknown, null, default)); Assert.Equal(1, reads);
        Evidence(await Executor().ExecuteAsync("club_context", context, known, null, default), context, model.Snapshot("2026-27", 17, "everton"));
    }

    [Theory]
    [InlineData("Now, show me, for Everton in this selected season, what points and results have accumulated?")]
    [InlineData("Please show me, for Everton in this season, what points and results have accumulated?")]
    [InlineData("Could you please tell us, for Everton in this season, what points and results have accumulated?")]
    [InlineData("Now, walk me through, for Everton in this season, what points and results have accumulated?")]
    [InlineData("On 20 September 2026, for Everton in this season, what points and results have accumulated?")]
    [InlineData("For Everton, recap our points and recent results.")]
    [InlineData("For Everton, lay out our points and results.")]
    [InlineData("For Everton, sum up our league record.")]
    [InlineData("Leave that topic for a moment and give me Everton's position and points here.")]
    public async Task R3BoundedRequestPreludesExposeTheSameWholeNamedOwnerWithoutChangingPayload(string question)
    {
        await Answer(question, Context("bournemouth"), "everton");
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model);
        var context = Context("bournemouth"); session.SetContext(context); await session.AskAsync(context, question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome); Assert.Equal("everton", Assert.Single(session.Turns).ResolvedClubId);
        Assert.Equal(Encoding.UTF8.GetBytes(question), Encoding.UTF8.GetBytes(Assert.Single(router.Inputs).Question));
        Assert.Equal(question, Assert.Single(session.Turns).Question);
        Assert.Equal(question, Assert.Single(Assert.Single(model.Requests).Messages.Where(message => message.Role == "user")).Content);
    }

    [Theory]
    [InlineData("Now, show me, for Barcelona in this selected season, what points and results have accumulated?")]
    [InlineData("Please show me, for Barcelona in this season, what points and results have accumulated?")]
    [InlineData("Now, walk me through, for Barcelona in this season, what points and results have accumulated?")]
    [InlineData("Now, show me, for Arsenal Town in this season, what points and results have accumulated?")]
    [InlineData("Now, show me, for Everton Barcelona in this season, what points and results have accumulated?")]
    [InlineData("Now, show me, for Arsenal and Everton in this season, what points and results have accumulated?")]
    [InlineData("Now, show me, for supportedclub in this season, what points and results have accumulated?")]
    [InlineData("Now, show me, for Everton in this season Barcelona, what points and results have accumulated?")]
    [InlineData("For 20 September 2026 Barcelona, what points and results have accumulated?")]
    [InlineData("For Barcelona 20 September 2026, what points and results have accumulated?")]
    [InlineData("Now, show me, for 2026-09-20 Everton, what points and results have accumulated?")]
    [InlineData("Now, show me, for Barcelona, where are we placed here?")]
    [InlineData("Could you unpack, for Barcelona in this selected season, what points and results have accumulated?")]
    [InlineData("Could you unpack, for Everton in this selected season, what points and results have accumulated?")]
    [InlineData("On 20 September 2026, for Barcelona in this season, what points and results have accumulated?")]
    [InlineData("For Barcelona, recap our points and recent results.")]
    [InlineData("For Barcelona, lay out our points and results.")]
    [InlineData("For Barcelona, sum up our league record.")]
    [InlineData("Leave that topic for Barcelona and give me our points here.")]
    [InlineData("Leave that topic for a moment Barcelona and give me our points here.")]
    [InlineData("For the Barcelona club under discussion, can you run through its results and points at this view?")]
    [InlineData("For the club under Barcelona discussion, can you run through its results and points at this view?")]
    public Task R3ComposedPrefixesRetainEveryUnknownMixedAndPartialIdentity(string question)
        => CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity(question);

    [Theory]
    [InlineData("Now, show me, for our club in this selected season, what points and results have accumulated?")]
    [InlineData("Now, tell us, for it in this view, what points and results have accumulated?")]
    [InlineData("Now, show me, for 20 September 2026, what points does our club have here?")]
    [InlineData("Now, show me, for the date currently chosen, what points does our club have here?")]
    [InlineData("For the club under discussion, can you run through its results and points at this view?")]
    public Task R3OwnerAndCalendarScopesKeepUiReferenceAcceptedAndGlobalNullAuthority(string question)
        => SharedFactAnalysisKeepsSelectedReferenceMissingAndAcceptedGlobalAuthority(question);

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task R3ExistingCalendarAndYearPrefixesRemainTemporalScopesWithoutClubAuthority()
    {
        var model = RawModel();
        LeagueWorkflowExecutor Executor() => new((season, index, club, _) => Task.FromResult(model.Snapshot(season, index, club)));
        var named = LeagueChatContext.From(model.Snapshot("2026-27", 17, "bournemouth"));
        Evidence(await Executor().ExecuteAsync("named_club_context", named, "For 20 September 2026, how has Arsenal won here?", null, default), named, model.Snapshot("2026-27", 17, "arsenal"));
        var selected = LeagueChatContext.From(model.Snapshot("2026-27", 17, "newcastle"));
        Evidence(await Executor().ExecuteAsync("club_context", selected, "For 20 September 2026, how many points do we have and where are we placed?", null, default), selected, model.Snapshot("2026-27", 17, "newcastle"));
        var year = LeagueChatContext.From(model.Snapshot("2015-16", 46, "watford"));
        Evidence(await Executor().ExecuteAsync("club_context", year, "For 2015, give me the points and league position of the club I’ve chosen.", null, default), year, model.Snapshot("2015-16", 46, "watford"));
        var global = LeagueChatContext.From(model.Snapshot("2026-27", 17, null));
        var result = await Executor().ExecuteAsync("league_context", global, "For 2026-09-20, show the league leaders and the rest of the table.", null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Null(result.ResolvedClubId);
        Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        using var data = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal(global.SeasonKey, data.RootElement.GetProperty("season").GetString()); Assert.Equal(global.Date, data.RootElement.GetProperty("date").GetString());
        Assert.Equal(20, data.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task R2PreviouslyFrozenNamedPreseasonAndGroundRequestsKeepTheirExactDomainEvidence()
    {
        var model = RawModel();
        LeagueWorkflowExecutor Executor() => new((season, index, club, _) => Task.FromResult(model.Snapshot(season, index, club)));
        var initial = LeagueChatContext.From(model.Snapshot("2026-27", 0, "ipswich"));
        Evidence(await Executor().ExecuteAsync("club_context", initial, "Before any fixtures have been counted, what points and match record does our club start with?", null, default), initial, model.Snapshot("2026-27", 0, "ipswich"));
        var named = LeagueChatContext.From(model.Snapshot("2015-16", 42, "chelsea"));
        Evidence(await Executor().ExecuteAsync("named_club_context", named, "For Watford in the selected 2015-16 season, what points and results have accumulated?", null, default), named, model.Snapshot("2015-16", 42, "watford"));
        var ground = LeagueChatContext.From(model.Snapshot("2016-17", 29, "southampton"));
        GroundEvidence(await Executor().ExecuteAsync("ground_context", ground, "What should I call our home ground, and what town is attached to it in the record?", null, default), model.Snapshot("2016-17", 29, "southampton"));
    }

    public static IEnumerable<object[]> R2CountNominals()
    {
        foreach (var count in new[] { "match", "matches", "game", "games", "point", "points", "win", "wins", "draw", "draws", "loss", "losses", "defeat", "defeats", "goal", "goals" })
            yield return [count + (count.StartsWith("m", StringComparison.Ordinal) || count.StartsWith("g", StringComparison.Ordinal) ? " record" : " summary")];
    }
    [Theory, MemberData(nameof(R2CountNominals))]
    public async Task R2ClosedCountModifiersBelongToTheirCompleteFactualHead(string nominal)
    {
        await Answer($"What points and {nominal} does our club start with here?", Context(), "everton");
        await CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity($"What points and Barcelona {nominal} does our club start with here?");
    }

    [Theory]
    [InlineData("For Everton in this season, what points and results have accumulated?")]
    [InlineData("For Everton in this season, what points and results accumulated here?")]
    [InlineData("What points does Everton accumulate here?")]
    [InlineData("What points has Everton accumulated here?")]
    [InlineData("What points is Everton accumulating here?")]
    [InlineData("What points has Everton been accumulating here?")]
    [InlineData("For Everton in this view, what points tally accumulates here?")]
    public Task R2AccumulationTensesRetainTheWholeNamedOwner(string question)
        => Answer(question, Context("bournemouth"), "everton", "named_club_context");

    [Theory]
    [InlineData("For our club in this season, what points and results have accumulated?")]
    [InlineData("For it in this view, what points and results have accumulated?")]
    [InlineData("For their club in this season, what points and results have accumulated?")]
    [InlineData("For me in this season, what points does our club have here?")]
    [InlineData("For the selected season, what points does our club have here?")]
    public Task R2LeadingContextOwnersKeepTheExistingAuthorityPrecedence(string question)
        => SharedFactAnalysisKeepsSelectedReferenceMissingAndAcceptedGlobalAuthority(question);

    [Theory]
    [InlineData("For Barcelona in this season, what points and results have accumulated?")]
    [InlineData("For Everton Barcelona in this season, what points and results have accumulated?")]
    [InlineData("For Arsenal Town in this season, what points and results have accumulated?")]
    [InlineData("For Arsenal and Everton in this season, what points and results have accumulated?")]
    [InlineData("For Everton in the selected Barcelona season, what points and results have accumulated?")]
    [InlineData("For Everton in this season Barcelona, what points and results have accumulated?")]
    [InlineData("For supportedclub in this season, what points and results have accumulated?")]
    [InlineData("For Barcelona, what ground is listed here?")]
    [InlineData("What town is attached to Barcelona in the record?")]
    [InlineData("What town is attached to Everton Barcelona in the record?")]
    [InlineData("What town is attached to Arsenal Town in the record?")]
    [InlineData("What town is attached to our Arsenal Town in the record?")]
    [InlineData("What town is attached to our Everton Town in the record?")]
    [InlineData("What town is attached to Everton record in the record?")]
    [InlineData("What town is attached to our Barcelona club in the record?")]
    [InlineData("What town is attached to supportedclub in the record?")]
    [InlineData("What town is attached to Barcelona's ground in the record?")]
    [InlineData("What town is attached to our club in Barcelona's record?")]
    public Task R2LeadingAndPassiveOwnerSpansNeverDiscardUnknownOrMixedTokens(string question)
        => CompleteFactSubjectAndPredicateSpansRetainUnknownOrMixedIdentity(question);

    [Theory]
    [InlineData("What should I call our home ground, and what town is attached to it in the record?")]
    [InlineData("What town is attached to it in the record?")]
    [InlineData("What town is attached to our ground in the record?")]
    [InlineData("Which town is associated with our club in this season?")]
    public async Task R2ImmediatePlaceAssociationRetainsSelectedReferenceAcceptedAndMissingAuthority(string question)
    {
        GroundEvidence(await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", Context(), question, null, default), LeagueChatFixtures.Snapshot());
        GroundEvidence(await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", Context(null), question, "arsenal", default), LeagueChatFixtures.Snapshot(club: "arsenal"));
        GroundEvidence(await LeagueChatFixtures.Executor().ExecuteContinuationAsync(Context("brighton"), question, new("ground_context", "arsenal"), default), LeagueChatFixtures.Snapshot(club: "arsenal"));
        var reads = 0;
        Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("ground_context", Context(null), question, null, default)); Assert.Equal(0, reads);
        Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteContinuationAsync(Context(), question, new("league_context", null), default)); Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("What town is attached to Everton in the record?", "everton")]
    [InlineData("What town is attached to Everton's ground in the record?", "everton")]
    [InlineData("What town is attached to Ipswich Town's ground in the record?", "ipswich")]
    [InlineData("What town is attached to Coventry City's ground in the record?", "coventry")]
    public async Task R2ImmediatePlaceAssociationPreservesWholeNamedAndOwnedIdentities(string question, string subject)
        => GroundEvidence(await LeagueChatFixtures.Executor().ExecuteAsync("named_ground_context", Context("bournemouth"), question, null, default), LeagueChatFixtures.Snapshot(club: subject));

    [Theory]
    [InlineData("For Barcelona, show our ground.", null)]
    [InlineData("For Everton, show our ground.", "everton")]
    [InlineData("For Arsenal Town, show our ground.", null)]
    [InlineData("For Everton Barcelona, show our ground.", null)]
    [InlineData("For Everton, please get our ground.", "everton")]
    [InlineData("For Everton, give me our ground.", "everton")]
    [InlineData("For Barcelona, give me our ground.", null)]
    public async Task R2LeadingImperativeOwnersNeverBorrowTheSelectedGround(string question, string? subject)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("ground_context", Context("bournemouth"), question, null, default);
        if (subject is null) Refused(result); else GroundEvidence(result, LeagueChatFixtures.Snapshot(club: subject));
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task R2NewNominalAndLeadingOwnerFormsBindAllSelectedSeasonMembersOnly()
    {
        var model = RawModel(); var entries = 0; var positive = 0; var absent = 0; var foreign = 0; var partial = 0;
        var seasons = model.Seasons(); var union = seasons.SelectMany(season => model.Snapshot(season.SeasonKey, null).Clubs).DistinctBy(club => club.Id).ToArray();
        foreach (var season in seasons)
        {
            var clubs = model.Snapshot(season.SeasonKey, null).Clubs;
            LeagueWorkflowExecutor Executor() => new((key, index, club, _) => Task.FromResult(model.Snapshot(key, index, club)));
            string[] Questions(string name) => [$"What points and match record does {name} start with here?", $"For {name} in the selected {season.SeasonKey} season, what points and results have accumulated?"];
            foreach (var club in clubs)
            {
                var selected = clubs.First(candidate => candidate.Id != club.Id);
                foreach (var (question, index) in Questions(club.Name).Zip(new int?[] { 0, null }))
                {
                    var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, index, selected.Id));
                    Evidence(await Executor().ExecuteAsync("named_club_context", context, question, null, default), context, model.Snapshot(season.SeasonKey, index, club.Id)); positive++;
                }
                entries++;
            }
            var ui = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, clubs[0].Id));
            foreach (var name in union.Where(club => !clubs.Any(current => current.Id == club.Id)).Select(club => club.Name))
                foreach (var question in Questions(name)) { Refused(await Executor().ExecuteAsync("club_context", ui, question, null, default)); absent++; }
            foreach (var name in new[] { "Barcelona", "Bayern Munich", "Real Madrid" })
                foreach (var question in Questions(name)) { Refused(await Executor().ExecuteAsync("club_context", ui, question, null, default)); foreign++; }
            foreach (var name in clubs.Select(club => Regex.Replace(club.Name, @" (City|Town)$", match => match.Value == " City" ? " Town" : " City")).Where(name => !clubs.Any(club => club.Name == name)))
            { Refused(await Executor().ExecuteAsync("club_context", ui, Questions(name)[1], null, default)); partial++; }
        }
        Assert.Equal(60, entries); Assert.Equal(120, positive); Assert.Equal(60, absent); Assert.Equal(18, foreign); Assert.Equal(14, partial);
    }

    private static void GroundEvidence(LeagueWorkflowResult result, Matchmate.Contracts.LeagueSeasonSnapshot expected)
    {
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(expected.SelectedClub!.Club.Id, result.ResolvedClubId);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        using var data = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal(expected.Season.SeasonKey, data.RootElement.GetProperty("season").GetString()); Assert.Equal(expected.Date, data.RootElement.GetProperty("date").GetString());
        Assert.Equal(expected.SelectedClub.Club.Ground, data.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
        Assert.Equal(expected.SelectedClub.Club.Town, data.RootElement.GetProperty("facts").GetProperty("Town").GetString());
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task FactGrammarBindsEveryLoadedSeasonClubAtPreseasonAndThePinnedCutoff()
    {
        var model = RawModel(); var entries = 0; var supported = 0; var refused = 0; var derivedNames = 0;
        var seasons = model.Seasons();
        var allClubs = seasons.SelectMany(season => model.Snapshot(season.SeasonKey, null).Clubs).DistinctBy(club => club.Id).ToArray();
        foreach (var season in seasons)
        {
            var clubs = model.Snapshot(season.SeasonKey, null).Clubs;
            LeagueWorkflowExecutor Executor() => new((key, cutoff, club, _) => Task.FromResult(model.Snapshot(key, cutoff, club)));
            foreach (var club in clubs)
            {
                foreach (var index in new int?[] { 0, null })
                {
                    var snapshot = model.Snapshot(season.SeasonKey, index, club.Id);
                    var context = LeagueChatContext.From(snapshot);
                    var question = index == 0 ? F010 : "What games played and points tally does our club have here?";
                    var result = await Executor().ExecuteAsync("club_context", context, question, null, default);
                    Evidence(result, context, snapshot); supported++;
                }
                var other = clubs.First(candidate => candidate.Id != club.Id);
                var namedContext = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, other.Id));
                foreach (var question in new[]
                {
                    $"What games played and points tally does {club.Name} have here?",
                    $"What {club.Name}'s games played and points tally does our club have here?"
                })
                {
                    var result = await Executor().ExecuteAsync("club_context", namedContext, question, null, default);
                    Evidence(result, namedContext, model.Snapshot(season.SeasonKey, null, club.Id)); supported++;
                }
                entries++;
            }
            var selected = LeagueChatContext.From(model.Snapshot(season.SeasonKey, 0, clubs[1].Id));
            var accepted = await Executor().ExecuteContinuationAsync(selected, F010, new("club_context", clubs[0].Id), default);
            Evidence(accepted, selected, model.Snapshot(season.SeasonKey, 0, clubs[0].Id)); supported++;
            var missing = LeagueChatContext.From(model.Snapshot(season.SeasonKey, 0, null));
            Refused(await Executor().ExecuteAsync("club_context", missing, F010, null, default)); refused++;
            Refused(await Executor().ExecuteContinuationAsync(selected, F010, new("league_context", null), default)); refused++;
            foreach (var name in allClubs.Where(club => !clubs.Any(current => current.Id == club.Id)).Select(club => club.Name)
                .Concat(new[] { "Barcelona", "Bayern Munich", "Real Madrid" }))
            {
                var question = $"What points total and league placing does {name} have here?";
                Refused(await Executor().ExecuteAsync("club_context", selected, question, null, default)); refused++;
            }
            // A listed name ending in City/Town remains one whole identity.
            // Swapping that suffix cannot borrow authority from its shorter ID.
            foreach (var name in clubs.Select(club => Regex.Replace(club.Name, @" (City|Town)$", match => match.Value == " City" ? " Town" : " City"))
                .Where(name => !clubs.Any(club => club.Name == name)))
            {
                foreach (var question in new[]
                {
                    $"What games played and points tally does {name} have here?",
                    $"What {name}'s games played and points tally does our club have here?"
                })
                { Refused(await Executor().ExecuteAsync("club_context", selected, question, null, default)); refused++; }
                derivedNames++;
            }
        }
        Assert.Equal(60, entries); Assert.Equal(243, supported); Assert.True(derivedNames > 0); Assert.Equal(45 + 2 * derivedNames, refused);
    }

    private static LeagueModel RawModel()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return new(LeagueReferenceCodec.Decode(bytes));
    }
    private static LeagueChatContext Context(string? club = "everton") => LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: club));
    private static async Task Answer(string question, LeagueChatContext context, string subject, string workflow = "club_context", string? reference = null)
    {
        var result = await LeagueChatFixtures.Executor().ExecuteAsync(workflow, context, question, reference, default);
        Evidence(result, context, LeagueChatFixtures.Snapshot(club: subject));
    }
    private static void Refused(LeagueWorkflowResult result, MatchChatOutcome outcome = MatchChatOutcome.Clarify)
    {
        Assert.Equal(outcome, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence);
    }
    private static void Evidence(LeagueWorkflowResult result, LeagueChatContext context, Matchmate.Contracts.LeagueSeasonSnapshot expected)
    {
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal(expected.SelectedClub!.Club.Id, result.ResolvedClubId);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var data = JsonDocument.Parse(item.Text);
            Assert.Equal(context.SeasonKey, data.RootElement.GetProperty("season").GetString());
            Assert.Equal(context.Date, data.RootElement.GetProperty("date").GetString());
        }
        using var facts = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal(expected.SelectedClub.Club.Name, facts.RootElement.GetProperty("facts").GetProperty("club").GetString());
        Assert.Equal(expected.SelectedClub.Row.Points, facts.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
        if (expected.SelectedClub.Row.Rank is null) Assert.Equal(JsonValueKind.Null, facts.RootElement.GetProperty("facts").GetProperty("Rank").ValueKind);
        else Assert.Equal(expected.SelectedClub.Row.Rank.Value, facts.RootElement.GetProperty("facts").GetProperty("Rank").GetInt32());
    }
}
