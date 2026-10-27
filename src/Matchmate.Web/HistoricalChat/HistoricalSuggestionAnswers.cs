using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.HistoricalChat;

// Only known server-owned suggestions can use this freshly prepared, topic-specific source answer.
internal sealed record HistoricalSuggestionAnswer(string Answer, MatchChatEvidence[] Evidence, bool EmptyForm, MatchLanguage Language = MatchLanguage.English)
{
    public const string RecoveryNotice = "I couldn’t provide a verified AI reply. Here are the match facts.\n\n";
    public string RecoveryAnswer => HistoricalChatText.Get(Language, "Source.Recovery") + Answer;
}

internal static class HistoricalSuggestionAnswers
{
    public static HistoricalSuggestionAnswer Prepare(HistoricalSuggestion suggestion, HistoricalPrepared prepared, LeagueHistoricalMatch fixture, MatchLanguage language = MatchLanguage.English)
    {
        var id = suggestion.Id switch { "result" => "fixture", "form" => "form", "table" => "table", "meetings" => "reference", _ => throw new InvalidDataException("Unknown historical suggestion.") };
        if (prepared.Workflow != suggestion.Workflow || prepared.ClubId is not null) throw new InvalidDataException("Historical suggestion context mismatch.");
        var evidence = prepared.Evidence.Single(e => e.Id == id);
        using var document = JsonDocument.Parse(evidence.Text);
        var source = document.RootElement.GetProperty("sourceAnswer").GetString() ?? throw new InvalidDataException("Missing historical source answer.");
        var home = suggestion.Id == "form" ? HistoricalEvidence.EligibleForm(fixture.HomePreMatchForm, fixture.Home.Id, fixture.Match.Date) : [];
        var away = suggestion.Id == "form" ? HistoricalEvidence.EligibleForm(fixture.AwayPreMatchForm, fixture.Away.Id, fixture.Match.Date) : [];
        var emptyForm = suggestion.Id == "form" && home.Length == 0 && away.Length == 0;
        var answer = suggestion.Id switch
        {
            "form" => Form(fixture, home, away, fixture.Home.Name, fixture.Away.Name),
            "meetings" => $"Strictly before {fixture.Match.Date}: " + source,
            _ => source
        };
        // Compact whole facts rather than cutting a qualification off at the character limit.
        if (answer.Length > MatchChatLimits.AnswerChars - HistoricalSuggestionAnswer.RecoveryNotice.Length)
            answer = suggestion.Id switch
            {
                "form" => Form(fixture, home, away, "Home club", "Away club"),
                "result" => $"Selected-season result on {fixture.Match.Date}: home club {fixture.Match.HomeGoals}–{fixture.Match.AwayGoals} away club, full time."
                    + (fixture.Season.Status == "ongoing" ? " This is an ongoing-season snapshot, with no automatic updates. See Supporting facts for its coverage and retrieval dates." : ""),
                "table" => $"End of {fixture.Match.Date}: home club has {fixture.HomeTable.Row.Points} points from {fixture.HomeTable.Row.Played} played; away club has {fixture.AwayTable.Row.Points} points from {fixture.AwayTable.Row.Played} played. "
                    + "All results on that date enter together. Changes compare the previous played date and are not solely attributed to this fixture. See Supporting facts for the standings and movements.",
                _ => $"Earlier meetings cover loaded results strictly before {fixture.Match.Date}, up to the 8 most recent. This is not an all-time head-to-head record. See Supporting facts for the available meetings."
            };
        if (string.IsNullOrWhiteSpace(answer) || answer.Length + HistoricalSuggestionAnswer.RecoveryNotice.Length > MatchChatLimits.AnswerChars)
            throw new InvalidDataException("Historical source answer budget exceeded.");
        if (language != MatchLanguage.English) answer = HistoricalChatPresentation.SourceAnswer(suggestion, fixture, language);
        if (answer.Length + HistoricalChatText.Get(language, "Source.Recovery").Length > MatchChatLimits.AnswerChars) throw new InvalidDataException("Historical localized source budget exceeded.");
        return new(answer, [evidence], emptyForm, language);
    }

    private static string Form(LeagueHistoricalMatch fixture, LeagueMatch[] home, LeagueMatch[] away, string homeName, string awayName)
    {
        var scope = $"In the selected season ({fixture.Season.SeasonKey}), strictly before {fixture.Match.Date}";
        if (home.Length == 0 && away.Length == 0)
            return $"There are no earlier loaded results for {homeName} or {awayName} in the selected season ({fixture.Season.SeasonKey}), strictly before {fixture.Match.Date}. "
                + "I can’t compare their form from this sample; that does not mean they had never played.";
        return scope + ", using up to 5 most recent results per club:\n" + Summary(home, fixture.Home.Id, homeName) + "\n" + Summary(away, fixture.Away.Id, awayName);
    }

    private static string Summary(LeagueMatch[] sample, string clubId, string name)
    {
        if (sample.Length == 0) return $"{name}: no earlier results supplied in this sample; this is not an all-time record.";
        int For(LeagueMatch match) => match.Home == clubId ? match.HomeGoals : match.AwayGoals;
        int Against(LeagueMatch match) => match.Home == clubId ? match.AwayGoals : match.HomeGoals;
        var wins = sample.Count(match => For(match) > Against(match)); var draws = sample.Count(match => For(match) == Against(match));
        return $"{name}: {sample.Length} earlier results in this sample — {wins} wins, {draws} draws, {sample.Length - wins - draws} losses; "
            + $"{wins * 3 + draws} points, {sample.Sum(For)} goals for and {sample.Sum(Against)} against in that earlier sample.";
    }
}
