using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public static class MatchLanguagePresentation
{
    public const string Version = "match-language-v1";
    public static MatchModelMessage NarratorLanguage(MatchLanguage language) => new("system", Version + "\n" + ChatText.Get(language, "Language.Instruction")
        + " Language changes presentation only. Keep names, output evidenceIds and source identifiers unchanged. The existing evidence, grounding, numeric, citation and temporal rules remain authoritative; unknown metrics remain unknown. Do not translate or reinterpret an evidence identifier. No additional facts or translation call are authorized.");
    internal static MatchChatResult Localize(MatchChatResult result, MatchLanguage language)
    {
        if (language == MatchLanguage.English) return result;
        var visual = result.Message switch {
            "Choose a goal in the highlights, then ask how it happened." => "Goal", "Choose a substitution in the highlights, then ask for its diagram." => "Substitution",
            "Choose a booking in the highlights, then ask for its diagram." => "Card", "Choose a completed possession interval in the highlights, then ask for its diagram." => "Possession",
            "Choose a supported event in the highlights, then ask for its diagram." => "Event", _ => null };
        if (visual is not null) return result with { Message = ChatText.Get(language, "Visual." + visual) };
        var key = result.Message switch
        {
            "Please use valid text of at most 1,000 characters." or "Please use a question of at most 1,000 characters." => "Input",
            "Choose a ready replay moment first." => "Ready",
            "Sign in with your test account to chat." => "SignIn",
            "AI follow-ups are not connected yet. Choose one of the suggested questions." => "Disconnected",
            "Choose a valid ready replay context before routing a question." => "InvalidContext",
            "Decision routing is unavailable or returned an invalid decision. No facts were retrieved." => "Routing",
            "Decision routing returned a code-owned action. No facts were retrieved." => "Routing",
            "I could not keep that follow-up tied to the previous player. Please name the player you mean." => "NamedReference",
            "Select a player on the pitch or in the roster, then ask again." => "SelectPlayer",
            "Generated replay statistics are unavailable for this recording at this moment. Actual match statistics are not supplied by the illustration." => "StatisticsUnavailable",
            "This recording does not supply reported match events or generated replay statistics." => "ReportComparisonUnavailable",
            "Reply grounded in the selected workflow evidence." => "Grounded",
            "Reply uses partial evidence; retain its coverage limits." => "Partial",
            "Reply cancelled." => "UserCancelled",
            _ => result.Outcome switch { MatchChatOutcome.Clarify => "Clarify", MatchChatOutcome.Unsupported => "Unsupported", MatchChatOutcome.Unavailable => "Unavailable",
                MatchChatOutcome.Stale => "Stale", MatchChatOutcome.Cancelled => "Cancelled", MatchChatOutcome.RetrievalFailure => "Retrieval", _ => "Failure" }
        };
        return result with { Message = ChatText.Get(language, "Feedback." + key) };
    }
}
