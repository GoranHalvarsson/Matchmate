using System.Collections.Frozen;

namespace Matchmate.Web.LeagueChat;

public static class LeagueWorkflowRegistry
{
    public const string Version = "league-evidence-workflows-v3";
    public static FrozenDictionary<string, string> TopicCriteria { get; } = new Dictionary<string, string>
    {
        ["league_context"] = "Whole-table/all-club/leader perspective or league-wide selected-day events; requested table facts even at zero games. A club's position IN a table is instead club_context: the bare word table does not change the subject. Actual whole-league requests remain global with a selected club. Generic recap with no selected/reference club belongs here.",
        ["club_context"] = "Requested facts about one club's standing/table position, points, record, recent form/results or day changes, even at zero games and even with missing/unknown identity. A generic recap follows selected/reference availability. Asking why records are empty is coverage; elaborating the preceding answer is continuation.",
        ["ground_context"] = "A club's listed ground/stadium and town/home reference. Ordinary new/home/town/stadium wording belongs here; missing identity is checked by code. No stadium history or architecture is available.",
        ["coverage_limits"] = "Capability/source speech act: WHY the view is empty or results are missing, WHAT the source covers, provenance/cutoff/freshness, WHETHER live/other dates/metrics are available, limitations or batching. Requesting zero-game points/table/record facts remains club/league context; requesting an unavailable metric/prediction is unsupported.",
        ["continue_topic"] = "Elaboration of the preceding answer/reference, even when the topic word repeats (expand that ground reference/continue that club picture). A genuinely new request for facts uses its matching topic. Generic continuation also belongs here with no accepted topic; code checks authority.",
        ["clarify"] = "No identifiable football/reference topic, or an unspecified comparison/statistic. Missing club or missing accepted topic is NOT a classifier clarification; use club/ground/continuation so code can check eligibility.",
        ["unsupported"] = "Invented evidence, private data/secrets, bypassing scope, unsupported player/tactical metrics, predictions or compound capabilities. A question about why data is missing instead means coverage_limits."
    }.ToFrozenDictionary(StringComparer.Ordinal);
    public static FrozenDictionary<string, string> TimeIntentCriteria { get; } = new Dictionary<string, string>
    {
        ["selected_view"] = "No explicit time reference: implicit here/current page/this snapshot, selected day, or before-any-results state. Coverage/capability questions are metadata. Never compare calendar values.",
        ["explicit_time_reference"] = "The question explicitly names a calendar date, season/year, another/relative time, or real-world live/now/today. SAME date or SAME season literals also count. Code alone compares dates and determines availability."
    }.ToFrozenDictionary(StringComparer.Ordinal);
    public static FrozenSet<string> SemanticTimeScopes { get; } = new[] { "selected_snapshot", "different_time", "real_world_now", "unresolved" }.ToFrozenSet(StringComparer.Ordinal);
    public static FrozenDictionary<string, string> BindingCriteria { get; } = new Dictionary<string, string>
    {
        ["explicit_name"] = "CurrentQuestion itself contains a club name or football alias, including unknown/incomplete names or a name plus our/their. A brief club-name clarification reply also counts. Ignore names in WorkflowQuestion and all other state.",
        ["context"] = "No club name in CurrentQuestion. Our, we, their, they, selected club, new/home/town/stadium are ordinary context wording. Generic Tell me more/Go on is context. Never infer an explicit name from WorkflowQuestion or availability booleans."
    }.ToFrozenDictionary(StringComparer.Ordinal);
    // Fine projections remain internal for existing deterministic source chips and accepted topics.
    public static FrozenDictionary<string, string> Criteria { get; } = TopicCriteria.Concat(new Dictionary<string, string>
    {
        ["league_table"] = "Legacy table projection.", ["club_standing"] = "Legacy club standing.", ["club_form"] = "Legacy recent results.", ["date_changes"] = "Legacy selected-day changes.",
        ["other_season"] = "Unavailable selected scope.", ["live_unavailable"] = "No live provider.",
        ["named_club_standing"] = "Explicit standing.", ["named_club_form"] = "Explicit results.", ["named_ground_context"] = "Explicit ground.", ["named_date_changes"] = "Explicit day changes.", ["named_club_context"] = "Explicit broad club context."
    }).ToFrozenDictionary(StringComparer.Ordinal);
    public static bool NeedsBinding(string topic) => topic is "club_context" or "ground_context" or "continue_topic";
    public static bool NeedsScope(string topic) => topic is "league_context" or "club_context" or "ground_context" or "continue_topic";
    public static string? Map(string topic, string binding, string scope = "selected_snapshot") => !TopicCriteria.ContainsKey(topic) || !BindingCriteria.ContainsKey(binding) || !SemanticTimeScopes.Contains(scope) ? null
        : NeedsScope(topic) && scope == "unresolved" ? "other_season"
        : NeedsScope(topic) && scope == "different_time" ? "other_season"
        : NeedsScope(topic) && scope == "real_world_now" ? "live_unavailable"
        : topic == "continue_topic" && binding == "explicit_name" ? null
        : binding == "explicit_name" && topic is "club_context" or "ground_context" ? "named_" + topic : topic;
    public static string Family(string workflow) => workflow.StartsWith("named_", StringComparison.Ordinal) ? workflow[6..] : workflow;
    public static bool ValidTopic(LeagueAcceptedTopic? topic) => topic is not null && topic.WorkflowId is "league_context" or "club_context" or "league_table" or "club_standing" or "club_form" or "date_changes" or "ground_context" or "coverage_limits"
        && (topic.WorkflowId is "league_context" or "league_table" or "coverage_limits" ? topic.SubjectId is null
            : topic.WorkflowId == "date_changes" ? topic.SubjectId is null || Matchmate.Core.LeagueSeasonModel.ValidId(topic.SubjectId)
            : Matchmate.Core.LeagueSeasonModel.ValidId(topic.SubjectId));
    public static FrozenSet<string> EvidenceIds { get; } = new[] { "table_snapshot", "club_standing", "club_results", "date_change", "ground_reference", "league_coverage" }.ToFrozenSet(StringComparer.Ordinal);
    public static LeagueSourceQuestion[] Suggestions(bool club) => club
        ? [new("standing", "Where do we stand?", "club_standing"), new("changes", "What changed on this date?", "date_changes"), new("ground", "Tell me about our ground?", "ground_context")]
        : [new("table", "Who leads this table?", "league_table"), new("changes", "What changed on this date?", "date_changes"), new("coverage", "What does this table include?", "coverage_limits")];
    public static LeagueSourceQuestion? Source(string id, bool club) => Suggestions(club).SingleOrDefault(q => q.Id == id);
}

