using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatInterrogativeAuthorityTests
{
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F028IndependentWhatFieldCannotReleaseSelectedBournemouthFacts()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot("2026-27", 8, "bournemouth"));
        Assert.Equal("2026-08-31", context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync("ground_context", context, "Show our ground, and what Barcelona stadium is listed?", null, default);

        Refused(result);
        Assert.Equal(1, reads);
        Assert.Equal("bournemouth", context.SelectedClubId);
    }

    public static IEnumerable<object[]> UnknownInterrogativePhrases()
    {
        foreach (var head in new[] { "What", "Which" })
        foreach (var phrase in new[]
        {
            "Barcelona stadium is listed here?",
            "Barcelona's recorded home ground is listed here?",
            "Everton Barcelona stadium is listed here?",
            "Everton's recorded Barcelona ground is listed here?",
            "town is Barcelona in here?",
            "ground does Barcelona use here?",
            "ground is Barcelona associated with in this club reference?",
            "town is Barcelona based in here?",
            "town is Barcelona located in here?",
            "stadium did Barcelona play at here?",
            "stadium has Barcelona played at here?",
            "stadium have Barcelona played at here?",
            "stadium had Barcelona played at here?",
            "stadium has Barcelona been playing at here?",
            "stadium has Barcelona been using here?",
            "stadium have Barcelona been using here?",
            "stadium had Barcelona been using here?",
            "stadium is Barcelona using here?",
            "stadium are Barcelona using here?",
            "stadium was Barcelona using here?",
            "stadium were Barcelona using here?",
            "stadium is Barcelona playing at here?",
            "stadium are Barcelona playing at here?",
            "stadium was Barcelona playing at here?",
            "stadium were Barcelona playing at here?",
            "town has Barcelona been based in here?",
            "town have Barcelona been located in here?",
            "town had Barcelona been in here?",
            "town is Barcelona's recorded home ground in here?",
            "town is Everton's recorded Barcelona ground in here?",
            "town is Everton and Barcelona's recorded home ground in here?",
            "ground are Man City and Arsenal associated with in this club reference?"
        })
        foreach (var question in ClauseOrders(head + " " + phrase))
            yield return [question];
    }

    [Theory, MemberData(nameof(UnknownInterrogativePhrases))]
    public async Task EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context", "club_context", "named_club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync(workflow, Context("bournemouth"), question, "arsenal", default);
            Refused(result);
            Assert.Equal(1, reads);
        }

        var acceptedReads = 0;
        var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++)
            .ExecuteContinuationAsync(Context("brighton"), question, new("ground_context", "everton"), default);
        Refused(accepted);
        Assert.Equal(1, acceptedReads);
    }

    public static IEnumerable<object[]> KnownInterrogativePhrases()
    {
        foreach (var head in new[] { "What", "Which" })
        foreach (var phrase in new[]
        {
            "Everton stadium is listed here?",
            "Everton's recorded home ground is listed here?",
            "town is Everton in here?",
            "ground does Everton use here?",
            "ground is Everton associated with in this club reference?",
            "town is Everton based in here?",
            "town is Everton located in here?",
            "stadium did Everton play at here?",
            "stadium has Everton played at here?",
            "stadium have Everton played at here?",
            "stadium had Everton played at here?",
            "stadium has Everton been playing at here?",
            "stadium has Everton been using here?",
            "stadium have Everton been using here?",
            "stadium had Everton been using here?",
            "stadium is Everton using here?",
            "stadium are Everton using here?",
            "stadium was Everton using here?",
            "stadium were Everton using here?",
            "stadium is Everton playing at here?",
            "stadium are Everton playing at here?",
            "stadium was Everton playing at here?",
            "stadium were Everton playing at here?",
            "town has Everton been based in here?",
            "town have Everton been located in here?",
            "town had Everton been in here?",
            "town is Everton's listed home ground in here?",
            "town is Everton's recorded home ground in here?"
        })
        foreach (var question in ClauseOrders(head + " " + phrase))
            yield return [question];
    }

    [Theory, MemberData(nameof(KnownInterrogativePhrases))]
    public async Task CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(string question)
    {
        foreach (var workflow in new[] { "ground_context", "named_ground_context" })
        {
            var reads = 0;
            var context = Context("bournemouth");
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync(workflow, context, question, "arsenal", default);
            GroundEvidence(result, context, "everton");
            Assert.Equal(2, reads);
            Assert.Equal("bournemouth", context.SelectedClubId);
        }

        var acceptedReads = 0;
        var acceptedContext = Context("brighton");
        var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++)
            .ExecuteContinuationAsync(acceptedContext, question, new("ground_context", "everton"), default);
        GroundEvidence(accepted, acceptedContext, "everton");
        Assert.Equal(2, acceptedReads);
    }

    public static IEnumerable<object[]> ClubDependentInterrogativePhrases()
    {
        foreach (var head in new[] { "What", "Which" })
        foreach (var phrase in new[]
        {
            "town is it in here?",
            "ground does it use here?",
            "ground is it associated with here?",
            "town is it based in here?",
            "town is it located in here?",
            "stadium did it play at here?",
            "stadium has it played at here?",
            "stadium has it been playing at here?",
            "stadium has it been using here?",
            "stadium had it been using here?",
            "stadium is it using here?",
            "stadium was it using here?",
            "stadium is it playing at here?",
            "stadium was it playing at here?",
            "town has it been based in here?",
            "town has it been located in here?",
            "town had it been in here?",
            "town is our club's recorded home ground in here?",
            "ground is listed for our selected club here?"
        })
            yield return [head + " " + phrase];
    }

    [Theory, MemberData(nameof(ClubDependentInterrogativePhrases))]
    public async Task InterrogativePlaceAnaphorsKeepSelectedReferenceAndAcceptedAuthoritySeparate(string question)
    {
        var selected = Context("bournemouth");
        var selectedReads = 0;
        var selectedResult = await LeagueChatFixtures.Executor((_, _, _) => selectedReads++)
            .ExecuteAsync("ground_context", selected, question, "arsenal", default);
        GroundEvidence(selectedResult, selected, "bournemouth");
        Assert.Equal(1, selectedReads);

        var referenceContext = Context(null);
        var referenceReads = 0;
        var referenceResult = await LeagueChatFixtures.Executor((_, _, _) => referenceReads++)
            .ExecuteAsync("ground_context", referenceContext, question, "everton", default);
        GroundEvidence(referenceResult, referenceContext, "everton");
        Assert.Equal(2, referenceReads);

        var acceptedReads = 0;
        var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++)
            .ExecuteContinuationAsync(selected, question, new("ground_context", "everton"), default);
        GroundEvidence(accepted, selected, "everton");
        Assert.Equal(2, acceptedReads);
    }

    [Theory, MemberData(nameof(ClubDependentInterrogativePhrases))]
    public async Task MissingAndAcceptedGlobalNullAuthorityCannotBorrowAUiClub(string question)
    {
        foreach (var workflow in new[] { "ground_context", "club_context", "date_changes", "league_context", "league_table" })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync(workflow, Context(null), question, null, default);
            Refused(result);
            Assert.Equal(0, reads);
        }

        foreach (var topic in new[] { new LeagueAcceptedTopic("league_context", null), new LeagueAcceptedTopic("date_changes", null) })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteContinuationAsync(Context("bournemouth"), question, topic, default);
            Refused(result);
            Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData("What club leads this table?")]
    [InlineData("Which club leads this table?")]
    [InlineData("What team leads the league here?")]
    [InlineData("Which team leads the league here?")]
    public async Task GenericInterrogativeFactHeadsRetainGlobalTableAuthority(string question)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync("league_context", Context(null), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Null(result.ResolvedClubId);
        Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("What does the recorded ground reference cover here?")]
    [InlineData("Which recorded ground facts are available here?")]
    public async Task CoverageQuestionsKeepTheirSeparateGlobalAuthority(string question)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync("coverage_limits", Context(null), question, null, default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Null(result.ResolvedClubId);
        Assert.Equal("league_coverage", Assert.Single(result.Evidence).Id);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("What are Everton's recorded home ground and town here?", "named_ground_context", "ground_reference", 2)]
    [InlineData("What are Everton’s recorded home ground and town here?", "named_ground_context", "ground_reference", 2)]
    [InlineData("What has Everton's recent record meant for their standing here?", "named_club_context", "club_standing", 3)]
    [InlineData("What have Everton’s latest results meant for their standing here?", "named_club_context", "club_standing", 3)]
    public async Task AuxiliaryWhatIsNotRewrittenAsAFactualOwner(string question, string workflow, string firstEvidence, int expectedReads)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync(workflow, Context("bournemouth"), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("everton", result.ResolvedClubId);
        Assert.Equal(firstEvidence, result.Evidence[0].Id);
        Assert.Equal(expectedReads, reads);
    }

    [Theory]
    [InlineData("What town is it in at this moment?", MatchChatOutcome.Unavailable)]
    [InlineData("Which town is it in at this moment?", MatchChatOutcome.Unavailable)]
    [InlineData("What town is it in in March?", MatchChatOutcome.Clarify)]
    [InlineData("Which town is it in in March?", MatchChatOutcome.Clarify)]
    [InlineData("What town is it in on 19 September 2026?", MatchChatOutcome.Clarify)]
    [InlineData("Which town is it in on 19 September 2026?", MatchChatOutcome.Clarify)]
    public async Task InterrogativeGrammarCannotOverrideTimeAuthority(string question, MatchChatOutcome expected)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync("ground_context", Context("bournemouth"), question, null, default);
        Refused(result, expected);
        Assert.Equal(0, reads);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("2026-27", 15, "2026-09-18", null, "What's Brentford's recent results record at this cutoff?", "named_club_context", "brentford", 3)]
    [InlineData("2015-16", 20, "2015-09-28", "liverpool", "What's Watford's stadium called in this season's reference?", "named_ground_context", "watford", 2)]
    [InlineData("2026-27", 3, "2026-08-23", "fulham", "Which clubs changed position on the day shown?", "league_context", null, 2)]
    public async Task RevisionOnePreservesExactPreviouslySeenNounHeadingCases(string season, int index, string date, string? selected, string question, string workflow, string? subject, int expectedReads)
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot(season, index, selected));
        Assert.Equal(date, context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((key, cutoff, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(key, cutoff, club));
        }).ExecuteAsync(workflow, context, question, null, default);

        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal(subject, result.ResolvedClubId);
        Assert.Equal(expectedReads, reads);
        var expectedIds = workflow switch
        {
            "named_ground_context" => new[] { "ground_reference", "league_coverage" },
            "league_context" => ["table_snapshot", "date_change", "league_coverage"],
            _ => ["club_standing", "club_results", "date_change", "league_coverage"]
        };
        Assert.Equal(expectedIds, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal(season, evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal(date, evidence.RootElement.GetProperty("date").GetString());
        }
        if (workflow == "named_ground_context")
        {
            using var ground = JsonDocument.Parse(result.Evidence[0].Text);
            var expected = model.Snapshot(season, index, subject).SelectedClub!.Club;
            Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
            Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
        }
        Assert.Equal(selected, context.SelectedClubId);
    }

    [Theory]
    [InlineData("What's Everton's recent results record at this cutoff?", "named_club_context")]
    [InlineData("What’s Everton’s recent results record at this cutoff?", "named_club_context")]
    [InlineData("What is Everton's recent results record at this cutoff?", "named_club_context")]
    [InlineData("What's Everton's stadium called in this season's reference?", "named_ground_context")]
    [InlineData("What’s Everton’s stadium called in this season’s reference?", "named_ground_context")]
    [InlineData("What is Everton's stadium called in this season's reference?", "named_ground_context")]
    public async Task ContractedInterrogativesKeepTheActualKnownPossessiveOwner(string question, string workflow)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync(workflow, Context("bournemouth"), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("everton", result.ResolvedClubId);
        Assert.Equal(workflow == "named_ground_context" ? 2 : 3, reads);
        Assert.Equal(workflow == "named_ground_context" ? "ground_reference" : "club_standing", result.Evidence[0].Id);
    }

    [Theory]
    [InlineData("What's Barcelona's recent results record at this cutoff?")]
    [InlineData("What’s Barcelona’s recent results record at this cutoff?")]
    [InlineData("What is Barcelona's recent results record at this cutoff?")]
    [InlineData("What's Barcelona's stadium called in this season's reference?")]
    [InlineData("What’s Barcelona’s stadium called in this season’s reference?")]
    [InlineData("What is Barcelona's stadium called in this season's reference?")]
    public Task ContractedInterrogativesNeverLicenseAnUnknownPossessiveOwner(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Theory]
    [InlineData("What clubs changed position on the day shown?")]
    [InlineData("Which clubs changed position on the day shown?")]
    [InlineData("What teams changed position on the day shown?")]
    [InlineData("Which teams changed position on the day shown?")]
    public async Task PluralGenericHeadsFinishBeforePredicatesAndKeepGlobalAuthority(string question)
    {
        foreach (var selected in new[] { null, "bournemouth" })
        {
            var context = Context(selected);
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync("league_context", context, question, null, default);
            Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
            Assert.Null(result.ResolvedClubId);
            Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
            Assert.Equal(2, reads);

            var acceptedReads = 0;
            var accepted = await LeagueChatFixtures.Executor((_, _, _) => acceptedReads++)
                .ExecuteContinuationAsync(context, question, new("league_context", null), default);
            Assert.Equal(MatchChatOutcome.Answered, accepted.Outcome);
            Assert.Null(accepted.ResolvedClubId);
            Assert.Equal(new[] { "table_snapshot", "date_change", "league_coverage" }, accepted.Evidence.Select(item => item.Id));
            Assert.Equal(2, acceptedReads);
        }
    }

    [Theory]
    [InlineData("What Barcelona clubs changed position on the day shown?")]
    [InlineData("Which Barcelona clubs changed position on the day shown?")]
    [InlineData("What Barcelona teams changed position on the day shown?")]
    [InlineData("Which Barcelona teams changed position on the day shown?")]
    public Task PluralGenericHeadCannotHideAnUnknownPrefix(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    public static IEnumerable<object[]> WrappedFactHeadings()
    {
        foreach (var wrapper in new[] { "Please show", "Could you tell me", "Would you explain" })
        foreach (var head in new[] { "what", "which" })
        foreach (var identity in new[] { "Barcelona", "Everton" })
        foreach (var phrase in new[] { "stadium is listed here?", "recorded home ground is listed here?" })
            yield return [wrapper + " " + head + " " + identity + " " + phrase, identity];
    }

    [Theory, MemberData(nameof(WrappedFactHeadings))]
    public Task BoundedRequestWrappersPreserveInnerFactualIdentityValidation(string question, string identity)
        => identity == "Barcelona"
            ? EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question)
            : CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(question);

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F005ExactHullPassiveReferenceKeepsTheRealOwnerAndCutoff()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot("2026-27", 6, null));
        Assert.Equal("2026-08-29", context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync("named_ground_context", context, "What stadium is recorded as Hull City's home in this season?", "hull", default);

        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("hull", result.ResolvedClubId);
        Assert.Equal(2, reads);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal("2026-27", evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal("2026-08-29", evidence.RootElement.GetProperty("date").GetString());
        }
        using var ground = JsonDocument.Parse(result.Evidence[0].Text);
        var expected = model.Snapshot("2026-27", 6, "hull").SelectedClub!.Club;
        Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
        Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
        Assert.Null(context.SelectedClubId);
    }

    [Theory]
    [InlineData("What's Barcelona stadium called here?")]
    [InlineData("What’s Barcelona stadium called here?")]
    [InlineData("What is Barcelona stadium called here?")]
    [InlineData("What was Barcelona stadium called here?")]
    [InlineData("What's Barcelona's stadium called here?")]
    [InlineData("What is Everton Barcelona stadium called here?")]
    [InlineData("What is Everton's Barcelona stadium called here?")]
    [InlineData("Show our ground, and what's Barcelona stadium called here?")]
    public Task F006CopularNominalsValidateEveryOwnerToken(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Theory]
    [InlineData("What's Everton stadium called here?")]
    [InlineData("What is Everton stadium called here?")]
    [InlineData("What was Everton stadium called here?")]
    [InlineData("What’s Everton’s recorded home ground called here?")]
    [InlineData("What is Everton's recorded home ground called here?")]
    [InlineData("Show our ground, and what's Everton stadium called here?")]
    public Task F006CopularNominalsKeepCompleteKnownOwnersAheadOfUi(string question)
        => CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(question);

    [Theory]
    [InlineData("What stadium is recorded as Everton's home here?")]
    [InlineData("Which ground is listed as Everton's home here?")]
    [InlineData("What stadium is shown as Everton's home stadium here?")]
    [InlineData("Show our ground, and what stadium is recorded as Everton's home here?")]
    public Task F005PassiveDescriptionsUseTheCompleteKnownOwner(string question)
        => CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(question);

    [Theory]
    [InlineData("What stadium is recorded as Barcelona's home here?")]
    [InlineData("Which ground is listed as Barcelona home here?")]
    [InlineData("What stadium is shown as Everton and Barcelona's home here?")]
    [InlineData("What stadium is recorded as Everton Barcelona's home here?")]
    [InlineData("What stadium is recorded as our Barcelona club's home here?")]
    [InlineData("What stadium is recorded as Everton's home Barcelona club here?")]
    public Task F005PassiveDescriptionsNeverLoseUnknownOrMixedOwnerTokens(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Theory]
    [InlineData("What's our club's recorded home ground called here?")]
    [InlineData("What is our club's recorded home ground called here?")]
    [InlineData("What stadium is recorded as our club's home here?")]
    [InlineData("Which ground is shown as its home here?")]
    [InlineData("What stadium is listed as its home ground here?")]
    public async Task FinalNominalRolesRetainSelectedReferenceAcceptedAndMissingAuthority(string question)
    {
        await InterrogativePlaceAnaphorsKeepSelectedReferenceAndAcceptedAuthoritySeparate(question);
        await MissingAndAcceptedGlobalNullAuthorityCannotBorrowAUiClub(question);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task AdditionalCycleKeepsTheExactWestBromLeagueRecordAndCutoff()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot("2016-17", 107, "chelsea"));
        Assert.Equal("2017-05-21", context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync("named_club_context", context, "As of 21 May 2017, what is West Bromwich Albion's league record?", "west-brom", default);

        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("west-brom", result.ResolvedClubId);
        Assert.Equal(3, reads);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal("2016-17", evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal("2017-05-21", evidence.RootElement.GetProperty("date").GetString());
        }
        using var standing = JsonDocument.Parse(result.Evidence[0].Text);
        var expected = model.Snapshot("2016-17", 107, "west-brom").SelectedClub!;
        Assert.Equal(expected.Club.Name, standing.RootElement.GetProperty("facts").GetProperty("club").GetString());
        Assert.Equal(expected.Row.Points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
        Assert.Equal("chelsea", context.SelectedClubId);
    }

    [Theory]
    [InlineData("What is Everton's league record here?")]
    [InlineData("What's Everton's league record here?")]
    [InlineData("What is Everton's current league record here?")]
    [InlineData("Which Everton league record is listed here?")]
    public async Task FactualQualifiersKeepTheCompleteKnownNominalOwner(string question)
    {
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
            .ExecuteAsync("named_club_context", Context("bournemouth"), question, "arsenal", default);
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("everton", result.ResolvedClubId);
        Assert.Equal(3, reads);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        using var standing = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal("Everton", standing.RootElement.GetProperty("facts").GetProperty("club").GetString());
    }

    [Theory]
    [InlineData("What is Barcelona's league record here?")]
    [InlineData("What's Barcelona's league record here?")]
    [InlineData("What is Everton Barcelona's league record here?")]
    [InlineData("What is Everton's league Barcelona record here?")]
    [InlineData("Which Barcelona league record is listed here?")]
    [InlineData("Which Everton Barcelona league record is listed here?")]
    [InlineData("Show our ground, and what is Barcelona's league record here?")]
    public Task FactualQualifierRolesNeverSkipUnknownOwnersOrModifiers(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task CorrectedNominalTemplatesBindEveryClubAcrossAllThreePinnedSeasons()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var bundle = LeagueReferenceCodec.Decode(bytes);
        var model = new LeagueModel(bundle);
        var seasonClubEntries = 0;
        var supportedExecutions = 0;
        var unsupportedExecutions = 0;
        var crossSeasonExecutions = 0;
        var allClubs = bundle.Seasons.SelectMany(season => season.Clubs).DistinctBy(club => club.Id).ToArray();
        var expectedCrossSeasonExecutions = bundle.Seasons.Sum(season => allClubs.Count(club => !season.Clubs.Any(current => current.Id == club.Id)) * 4);
        (string Question, string Workflow)[] Questions(string name) =>
        [
            ($"What is {name}'s league record here?", "named_club_context"),
            ($"What's {name} stadium called here?", "named_ground_context"),
            ($"What stadium is recorded as {name}'s home in this season?", "named_ground_context"),
            ($"Please show what {name} stadium is listed here?", "named_ground_context")
        ];
        Assert.Equal(3, bundle.Seasons.Length);
        foreach (var season in bundle.Seasons)
        {
            Assert.Equal(20, season.Clubs.Length);
            for (var index = 0; index < season.Clubs.Length; index++)
            {
                var club = season.Clubs[index];
                var selected = season.Clubs[(index + 1) % season.Clubs.Length].Id;
                Assert.NotEqual(club.Id, selected);
                var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, selected));
                var expected = model.Snapshot(season.SeasonKey, context.DateIndex, club.Id).SelectedClub!;
                foreach (var (question, workflow) in Questions(club.Name))
                {
                    var reads = 0;
                    var result = await new LeagueWorkflowExecutor((key, cutoff, subject, _) =>
                    {
                        reads++;
                        return Task.FromResult(model.Snapshot(key, cutoff, subject));
                    }).ExecuteAsync(workflow, context, question, null, default);
                    Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
                    Assert.Equal(club.Id, result.ResolvedClubId);
                    Assert.Equal(workflow == "named_club_context" ? 3 : 2, reads);
                    Assert.Equal(workflow == "named_club_context"
                        ? new[] { "club_standing", "club_results", "date_change", "league_coverage" }
                        : ["ground_reference", "league_coverage"], result.Evidence.Select(item => item.Id));
                    foreach (var item in result.Evidence)
                    {
                        using var evidence = JsonDocument.Parse(item.Text);
                        Assert.Equal(season.SeasonKey, evidence.RootElement.GetProperty("season").GetString());
                        Assert.Equal(context.Date, evidence.RootElement.GetProperty("date").GetString());
                    }
                    using var facts = JsonDocument.Parse(result.Evidence[0].Text);
                    Assert.Equal(club.Name, facts.RootElement.GetProperty("facts").GetProperty("club").GetString());
                    if (workflow == "named_club_context")
                        Assert.Equal(expected.Row.Points, facts.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
                    else
                    {
                        Assert.Equal(expected.Club.Ground, facts.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
                        Assert.Equal(expected.Club.Town, facts.RootElement.GetProperty("facts").GetProperty("Town").GetString());
                    }
                    Assert.Equal(selected, context.SelectedClubId);
                    supportedExecutions++;
                }
                seasonClubEntries++;
            }

            foreach (var name in new[] { "Barcelona", "Bayern Munich", "Real Madrid" })
            {
                Assert.DoesNotContain(season.Clubs, club => LeagueClubResolver.Normalize(club.Name) == LeagueClubResolver.Normalize(name)
                    || LeagueClubResolver.Normalize(club.Id) == LeagueClubResolver.Normalize(name));
                var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, season.Clubs[0].Id));
                foreach (var (question, workflow) in Questions(name))
                {
                    var reads = 0;
                    var result = await new LeagueWorkflowExecutor((key, cutoff, subject, _) =>
                    {
                        reads++;
                        return Task.FromResult(model.Snapshot(key, cutoff, subject));
                    }).ExecuteAsync(workflow, context, question, null, default);
                    Refused(result);
                    Assert.Equal(1, reads);
                    unsupportedExecutions++;
                }
            }

            foreach (var absentClub in allClubs.Where(club => !season.Clubs.Any(current => current.Id == club.Id)))
            {
                Assert.DoesNotContain(season.Clubs, club => club.Id == absentClub.Id);
                var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, season.Clubs[0].Id));
                foreach (var (question, workflow) in Questions(absentClub.Name))
                {
                    var reads = 0;
                    var result = await new LeagueWorkflowExecutor((key, cutoff, subject, _) =>
                    {
                        reads++;
                        return Task.FromResult(model.Snapshot(key, cutoff, subject));
                    }).ExecuteAsync(workflow, context, question, null, default);
                    Refused(result);
                    Assert.Equal(1, reads);
                    crossSeasonExecutions++;
                }
            }
        }
        Assert.Equal(60, seasonClubEntries);
        Assert.Equal(240, supportedExecutions);
        Assert.Equal(36, unsupportedExecutions);
        Assert.True(expectedCrossSeasonExecutions > 0);
        Assert.Equal(expectedCrossSeasonExecutions, crossSeasonExecutions);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("named_ground_context")]
    [InlineData("ground_context")]
    public async Task F008ExactN05KeepsTheOriginalQuestionOwnerAndCutoff(string workflow)
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot("2015-16", 99, "everton"));
        Assert.Equal("2016-05-17", context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync(workflow, context, "What ground is listed for Everton in this season?", null, default);

        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("everton", result.ResolvedClubId);
        Assert.Equal(1, reads);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal("2015-16", evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal("2016-05-17", evidence.RootElement.GetProperty("date").GetString());
        }
        using var ground = JsonDocument.Parse(result.Evidence[0].Text);
        var expected = model.Snapshot("2015-16", 99, "everton").SelectedClub!.Club;
        Assert.Equal(expected.Name, ground.RootElement.GetProperty("facts").GetProperty("club").GetString());
        Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
        Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
    }

    [Theory]
    [InlineData("What ground is listed for Everton in this season?")]
    [InlineData("Which stadium is recorded for Everton in this season?")]
    [InlineData("What ground is shown for Everton here?")]
    [InlineData("What ground was listed for Everton in this season?")]
    [InlineData("Show our ground, and what ground is listed for Everton in this season?")]
    [InlineData("Please show which ground is recorded for Everton here?")]
    [InlineData("What ground is listed for this season's Everton club here?")]
    public Task F008PassiveForOwnersKeepCompleteKnownIdentityAheadOfUi(string question)
        => CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(question);

    [Theory]
    [InlineData("What ground is listed for Barcelona in this season?")]
    [InlineData("Which stadium is recorded for Bayern Munich in this season?")]
    [InlineData("What ground is shown for Everton and Barcelona here?")]
    [InlineData("What ground is listed for Everton Barcelona in this season?")]
    [InlineData("What ground is recorded for our Barcelona club in this season?")]
    [InlineData("What ground is listed for Everton's Barcelona club in this season?")]
    [InlineData("Which ground is shown for Everton and Arsenal in this season?")]
    [InlineData("What ground is listed for Everton Barcelona club here?")]
    [InlineData("Show our ground, and what ground is listed for Real Madrid in this season?")]
    [InlineData("What ground is listed for this season's Barcelona club here?")]
    public Task F008PassiveForOwnersNeverDropUnknownOrMixedIdentityTokens(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Theory]
    [InlineData("What ground is listed for our club in this season?")]
    [InlineData("Which stadium is recorded for this selected club in this season?")]
    [InlineData("What ground is shown for it in this season?")]
    [InlineData("What ground was listed for it here?")]
    public async Task F008PassiveForContextOwnersRetainEveryAuthorityBoundary(string question)
    {
        await InterrogativePlaceAnaphorsKeepSelectedReferenceAndAcceptedAuthoritySeparate(question);
        await MissingAndAcceptedGlobalNullAuthorityCannotBorrowAUiClub(question);
        foreach (var workflow in new[] { "league_context", "league_table" })
        {
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++)
                .ExecuteAsync(workflow, Context("bournemouth"), question, null, default);
            Refused(result);
            Assert.Equal(1, reads);
        }
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F008PassiveForTemplateRespectsEverySelectedSeasonClubAndItsReference()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var bundle = LeagueReferenceCodec.Decode(bytes);
        var model = new LeagueModel(bundle);
        var allClubs = bundle.Seasons.SelectMany(season => season.Clubs).DistinctBy(club => club.Id).ToArray();
        var seasonClubEntries = 0;
        var supportedExecutions = 0;
        var foreignExecutions = 0;
        var crossSeasonExecutions = 0;
        var expectedCrossSeasonExecutions = bundle.Seasons.Sum(season => allClubs.Count(club => !season.Clubs.Any(current => current.Id == club.Id)) * 2);
        string Question(string name) => $"What ground is listed for {name} in this season?";
        Assert.Equal(3, bundle.Seasons.Length);
        foreach (var season in bundle.Seasons)
        {
            Assert.Equal(20, season.Clubs.Length);
            for (var index = 0; index < season.Clubs.Length; index++)
            {
                var club = season.Clubs[index];
                var selected = season.Clubs[(index + 1) % season.Clubs.Length].Id;
                Assert.NotEqual(club.Id, selected);
                var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, selected));
                var expected = model.Snapshot(season.SeasonKey, context.DateIndex, club.Id).SelectedClub!.Club;
                foreach (var workflow in new[] { "named_ground_context", "ground_context" })
                {
                    var reads = 0;
                    var result = await new LeagueWorkflowExecutor((key, cutoff, subject, _) =>
                    {
                        reads++;
                        return Task.FromResult(model.Snapshot(key, cutoff, subject));
                    }).ExecuteAsync(workflow, context, Question(club.Name), null, default);
                    Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
                    Assert.Equal(club.Id, result.ResolvedClubId);
                    Assert.Equal(2, reads);
                    Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
                    foreach (var item in result.Evidence)
                    {
                        using var evidence = JsonDocument.Parse(item.Text);
                        Assert.Equal(season.SeasonKey, evidence.RootElement.GetProperty("season").GetString());
                        Assert.Equal(context.Date, evidence.RootElement.GetProperty("date").GetString());
                    }
                    using var ground = JsonDocument.Parse(result.Evidence[0].Text);
                    Assert.Equal(expected.Name, ground.RootElement.GetProperty("facts").GetProperty("club").GetString());
                    Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
                    Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
                    Assert.Equal(selected, context.SelectedClubId);
                    supportedExecutions++;
                }
                seasonClubEntries++;
            }

            async Task RefuseName(string name, bool crossSeason)
            {
                var context = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, season.Clubs[0].Id));
                foreach (var workflow in new[] { "named_ground_context", "ground_context" })
                {
                    var reads = 0;
                    var result = await new LeagueWorkflowExecutor((key, cutoff, subject, _) =>
                    {
                        reads++;
                        return Task.FromResult(model.Snapshot(key, cutoff, subject));
                    }).ExecuteAsync(workflow, context, Question(name), null, default);
                    Refused(result);
                    Assert.Equal(1, reads);
                    if (crossSeason) crossSeasonExecutions++;
                    else foreignExecutions++;
                }
            }
            foreach (var absentClub in allClubs.Where(club => !season.Clubs.Any(current => current.Id == club.Id)))
                await RefuseName(absentClub.Name, true);
            foreach (var name in new[] { "Barcelona", "Bayern Munich", "Real Madrid" })
            {
                Assert.DoesNotContain(season.Clubs, club => LeagueClubResolver.Normalize(club.Name) == LeagueClubResolver.Normalize(name)
                    || LeagueClubResolver.Normalize(club.Id) == LeagueClubResolver.Normalize(name));
                await RefuseName(name, false);
            }
        }
        Assert.Equal(60, seasonClubEntries);
        Assert.Equal(120, supportedExecutions);
        Assert.Equal(18, foreignExecutions);
        Assert.True(expectedCrossSeasonExecutions > 0);
        Assert.Equal(expectedCrossSeasonExecutions, crossSeasonExecutions);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F009ExactU60028KeepsTheSelectedClubAndHistoricalCutoff()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var model = new LeagueModel(LeagueReferenceCodec.Decode(bytes));
        var context = LeagueChatContext.From(model.Snapshot("2016-17", 55, "burnley"));
        Assert.Equal("2017-01-14", context.Date);
        var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        {
            reads++;
            return Task.FromResult(model.Snapshot(season, index, club));
        }).ExecuteAsync("ground_context", context, "What stadium is recorded for the club I have selected?", null, default);

        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal("burnley", result.ResolvedClubId);
        Assert.Equal(1, reads);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal("2016-17", evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal("2017-01-14", evidence.RootElement.GetProperty("date").GetString());
        }
        using var ground = JsonDocument.Parse(result.Evidence[0].Text);
        var expected = model.Snapshot("2016-17", 55, "burnley").SelectedClub!.Club;
        Assert.Equal(expected.Name, ground.RootElement.GetProperty("facts").GetProperty("club").GetString());
        Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
        Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
    }

    [Theory]
    [InlineData("What stadium is recorded for the club I have selected?")]
    [InlineData("Which ground is listed for the team I've picked here?")]
    [InlineData("What ground is shown for this club I have chosen in this season?")]
    [InlineData("What ground is listed for the club I have open here?")]
    [InlineData("What ground is recorded for our selected team I selected here?")]
    public Task F009CompleteSelectorRelativesRetainAllContextAuthorityBoundaries(string question)
        => F008PassiveForContextOwnersRetainEveryAuthorityBoundary(question);

    [Theory]
    [InlineData("What stadium is recorded for the Everton club I have selected here?")]
    [InlineData("Which ground is shown for their Everton team I have chosen here?")]
    public Task F009CompleteNamedSelectorRelativesKeepExplicitSubjectAheadOfUi(string question)
        => CompleteKnownInterrogativePhrasesKeepExplicitIdentityAheadOfUiAndReference(question);

    [Theory]
    [InlineData("What stadium is recorded for the Barcelona club I have selected here?")]
    [InlineData("What stadium is recorded for the club Barcelona I have selected here?")]
    [InlineData("What stadium is recorded for the club I have selected Barcelona here?")]
    [InlineData("What stadium is recorded for the club I have Barcelona selected here?")]
    [InlineData("What stadium is recorded for the Everton and Barcelona club I have selected here?")]
    [InlineData("What stadium is recorded for the club I have selected and Barcelona here?")]
    [InlineData("What stadium is recorded for this season's Barcelona club I have selected here?")]
    [InlineData("Which ground is shown for the Bayern Munich team I have chosen here?")]
    [InlineData("What ground is listed for the team I've picked Real Madrid here?")]
    public Task F009SelectorRelativesNeverDiscardUnknownOrMixedIdentityTokens(string question)
        => EveryInterrogativeIdentityMustPassBeforeAnyFactualProjection(question);

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task F009SelectorRelativeBindsEverySeasonClubAndKeepsAcceptedAuthority()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var bundle = LeagueReferenceCodec.Decode(bytes);
        var model = new LeagueModel(bundle);
        var allClubs = bundle.Seasons.SelectMany(season => season.Clubs).DistinctBy(club => club.Id).ToArray();
        var seasonClubEntries = 0;
        var supportedExecutions = 0;
        var refusedExecutions = 0;
        var expectedAbsentClubs = bundle.Seasons.Sum(season => allClubs.Count(club => !season.Clubs.Any(current => current.Id == club.Id)));
        const string question = "What stadium is recorded for the club I have selected?";
        Assert.Equal(3, bundle.Seasons.Length);
        foreach (var season in bundle.Seasons)
        {
            Assert.Equal(20, season.Clubs.Length);
            LeagueWorkflowExecutor Executor() => new((key, cutoff, subject, _) => Task.FromResult(model.Snapshot(key, cutoff, subject)));
            for (var index = 0; index < season.Clubs.Length; index++)
            {
                var club = season.Clubs[index];
                var other = season.Clubs[(index + 1) % season.Clubs.Length].Id;
                Assert.NotEqual(club.Id, other);
                var selected = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, club.Id));
                var expected = model.Snapshot(season.SeasonKey, selected.DateIndex, club.Id).SelectedClub!.Club;
                var selectedResult = await Executor().ExecuteAsync("ground_context", selected, question, other, default);
                var differentUi = LeagueChatContext.From(model.Snapshot(season.SeasonKey, selected.DateIndex, other));
                var acceptedResult = await Executor().ExecuteContinuationAsync(differentUi, question, new("ground_context", club.Id), default);
                foreach (var result in new[] { selectedResult, acceptedResult })
                {
                    Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
                    Assert.Equal(club.Id, result.ResolvedClubId);
                    Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
                    foreach (var item in result.Evidence)
                    {
                        using var evidence = JsonDocument.Parse(item.Text);
                        Assert.Equal(season.SeasonKey, evidence.RootElement.GetProperty("season").GetString());
                        Assert.Equal(selected.Date, evidence.RootElement.GetProperty("date").GetString());
                    }
                    using var ground = JsonDocument.Parse(result.Evidence[0].Text);
                    Assert.Equal(expected.Name, ground.RootElement.GetProperty("facts").GetProperty("club").GetString());
                    Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
                    Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
                    supportedExecutions++;
                }
                Assert.Equal(club.Id, selected.SelectedClubId);
                Assert.Equal(other, differentUi.SelectedClubId);
                seasonClubEntries++;
            }

            var noUi = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, null));
            Refused(await Executor().ExecuteAsync("ground_context", noUi, question, null, default));
            refusedExecutions++;
            var withUi = LeagueChatContext.From(model.Snapshot(season.SeasonKey, null, season.Clubs[0].Id));
            Refused(await Executor().ExecuteContinuationAsync(withUi, question, new("league_context", null), default));
            refusedExecutions++;
            foreach (var name in allClubs.Where(club => !season.Clubs.Any(current => current.Id == club.Id)).Select(club => club.Name)
                .Concat(new[] { "Barcelona", "Bayern Munich", "Real Madrid" }))
            {
                Assert.DoesNotContain(season.Clubs, club => LeagueClubResolver.Normalize(club.Name) == LeagueClubResolver.Normalize(name));
                var unknownQuestion = $"What stadium is recorded for the {name} club I have selected here?";
                Refused(await Executor().ExecuteAsync("ground_context", withUi, unknownQuestion, null, default));
                refusedExecutions++;
            }
        }
        Assert.Equal(60, seasonClubEntries);
        Assert.Equal(120, supportedExecutions);
        Assert.Equal(expectedAbsentClubs + 15, refusedExecutions);
    }

    private static IEnumerable<string> ClauseOrders(string question)
    {
        yield return question;
        yield return "Show our ground, and " + char.ToLowerInvariant(question[0]) + question[1..];
        yield return question.TrimEnd('?') + ", and what ground is listed for our club here?";
    }

    private static LeagueChatContext Context(string? club) => LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: club));

    private static void Refused(LeagueWorkflowResult result, MatchChatOutcome expected = MatchChatOutcome.Clarify)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.ResolvedClubId);
        Assert.Empty(result.Evidence);
    }

    private static void GroundEvidence(LeagueWorkflowResult result, LeagueChatContext context, string subject)
    {
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome);
        Assert.Equal(subject, result.ResolvedClubId);
        Assert.Equal(new[] { "ground_reference", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var evidence = JsonDocument.Parse(item.Text);
            Assert.Equal(context.SeasonKey, evidence.RootElement.GetProperty("season").GetString());
            Assert.Equal(context.Date, evidence.RootElement.GetProperty("date").GetString());
        }
        using var ground = JsonDocument.Parse(result.Evidence[0].Text);
        var expected = LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Club;
        Assert.Equal(expected.Ground, ground.RootElement.GetProperty("facts").GetProperty("Ground").GetString());
        Assert.Equal(expected.Town, ground.RootElement.GetProperty("facts").GetProperty("Town").GetString());
    }
}
