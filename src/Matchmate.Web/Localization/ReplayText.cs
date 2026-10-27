using System.Globalization;
using System.Resources;
using Matchmate.Contracts;
using Matchmate.Web.Chat;

namespace Matchmate.Web.Localization;

/// <summary>Replay presentation resources. Never changes ambient or thread culture.</summary>
public static class ReplayText
{
    private static readonly ResourceManager Resources = new("Matchmate.Web.Localization.ReplayText", typeof(ReplayText).Assembly);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishStatusKeys = new(() =>
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
            if (entry.Value is string text) keys.TryAdd(text, (string)entry.Key);
        return keys;
    });
    public static string Get(MatchLanguage language, string key, params object?[] args)
    {
        var culture = MatchLanguages.Culture(language);
        var format = Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException($"Missing replay resource: {key}");
        return args.Length == 0 ? format : string.Format(culture, format, args);
    }

    // These are display mappings for existing app-generated statuses, never workflow selectors.
    public static string Status(MatchLanguage language, string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        if (EnglishStatusKeys.Value.TryGetValue(message, out var key)) return Get(language, key);
        const string updating = "Updating… Showing accepted replay ";
        const string until = " until the new moment is ready.";
        if (message.StartsWith(updating, StringComparison.Ordinal) && message.EndsWith(until, StringComparison.Ordinal))
            return Get(language, "StatusUpdatingAccepted", message[updating.Length..^until.Length]);
        return language == MatchLanguage.English ? message : Get(language, "StatusUnavailable");
    }

    public static string Audience(MatchLanguage language, MatchAudienceMode audience) => Get(language,
        MatchAudiencePresentation.Normalize(audience) == MatchAudienceMode.Analyst ? "AudienceAnalyst" : "AudienceFan");
    public static string Kind(MatchLanguage language, string kind) => Get(language, kind switch
    {
        "goal" => "KindGoal", "substitution" => "KindSubstitution", "yellow_card" or "booking" => "KindBooking",
        "red_card" => "KindRedCard", "var_decision" => "KindVarDecision",
        "reported_assist" => "StepReportedAssist",
        "completed_possession" => "KindCompletedPossession", "corner" => "StepCorner", "headed_assist" => "StepHeadedAssist",
        "header_finish" => "StepHeaderFinish", "advance" => "StepAdvance", "keeper_out" => "StepKeeperOut",
        "uncertain_goalmouth" => "StepUncertainGoalmouth", "reported_finish" => "StepReportedFinish", "throw_in" => "StepThrowIn",
        "into_area" => "StepIntoArea", "release_pass" => "StepReleasePass", "shot" => "StepShot", "save" => "StepSave",
        "rebound_finish" => "StepReboundFinish", "cross" => "StepCross", "deflection" => "StepDeflection", _ => "KindReportedEvent"
    });
    public static string Value(MatchLanguage language, string? value) => value switch
    {
        null => Get(language, "Unknown"), "observed" => Get(language, "Observed"), "estimated" => Get(language, "Estimated"),
        "generated" => Get(language, "Generated"), "reported" => Get(language, "ReportedValue"), "unknown" => Get(language, "Unknown"),
        "present" => Get(language, "Present"), "missing" => Get(language, "Missing"), "not_observed" => Get(language, "NotObserved"),
        "confirmed" => Get(language, "Confirmed"), "unconfirmed" => Get(language, "Unconfirmed"), "disallowed" => Get(language, "Disallowed"),
        "straight_red" => Get(language, "StraightRed"), "second_yellow" => Get(language, "SecondYellow"),
        "no_penalty" => Get(language, "NoPenalty"), "card_upgraded" => Get(language, "CardUpgraded"),
        "high" => Get(language, "High"), "medium" => Get(language, "Medium"), "low" => Get(language, "Low"),
        _ => value // Unrecognized source metadata remains original evidence.
    };
    public static string Coverage(MatchLanguage language, FootballView view) => Get(language, view.CoverageMessage switch
    {
        "No committed tracking sample at this cursor; positions are not carried across gaps." => "CoverageNoSample",
        "Explicit empty player frame; independent ball evidence is preserved." => "CoverageEmptyFrame",
        "Source sample; no interpolation or measured movement statistics." => "CoverageSourceSample",
        _ => "CoverageUnknown"
    });
}
