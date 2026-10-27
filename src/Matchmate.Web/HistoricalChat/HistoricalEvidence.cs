using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Web.Chat;

namespace Matchmate.Web.HistoricalChat;

public sealed class HistoricalEvidence(
    Func<string, string, CancellationToken, Task<LeagueHistoricalMatch>> match,
    Func<string, int?, string?, CancellationToken, Task<LeagueSeasonSnapshot>> snapshot)
{
    public HistoricalEvidence(ReplayApiClient api) : this(api.LeagueMatchAsync, api.LeagueSnapshotAsync) { }
    public async Task<(LeagueHistoricalMatch Match, LeagueClub[] Clubs)> LoadAsync(HistoricalChatContext context, CancellationToken token)
    {
        if (!HistoricalChatContext.Valid(context)) throw new InvalidDataException("Invalid historical context.");
        var fixture = await match(context.SeasonKey, context.MatchKey, token);
        var clubs = await snapshot(context.SeasonKey, null, null, token);
        if (HistoricalChatContext.From(fixture) != context || clubs.Version != context.Version || fixture.Season.Version != context.Version
            || clubs.Season.SeasonKey != context.SeasonKey || fixture.Match.Home != fixture.Home.Id || fixture.Match.Away != fixture.Away.Id
            || fixture.Home.Id == fixture.Away.Id || fixture.HomeTable.Club.Id != fixture.Home.Id || fixture.AwayTable.Club.Id != fixture.Away.Id
            || fixture.HomeTable.Row.Id != fixture.Home.Id || fixture.AwayTable.Row.Id != fixture.Away.Id
            || fixture.HomeTable.Date != context.Date || fixture.AwayTable.Date != context.Date || !ValidMatch(fixture.Match)
            || clubs.Clubs.Length is < 2 or > 40 || clubs.Clubs.DistinctBy(c => c.Id).Count() != clubs.Clubs.Length
            || !clubs.Clubs.Contains(fixture.Home) || !clubs.Clubs.Contains(fixture.Away)) throw new InvalidDataException("Historical snapshot changed or mismatched.");
        return (fixture, clubs.Clubs);
    }
    public static HistoricalPrepared Prepare(LeagueHistoricalMatch fixture, LeagueClub[] clubs, string workflow, string? clubId)
    {
        if (!HistoricalWorkflows.ValidTopic(new(workflow, clubId)) || clubId is not null && clubId != fixture.Home.Id && clubId != fixture.Away.Id)
            throw new InvalidDataException("Unsupported historical topic or club.");
        var evidence = new List<MatchChatEvidence>();
        var goalFacts = new List<HistoricalGoalFact> { new(fixture.Home.Id, fixture.Home.Name, fixture.Match.HomeGoals, "fixture", "fixture"),
            new(fixture.Away.Id, fixture.Away.Name, fixture.Match.AwayGoals, "fixture", "fixture") };
        void Add(string id, string answer, object? detail = null)
        {
            // Numeric authority comes from calculated football facts, not version/hash/source metadata.
            var text = JsonSerializer.Serialize(new { sourceAnswer = answer, detail });
            if (text.Length > 20000) throw new InvalidDataException("Historical evidence budget exceeded.");
            var dateParts = Regex.Matches(answer, @"\b\d{4}-\d{2}-\d{2}\b").SelectMany(m => m.Value.Split('-')).Select(p => int.Parse(p).ToString(System.Globalization.CultureInfo.InvariantCulture));
            evidence.Add(new(id, id, text, MatchReplyValidation.Numbers(answer).Concat(dateParts).Distinct(StringComparer.Ordinal).ToArray()));
        }
        Add("fixture", $"{fixture.Season.Label}, {fixture.Match.Date}: {fixture.Home.Name} {fixture.Match.HomeGoals}–{fixture.Match.AwayGoals} {fixture.Away.Name}, full time. "
            + (fixture.Match.HomeGoals == fixture.Match.AwayGoals ? "A draw." : $"{(fixture.Match.HomeGoals > fixture.Match.AwayGoals ? fixture.Home.Name : fixture.Away.Name)} won.")
            + (fixture.Season.Status == "ongoing" ? $" Ongoing season snapshot: results through {fixture.Season.ResultsThrough}; retrieved {fixture.Season.RetrievedAt}. No automatic updates." : ""),
            new { fixture.Version, fixture.Season.SeasonKey, fixture.Match.MatchKey, fixture.Match.Date });
        var selected = new[] { fixture.Home, fixture.Away }.Where(c => clubId is null || c.Id == clubId).ToArray();
        if (workflow is "overview" or "form")
        {
            var forms = selected.Select(club => Form(club.Id == fixture.Home.Id ? fixture.HomePreMatchForm : fixture.AwayPreMatchForm, club, fixture.Match.Date, clubs)).ToArray();
            Add("form", string.Join("\n", forms), new { boundary = "strictly before played date", order = "oldest first", sampleLimit = 5 });
            foreach (var club in selected)
            {
                var earlier = EligibleForm(club.Id == fixture.Home.Id ? fixture.HomePreMatchForm : fixture.AwayPreMatchForm, club.Id, fixture.Match.Date);
                goalFacts.Add(new(club.Id, club.Name, earlier.Sum(m => m.Home == club.Id ? m.HomeGoals : m.AwayGoals), "earlier_results", "form"));
            }
        }
        if (workflow is "overview" or "table")
            Add("table", string.Join("\n", selected.Select(club => Table(club.Id == fixture.Home.Id ? fixture.HomeTable : fixture.AwayTable)))
                + $" End of {fixture.Match.Date}: all results on that date enter together. Changes compare the previous played date and are not solely attributed to this fixture.");
        if (workflow is "overview" or "meetings" or "ground")
        {
            var prior = fixture.PriorMeetings.Where(p => ValidMatch(p.Match) && string.CompareOrdinal(p.Match.Date, fixture.Match.Date) < 0
                && (p.Match.Home == fixture.Home.Id && p.Match.Away == fixture.Away.Id || p.Match.Home == fixture.Away.Id && p.Match.Away == fixture.Home.Id))
                .OrderBy(p => p.Match.Date, StringComparer.Ordinal).TakeLast(8).ToArray();
            var reference = workflow == "ground"
                ? string.Join("\n", selected.Select(c => $"{c.Name}: season ground reference {c.Ground}, {c.Town}. This is not confirmation of this fixture's venue, and no stadium history is supplied."))
                : $"{prior.Length} earlier meetings shown from the loaded seasons (up to 8 most recent): " + (prior.Length == 0 ? "none in this loaded sample."
                    : string.Join("; ", prior.Select(p => $"{p.Match.Date}, {p.HomeName} {p.Match.HomeGoals}–{p.Match.AwayGoals} {p.AwayName}")))
                    + " This is not an all-time head-to-head record.";
            if (workflow == "overview") reference += " " + string.Join(" ", selected.Select(c => $"{c.Name}: season ground reference {c.Ground}, {c.Town}; fixture venue unverified. No stadium history supplied."));
            Add("reference", reference);
        }
        return new(workflow, clubId, evidence.ToArray(), clubs, goalFacts.ToArray());
    }
    public static string Form(LeagueMatch[] results, LeagueClub club, string before, LeagueClub[] clubs)
    {
        var eligible = EligibleForm(results, club.Id, before);
        int For(LeagueMatch m) => m.Home == club.Id ? m.HomeGoals : m.AwayGoals;
        int Against(LeagueMatch m) => m.Home == club.Id ? m.AwayGoals : m.HomeGoals;
        var wins = eligible.Count(m => For(m) > Against(m)); var draws = eligible.Count(m => For(m) == Against(m));
        string Name(string id) => clubs.FirstOrDefault(c => c.Id == id)?.Name ?? throw new InvalidDataException("Unknown form club.");
        return $"{club.Name} before {before}: {eligible.Length} earlier results in this sample, {wins} wins, {draws} draws, {eligible.Length - wins - draws} losses; "
            + $"{wins * 3 + draws} points, {eligible.Sum(For)} goals for, {eligible.Sum(Against)} against. Oldest first: "
            + (eligible.Length == 0 ? "no earlier results supplied." : string.Join("; ", eligible.Select(m => $"{m.Date}, {Name(m.Home)} {m.HomeGoals}–{m.AwayGoals} {Name(m.Away)}")));
    }
    internal static LeagueMatch[] EligibleForm(LeagueMatch[] results, string clubId, string before) => results
        .Where(m => ValidMatch(m) && string.CompareOrdinal(m.Date, before) < 0 && (m.Home == clubId || m.Away == clubId))
        .DistinctBy(m => m.MatchKey).OrderBy(m => m.Date, StringComparer.Ordinal).ThenBy(m => m.MatchKey, StringComparer.Ordinal).TakeLast(5).ToArray();
    public static string Table(LeagueClubFacts facts)
    {
        var row = facts.Row;
        var rank = row.Rank is { } r ? $"position {r}{(row.Shared ? " (shared)" : "")}" : "unranked";
        return $"{facts.Club.Name}: {rank}, {row.Points} points from {row.Played} played; {row.Won} wins, {row.Drawn} draws, {row.Lost} losses, "
            + $"{row.GoalsFor} goals for, {row.GoalsAgainst} against, goal difference {row.GoalDifference}. "
            + $"Points change on date: {facts.PointsChange}. "
            + (facts.PositionChange is { } change ? $"Position movement: {change} (positive means places gained)." : "Previous played-date rank unavailable.");
    }
    private static bool ValidMatch(LeagueMatch m) => DateOnly.TryParseExact(m.Date, "yyyy-MM-dd", out _) && m.HomeGoals is >= 0 and <= 50 && m.AwayGoals is >= 0 and <= 50;
}

public sealed record HistoricalClubSelection(string? ClubId, string? Clarification);
public static class HistoricalClubResolver
{
    public static string Normalize(string text) => Regex.Replace((text ?? "").ToLowerInvariant().Replace("’s", "").Replace("'s", ""), "[^a-z0-9]+", " ").Trim();
    public static HistoricalClubSelection Resolve(LeagueHistoricalMatch fixture, LeagueClub[] clubs, string question, string? prior, string workflow)
    {
        var text = " " + Normalize(question) + " "; var hits = new List<(string Id, string Alias)>();
        foreach (var c in clubs)
        {
            var aliases = new[] { c.Id, c.Name, c.Initials }.Select(Normalize).Where(a => a.Length > 2).ToList();
            aliases.AddRange(c.Id switch { "stoke-city" => ["stoke"], "tottenham" => ["spurs"], "manchester-city" => ["man city"], "manchester-united" => ["man utd", "man united"],
                "aston-villa" => ["villa"], "crystal-palace" => ["palace"], "nottingham-forest" => ["forest"], _ => Array.Empty<string>() });
            foreach (var alias in aliases) if (text.Contains(" " + alias + " ", StringComparison.Ordinal)) hits.Add((c.Id, alias));
        }
        var ids = hits.Where(h => !hits.Any(o => o.Id != h.Id && o.Alias.Length > h.Alias.Length && (" " + o.Alias + " ").Contains(" " + h.Alias + " ", StringComparison.Ordinal)))
            .Select(h => h.Id).Distinct().ToArray();
        if (ids.Any(id => id != fixture.Home.Id && id != fixture.Away.Id)) return new(null, $"This conversation is about {fixture.Home.Name} and {fixture.Away.Name}. Which of those clubs did you mean?");
        var masked = text;
        foreach (var hit in hits.OrderByDescending(h => h.Alias.Length)) masked = masked.Replace(" " + hit.Alias + " ", " club ", StringComparison.Ordinal);
        if (Regex.IsMatch(masked, @"\b(?:barcelona|real madrid|bayern|psg)\b|\b(?:and|for) (?!club\b|their\b|them\b|both\b|the\b|this\b|that\b|a\b|me\b|us\b|form\b|table\b|points\b|ground\b|stadium\b|meetings\b|results\b|standings?\b)[a-z]+\s*\z")
            || Regex.IsMatch(masked, @"^ (?!club\b|home\b|away\b|hosts\b|visitors\b|both\b|each\b|compare\b|what\b|which\b|their\b|the\b|our\b|recent\b|pre\b|current\b|match\b|team\b|this\b|that\b|its\b)[a-z]+ (?:form|ground)\b"))
            return new(null, $"Which club do you mean: {fixture.Home.Name} or {fixture.Away.Name}?");
        if (ids.Length == 1) return new(ids[0], null);
        if (ids.Length == 2 || Regex.IsMatch(text, @"\b(?:both|each|two teams|home and away|compare|their league positions|their positions|(?:earlier|previous|prior) meetings?|head to head(?: meetings?)?)\b")) return new(null, null);
        if (Regex.IsMatch(text, @"\b(?:home|hosts)\b")) return new(fixture.Home.Id, null);
        if (Regex.IsMatch(text, @"\b(?:away|visitors)\b")) return new(fixture.Away.Id, null);
        if (Regex.IsMatch(text, @"\b(?:they|their|them|that club|that team)\b"))
            return prior is not null ? new(prior, null) : new(null, $"Do you mean {fixture.Home.Name} or {fixture.Away.Name}?");
        return new(workflow == "ground" ? prior ?? fixture.Home.Id : prior, null);
    }
}

public static class HistoricalReplyValidation
{
    private const RegexOptions Policy = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string DetailNoun = @"(?:goal[\s-]+(?:scorers?|times?|minutes?|events?|mechanisms?)|match events?|scorers?|assists?|penalt(?:y|ies)|red cards?|yellow cards?|lineups?|line-ups?|formations?|tactics?|tactical(?: (?:details|evidence|data))?|pressing|counterattacks?|headers?|corners?|cross(?:es)?|set[ -]pieces?|open[ -]play|rebounds?|volleys?|free[ -]kicks?|shots?|saves?|substitutions?|kick.?off(?: time)?|half.?time(?: score)?|stadium history|capacity)";
    private const string NounList = DetailNoun + @"(?:\s*(?:,\s*(?:(?:and|or)\s+)?|\s+(?:and|or)\s+)" + DetailNoun + @"){0,8}";
    // Remove only the negated availability predicate and its explicit object, never the rest of a sentence.
    private static readonly Regex NegativeCoverage = new(@"\b(?:no|without)\s+(?:(?:verified|reported|recorded|supplied|available|reliable)\s+){0,2}" + NounList
        + @"|\b" + NounList + @"\s+(?:(?:is|are|was|were|remains?|remain)\s+)?(?:unavailable|unknown|unverified|not (?:available|supplied|recorded|provided|known|established))\b"
        + @"|\b(?:I|we|(?:this|the) (?:record|source|data|(?:available|supplied) evidence))\s+(?:cannot|can't|do not|don't|does not|doesn't)\s+(?:say|tell|know|establish|identify|show|provide|supply|have|include|contain)\s+(?:you\s+)?(?:(?:any|the)\s+)?(?:who scored|how (?:the )?goals? (?:was|were) scored|" + NounList + @")", Policy);
    private static readonly Regex UnsupportedDetail = new(@"\b(?:" + DetailNoun + @"|scored|scoring|netted|sent off|pressed|opened|built|inaugurat\w*|renovat\w*)\b", Policy);
    public static (string Answer, MatchChatEvidence[] Evidence) Validate(string json, HistoricalPrepared prepared)
    {
        var result = MatchReplyValidation.Validate(json, prepared.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal));
        var cited = result.Evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var identitySentences = new List<string>();
        foreach (var paragraph in Regex.Split(result.Answer, @"\r?\n\s*\r?\n"))
        {
          string? statedScope = null;
          var statedClub = prepared.ClubId;
          foreach (var sentence in Regex.Split(paragraph, @"(?<=[.!?])\s+|\n"))
          {
            var policyText = NegativeCoverage.Replace(sentence.Replace('’', '\''), "");
            var named = prepared.GoalFacts.Where(f => Regex.IsMatch(sentence, @"\b(?:" + Regex.Escape(f.ClubName) + "|" + Regex.Escape(f.ClubId.Replace('-', ' ')) + @")\b", Policy))
                .Select(f => f.ClubId).Distinct(StringComparer.Ordinal).ToArray();
            var sample = ExplicitTeamSampleSubject(sentence, prepared);
            identitySentences.Add(sample is { } verified ? sentence.Remove(verified.ContextStart, verified.ContextLength) : sentence);
            if (named.Length > 0) statedClub = named.Length == 1 ? named[0] : sample?.ClubId;
            if (Regex.IsMatch(sentence, @"\b(?:in|during) (?:this|the|that) (?:fixture|match|game)\b|\bfull[ -]time\b", Policy)) statedScope = "fixture";
            else if (Regex.IsMatch(sentence, @"\b(?:earlier|previous|recent|last \d+|pre-match|before|preceding|sample)\b", Policy)) statedScope = "earlier_results";
            var scope = statedScope ?? "fixture";
            policyText = MaskVerifiedStatisticalGoals(policyText, prepared, cited, scope);
            policyText = MaskVerifiedClauseGoals(policyText, prepared, cited, scope);
            foreach (var fact in prepared.GoalFacts.Where(f => f.Scope == scope && cited.Contains(f.EvidenceId)))
            {
                var names = new List<string> { fact.ClubName, fact.ClubId.Replace('-', ' ') };
                if (fact.ClubId == "stoke-city") names.Add("Stoke");
                if (statedClub == fact.ClubId) names.Add("they");
                var subject = string.Join("|", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
                // Exact team/count/scope/evidence binding permits aggregate language, never a named scorer or action.
                var aggregate = @"\b(?:" + subject + @")\s+(?:had\s+)?(?<goal>scored\s+(?:(?:just|only)\s+)?" + fact.Goals.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"\b(?:\s+goals?)?)";
                policyText = Regex.Replace(policyText, aggregate, match => MaskLegacyGoal(match, policyText, prepared), Policy);
                // A trailing aggregate gerund inherits only one bound team subject, never a player or an ambiguous pair.
                if (statedClub == fact.ClubId && named.Length <= 1 && Regex.IsMatch(sentence, @"^\s*(?:" + subject + @")\s+(?:had|have|has|were|was|won|drew|lost)\b", Policy))
                    policyText = Regex.Replace(policyText, @",\s+(?<goal>scoring\s+" + fact.Goals.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"\s+goals?\b)",
                        match => MaskLegacyGoal(match, policyText, prepared), Policy);
            }
            if (UnsupportedDetail.IsMatch(policyText) || Regex.IsMatch(sentence, @"\b(?:scored|scoring)\b.*\b(?:through|via|courtesy of|minute|minutes|rebound|volley|free kick)\b", Policy))
                throw new InvalidDataException(DetailRejectionCode(sentence, policyText, prepared, cited, named.Length, statedClub, scope));
            if (prepared.Clubs.Any(c => sentence.Contains(c.Ground, StringComparison.OrdinalIgnoreCase))
                && Regex.IsMatch(RemoveNegatedVenuePredicate(sentence), @"\b(?:played|held|took place|staged|hosted|won|lost|drew)\b", Policy))
                throw new InvalidDataException("Season ground cannot establish the fixture venue.");
          }
        }
        var text = string.Join(" ", result.Evidence.Select(e => {
            using var document = JsonDocument.Parse(e.Text);
            return document.RootElement.GetProperty("sourceAnswer").GetString() ?? "";
        }));
        var identityText = string.Join(" ", identitySentences);
        foreach (var club in prepared.Clubs)
            if (Regex.IsMatch(identityText, @"(?<![\p{L}])" + Regex.Escape(club.Name) + @"(?![\p{L}])", RegexOptions.IgnoreCase) && !text.Contains(club.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Uncited club identity.");
        return result;
    }
    private static string CitedSourceAnswer(HistoricalPrepared prepared, IReadOnlySet<string> cited, string id)
    {
        if (!cited.Contains(id)) return "";
        var evidence = prepared.Evidence.SingleOrDefault(e => e.Id == id);
        if (evidence is null) return "";
        using var document = JsonDocument.Parse(evidence.Text);
        return document.RootElement.GetProperty("sourceAnswer").GetString() ?? "";
    }
    private static string MaskVerifiedStatisticalGoals(string text, HistoricalPrepared prepared, IReadOnlySet<string> cited, string scope)
    {
        if (scope != "earlier_results") return text;
        var pair = prepared.GoalFacts.Where(f => f.Scope == "fixture").DistinctBy(f => f.ClubId).ToArray();
        if (pair.Length != 2) return text;
        var form = CitedSourceAnswer(prepared, cited, "form");
        foreach (var fact in prepared.GoalFacts.Where(f => f.Scope == scope && cited.Contains(f.EvidenceId) && pair.Any(p => p.ClubId == f.ClubId)))
        {
            // These are the code-produced form fields, not arbitrary numbers borrowed from another evidence item.
            var stats = Regex.Match(form, @"(?:\A|\n)" + Regex.Escape(fact.ClubName) + @" before \d{4}-\d{2}-\d{2}: (?<sample>\d+) earlier results in this sample, "
                + @"(?<wins>\d+) wins, (?<draws>\d+) draws, (?<losses>\d+) losses; (?<points>\d+) points,", Policy);
            if (!stats.Success) continue;
            var team = "(?:" + string.Join("|", new[] { fact.ClubName, fact.ClubId.Replace('-', ' ') }.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).Select(Regex.Escape)) + ")";
            var count = stats.Groups["sample"].Value;
            var sample = @"(?:in|across|over)\s+(?:(?:the|this|that|their|its)\s+)?(?:" + count + @"\s+)?(?:earlier|previous|recent)\s+(?:sampled\s+)?results?(?:\s+in\s+(?:the|this)\s+sample)?";
            var end = @"(?:\s+" + sample + @")?\s*[.!?]?\s*\z";
            var goal = @"(?<goal>(?:scored|scoring)\s+(?<count>\d+)\s+goals?)";
            var wdl = stats.Groups["wins"].Value + @"\s+wins?,\s+" + stats.Groups["draws"].Value + @"\s+draws?\s+and\s+" + stats.Groups["losses"].Value + @"\s+loss(?:es)?";
            var record = @"(?:had\s+" + stats.Groups["wins"].Value + @"\s+wins?|(?:had|recorded)\s+" + wdl;
            if (stats.Groups["losses"].Value == count)
                record += @"|(?:had\s+)?lost\s+(?:all\s+)?" + count + @"\s+(?:of\s+their\s+)?earlier\s+results";
            record += ")";
            var points = @"(?:earned|earning|with|yielding|totalling|totaling|gaining|collecting)\s+" + stats.Groups["points"].Value + @"\s+points?";
            var statistical = record + @"(?:,\s+" + points + @")?(?:,\s+|\s+and\s+)" + goal + end;
            var heading = @"(?:(?:The|This)\s+result\s+(?:extended|continued)\s+a\s+(?:difficult|strong|mixed)\s+start\s+for\s+" + team
                + @"|Before\s+(?:the|this)\s+(?:match|fixture),\s+" + team + @"\s+had\s+(?:the\s+)?(?:stronger|weaker|better|worse)\s+recent\s+form)";
            var start = @"\A\s*(?:" + team + @"\s+|" + heading + @":\s*" + sample + @",\s+they\s+)";

            // A goal clause may end before this closed independent table statement only when its tuple is cited.
            // The continuation itself stays in policy text, so it cannot hide another action, identity or mechanism.
            var candidate = text;
            var continuation = Regex.Match(text, @",\s+and\s+this\s+(?:win|result)\s+came\s+alongside\s+a\s+(?:strong\s+)?end-of-date\s+table\s+position:\s+"
                + @"(?<rank>\d+)(?:st|nd|rd|th)\s+with\s+(?<points>\d+)\s+points?\s+from\s+(?<played>\d+)\s+(?:matches|games)\s*[.!?]?\s*\z", Policy);
            if (continuation.Success)
            {
                var table = Regex.Match(CitedSourceAnswer(prepared, cited, "table"), @"(?:\A|\n)" + Regex.Escape(fact.ClubName)
                    + @": position (?<rank>\d+)(?: \(shared\))?, (?<points>\d+) points from (?<played>\d+) played;", Policy);
                if (table.Success && new[] { "rank", "points", "played" }.All(field => table.Groups[field].Value == continuation.Groups[field].Value))
                    candidate = text[..continuation.Index];
            }
            var expression = Regex.Match(candidate, start + statistical, Policy);
            var opponent = prepared.GoalFacts.Where(f => f.Scope == scope && f.ClubId == pair.Single(p => p.ClubId != fact.ClubId).ClubId && cited.Contains(f.EvidenceId)).ToArray();
            var greater = opponent.Length == 1 && fact.Goals > opponent[0].Goals;
            if (!expression.Success && greater)
                expression = Regex.Match(candidate, @"\A\s*" + team + @"\s+had\s+been\s+more\s+productive\s+before\s+(?:the|this)\s+(?:match|fixture),\s+" + goal + end, Policy);
            if (expression.Success && expression.Groups["count"].Value == fact.Goals.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return text.Remove(expression.Groups["goal"].Index, expression.Groups["goal"].Length);
            if (!greater) continue;
            expression = Regex.Match(text, @"\A\s*(?:That|This)\s+contrast\s+makes\s+" + team + @"'s\s+(?:better|stronger)\s+results\s+and\s+"
                + @"(?<goal>greater\s+scoring\s+output)\s+stand\s+out(?:,\s+though\s+(?:the|these)\s+samples\s+cover\s+only\s+(?:those|the)\s+earlier\s+matches)?\s*[.!?]?\s*\z", Policy);
            if (expression.Success) return text.Remove(expression.Groups["goal"].Index, expression.Groups["goal"].Length);
        }
        return text;
    }
    private static string MaskLegacyGoal(Match match, string text, HistoricalPrepared prepared)
    {
        var goal = match.Groups["goal"];
        if (Regex.IsMatch(goal.Value, @"\Ascored\b", Policy) && !ClosedFiniteSubjectStart(text[..match.Index])) return match.Value;
        var remainder = text[(goal.Index + goal.Length)..];
        var team = string.Join("|", prepared.GoalFacts.Where(f => f.Scope == "fixture")
            .SelectMany(f => new[] { f.ClubName, f.ClubId.Replace('-', ' ') }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).Select(Regex.Escape));
        var next = Regex.Match(remainder, @"(?:[:;]|,\s*(?:while|whereas|but|and)|\b(?:and|while|whereas))\s*"
            + @"(?:(?:Before (?:the|this) (?:match|fixture)|At full[ -]time),\s*)?(?:" + team + @")\s+(?:had|have|has|were|was|won|drew|lost|scored|therefore|came)\b", Policy);
        if (next.Success) remainder = remainder[..next.Index];
        const string qualifier = @"(?:before\s+(?:this|the|that)\s+(?:fixture|match|game)|(?:in|during|across|over)\s+(?:(?:this|the|that)\s+(?:fixture|match|game)|(?:(?:the|this|that|these|those|its|their)\s+)?"
            + @"(?:(?:last\s+)?\d+\s+)?(?:(?:earlier|previous|recent|pre-match)\s+)?(?:results?(?:\s+sample)?|sample|matches|games)))";
        return Regex.IsMatch(remainder, @"\A(?:\s+" + qualifier + @")?(?:\s+and\s+(?:had\s+)?(?:conceded|conceding)\s+\d+(?:\s+goals?)?)?(?:\s+" + qualifier + @")?\s*[.!?]?\s*\z", Policy)
            ? match.Value.Remove(goal.Index - match.Index, goal.Length) : match.Value;
    }
    private static bool ClosedFiniteSubjectStart(string prefix)
    {
        // Prove a clause start and a closed introduction; arbitrary preceding nouns never establish its subject.
        var boundaries = Regex.Matches(prefix, @"[:;]|,\s*(?:while|whereas|but)\s+|\b(?:while|whereas)\s+", Policy);
        var introduction = boundaries.Count == 0 ? prefix : prefix[(boundaries[^1].Index + boundaries[^1].Length)..];
        const string sample = @"(?:(?:the|this|that|these|those|their|its)\s+)?(?:(?:last\s+)?\d+\s+)?"
            + @"(?:(?:earlier|previous|recent|pre-match)\s+)?(?:results?(?:\s+sample)?|sample)";
        const string temporal = @"(?:Before\s+(?:the|this|that)\s+(?:match|fixture|game)|At full[ -]time|(?:In|Across|Over|During)\s+" + sample
            + @"|(?:In|During)\s+(?:the|this|that)\s+(?:match|fixture|game)|Compared with\s+(?:(?:their|the|those)\s+)?(?:earlier|previous|recent)\s+results)";
        return Regex.IsMatch(introduction, @"\A\s*(?:" + temporal + @",\s*)?\z", Policy);
    }
    private static string MaskVerifiedClauseGoals(string text, HistoricalPrepared prepared, IReadOnlySet<string> cited, string scope)
    {
        var pair = prepared.GoalFacts.Where(f => f.Scope == "fixture").DistinctBy(f => f.ClubId).ToArray();
        if (pair.Length != 2) return text;
        var names = pair.SelectMany(f => new[] { f.ClubName, f.ClubId.Replace('-', ' ') }.Select(name => (Name: name, f.ClubId)))
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(n => n.ClubId).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().ClubId, StringComparer.OrdinalIgnoreCase);
        var team = string.Join("|", names.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape));
        var starts = Regex.Matches(text, @"(?<boundary>\A|[:;]\s*|,\s*(?:while|whereas|but|and)\s+|\b(?:and|while|whereas)\s+)\s*"
            + @"(?:(?:Before (?:the|this) (?:match|fixture)|At full[ -]time),\s*)?(?<team>" + team + @")\s+", Policy).Cast<Match>().ToArray();
        const string sample = @"(?:in|across)\s+(?:(?:the|this|that|its|their)\s+(?:(?:earlier|previous|recent)\s+)?(?:results?|sample)|theirs)";
        const string tail = @"(?:\s+(?:" + sample + @"|in (?:this|the|that) (?:fixture|match|game)))?\s*[.!?]?\s*\z";
        const string record = @"(?:lost|won|drawn)\s+(?:all\s+)?\d+\s+(?:earlier\s+)?results?(?:\s+" + sample + @")?";
        const string wdl = @"\d+\s+wins?,\s+\d+\s+draws?\s+and\s+\d+\s+loss(?:es)?";
        const string points = @"(?:with|earning|yielding|totalling|totaling|gaining|collecting)\s+\d+\s+points?";
        var spans = new List<(int Start, int Length)>();
        var previousEnd = -1; var previousVerified = false;
        for (var i = 0; i < starts.Length; i++)
        {
            var start = starts[i]; var bodyStart = start.Index + start.Length;
            var end = i + 1 < starts.Length ? starts[i + 1].Index : text.Length;
            var punctuation = text.IndexOfAny([':', ';'], bodyStart);
            if (punctuation >= 0 && punctuation < end) end = punctuation;
            var body = text[bodyStart..end];
            var bareAnd = Regex.IsMatch(start.Groups["boundary"].Value, @"\A,?\s*and\s+\z", Policy);
            var followsProvedClause = previousVerified && previousEnd == start.Index;
            previousEnd = end; previousVerified = false;
            if (bareAnd && !followsProvedClause) continue;
            // A coordinated pair of nouns is not two independent team predicates.
            if (i + 1 < starts.Length && string.IsNullOrWhiteSpace(body)
                && Regex.IsMatch(starts[i + 1].Groups["boundary"].Value, @"\band\b", Policy)) return text;
            var club = names[start.Groups["team"].Value];
            var facts = prepared.GoalFacts.Where(f => f.ClubId == club && f.Scope == scope && cited.Contains(f.EvidenceId)).ToArray();
            if (facts.Length != 1) continue;
            var fact = facts[0];
            var goal = Regex.Match(body, @"\A(?:had\s+)?(?<goal>scored\s+(?:(?:just|only)\s+)?(?<count>\d+)\s+goals?)" + tail, Policy);
            if (!goal.Success) goal = Regex.Match(body, @"\A(?:had|have|has)\s+(?:taken|earned|collected|gained)\s+\d+\s+points?\s+and\s+(?<goal>scored\s+(?<count>\d+)\s+goals?)" + tail, Policy);
            if (!goal.Success) goal = Regex.Match(body, @"\A(?:had|have|has)\s+(?:" + record + "|" + wdl + @")(?:,\s+" + points
                + @")?(?:,\s+|\s+and\s+)(?<goal>scoring\s+(?<count>\d+)\s+goals?)" + tail, Policy);
            if (goal.Success && goal.Groups["count"].Value == fact.Goals.ToString(System.Globalization.CultureInfo.InvariantCulture))
            { spans.Add((bodyStart + goal.Groups["goal"].Index, goal.Groups["goal"].Length)); previousVerified = true; }
            else if (fact.Goals == 0)
            {
                goal = Regex.Match(body, @"\A(?:had|have|has)\s+" + record + @"\s+(?<goal>without\s+scoring)" + tail, Policy);
                if (goal.Success) { spans.Add((bodyStart + goal.Groups["goal"].Index, goal.Groups["goal"].Length)); previousVerified = true; }
            }
            if (scope != "earlier_results") continue;
            var opponent = prepared.GoalFacts.Where(f => f.ClubId == pair.Single(p => p.ClubId != club).ClubId
                && f.Scope == scope && cited.Contains(f.EvidenceId)).ToArray();
            if (opponent.Length != 1 || fact.Goals <= opponent[0].Goals) continue;
            goal = Regex.Match(body, @"\A(?:therefore\s+)?(?:came in with|had)\s+(?:the\s+)?stronger\s+(?:recent|earlier|previous)\s+results\s+and\s+(?<goal>more\s+goals\s+scored)" + tail, Policy);
            if (goal.Success) { spans.Add((bodyStart + goal.Groups["goal"].Index, goal.Groups["goal"].Length)); previousVerified = true; }
        }
        // Only proved statistical expressions leave policy text; names, predicates and every residual token remain.
        foreach (var span in spans.OrderByDescending(s => s.Start)) text = text.Remove(span.Start, span.Length);
        return text;
    }
    private static (string ClubId, int ContextStart, int ContextLength)? ExplicitTeamSampleSubject(string sentence, HistoricalPrepared prepared)
    {
        // Only the captured explicit sample clause can distinguish the contextual opponent from the team subject.
        var clause = Regex.Match(sentence, @"^\s*Before the (?<opponent>.+?) fixture,\s*(?<team>.+?)['’]s sample showed (?<metrics>.+)$", Policy);
        if (!clause.Success || !Regex.IsMatch(clause.Groups["metrics"].Value,
            @"^\d+ wins?, \d+ draws? and \d+ loss(?:es)?(?:, (?:with|earning|yielding|totalling|totaling|gaining|collecting) \d+ points?)?\.?\s*$", Policy)) return null;
        var pair = prepared.GoalFacts.Where(f => f.Scope == "fixture").DistinctBy(f => f.ClubId).ToArray();
        if (pair.Length != 2) return null;
        var context = clause.Groups["opponent"];
        var opponent = pair.SingleOrDefault(f => string.Equals(f.ClubName, context.Value, StringComparison.OrdinalIgnoreCase));
        if (opponent is null) throw new InvalidDataException("Uncited club identity.");
        var team = pair.SingleOrDefault(f => string.Equals(f.ClubName, clause.Groups["team"].Value, StringComparison.OrdinalIgnoreCase));
        if (team is null || team.ClubId == opponent.ClubId) throw new InvalidDataException("Historical rejection: AggregateSubjectUnbound.");
        return (team.ClubId, context.Index, context.Length);
    }
    // Observational only: called after the unchanged rejection condition. No content leaves this method.
    private static string DetailRejectionCode(string sentence, string remaining, HistoricalPrepared prepared, IReadOnlySet<string> cited,
        int namedSubjects, string? statedClub, string scope)
    {
        try
        {
            if (Regex.IsMatch(sentence, @"\b(?:scored|scoring)\b.*\b(?:through|via|courtesy of|minute|minutes|rebound|volley|free kick)\b", Policy))
                return "Historical rejection: DetailMechanismQualifier.";
            if (Regex.IsMatch(remaining, @"\b" + DetailNoun + @"\b", Policy)) return "Historical rejection: DetailForbiddenNoun.";
            if (Regex.IsMatch(remaining, @"\b(?:netted|sent off|pressed|opened|built|inaugurat\w*|renovat\w*)\b", Policy)) return "Historical rejection: DetailForbiddenAction.";
            var gerund = Regex.IsMatch(remaining, @"\bscoring\b", Policy);
            string Subject(HistoricalGoalFact fact)
            {
                var names = new List<string> { fact.ClubName, fact.ClubId.Replace('-', ' ') };
                if (fact.ClubId == "stoke-city") names.Add("Stoke");
                if (statedClub == fact.ClubId) names.Add("they");
                return string.Join("|", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
            }
            var subjects = prepared.GoalFacts.Where(f => gerund
                ? statedClub == f.ClubId && Regex.IsMatch(sentence, @"^\s*(?:" + Subject(f) + @")\b", Policy)
                : Regex.IsMatch(remaining, @"\b(?:" + Subject(f) + @")\s+(?:(?:had|have|has)\s+)?scored\b", Policy)).DistinctBy(f => f.ClubId).ToArray();
            if (subjects.Length != 1 || gerund && namedSubjects > 1) return "Historical rejection: AggregateSubjectUnbound.";
            var scoped = prepared.GoalFacts.Where(f => f.ClubId == subjects[0].ClubId && f.Scope == scope).ToArray();
            if (scoped.Length == 0) return "Historical rejection: AggregateScopeMissing.";
            var supported = scoped.Where(f => cited.Contains(f.EvidenceId)).ToArray();
            if (supported.Length == 0) return "Historical rejection: AggregateCitationMissing.";
            var subject = Subject(subjects[0]);
            if (gerund && !Regex.IsMatch(sentence, @"^\s*(?:" + subject + @")\s+(?:had|have|has|were|was|won|drew|lost)\b", Policy))
                return "Historical rejection: AggregateTeamPredicateMismatch.";
            if (gerund && !Regex.IsMatch(remaining, @",\s+scoring\b", Policy)) return "Historical rejection: AggregateGerundWithoutComma.";
            var prefix = gerund ? @",\s+scoring\s+" : @"\b(?:" + subject + @")\s+(?:had\s+)?scored\s+(?:(?:just|only)\s+)?";
            var suffix = gerund ? @"\s+goals?\b" : @"\b(?:\s+goals?)?";
            if (Regex.IsMatch(remaining, prefix + @"\d+" + suffix, Policy)
                && !supported.Any(f => Regex.IsMatch(remaining, prefix + f.Goals.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix, Policy)))
                return "Historical rejection: AggregateValueMismatch.";
            if (!gerund && Regex.IsMatch(remaining, @"\bscored\s+a\s+total\s+of\b", Policy)) return "Historical rejection: AggregateFiniteTotalPhrase.";
            return gerund ? "Historical rejection: AggregateGerundGrammarMismatch." : "Historical rejection: AggregateFiniteGrammarMismatch.";
        }
        catch { return "Unsupported historical match detail."; }
    }
    private static string RemoveNegatedVenuePredicate(string sentence) => Regex.Replace(sentence.Replace('’', '\''),
        @"\b(?:I|we|this (?:record|source|data)|the (?:record|source|data))\s+(?:cannot|can't|does not|doesn't)\s+(?:confirm|establish|verify|show)\s+(?:that|whether|if)\s+(?:(?!\bbut\b|\bhowever\b)[^,;.!?]){1,180}", "", Policy);
}

public static class HistoricalTimeScope
{
    // This is an eligibility check, not topic routing: no model can authorize another date or live retrieval.
    public static bool SelectedFixture(string question, HistoricalChatContext context)
    {
        var text = HistoricalClubResolver.Normalize(question);
        if (Regex.IsMatch(text, @"\b(?:live|today|tomorrow|yesterday|right now|next (?:match|game|season)|last (?:year|season)|known then|known at the time|as it happened|another (?:match|game|date|season)|other (?:match|game|date|season))\b")) return false;
        foreach (Match m in Regex.Matches(question, @"\b\d{4}-\d{2}-\d{2}\b")) if (m.Value != context.Date) return false;
        foreach (Match m in Regex.Matches(question, @"\b\d{4}[-/]\d{2}\b(?!-\d)")) if (m.Value.Replace('/', '-') != context.SeasonKey) return false;
        foreach (Match m in Regex.Matches(question, @"\b(?:19|20)\d{2}\b"))
            if (m.Value != context.Date[..4] && m.Value != context.SeasonKey[..4]) return false;
        foreach (Match m in Regex.Matches(question, @"\b\d{1,2}\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{4}\b", RegexOptions.IgnoreCase))
            if (!DateOnly.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) || date.ToString("yyyy-MM-dd") != context.Date) return false;
        return true;
    }
}
