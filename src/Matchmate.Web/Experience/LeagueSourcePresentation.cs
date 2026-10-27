using System.Globalization;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public sealed record LeagueSourceResult(string Date, string Home, string Away, int HomeGoals, int AwayGoals);
public sealed record LeagueSourceMovement(string Club, bool PlayedOnDate, int Played, int Points, int PreviousPoints, int PointsChange,
    int? Position, bool Shared, int? PreviousPosition, bool? PreviousShared)
{
    public bool Changed => PlayedOnDate || PointsChange != 0 || Points != PreviousPoints || Position != PreviousPosition || Shared != (PreviousShared ?? false);
}
public sealed record LeagueSourceDateChanges(string? Date, string? PreviousDate, IReadOnlyList<LeagueSourceMovement> Changed,
    IReadOnlyList<LeagueSourceMovement> Unchanged, IReadOnlyList<LeagueSourceResult> Results, string Rule, bool Preseason);
public sealed record LeagueSourceEvidence(string Text, LeagueSourceDateChanges? DateChanges = null, IReadOnlyList<LeagueSourceResult>? RecentResults = null);

// Read-only presentation of the executor's prepared fields. It does not fetch data,
// recalculate football statistics or alter stored answers/evidence.
public static class LeagueSourcePresentation
{
    public static IReadOnlyList<MatchChatEvidence> MainEvidence(IReadOnlyList<MatchChatEvidence> evidence)
        => evidence.Count == 1 ? evidence : evidence.Where(e => e.Id != "league_coverage").ToArray();

    public static LeagueSourceEvidence Read(MatchChatEvidence? evidence, string? fallback = null)
    {
        var text = string.IsNullOrWhiteSpace(fallback) ? "Source detail is unavailable for this reply." : fallback;
        if (evidence?.Text is not { Length: > 0 and <= 12000 } json) return new(text);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!Object(root) || !Text(root, "sourceAnswer", 12000, out var answer)) return new(text);
            text = answer;
            if (!root.TryGetProperty("facts", out var facts) || !Object(facts)) return new(text);
            if (evidence.Id == "date_change" && Date(root, "date", out var date) && Date(facts, "previousDate", out var previousDate)
                && Text(facts, "rule", 1000, out var rule) && Movements(facts, out var rows)
                && Results(facts, "resultsOnSelectedDate", date, 20, out var results)
                && (previousDate is null || date is not null && string.CompareOrdinal(previousDate, date) <= 0))
            {
                var preseason = date is null;
                if (preseason && (results.Count != 0 || rows.Any(row => row.Played != 0 || row.Points != 0 || row.PreviousPoints != 0
                    || row.PointsChange != 0 || row.Position is not null || row.PreviousPosition is not null || row.PlayedOnDate))) return new(text);
                return new(text, new(date, previousDate, rows.Where(row => row.Changed).ToArray(), rows.Where(row => !row.Changed).ToArray(), results, rule, preseason));
            }
            if (evidence.Id == "club_results" && Date(root, "date", out var cutoff) && Results(facts, "recent", cutoff, 3, out var recent))
                return new(text, RecentResults: recent);
        }
        catch (JsonException) { /* Preserve the safe existing answer when prepared detail is malformed. */ }
        return new(text);
    }

    public static string Position(int? rank, bool shared, MatchLanguage language = MatchLanguage.English) => rank is null ? LeagueText.Get(language, "Unranked")
        : shared ? LeagueText.Get(language, "SharedPosition", rank.Value) : rank.Value.ToString(MatchLanguages.Culture(language));
    public static string Change(int amount, MatchLanguage language = MatchLanguage.English) => amount.ToString("+0;-0;0", MatchLanguages.Culture(language));
    public static string ResultDate(string date, MatchLanguage language = MatchLanguage.English) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMM yyyy", MatchLanguages.Culture(language));

    private static bool Movements(JsonElement facts, out IReadOnlyList<LeagueSourceMovement> rows)
    {
        rows = [];
        if (!facts.TryGetProperty("rows", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 20) return false;
        var parsed = new List<LeagueSourceMovement>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in array.EnumerateArray())
        {
            if (!Object(row) || !Text(row, "club", 150, out var club) || !names.Add(club)
                || !Boolean(row, "playedOnDate", out var playedOnDate) || !Integer(row, "played", 0, 38, out var played)
                || !Integer(row, "points", 0, 114, out var points) || !Integer(row, "previousPoints", 0, 114, out var previousPoints)
                || !Integer(row, "pointsChange", -114, 114, out var pointsChange) || !NullableInteger(row, "position", 1, 20, out var position)
                || !Boolean(row, "shared", out var shared) || !NullableInteger(row, "previousPosition", 1, 20, out var previousPosition)
                || !NullableBoolean(row, "previousShared", out var previousShared) || previousPosition is not null && previousShared is null) return false;
            parsed.Add(new(club, playedOnDate, played, points, previousPoints, pointsChange, position, shared, previousPosition, previousShared));
        }
        rows = parsed.ToArray(); return true;
    }
    private static bool Results(JsonElement facts, string property, string? cutoff, int maximum, out IReadOnlyList<LeagueSourceResult> results)
    {
        results = [];
        if (!facts.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) return false;
        var parsed = new List<LeagueSourceResult>(); var keys = new HashSet<(string, string, string)>();
        foreach (var result in array.EnumerateArray())
        {
            if (!Object(result) || !Date(result, "Date", out var date) || date is null || cutoff is null || string.CompareOrdinal(date, cutoff) > 0
                || !Text(result, "home", 150, out var home) || !Text(result, "away", 150, out var away) || home == away || !keys.Add((date, home, away))
                || !Integer(result, "HomeGoals", 0, 100, out var homeGoals) || !Integer(result, "AwayGoals", 0, 100, out var awayGoals)) return false;
            parsed.Add(new(date, home, away, homeGoals, awayGoals));
        }
        results = parsed.ToArray(); return true;
    }
    private static bool Object(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static bool Text(JsonElement parent, string name, int maximum, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } text
            || text.Length > maximum || string.IsNullOrWhiteSpace(text)) return false;
        value = text; return true;
    }
    private static bool Integer(JsonElement parent, string name, int minimum, int maximum, out int value)
    {
        value = 0; return parent.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
            && item.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }
    private static bool NullableInteger(JsonElement parent, string name, int minimum, int maximum, out int? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        if (!Integer(parent, name, minimum, maximum, out var number)) return false;
        value = number; return true;
    }
    private static bool Boolean(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var item) || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = item.GetBoolean(); return true;
    }
    private static bool NullableBoolean(JsonElement parent, string name, out bool? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        if (!Boolean(parent, name, out var boolean)) return false;
        value = boolean; return true;
    }
    private static bool Date(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Null) return true;
        if (item.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(item.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        value = item.GetString(); return true;
    }
}
