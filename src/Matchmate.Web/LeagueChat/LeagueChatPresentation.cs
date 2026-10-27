using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.LeagueChat;

public static class LeagueChatPresentation
{
    public const string Version = "league-language-v1";
    public static string DisplayQuestion(LeagueSourceQuestion question, MatchLanguage language) =>
        MatchLanguages.Normalize(language) == MatchLanguage.English ? question.Question : LeagueChatText.Get(language, "Question." + question.Id);
    public static MatchModelMessage NarratorLanguage(MatchLanguage language) => new("system", Version + "\n" + ChatText.Get(language, "Language.Instruction")
        + " Language changes presentation only. Keep prepared club names, evidenceIds and source IDs unchanged. All existing grounding, numeric, temporal and citation rules remain authoritative. Do not add facts or translate the evidence payload.");

    public static string Feedback(string original, MatchChatOutcome outcome, MatchLanguage language)
    {
        if (MatchLanguages.Normalize(language) == MatchLanguage.English) return original;
        var key = original switch
        {
            "Live football data is not available here. This page is a pinned season snapshot, not today's live table." => "Live",
            "Please select the season and played date you want, then ask again. I will keep this question within the selected snapshot." => "SelectScope",
            _ => outcome switch
            {
                MatchChatOutcome.Clarify => "Clarify", MatchChatOutcome.Unsupported => "Unsupported",
                MatchChatOutcome.RoutingUnavailable => "Routing", MatchChatOutcome.InvalidRequest => "Input",
                MatchChatOutcome.Cancelled => "Cancelled", MatchChatOutcome.Stale => "Stale", _ => "Unavailable"
            }
        };
        return LeagueChatText.Get(language, "Feedback." + key);
    }

    // Presentation over the exact already-prepared JSON fields. No reads, English prose translation or evidence mutation.
    public static string SourceAnswer(ImmutableArray<MatchChatEvidence> evidence, MatchLanguage language)
    {
        if (MatchLanguages.Normalize(language) == MatchLanguage.English) return LeagueWorkflowExecutor.SourceAnswer(evidence);
        string T(string key, params object?[] values) => LeagueChatText.Get(language, "Source." + key, values);
        string Rank(JsonElement value, bool shared) => value.ValueKind == JsonValueKind.Number ? T(shared ? "SharedRank" : "Rank", value.GetInt32()) : T("Unranked");
        string Result(JsonElement match) => $"{match.GetProperty("Date").GetString()}: {match.GetProperty("home").GetString()} {match.GetProperty("HomeGoals").GetInt32()}–{match.GetProperty("AwayGoals").GetInt32()} {match.GetProperty("away").GetString()}.";
        var parts = new List<string>(); string? scope = null;
        foreach (var item in evidence.Where(e => evidence.Length == 1 || e.Id != "league_coverage"))
        {
            using var document = JsonDocument.Parse(item.Text); var root = document.RootElement; var facts = root.GetProperty("facts");
            scope ??= root.GetProperty("date").ValueKind == JsonValueKind.String
                ? T("Scope", root.GetProperty("season").GetString(), root.GetProperty("date").GetString())
                : T("BeforeResults", root.GetProperty("season").GetString());
            var text = item.Id switch
            {
                "table_snapshot" => Table(),
                "club_standing" => T("Standing", facts.GetProperty("club").GetString(), Rank(facts.GetProperty("Rank"), facts.GetProperty("Shared").GetBoolean()),
                    facts.GetProperty("Points").GetInt32(), facts.GetProperty("Played").GetInt32(), facts.GetProperty("Won").GetInt32(), facts.GetProperty("Drawn").GetInt32(), facts.GetProperty("Lost").GetInt32()),
                "club_results" => Recent(),
                "ground_reference" => T("Ground", facts.GetProperty("club").GetString(), facts.GetProperty("Ground").GetString(), facts.GetProperty("Town").GetString()),
                "date_change" => Changes(),
                "league_coverage" => T("Coverage", facts.GetProperty("completedResultsAtSelectedDate").GetInt32(), facts.GetProperty("scheduledFixtures").GetInt32()),
                _ => throw new InvalidDataException("Unsupported league source presentation.")
            };
            parts.Add(text);
            string Table()
            {
                var row = facts.GetProperty("rows")[0];
                return row.GetProperty("Rank").ValueKind == JsonValueKind.Null ? T("EmptyTable")
                    : T(row.GetProperty("Shared").GetBoolean() ? "SharedTable" : "Table", row.GetProperty("club").GetString(), row.GetProperty("Points").GetInt32(), row.GetProperty("Played").GetInt32(), facts.GetProperty("Completed").GetInt32());
            }
            string Recent()
            {
                var recent = facts.GetProperty("recent").EnumerateArray().ToArray();
                return recent.Length == 0 ? T("NoRecent", facts.GetProperty("club").GetString())
                    : T("Recent", facts.GetProperty("club").GetString(), string.Join(" ", recent.Select(Result)));
            }
            string Changes()
            {
                if (root.GetProperty("date").ValueKind == JsonValueKind.Null) return T("EmptyTable");
                var rule = T("DayRule");
                var rows = facts.GetProperty("rows").EnumerateArray().Select(row => T("Change", row.GetProperty("club").GetString(),
                    T(row.GetProperty("playedOnDate").GetBoolean() ? "Played" : "DidNotPlay"), row.GetProperty("previousPoints").GetInt32(), row.GetProperty("points").GetInt32(),
                    Rank(row.GetProperty("previousPosition"), row.GetProperty("previousShared").ValueKind == JsonValueKind.True), Rank(row.GetProperty("position"), row.GetProperty("shared").GetBoolean()))).ToArray();
                var results = facts.GetProperty("resultsOnSelectedDate").EnumerateArray().ToArray();
                var day = results.Length == 0 ? T("NoDayResults") : T("DayResults", string.Join(" ", results.Select(Result)));
                // Retain whole qualifications when the all-club rows exceed the conversational budget.
                var complete = string.Join(" ", rows) + " " + day + " " + rule;
                return complete.Length < 1400 ? complete : day + " " + rule + " " + T("Compact");
            }
        }
        var answer = scope + " " + string.Join(" ", parts);
        if (answer.Length > MatchChatLimits.AnswerChars) throw new InvalidDataException("Localized league source answer budget exceeded.");
        return answer;
    }
}
