using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.HistoricalChat;

public static class HistoricalChatPresentation
{
    public const string Version = "historical-language-v1";
    public static string DisplayQuestion(HistoricalSuggestion suggestion, MatchLanguage language) => MatchLanguages.Normalize(language) == MatchLanguage.English
        ? suggestion.Question : HistoricalChatText.Get(language, "Question." + suggestion.Id);
    public static string DisplayTitle(HistoricalSuggestion suggestion, MatchLanguage language) => MatchLanguages.Normalize(language) == MatchLanguage.English
        ? suggestion.Title : HistoricalChatText.Get(language, "Title." + suggestion.Id);
    public static MatchModelMessage NarratorLanguage(MatchLanguage language) => new("system", Version + "\n" + ChatText.Get(language, "Language.Instruction")
        + " Language changes presentation only. Keep prepared club names, evidenceIds and source IDs unchanged. Existing grounding, numeric, temporal, goal scope and citation rules remain authoritative. "
        + HistoricalChatText.Get(language, "Narrator.Goals"));
    public static string Feedback(string original, MatchChatOutcome outcome, MatchLanguage language) => MatchLanguages.Normalize(language) == MatchLanguage.English ? original
        : HistoricalChatText.Get(language, "Feedback." + (outcome switch
        {
            MatchChatOutcome.Clarify => "Clarify", MatchChatOutcome.Unsupported => "Unsupported", MatchChatOutcome.RoutingUnavailable => "Routing",
            MatchChatOutcome.Cancelled => "Cancelled", MatchChatOutcome.Stale => "Stale", MatchChatOutcome.InvalidRequest => "Input", _ => "Unavailable"
        }));

    internal static string SourceAnswer(HistoricalSuggestion suggestion, LeagueHistoricalMatch fixture, MatchLanguage language)
    {
        string T(string key, params object?[] values) => HistoricalChatText.Get(language, "Source." + key, values);
        var limit = MatchChatLimits.AnswerChars - T("Recovery").Length;
        string Result(string home, string away) => T("Result", fixture.Season.Label, fixture.Match.Date, home, fixture.Match.HomeGoals, fixture.Match.AwayGoals, away)
            + (fixture.Season.Status == "ongoing" ? " " + T("Snapshot", fixture.Season.ResultsThrough, fixture.Season.RetrievedAt) : "");
        string Form(string home, string away)
        {
            var h = HistoricalEvidence.EligibleForm(fixture.HomePreMatchForm, fixture.Home.Id, fixture.Match.Date);
            var a = HistoricalEvidence.EligibleForm(fixture.AwayPreMatchForm, fixture.Away.Id, fixture.Match.Date);
            if (h.Length == 0 && a.Length == 0) return T("EmptyForm", home, away, fixture.Season.SeasonKey, fixture.Match.Date);
            return T("FormScope", fixture.Season.SeasonKey, fixture.Match.Date) + "\n" + Summary(h, fixture.Home.Id, home) + "\n" + Summary(a, fixture.Away.Id, away);
        }
        string Summary(LeagueMatch[] sample, string club, string name)
        {
            if (sample.Length == 0) return T("NoForm", name);
            int For(LeagueMatch match) => match.Home == club ? match.HomeGoals : match.AwayGoals;
            int Against(LeagueMatch match) => match.Home == club ? match.AwayGoals : match.HomeGoals;
            var won = sample.Count(match => For(match) > Against(match)); var drawn = sample.Count(match => For(match) == Against(match));
            return T("Form", name, sample.Length, won, drawn, sample.Length - won - drawn, won * 3 + drawn, sample.Sum(For), sample.Sum(Against));
        }
        string Table(LeagueClubFacts facts, string name) => T("Table", name, facts.Row.Rank is { } rank ? T(facts.Row.Shared ? "SharedRank" : "Rank", rank) : T("Unranked"),
            facts.Row.Points, facts.Row.Played, facts.PointsChange, facts.PositionChange is { } movement ? T("Movement", movement) : T("NoMovement"));
        string Meetings(bool compact)
        {
            var prior = fixture.PriorMeetings.Where(p => DateOnly.TryParseExact(p.Match.Date, "yyyy-MM-dd", out _) && p.Match.HomeGoals is >= 0 and <= 50 && p.Match.AwayGoals is >= 0 and <= 50
                && string.CompareOrdinal(p.Match.Date, fixture.Match.Date) < 0 && (p.Match.Home == fixture.Home.Id && p.Match.Away == fixture.Away.Id || p.Match.Home == fixture.Away.Id && p.Match.Away == fixture.Home.Id))
                .OrderBy(p => p.Match.Date, StringComparer.Ordinal).TakeLast(8).ToArray();
            var rows = prior.Length == 0 ? T("NoMeetings") : compact ? T("Compact")
                : string.Join("; ", prior.Select(p => $"{p.Match.Date}: {p.HomeName} {p.Match.HomeGoals}–{p.Match.AwayGoals} {p.AwayName}"));
            return T("Meetings", fixture.Match.Date, prior.Length, rows);
        }
        string Answer(bool compact) => suggestion.Id switch
        {
            "result" => Result(compact ? T("Home") : fixture.Home.Name, compact ? T("Away") : fixture.Away.Name),
            "form" => Form(compact ? T("Home") : fixture.Home.Name, compact ? T("Away") : fixture.Away.Name),
            "table" => T("TableScope", fixture.Match.Date) + "\n" + Table(fixture.HomeTable, compact ? T("Home") : fixture.Home.Name) + "\n" + Table(fixture.AwayTable, compact ? T("Away") : fixture.Away.Name),
            "meetings" => Meetings(compact),
            _ => throw new InvalidDataException("Unknown historical source action.")
        };
        var answer = Answer(false);
        return answer.Length <= limit ? answer : Answer(true);
    }
}
