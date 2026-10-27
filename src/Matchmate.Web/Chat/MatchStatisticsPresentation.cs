using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

// Display selection only, after the statistics workflow has retrieved and validated its unchanged evidence.
internal static class MatchStatisticsPresentation
{
    internal enum Metric { Overview, Shots, Attempts, Completed, Rate, ControlShare, ControlTime }
    internal sealed record Selection(Metric Metric, int[] TeamIndexes, string? Unavailable, bool ChartEligible, bool ChartRequested);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly HashSet<string> Words = ("a an and are at attempt attempts attempted been both by can club clubs compare compared comparing comparison could "
        + "accuracy complete completed completion control count did do does far finished for from generated give had has have how illustration illustrated in is made many match me more much of "
        + "on opponent or pass passes percent percentage please possession rate replay share show so statistics stats successful taken team teams tell the these this time to "
        + "than total totals up versus vs was were what which with would you duration long other as chart charts graph graphs bar diagram diagrams make create").Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static string Format(JsonElement data, string? question) => FormatForAudience(data, question, MatchAudienceMode.Fan);

    internal static string FormatForAudience(JsonElement data, string? question, MatchAudienceMode mode) => FormatForLanguage(data, question, mode, MatchLanguage.English);

    internal static string FormatForLanguage(JsonElement data, string? question, MatchAudienceMode mode, MatchLanguage language)
    {
        string T(string key, params object?[] args) => ChatText.Get(language, "Stats." + key, args);
        var teams = data.GetProperty("teams").EnumerateArray().ToArray();
        var prefix = T("Prefix", data.GetProperty("replayClock").GetString());
        var selection = Select(data, question, language);
        if (selection.Unavailable is { } unavailable) return prefix + T(unavailable);
        var metric = selection.Metric;
        var selected = selection.TeamIndexes.Select(index => teams[index]).ToArray();
        string Rows(Func<JsonElement, string> row) => string.Join("; ", selected.Select(row)) + ".";
        string Name(JsonElement team) => team.GetProperty("name").GetString()!;
        int Count(JsonElement team, string property) => team.GetProperty(property).GetInt32();
        string Percent(JsonElement value) => value.ValueKind == JsonValueKind.Null ? T("Unavailable") : value.GetDecimal().ToString("0.#", MatchLanguages.Culture(language)) + "%";
        string Duration(long ms) => HumanDuration(ms, language);
        string Rate(JsonElement team) => Percent(team.GetProperty("passCompletionPercent"));
        string PassWord(JsonElement team) => T(Count(team, "passAttempts") == 1 ? "PassOne" : "PassMany");
        var caveat = T("Exclusions");
        var answer = prefix + (metric switch
        {
            Metric.Shots => Rows(team => T(Count(team, "shots") switch { 0 => selected.All(t => Count(t, "shots") == 0) ? "ShotsNone" : "ShotsZero", 1 => "ShotOne", _ => "ShotsMany" }, Name(team), Count(team, "shots"))),
            Metric.Attempts => Rows(team => T(Count(team, "passAttempts") switch { 0 => "AttemptsZero", 1 => "AttemptsOne", _ => "AttemptsMany" }, Name(team), Count(team, "passAttempts"))),
            Metric.Completed => Rows(team => T(Count(team, "passCompleted") switch { 0 => "CompletedZero", 1 => "CompletedOne", _ => "CompletedMany" }, Name(team), Count(team, "passCompleted"))),
            Metric.Rate => Rows(team => Count(team, "passAttempts") == 0 ? T("RateZero", Name(team)) : T("Rate", Name(team), Count(team, "passCompleted"), Count(team, "passAttempts"), PassWord(team), Rate(team))),
            Metric.ControlTime => Rows(team => T("ControlTime", Name(team), Duration(team.GetProperty("controlledMs").GetInt64()))) + T("FinishedOnly") + caveat,
            Metric.ControlShare => Rows(team => T("ControlShare", Name(team), Percent(team.GetProperty("illustratedControlSharePercent")))) + T("ControlDenominator", Duration(data.GetProperty("totalClassifiedControlMs").GetInt64())) + caveat,
            _ => Rows(team => T("Overview", Name(team), Count(team, "shots"), T(Count(team, "shots") == 1 ? "ShotWordOne" : "ShotWordMany"), Count(team, "passCompleted"), Count(team, "passAttempts"), PassWord(team), Rate(team), Duration(team.GetProperty("controlledMs").GetInt64()), Percent(team.GetProperty("illustratedControlSharePercent"))))
                + T("ControlDenominator", Duration(data.GetProperty("totalClassifiedControlMs").GetInt64())) + caveat
        });
        if (selection.ChartRequested && !selection.ChartEligible) answer += ChatText.Get(language, "Chart.Unavailable");
        if (MatchAudiencePresentation.Normalize(mode) != MatchAudienceMode.Analyst) return answer;
        return answer + (metric switch
        {
            Metric.Shots => T("MethodShots"), Metric.Attempts => T("MethodAttempts"), Metric.Completed => T("MethodCompleted"), Metric.Rate => T("MethodRate"),
            Metric.ControlTime => T("MethodTime"), Metric.ControlShare => T("MethodShare"), _ => T("MethodOverview")
        });
    }

    // Both text and charts use this closed selector after the unchanged evidence validation.
    internal static Selection Select(JsonElement data, string? question, MatchLanguage language)
    {
        var teams = data.GetProperty("teams").EnumerateArray().ToArray();
        var text = MatchLocalizedIntent.Canonical((question ?? "").Trim());
        bool Has(string pattern) => Regex.IsMatch(text, pattern, Options);
        var chartRequested = Has(@"\b(charts?|graphs?|diagrams?)\b");
        Selection Unsupported(string key) => new(Metric.Overview, [0, 1], key, false, chartRequested);
        if (Has(@"\b(actual|real|measured)\b")) return Unsupported("ActualUnavailable");
        if (Has(@"\b(on[ -]target|off[ -]target)\b")) return Unsupported("TargetUnavailable");
        if (Has(@"\b(xg|expected goals|speed|distance)\b")) return Unsupported("MeasuredUnavailable");
        if (Has(@"\b(player|players|individual|person)\b|\bpass(?:es)?\s+to\b")) return Unsupported("PlayerUnavailable");
        if (Has(@"\b(first|second|last|previous|earlier|half|halves|minutes?|since|between|before|after)\b"))
            return Unsupported("WindowUnavailable");

        var remaining = text; var named = new List<JsonElement>();
        foreach (var team in teams)
        {
            var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(team.GetProperty("name").GetString()!)
                + (language == MatchLanguage.Swedish ? @"(?:['’]s|s)?(?![\p{L}\p{N}])" : @"(?:['’]s)?(?![\p{L}\p{N}])");
            if (Regex.IsMatch(text, pattern, Options)) named.Add(team);
            remaining = Regex.Replace(remaining, pattern, " ", Options);
        }
        var shots = Has(@"\bshots?\b"); var passes = Has(@"\bpass(?:es)?\b|\bcompletion\b"); var control = Has(@"\b(control|possession)\b");
        var rate = passes && Has(@"\b(rate|percent|percentage|completion|accuracy)\b");
        var completed = passes && Has(@"\b(completed|successful)\b"); var attempts = passes && Has(@"\b(attempts?|attempted)\b");
        var share = control && Has(@"\b(share|percent|percentage)\b"); var time = control && Has(@"\b(time|duration|long)\b");
        // Unknown wording, pronouns, negation and compound scopes keep the overview rather than guessing a subject or metric.
        var recognized = Regex.Matches(remaining, @"[\p{L}\p{N}]+").All(word => Words.Contains(word.Value) || word.Value.Equals("shot", StringComparison.OrdinalIgnoreCase) || word.Value.Equals("shots", StringComparison.OrdinalIgnoreCase));
        if (recognized && HasNonTeamSubject(teams, question ?? "")) return Unsupported("PlayerUnavailable");
        var compound = (shots ? 1 : 0) + (passes ? 1 : 0) + (control ? 1 : 0) != 1
            || share && time || rate && (completed || attempts) && Has(@"\b(and|or)\b") || completed && attempts;
        var metric = !recognized || compound ? Metric.Overview : shots ? Metric.Shots : control ? time ? Metric.ControlTime : Metric.ControlShare
            : rate ? Metric.Rate : completed ? Metric.Completed : Metric.Attempts;
        var indexes = metric != Metric.Overview && named.Count == 1 && !Has(@"\b(both|compare|compared|comparing|comparison|versus|vs|opponent|other|more|than)\b")
            ? new[] { Array.FindIndex(teams, team => team.GetProperty("name").GetString() == named[0].GetProperty("name").GetString()) } : Enumerable.Range(0, teams.Length).ToArray();
        var eligible = recognized && !control && !completed && (metric is Metric.Shots or Metric.Attempts or Metric.Rate
            || metric == Metric.Overview && (!shots && !passes || shots && passes || rate && attempts));
        return new(metric, indexes, null, eligible, chartRequested);
    }

    private static bool HasNonTeamSubject(JsonElement[] teams, string question)
    {
        // Inspect explicit actor qualifications before aliases can turn a name such as Son into "are",
        // or make a name such as Can look like the allowed English modal. Exact fixture teams remain valid.
        var raw = Regex.Replace(question, @"\bpor\s+favor\b", "please", Options);
        foreach (var team in teams)
            raw = Regex.Replace(raw, @"(?<![\p{L}\p{N}])" + Regex.Escape(team.GetProperty("name").GetString()!) + @"(?:['’]s|s)?(?![\p{L}\p{N}])", "chartteam", Options);
        const string metrics = @"(?:passes?|shots?|pases?|tiros?|disparos?|passningar|passning|skott|passningsförsök|passningssäkerhet|passningsprocent|passningsprecision)";
        const string metricIntroducer = @"^(?:chartteam|a|an|the|both|team|teams|other|opponent|this|these|generated|replay|show|compare|many|more|total|completed|successful|attempted|of|for|in|and|or|versus|vs|accuracy|rate|completion|percent|percentage|bar|chart|graph|diagram|visa|visar|många|båda|lagen|laget|lag|lyckade|av|för|i|och|eller|över|ett|cuántos|cuántas|de|del|el|los|las|la|ambos|equipo|equipos|mostrar|muestra|muéstrame|completados|exitosos|intentos|en|por|precisión|porcentaje)$";
        // A bare subject immediately before the metric is a subject slot too; the modal in "Can you show" is not.
        foreach (Match bare in Regex.Matches(raw, @"\b(?<subject>[\p{L}\p{N}]+)\s+(?=" + metrics + @"\b)", Options))
            if (!Regex.IsMatch(bare.Groups["subject"].Value, metricIntroducer, Options)) return true;
        const string teamSubject = @"(?:chartteam|(?:(?:the|both|these|this|other)\s+)?teams?|(?:the\s+)?opponent|(?:båda\s+)?(?:lagen|laget|lag)|(?:(?:ambos|los|el)\s+)?equipos?|(?:this|the)\s+(?:generated\s+)?(?:replay|match)|(?:esta|la)\s+repetición(?:\s+generada)?)\b";
        const string actorLink = @"\b(?:by|av|por|versus|vs|than|compared\s+with|jämfört\s+med|comparado\s+con)\s+";
        const string metricLink = @"\b(?:passes?|attempts?|shots?|accuracy|rate|pases?|tiros?|disparos?|passningar|passning|skott|passningsförsök|passningssäkerhet|passningsprocent|passningsprecision)\s+(?:of|de|from|for|för|para)\s+";
        const string subjectVerb = @"\b(?<verb>has|have|har|ha|han|tiene)\s+";
        const string continuationVerb = @"(?:made|taken|completed|attempted|been|had|gjort|slagit|tagit|realizado|completado|completados|hecho)\b";
        const string continuation = @"(?:as|in|at|so|please|more|less|fewer|made|taken|completed|attempted|been|had|som|i|vid|hittills|gjort|slagit|tagit|como|en|hasta|realizado|completado|completados|hecho|passes?|shots?|pases?|tiros?|passningar|skott)\b";
        foreach (Match link in Regex.Matches(raw, "(?:" + actorLink + "|" + metricLink + "|" + subjectVerb + ")", Options))
        {
            var tail = raw[(link.Index + link.Length)..].TrimStart();
            if (link.Groups["verb"].Success && Regex.IsMatch(tail, "^" + continuationVerb, Options)) continue;
            while (true)
            {
                var subject = Regex.Match(tail, "^" + teamSubject, Options);
                if (!subject.Success) return true;
                tail = tail[subject.Length..].TrimStart();
                var conjunction = Regex.Match(tail, @"^(?:(?:and|or|och|eller|y|o|&)\s+|,\s*(?:(?:and|or|och|eller|y|o)\s+)?)", Options);
                if (!conjunction.Success) break;
                tail = tail[conjunction.Length..].TrimStart(); // Every joined subject must be a known team too.
            }
            if (tail.Length > 0 && !tail.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c)) && !Regex.IsMatch(tail, "^" + continuation, Options)) return true;
        }
        return false;
    }

    private static string HumanDuration(long milliseconds, MatchLanguage language)
    {
        var seconds = milliseconds / 1000; var minutes = seconds / 60; seconds %= 60;
        return minutes == 0 ? ChatText.Get(language, seconds == 1 ? "Stats.Second" : "Stats.Seconds", seconds)
            : ChatText.Get(language, seconds == 0 ? "Stats.Minutes" : "Stats.MinutesSeconds", minutes, seconds);
    }
}
