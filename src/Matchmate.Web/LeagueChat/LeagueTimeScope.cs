using System.Globalization;
using System.Text.RegularExpressions;

namespace Matchmate.Web.LeagueChat;

public enum LeagueTimeResolution { MatchesSelection, DifferentTime, RealWorldNow, Unresolved }
public enum LeagueTimeAssessment { NoReference, MatchesSelection, DifferentTime, RealWorldNow, Unresolved }

// A bounded scope validator shared by routing and execution. It never selects a
// football topic, enumerates available dates, or fetches a substitute snapshot.
public static class LeagueTimeScope
{
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Month = LeagueTemporalSyntax.MonthPattern;
    private const string UnresolvedCalendar = @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Christmas(?:\s+(?:Day|Eve))?|Easter(?:\s+(?:Sunday|Monday))?|New\s+Year['\u2019]?s?(?:\s+(?:Day|Eve))?)";
    private const string InstantQualifier = @"(?:current|present|real[-\s]+world)";
    private const string CurrentInstant = @"(?:(?:this|the)\s+(?:" + InstantQualifier + @"\s+)?(?:very\s+)?|" + InstantQualifier + @"\s+(?:very\s+)?)(?:moment|instant|minute|second)";
    private const string ActualPresent = @"(?:live|right\s+now|current[-\s]+now|today|tonight|real[-\s]+time|latest\s+real|now|currently|at\s+present|at\s+(?:(?:this|the)\s+)?(?:" + InstantQualifier + @"\s+)?(?:very\s+)?moment|(?:at|as\s+of)\s+(?:right\s+)?" + CurrentInstant
        + @"|right\s+(?:at\s+)?" + CurrentInstant + @"|as\s+we\s+speak|as\s+we(?:\s+are|['\u2019]re)\s+speaking)";
    private const string UnresolvedDeicticTime = @"(?:this|that|current|present)\s+(?:very\s+)?(?:week|month|year|morning|afternoon|evening|night|hour|moment|instant|minute|second)s?";
    private const string OutsideView = @"(?:outside(?:\s+of)?|beyond|not[-\s]+in)\s+(?:(?:this|that|the|our|selected|current)\s+){0,2}(?:replay|snapshot|view)";
    private const string TimeUnit = @"(?:(?:match|played)\s+(?:date|day)s?|(?:season|year|date|day|week|month)s?)";
    private const string RelativeTime = @"(?:(?:last|next|another|other|previous|different|preceding|following)\s+" + TimeUnit
        + @"|" + TimeUnit + @"\s+(?:(?:immediately|directly)\s+)?(?:before|after|preceding|following)\s+(?:(?:this|that|the|our|selected|current)\s+){0,2}(?:one(?:\s+selected)?|" + TimeUnit + @")(?:\s+selected)?|all\s+time|yesterday|tomorrow|end\s+of\s+season)";
    internal const string TaskOrderRequest = @"(?:please\s+)?(?:(?:can|could|would|will|may)\s+(?:you|we|I)\s+)?(?:please\s+)?(?:(?:explain|show|tell|describe|summari[sz]e|recap|review|give|list|see|get)(?:\s+(?:me|us))?\b|(?:break\s+down|lay\s+out|sum\s+up)\b|(?:talk|walk|run|take)(?:\s+(?:me|us))?\s+through\b)";
    private const string PreseasonReference = @"(?:before\s+(?:the\s+)?(?:first(?:\s+played)?\s+(?:days?|dates?|match(?:es)?|results?|fixtures?)|any\s+(?:results?|match(?:es)?|fixtures?))|pre[- ]?season|season start)";
    private const string SelectionWords = @"\b(?:here|(?:this|selected|the selected) (?:(?:particular|older|historical|current|played|committed|selected) )*(?:date|day|snapshot|view|cutoff|reference|season|table|standings)(?:['\u2019]s)?(?:\s+(?:reference|snapshot|view))?|(?:recent|latest) (?:form|results|matches|games)|last (?:[1-3]|one|two|three) (?:results|matches|games)|so far|to date)\b";
    internal const string SelectedDay = @"\b(?:(?:(?:the|this|that|our)\s+)?(?:currently\s+)?(?:selected|chosen|shown)\s+(?:(?:match|played)\s+)?(?:date|day)|(?:(?:the|this|that|our)\s+)?(?:(?:match|played)\s+)?(?:date|day)\s+(?:(?:that|which)\s+)?(?:(?:I|we)(?:['\u2019]ve|\s+have)?\s+(?:currently\s+)?(?:selected|chosen|chose)|(?:currently\s+)?(?:selected|chosen|shown)))\b";
    public static string Semantic(LeagueTimeResolution value) => value switch
    {
        LeagueTimeResolution.MatchesSelection => "selected_snapshot", LeagueTimeResolution.DifferentTime => "different_time",
        LeagueTimeResolution.RealWorldNow => "real_world_now", _ => "unresolved"
    };
    internal static string TaskOrderView(string question)
        => Regex.IsMatch(question, @"\b" + OutsideView + @"\b", Flags) ? question
            : Regex.Replace(question, @"^\s*now(?:\s*,\s*|\s+)(?=" + TaskOrderRequest + @"(?:\s|[,;:]|$))", "", Flags);
    internal static string RequestBody(string question) => Regex.Replace(TaskOrderView(question).Trim(),
        @"^(?:(?:" + TaskOrderRequest + @")(?:\s*[,;:]\s*|\s+|$)){1,4}", "", Flags);
    public static bool HasTimeSyntax(string question) => Regex.IsMatch(question,
        @"\d|\b(?:" + Month + @"|" + UnresolvedCalendar + @"|" + ActualPresent + @"|" + OutsideView + @"|" + RelativeTime + @"|" + UnresolvedDeicticTime + @"|" + PreseasonReference + @"|season|date|day|week|month|year)\b", Flags);
    public static LeagueTimeResolution Resolve(string? season, string? selectedDate, string? question, bool explicitReference = false)
        => Assess(season, selectedDate, question) switch
        {
            LeagueTimeAssessment.MatchesSelection => LeagueTimeResolution.MatchesSelection,
            LeagueTimeAssessment.NoReference => explicitReference ? LeagueTimeResolution.Unresolved : LeagueTimeResolution.MatchesSelection,
            LeagueTimeAssessment.DifferentTime => LeagueTimeResolution.DifferentTime,
            LeagueTimeAssessment.RealWorldNow => LeagueTimeResolution.RealWorldNow,
            _ => LeagueTimeResolution.Unresolved
        };
    public static LeagueTimeAssessment Assess(string? season, string? selectedDate, string? question)
    {
        if (season is null || !Regex.IsMatch(season, @"^[0-9]{4}-[0-9]{2}$", Flags) || string.IsNullOrWhiteSpace(question)
            || selectedDate is not null && !DateOnly.TryParseExact(selectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return LeagueTimeAssessment.Unresolved;
        // Only a leading task-order Now before a bounded request is discourse. Keep
        // every later cue and every explicit outside-view qualifier authoritative.
        // CURRENTLY modifies the explicit UI selection action only inside a
        // complete date/day reference. Keep every independent time cue intact.
        var assessmentQuestion = Regex.Replace(TaskOrderView(question), SelectedDay,
            match => Regex.Replace(match.Value, @"\bcurrently\s+", "", Flags), Flags);
        var outsideView = Regex.IsMatch(question, @"\b" + OutsideView + @"\b", Flags);
        if (Regex.IsMatch(assessmentQuestion, @"\b" + ActualPresent + @"\b", Flags)) return LeagueTimeAssessment.RealWorldNow;
        if (Regex.IsMatch(assessmentQuestion, @"\b" + RelativeTime + @"\b", Flags)) return LeagueTimeAssessment.DifferentTime;
        var literals = LeagueTemporalSyntax.Scan(question);
        var recognized = assessmentQuestion != question || literals.Count > 0; var different = false; var invalid = false;
        var year = int.Parse(selectedDate is not null ? selectedDate[..4] : season[..4], CultureInfo.InvariantCulture);
        var remainder = question;
        foreach (var literal in literals.Reverse())
        {
            // Remove exactly the original complete span. No smaller year or
            // season substring can survive an invalid date-shaped literal.
            remainder = remainder.Remove(literal.Start, literal.Length).Insert(literal.Start, " ");
            if (!literal.Valid) { invalid = true; continue; }
            switch (literal.Type)
            {
                case LeagueTemporalSyntax.Kind.Calendar:
                    var date = literal.CalendarDate(year);
                    if (date is null) invalid = true;
                    else if (date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != selectedDate) different = true;
                    break;
                case LeagueTemporalSyntax.Kind.Season:
                    if (literal.Season != season) different = true;
                    break;
                case LeagueTemporalSyntax.Kind.SeasonYear:
                    if (literal.Year != int.Parse(season[..4], CultureInfo.InvariantCulture)) different = true;
                    break;
                case LeagueTemporalSyntax.Kind.CalendarYear:
                    if (literal.Year != year) different = true;
                    break;
            }
        }
        remainder = Regex.Replace(TaskOrderView(remainder), SelectedDay,
            match => Regex.Replace(match.Value, @"\bcurrently\s+", "", Flags), Flags);
        remainder = Regex.Replace(remainder, @"\b" + PreseasonReference + @"\b", _ =>
        { recognized = true; if (selectedDate is not null) different = true; return " "; }, Flags);
        // Complete UI day references identify the committed day. Consume only the
        // bounded reference, leaving every separate calendar/current/relative cue intact.
        remainder = Regex.Replace(remainder, SelectedDay, _ => { recognized = true; return " "; }, Flags);
        remainder = Regex.Replace(remainder, SelectionWords, _ => { recognized = true; return " "; }, Flags);
        // Recognizable but unresolved calendar syntax still cannot acquire selected facts,
        // even when the model incorrectly says selected_view. Arbitrary language is not proven.
        // Complete dates are already consumed. A remaining month alone cannot identify a day;
        // a temporal preposition keeps ordinary modal wording such as "May we" out of this guard.
        if (Regex.IsMatch(remainder, @"\b(?:in|on|as of|since|during|by)\s+(?:(?:the|a)\s+)?" + Month + @"\b", Flags)) invalid = true;
        // Month-granularity syntax remains unresolved under either model choice. Remove
        // grammatical modal/verb uses only; selection anchors never erase other calendar tokens.
        var calendarRemainder = Regex.Replace(remainder, @"\bMay(?=\s+(?:I|we|you|they|he|she|it)\b)", " ", Flags);
        calendarRemainder = Regex.Replace(calendarRemainder, @"\b(?<subject>(?:(?:can|could|may|might|will|would|should|must)\s+)?(?:I|we|you|they|he|she|it)\s+(?:(?:can|could|may|might|will|would|should|must)\s+)?)march\b", "${subject} ", Flags);
        if (Regex.IsMatch(calendarRemainder, @"\b(?:" + Month + @"|" + UnresolvedCalendar + @")\b", Flags)) invalid = true;
        if (Regex.IsMatch(remainder, @"\b(?:on|as of|since|during|by)\s+(?:(?:the|a)\s+)?(?:\d|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|\w+\s+(?:\d|day|eve|weekend)\b)|\b(?:in|on)\s+\w+\s+\d", Flags)) invalid = true;
        // Only complete selected/calendar references were consumed above. A remaining
        // bounded deictic period/daypart cannot become an implicit page default, even
        // when another anchor matched. Numerical football quantities are not this syntax.
        if (Regex.IsMatch(remainder, @"\b" + UnresolvedDeicticTime + @"\b", Flags)) invalid = true;
        if (invalid) return LeagueTimeAssessment.Unresolved;
        if (different) return LeagueTimeAssessment.DifferentTime;
        if (outsideView) return LeagueTimeAssessment.Unresolved;
        return recognized ? LeagueTimeAssessment.MatchesSelection : LeagueTimeAssessment.NoReference;
    }
}
public static class LeagueQuestionProjection
{
    public static bool BriefIdentityReply(string question) => BriefIdentity(question) is not null;
    public static string? BriefIdentity(string question)
    {
        if (question.Length is < 1 or > 80) return null;
        const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var name = Regex.Replace(question.Trim(), @"^please(?:\s*,\s*|\s+)", "", flags);
        name = Regex.Replace(name, @"(?:\s*,\s*|\s+)please[.!?]?$", "", flags);
        if (!Regex.IsMatch(name, @"^[\p{L}][\p{L} .'\u2019&-]*[.!?]?$", RegexOptions.CultureInvariant)) return null;
        var text = LeagueClubResolver.Normalize(name); var words = text.Split(' ');
        if (words.Length is < 1 or > 5 || LeagueTimeScope.HasTimeSyntax(name)) return null;
        // Speech-act/function words and explicit new-topic nouns exclude full questions.
        // This shape check grants no identity: one trusted club and explicit binding are required later.
        return Regex.IsMatch(text, @"\b(?:what|why|how|who|where|when|tell|show|explain|give|does|do|did|is|are|can|could|go|keep|continue|more|yes|no|we|our|they|their|it|that|this|on|in|at|for|by|before|after|points|table|standings|position|results|form|ground|stadium|coverage|source)\b", RegexOptions.CultureInvariant) ? null : text;
    }
    public static string? Original(LeagueRoutingInput input) => input.Clarification is not null && BriefIdentityReply(input.Question) ? input.Clarification.OriginalQuestion : null;
    public static string WorkflowQuestion(LeagueRoutingInput input) => Original(input) ?? input.Question;
}
