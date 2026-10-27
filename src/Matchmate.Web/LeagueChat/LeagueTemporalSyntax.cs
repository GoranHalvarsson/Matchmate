using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Matchmate.Web.LeagueChat;

// Raw, context-independent literals. Offsets always refer to the original text;
// selection, aliases and the host clock never participate in this scan.
internal static class LeagueTemporalSyntax
{
    internal enum Kind { Calendar, Season, CalendarYear, SeasonYear, Unresolved }
    internal sealed record Literal(int Start, int Length, Kind Type, bool Valid, int? Year = null,
        int? Month = null, int? Day = null, string? Season = null)
    {
        internal DateOnly? CalendarDate(int fallbackYear)
            => Type == Kind.Calendar && Valid && TryDate(Year ?? fallbackYear, Month!.Value, Day!.Value, out var date) ? date : null;
    }
    internal const string Marker = "temporalreference";
    internal const string MonthPattern = @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)";
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Separator = @"(?:\s*[-/\u2013\u2014_.]+\s*|\s+)";
    // Lexical atoms deliberately include malformed suffixes and incomplete
    // delimiters. Strict validation happens only after their entire capture.
    // A terminal sentence period stays outside; a dot joining digits does not.
    private const string NumberAtom = @"(?=[\p{L}\p{N}_]*\d)[\p{L}\p{N}_]+(?:(?:[-/\u2013\u2014_]+|\.(?=\d))[\p{L}\p{N}_]*)*";
    private const string NumberRun = NumberAtom + "(?:" + Separator + NumberAtom + @")*(?:\s*[-/\u2013\u2014_]+)?";
    private const string NamedDate = "(?:" + NumberAtom + @"\s+" + MonthPattern + @"[\p{L}\p{N}_]*|" + MonthPattern
        + @"[\p{L}\p{N}_]*\s+" + NumberAtom + @")(?:,?\s*" + NumberRun + @")?(?:\s*[-/\u2013\u2014_]+)?";
    private const string QuantityUnit = @"(?:goals?|points?|matches|games?|wins?|draws?|losses|defeats?|minutes?|seconds?)";
    // Atomic lexical capture cannot retry a shorter yearless named date or
    // numeric prefix after an invalid explicit year, ordinal or delimiter.
    private static readonly Regex Candidates = new(@"(?<![\p{L}\p{N}_])(?:(?<calendar>(?>" + NamedDate
        + @"))|(?<scoped>season\s+(?<number>(?>" + NumberRun + @"))|(?<number>(?>" + NumberRun
        + @"))\s+(?:season|table|standings)\b)|(?<numeric>(?>" + NumberRun + @")))(?![\p{L}\p{N}_])", Flags);

    internal static IReadOnlyList<Literal> Scan(string question)
    {
        var literals = new List<Literal>();
        foreach (Match match in Candidates.Matches(question))
        {
            Literal Invalid() => new(match.Index, match.Length, Kind.Unresolved, false);
            if (match.Groups["calendar"].Success)
            {
                var parsed = Regex.Match(match.Value, @"^(?:(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?<month>" + MonthPattern
                    + @")|(?<month>" + MonthPattern + @")\s+(?<day>\d{1,2})(?:st|nd|rd|th)?)(?:,?\s+(?<year>\d{4}))?$", Flags);
                if (!parsed.Success) { literals.Add(Invalid()); continue; }
                var month = parsed.Groups["month"].Value[..3].ToLowerInvariant() switch
                { "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6, "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, _ => 12 };
                if (!int.TryParse(parsed.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
                { literals.Add(Invalid()); continue; }
                int? year = null;
                if (parsed.Groups["year"].Success)
                {
                    if (!int.TryParse(parsed.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var explicitYear))
                    { literals.Add(Invalid()); continue; }
                    year = explicitYear;
                }
                // Leap-day without a year is syntactically possible. Assessment
                // validates it again using only the selected date/season year.
                literals.Add(new(match.Index, match.Length, Kind.Calendar, TryDate(year ?? 2000, month, day, out _), year, month, day));
                continue;
            }
            var scoped = match.Groups["scoped"].Success;
            var numberText = scoped ? match.Groups["number"].Value : match.Value;
            // Units license only complete pure quantities. They cannot erase an
            // explicit calendar/season cue, a malformed prefix, or a date run.
            if (Regex.IsMatch(numberText, @"^[0-9]+(?:\.[0-9]+)?" + QuantityUnit + "$", Flags)
                || Regex.IsMatch(numberText, @"^[0-9]+(?:\.[0-9]+)?$", Flags)
                    && Regex.IsMatch(question[(match.Index + match.Length)..], @"^\s+" + QuantityUnit + @"\b", Flags))
            {
                if (scoped || CalendarCue(question[..match.Index], false)) literals.Add(Invalid());
                continue;
            }
            if (!scoped && Regex.IsMatch(numberText, @"^\d{4}-\d{2}-\d{2}$", Flags))
            {
                var valid = DateOnly.TryParseExact(numberText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
                literals.Add(valid ? new(match.Index, match.Length, Kind.Calendar, true, date.Year, date.Month, date.Day) : Invalid());
                continue;
            }
            var season = Regex.Match(numberText, @"^(?<start>\d{4})[/\u2013-](?<end>\d{2}(?:\d{2})?)$", Flags);
            if (season.Success)
            {
                if (!int.TryParse(season.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(season.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                { literals.Add(Invalid()); continue; }
                var valid = start is >= 1 and < 9999 && (season.Groups["end"].Length == 2 ? end == (start + 1) % 100 : end == start + 1);
                literals.Add(new(match.Index, match.Length, Kind.Season, valid, start, Season: valid ? $"{start:D4}-{(start + 1) % 100:D2}" : null));
                continue;
            }
            if (Regex.IsMatch(numberText, @"^\d{4}$", Flags))
            {
                if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) { literals.Add(Invalid()); continue; }
                literals.Add(new(match.Index, match.Length, scoped ? Kind.SeasonYear : Kind.CalendarYear, year > 0, year));
                continue;
            }
            // Compact and joined-month dates are unsupported, including when
            // their small day number could otherwise resemble a quantity.
            var compact = Regex.IsMatch(numberText, @"(?<!\d)\d{8}(?!\d)", Flags);
            var joinedCalendar = Regex.IsMatch(numberText, "^(?:" + MonthPattern + @"(?=\d)[\p{L}\p{N}_]+|\d[\p{L}\p{N}_]*" + MonthPattern + @")(?=\s|$)", Flags);
            var parts = Regex.Matches(numberText, @"\d+", Flags).Select(part => part.Value).ToArray();
            var fragment = numberText.Contains('/') || numberText.Contains('_') || parts.Length >= 2 && numberText.Contains('.')
                || parts.Length >= 3 && parts[^1].Length >= 2 || parts.Length == 2 && parts.All(part => part.Length == 2);
            // A normal hyphenated score remains non-temporal without a calendar
            // cue. Every malformed year and complete date-shaped run stays whole.
            if (scoped || compact || joinedCalendar || fragment || parts.Any(part => part.Length >= 4)
                || CalendarCue(question[..match.Index], true)) literals.Add(Invalid());
        }
        return literals;
    }

    internal static string Project(string question, IReadOnlyList<Literal> literals, string stamp)
    {
        var result = new StringBuilder(question.Length); var at = 0;
        foreach (var literal in literals)
        {
            result.Append(question, at, literal.Start - at);
            result.Append(literal.Valid ? " " + stamp + " " : question.Substring(literal.Start, literal.Length));
            at = literal.Start + literal.Length;
        }
        return result.Append(question, at, question.Length - at).ToString();
    }
    private static bool TryDate(int year, int month, int day, out DateOnly date)
        => DateOnly.TryParseExact(FormattableString.Invariant($"{year:D4}-{month:D2}-{day:D2}"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    private static bool CalendarCue(string prefix, bool includeAmbiguousPrepositions)
        => Regex.IsMatch(prefix, @"\b(?:on|as\s+of|date|day|calendar|year|season|cutoff"
            + (includeAmbiguousPrepositions ? "|in|for|since|during|by" : "") + @")(?:\s+(?:the|a|an))?\s*$", Flags);
}
