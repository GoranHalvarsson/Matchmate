using System.Security.Cryptography;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatTemporalLiteralTests
{
    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("Show our points for x2026_09_19.")]
    [InlineData("Show our points for x2026 09 19.")]
    [InlineData("Show our points for x2026/09/19.")]
    [InlineData("For x2026 09 19, how many points do we have?")]
    public async Task Correction2F002SeenWholeAtomCasesCannotReleaseSelectedBournemouthFacts(string question)
    {
        var model = RawModel(); var context = LeagueChatContext.From(model.Snapshot("2026-27", 17, "bournemouth")); var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(season, index, club)); }).ExecuteAsync("club_context", context, question, null, default);
        Refused(result); Assert.Equal(0, reads);
        Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
    }

    public static string[] Correction2WholeAtoms()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var year in new[] { "x2026", "2026x", "_2026", "2026_" })
        {
            values.Add(year);
            foreach (var separator in new[] { "-", "/", "_", " " })
            {
                foreach (var day in new[] { "19", "20" }) values.Add(year + separator + "09" + separator + day);
                values.Add(year + separator + "27");
            }
        }
        foreach (var date in new[] { "20260919", "20260920" })
            foreach (var affix in new[] { "", "x", "_" }) { values.Add(affix + date); values.Add(date + affix); }
        foreach (var joined in new[] { "September19th", "September20th", "19September", "20September" })
        { values.Add(joined); values.Add(joined + " 2026"); }
        foreach (var separator in new[] { "/", "_", ".", "-", " " })
            foreach (var day in new[] { "19", "20" }) values.Add("9" + separator + day);
        foreach (var scoped in new[] { "season x2026", "x2026 season", "season 2026x", "2026x season", "season _2026", "_2026 season" }) values.Add(scoped);
        return values.Order(StringComparer.Ordinal).ToArray();
    }
    public static IEnumerable<object[]> Correction2WholeAtomCases() => Correction2WholeAtoms().Select(literal => new object[] { literal });

    [Theory, MemberData(nameof(Correction2WholeAtomCases))]
    public async Task Correction2CompleteAtomFamiliesRejectMatchingAndDifferentNumbersUnderForcedFactualPlans(string literal)
    {
        foreach (var question in new[] { $"Show our points for {literal}.", $"For {literal}, how many points do we have?" })
        {
            Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess("2026-27", "2026-09-20", question));
            var atom = Assert.Single(RawLiterals(question));
            Assert.False(atom.GetProperty("Valid").GetBoolean());
            Assert.Equal(literal, question.Substring(atom.GetProperty("Start").GetInt32(), atom.GetProperty("Length").GetInt32()));
            foreach (var selected in new string?[] { "bournemouth", null })
            foreach (var workflow in new[] { "club_context", "league_context", "named_club_context" })
            {
                var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: selected)); var reads = 0;
                Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, context, question, "everton", default));
                Assert.Equal(0, reads);
            }
        }
    }

    [Theory]
    [InlineData("Show our points September19th.")]
    [InlineData("Show our points 19September.")]
    [InlineData("Show our points 20260919.")]
    [InlineData("Show our points _x2026_09_19.")]
    [InlineData("Show our points 9/.")]
    [InlineData("Show our points 9_.")]
    [InlineData("Show our points on 9-19.")]
    [InlineData("Show our points on 9 19.")]
    [InlineData("Show our points on 2026 goals.")]
    [InlineData("Show our points on the date 20260919 points.")]
    [InlineData("Show our points in season 2026points.")]
    public async Task Correction2DateStructureAndExplicitCuesCannotBecomeSmallOrUnitQualifiedQuantities(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); var reads = 0;
        Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
        Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("Did we score 20260919 goals here?")]
    [InlineData("Did we collect 20260920 points here?")]
    [InlineData("Did we play 2026 games here?")]
    [InlineData("Did we score 2026goals here?")]
    [InlineData("Did we play 2026 minutes here?")]
    [InlineData("Did we play 90 minutes here?")]
    [InlineData("Did we score in 30 seconds here?")]
    [InlineData("Did we win 3-0 here?")]
    [InlineData("Did we lose 0-3 here?")]
    [InlineData("Did we draw 2-2 here?")]
    public async Task Correction2PureQuantityUnitsAndOrdinaryScoresPreserveSelectedFacts(string question)
    {
        var snapshot = LeagueChatFixtures.Snapshot(); var context = LeagueChatContext.From(snapshot); var reads = 0;
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        Assert.Empty(RawLiterals(question));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        Evidence(result, context, snapshot.SelectedClub!.Row.Points, "everton"); Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void Correction2WholeAtomAndCanonicalControlsDoNotDependOnHostCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess("2026-27", "2026-09-20", "Show our points for x2026_09_19."));
            Assert.Equal(LeagueTimeAssessment.MatchesSelection, LeagueTimeScope.Assess("2026-27", "2026-09-20", "Show our points for 2026-09-20."));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task Correction1F001MalformedNamedYearCannotReleaseSelectedFacts()
    {
        const string question = "Show our points for 20 September 2025x.";
        var model = RawModel(); var context = LeagueChatContext.From(model.Snapshot("2026-27", 17, "bournemouth")); var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(season, index, club)); }).ExecuteAsync("club_context", context, question, null, default);
        Refused(result); Assert.Equal(0, reads);
        Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task Correction1V8Fresh053RetainsTheCompleteSelectedSeasonWrapper()
    {
        const string question = "For the 2016-17 season selected here, recap our points and recent results.";
        var model = RawModel(); var snapshot = model.Snapshot("2016-17", 50, "manchester-city");
        var context = LeagueChatContext.From(snapshot); var reads = 0;
        Assert.Equal("2016-12-31", context.Date);
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(season, index, club)); }).ExecuteAsync("club_context", context, question, null, default);
        Evidence(result, context, snapshot.SelectedClub!.Row.Points, "manchester-city"); Assert.Equal(2, reads);
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("For 2026-09-20/, how many points do we have?", false, 0)]
    [InlineData("For 2026-09-20-, how many points do we have?", false, 0)]
    [InlineData("Show our points for 20 September 2025x.", false, 0)]
    [InlineData("Show our points for September 20, 2025x.", false, 0)]
    [InlineData("Show our points for 2026_09_19.", false, 0)]
    [InlineData("Show our points for 20 September 2026.", true, 2)]
    [InlineData("What internaltemporalstampx points does Everton have here?", false, 1)]
    [InlineData("For 2026-09-20, what points does Everton Barcelona have?", false, 1)]
    public async Task Correction1SeenReviewQuestionsRetainTheirOriginalSafetyAndControlExpectations(string question, bool accepted, int expectedReads)
    {
        var model = RawModel(); var snapshot = model.Snapshot("2026-27", 17, "bournemouth"); var context = LeagueChatContext.From(snapshot); var reads = 0;
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(season, index, club)); }).ExecuteAsync("club_context", context, question, null, default);
        if (accepted) Evidence(result, context, snapshot.SelectedClub!.Row.Points, "bournemouth");
        else Refused(result);
        Assert.Equal(expectedReads, reads);
        if (expectedReads == 0) Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
    }

    public static readonly string[] Correction1MalformedLiterals =
    ["20 September 2025x", "September 20, 2025x", "20 September 2026x", "September 20, 2026x", "20 September x2025", "September 20, x2025", "20 September x2026", "September 20, x2026", "20thx September 2026", "September 20thx, 2026", "20 September 2026_", "September 20, 2026_", "20 September 2026/", "September 20, 2026-", "2026-09-20/", "2026-09-20-", "2026_09_19", "2026_09_20", "20 September_2026", "September 20_2026", "20 September 2026-09-20", "September 20, 2026-09-20", "2026-27_", "2026x", "2026_", "2026-09-20 /", "20 September 2026 /"];
    public static IEnumerable<object[]> Correction1MalformedCases() => Correction1MalformedLiterals.Select(literal => new object[] { literal });

    [Theory, MemberData(nameof(Correction1MalformedCases))]
    public async Task Correction1WholeLexicalAtomsRejectImperativeAndWhPairsBeforeAnyFactualRead(string literal)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: "bournemouth"));
        foreach (var question in new[] { $"Show our points for {literal}.", $"For {literal}, how many points do we have?" })
        {
            Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
            var values = RawLiterals(question);
            var value = Assert.Single(values);
            Assert.Equal(literal, question.Substring(value.GetProperty("Start").GetInt32(), value.GetProperty("Length").GetInt32()));
            Assert.False(value.GetProperty("Valid").GetBoolean());
            foreach (var workflow in new[] { "club_context", "named_club_context", "league_context" })
            {
                var reads = 0;
                Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, context, question, "everton", default));
                Assert.Equal(0, reads);
            }
        }
    }

    [Theory]
    [InlineData("Show our points for 20 September 2026.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for September 20, 2026.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for 20 September 2025.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("Show our points for September 20, 2025.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("Show our points for 20 September.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for September 20!", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for 2026-09-20.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for 2026-09-20?", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for 20 September here.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points for September 20 here.", LeagueTimeAssessment.MatchesSelection)]
    public async Task Correction1StrictDatesKeepYearMeaningAndOrdinarySentencePunctuation(string question, LeagueTimeAssessment expected)
    {
        var snapshot = LeagueChatFixtures.Snapshot(club: "bournemouth"); var context = LeagueChatContext.From(snapshot); var reads = 0;
        Assert.Equal(expected, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        if (expected == LeagueTimeAssessment.MatchesSelection) { Evidence(result, context, snapshot.SelectedClub!.Row.Points, "bournemouth"); Assert.Equal(2, reads); }
        else { Refused(result); Assert.Equal(0, reads); }
    }

    [Theory]
    [InlineData("Show our points for temporalreference.", null)]
    [InlineData("Show our points for internaltemporalstamp.", null)]
    [InlineData("Show our points for temporalreferencex.", null)]
    [InlineData("Show our points for Barcelona 20 September.", null)]
    [InlineData("Show our points for 20 September Barcelona.", null)]
    [InlineData("Show our points for Everton Barcelona.", null)]
    [InlineData("Show our points for Everton.", "everton")]
    public async Task Correction1TerminalForConsumesOnlyTrustedTemporalOrClubConstituents(string question, string? subject)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: "bournemouth")); var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        if (subject is null) { Refused(result); Assert.Equal(1, reads); }
        else { Evidence(result, context, LeagueChatFixtures.Snapshot(club: subject).SelectedClub!.Row.Points, subject); Assert.Equal(3, reads); }
    }

    public static readonly string[] Correction1SeasonWrappers =
    ["the 2016-17 season selected here", "the 2016-17 season chosen here", "the 2016-17 season shown here", "the 2016-17 season selected", "the 2016-17 season chosen", "the 2016-17 season shown", "the selected 2016-17 season", "our chosen 2016-17 season", "2016-17 season", "the selected 2016/17 season shown here"];
    public static IEnumerable<object[]> Correction1SeasonWrapperCases() => Correction1SeasonWrappers.Select(wrapper => new object[] { wrapper });

    [Theory, Trait("RequiresLocalData", "true"), MemberData(nameof(Correction1SeasonWrapperCases))]
    public async Task Correction1BoundedSeasonWrappersPreserveUiKnownUnknownAndGlobalOwnership(string wrapper)
    {
        var model = RawModel(); var context = LeagueChatContext.From(model.Snapshot("2016-17", 50, "manchester-city"));
        foreach (var owner in new[] { "", "Everton", "Barcelona", "Everton Barcelona" })
        {
            var question = owner.Length == 0 ? $"For {wrapper}, recap our points and recent results."
                : $"For {owner} in {wrapper}, what points and results have accumulated?";
            var reads = 0;
            var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
            { reads++; return Task.FromResult(model.Snapshot(season, index, club)); }).ExecuteAsync("club_context", context, question, null, default);
            if (owner is "Barcelona" or "Everton Barcelona") { Refused(result); Assert.Equal(1, reads); }
            else
            {
                var subject = owner.Length == 0 ? "manchester-city" : "everton";
                Evidence(result, context, model.Snapshot("2016-17", 50, subject).SelectedClub!.Row.Points, subject);
                Assert.Equal(owner.Length == 0 ? 2 : 3, reads);
            }
        }
        var residualReads = 0;
        Refused(await new LeagueWorkflowExecutor((season, index, club, _) =>
        { residualReads++; return Task.FromResult(model.Snapshot(season, index, club)); })
            .ExecuteAsync("club_context", context, $"For {wrapper} Barcelona, recap our points and recent results.", null, default));
        Assert.Equal(1, residualReads);
        var global = context with { SelectedClubId = null, SelectedClubName = null }; var globalReads = 0;
        var table = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { globalReads++; return Task.FromResult(model.Snapshot(season, index, club)); })
            .ExecuteAsync("league_context", global, $"For {wrapper}, show the whole league table.", null, default);
        Assert.Equal(MatchChatOutcome.Answered, table.Outcome); Assert.Null(table.ResolvedClubId); Assert.Equal(2, globalReads);
        using var data = JsonDocument.Parse(table.Evidence.Single(item => item.Id == "table_snapshot").Text);
        Assert.Equal(global.SeasonKey, data.RootElement.GetProperty("season").GetString());
        Assert.Equal(global.Date, data.RootElement.GetProperty("date").GetString());
    }

    [Theory, Trait("RequiresLocalData", "true")]
    [InlineData("For 2026 09 19, how many points do we have?", false)]
    [InlineData("For 2026-09-20, how many points do we have?", true)]
    public async Task F010RawDateRepresentationCannotBorrowSelectedBournemouthFacts(string question, bool accepted)
    {
        var model = RawModel(); var snapshot = model.Snapshot("2026-27", 17, "bournemouth");
        var context = LeagueChatContext.From(snapshot); var reads = 0;
        Assert.Equal("2026-09-20", context.Date);
        var result = await new LeagueWorkflowExecutor((season, index, club, _) =>
        { reads++; return Task.FromResult(model.Snapshot(season, index, club)); })
            .ExecuteAsync("club_context", context, question, null, default);
        if (accepted) { Evidence(result, context, snapshot.SelectedClub!.Row.Points, "bournemouth"); Assert.Equal(2, reads); }
        else { Refused(result); Assert.Equal(0, reads); }
    }

    public static readonly string[] MatchingLiterals =
    ["2026-09-20", "20 September 2026", "September 20th, 2026", "20th Sept 2026", "20 September", "Sep 20", "2026-27", "2026/27", "2026–2027", "season 2026", "2026 season", "2026"];
    public static IEnumerable<object[]> MatchingLiteralCases() => MatchingLiterals.Select(literal => new object[] { literal });

    [Theory, MemberData(nameof(MatchingLiteralCases))]
    public async Task OneTrustedProjectionPreservesUiKnownUnknownAndGlobalOwnership(string literal)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: "bournemouth"));
        foreach (var subject in new[] { "we", "the selected club", "Everton", "Barcelona" })
        {
            var question = subject == "we" ? $"For {literal}, how many points do we have?" : $"For {literal}, what points does {subject} have?";
            var reads = 0;
            var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
            if (subject == "Barcelona") { Refused(result); Assert.Equal(1, reads); }
            else
            {
                var club = subject == "Everton" ? "everton" : "bournemouth";
                Evidence(result, context, LeagueChatFixtures.Snapshot(club: club).SelectedClub!.Row.Points, club);
                Assert.Equal(club == context.SelectedClubId ? 2 : 3, reads);
            }
        }
        var global = context with { SelectedClubId = null, SelectedClubName = null }; var globalReads = 0;
        var table = await LeagueChatFixtures.Executor((_, _, _) => globalReads++).ExecuteAsync("league_context", global, $"For {literal}, show the whole league table.", null, default);
        Assert.Equal(MatchChatOutcome.Answered, table.Outcome); Assert.Null(table.ResolvedClubId); Assert.Equal(2, globalReads);
        using var data = JsonDocument.Parse(table.Evidence.Single(item => item.Id == "table_snapshot").Text);
        Assert.Equal(global.Date, data.RootElement.GetProperty("date").GetString());
        Assert.Equal(20, data.RootElement.GetProperty("facts").GetProperty("rows").GetArrayLength());
        var missingReads = 0;
        Refused(await LeagueChatFixtures.Executor((_, _, _) => missingReads++).ExecuteAsync("club_context", global, $"For {literal}, how many points do we have?", null, default));
        Assert.Equal(0, missingReads);
    }

    public static readonly string[] UnsupportedLiterals =
    ["2026 09 19", "2026 09 20", "2026/09/20", "2026.09.20", "20/09/2026", "20.09.2026", "20 09 2026", "2026-09/20", "2026/09-20", "2026-09-20-21", "2026-09-20 21", "2026/27/20", "2026 27", "2026-27-20", "2026-09-200", "2026-09-20x", "2026--09-20", "2026-/09-20", "2026 - 09 - 20", "2026-02-29", "2026-13-20", "31 September 2026", "29 February 2026", "20 September 2026 09", "2026-28", "2026/2028", "0000-01", "9999-00", "٢٠٢٦-٠٩-٢٠", "٢٠ September 2026", "20 September ٢٠٢٦", "٢٠٢٦-٢٧", "٢٠٢٦", "season ٢٠٢٦"];
    public static IEnumerable<object[]> UnsupportedLiteralCases() => UnsupportedLiterals.Select(literal => new object[] { literal });

    [Theory, MemberData(nameof(UnsupportedLiteralCases))]
    public async Task UnsupportedOrInvalidCompleteLiteralsRejectEveryFactualWorkflowBeforeReads(string literal)
    {
        var question = $"For {literal}, how many points do we have?";
        foreach (var selected in new string?[] { "bournemouth", null })
        {
            var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot(club: selected));
            Assert.Equal(LeagueTimeAssessment.Unresolved, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
            foreach (var workflow in new[] { "club_context", "named_club_context", "league_context", "ground_context", "date_changes" })
            {
                var reads = 0;
                Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync(workflow, context, question, "everton", default));
                Assert.Equal(0, reads);
            }
            var continuationReads = 0;
            Refused(await LeagueChatFixtures.Executor((_, _, _) => continuationReads++).ExecuteContinuationAsync(context, question, new("club_context", "everton"), default));
            Assert.Equal(0, continuationReads);
        }
    }

    [Theory]
    [InlineData("2026-27", "2027-01-02", "Show our points in 2027.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("2026-27", "2027-01-02", "Show our points in 2026.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("2026-27", "2027-01-02", "Show our points in the 2026 season.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("2026-27", "2027-01-02", "Show our points in season 2027.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("2026-27", "2027-01-02", "Show our points on January 2nd.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("2027-28", "2028-02-29", "Show our points on 29 February.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("2026-27", "2027-02-28", "Show our points on February 29.", LeagueTimeAssessment.Unresolved)]
    [InlineData("2027-28", null, "Show our points on 29 February.", LeagueTimeAssessment.Unresolved)]
    [InlineData("2026-27", null, "Show our points in 2026.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("1999-00", "2000-01-01", "Show our points in 1999/2000.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("1999-00", "2000-01-01", "Show our points in 1999-00.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("1999-00", "2000-01-01", "Show our points in 1999-99.", LeagueTimeAssessment.Unresolved)]
    public void ExplicitCalendarAndSeasonFieldsKeepTheirDifferentYearMeanings(string season, string? date, string question, LeagueTimeAssessment expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Assert.Equal(expected, LeagueTimeScope.Assess(season, date, question));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("Show our points on 2026-09-20 and on 2026-09-20.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points in 2026/27 on 20 September 2026.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points in 2026 and in 2026.", LeagueTimeAssessment.MatchesSelection)]
    [InlineData("Show our points on 2026-09-20 and on 2026-09-19.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("Show our points in 2026/27 on 20 September 2025.", LeagueTimeAssessment.DifferentTime)]
    [InlineData("Show our points on 2026-09-19 and 2026 09 20.", LeagueTimeAssessment.Unresolved)]
    [InlineData("Show our points on the date currently chosen and 2026/09/20.", LeagueTimeAssessment.Unresolved)]
    [InlineData("Show our points on 2026-09-20 and on 29 February 2026.", LeagueTimeAssessment.Unresolved)]
    public async Task EveryCompleteLiteralKeepsAuthorityAlongsideMatchingAnchors(string question, LeagueTimeAssessment expected)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(expected, LeagueTimeScope.Assess(context.SeasonKey, context.Date, question));
        var reads = 0;
        var result = await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default);
        if (expected == LeagueTimeAssessment.MatchesSelection) { Evidence(result, context, LeagueChatFixtures.Snapshot().SelectedClub!.Row.Points, "everton"); Assert.Equal(2, reads); }
        else { Refused(result); Assert.Equal(0, reads); }
    }

    [Theory]
    [InlineData("Did we score 2026 goals here?")]
    [InlineData("Did we collect 2027 points here?")]
    [InlineData("Did we win 3-0 here?")]
    [InlineData("Did we draw 2-2 here?")]
    [InlineData("Did we play 90 minutes and score in 30 seconds here?")]
    [InlineData("May we see our points here?")]
    [InlineData("Can we march up the selected table?")]
    public async Task FootballQuantitiesScoresAndOrdinaryMonthWordsKeepExistingScope(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        Assert.Equal(LeagueTimeResolution.MatchesSelection, LeagueTimeScope.Resolve(context.SeasonKey, context.Date, question));
        var result = await LeagueChatFixtures.Executor().ExecuteAsync("club_context", context, question, null, default);
        Evidence(result, context, LeagueChatFixtures.Snapshot().SelectedClub!.Row.Points, "everton");
    }

    [Theory]
    [InlineData("For temporalreference, how many points do we have?")]
    [InlineData("For internaltemporalstamp, how many points do we have?")]
    [InlineData("For internaltemporalstamp internaltemporalstampx, how many points do we have?")]
    [InlineData("For temporalreference 2026-09-20, how many points do we have?")]
    [InlineData("For temporalreference's points, what points does our club have?")]
    [InlineData("For temporalreference, what points does Everton have?")]
    [InlineData("What temporalreference points does Everton have here?")]
    public async Task RawMarkerWordsCannotAcquireTrustedTemporalAuthority(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot()); var reads = 0;
        Refused(await LeagueChatFixtures.Executor((_, _, _) => reads++).ExecuteAsync("club_context", context, question, null, default));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("Now, show me, for 20 September 2026, what points does our club have here?")]
    [InlineData("For 2026-09-20, how many points do we have?")]
    public async Task TemporalProjectionPreservesOriginalTypedRouterHistoryAndModelPayload(string question)
    {
        var context = LeagueChatContext.From(LeagueChatFixtures.Snapshot());
        var router = new LeagueChatFixtures.Router { Workflow = "club_context" }; var model = new LeagueChatFixtures.Model();
        using var session = LeagueChatFixtures.Session(router: router, model: model); session.SetContext(context);
        await session.AskAsync(context, question);
        Assert.Equal(MatchChatOutcome.Answered, Assert.Single(session.Turns).Outcome);
        Assert.Equal(question, Assert.Single(session.Turns).Question); Assert.Equal(question, Assert.Single(router.Inputs).Question);
        Assert.Equal(question, Assert.Single(Assert.Single(model.Requests).Messages.Where(message => message.Role == "user")).Content);
    }

    [Fact]
    public void RawLiteralProvenanceConsumesMaximalRunsWithoutOverlapsOrSalvagedSubstrings()
    {
        const string question = "For 2026-09-20-21 and 20 September 2026 09 and 2026/27 and season 2026 and 2027, show our points.";
        // Keep the scanner internal; inspect its typed compiled result without
        // widening production API just for an offset/provenance assertion.
        var values = RawLiterals(question);
        Assert.Equal(new[] { "2026-09-20-21", "20 September 2026 09", "2026/27", "season 2026", "2027" },
            values.Select(value => question.Substring(value.GetProperty("Start").GetInt32(), value.GetProperty("Length").GetInt32())));
        Assert.Equal(new[] { false, false, true, true, true }, values.Select(value => value.GetProperty("Valid").GetBoolean()));
        Assert.Equal("2026-27", values[2].GetProperty("Season").GetString());
        Assert.Equal(2026, values[3].GetProperty("Year").GetInt32());
        for (var i = 1; i < values.Length; i++)
            Assert.True(values[i - 1].GetProperty("Start").GetInt32() + values[i - 1].GetProperty("Length").GetInt32() <= values[i].GetProperty("Start").GetInt32());
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task EveryLoadedClubSeasonKeepsExactIsoFactsAndRejectsTheSameSpacedNumbers()
    {
        var model = RawModel(); var entries = 0; var executions = 0;
        foreach (var season in model.Seasons())
        foreach (var club in model.Snapshot(season.SeasonKey, null).Clubs)
        {
            var snapshot = model.Snapshot(season.SeasonKey, null, club.Id); var context = LeagueChatContext.From(snapshot);
            Assert.NotNull(context.Date);
            foreach (var valid in new[] { true, false })
            {
                var literal = valid ? context.Date : context.Date.Replace('-', ' ');
                var question = $"For {literal}, how many points do we have?"; var reads = 0;
                var result = await new LeagueWorkflowExecutor((key, index, subject, _) =>
                { reads++; return Task.FromResult(model.Snapshot(key, index, subject)); }).ExecuteAsync("club_context", context, question, null, default);
                if (valid) { Evidence(result, context, snapshot.SelectedClub!.Row.Points, club.Id); Assert.Equal(2, reads); }
                else { Refused(result); Assert.Equal(0, reads); }
                executions++;
            }
            entries++;
        }
        Assert.Equal(60, entries); Assert.Equal(120, executions);
    }

    private static LeagueModel RawModel()
    {
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), ".local", "league-reference-v2", "league-reference.json"));
        Assert.Equal("b3e5828d76bc77002fed03df3a11ab5942d31487253d9323f7efd43c6b7e795a", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return new(LeagueReferenceCodec.Decode(bytes));
    }
    private static JsonElement[] RawLiterals(string question)
    {
        var scan = typeof(LeagueTimeScope).Assembly.GetType("Matchmate.Web.LeagueChat.LeagueTemporalSyntax")!
            .GetMethod("Scan", BindingFlags.Static | BindingFlags.NonPublic)!;
        return ((IEnumerable)scan.Invoke(null, [question])!).Cast<object>().Select(value => JsonSerializer.SerializeToElement(value)).ToArray();
    }
    private static void Refused(LeagueWorkflowResult result)
    { Assert.Equal(MatchChatOutcome.Clarify, result.Outcome); Assert.Null(result.ResolvedClubId); Assert.Empty(result.Evidence); }
    private static void Evidence(LeagueWorkflowResult result, LeagueChatContext context, int points, string club)
    {
        Assert.Equal(MatchChatOutcome.Answered, result.Outcome); Assert.Equal(club, result.ResolvedClubId);
        Assert.Equal(new[] { "club_standing", "club_results", "date_change", "league_coverage" }, result.Evidence.Select(item => item.Id));
        foreach (var item in result.Evidence)
        {
            using var data = JsonDocument.Parse(item.Text);
            Assert.Equal(context.SeasonKey, data.RootElement.GetProperty("season").GetString());
            Assert.Equal(context.Date, data.RootElement.GetProperty("date").GetString());
        }
        using var standing = JsonDocument.Parse(result.Evidence[0].Text);
        Assert.Equal(points, standing.RootElement.GetProperty("facts").GetProperty("Points").GetInt32());
    }
}
