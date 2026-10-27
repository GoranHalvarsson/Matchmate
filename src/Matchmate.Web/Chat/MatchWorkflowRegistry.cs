using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Matchmate.Web.Chat;

public static class MatchWorkflowRegistry
{
    public const string Version = "recorded-workflows-v6";
    public static FrozenDictionary<string, MatchWorkflowDefinition> Workflows { get; } = new[]
    {
        Replay("current_match_summary", ["match_summary", "coverage"]),
        Replay("current_replay_statistics", ["illustration_statistics", "coverage"]),
        Replay(MatchSourceQuestions.CompareTeamsWorkflow, ["reported_comparison", "coverage"]),
        new MatchWorkflowDefinition(MatchSourceQuestions.ExplainMomentWorkflow, Version, ["pitch_snapshot"],
            [MatchWorkflowTemporalMode.ReplayAtCursor], false, 2, CommentatorPrompt.Version),
        Replay("current_match_events", ["current_events", "match_summary", "coverage", "selected_event"]),
        Replay("current_match_moment", ["current_events", "match_summary", "moment_context", "coverage", "selected_event"]),
        new MatchWorkflowDefinition("selected_goal_event", Version, ["selected_goal", "match_summary", "moment_context", "coverage"],
            [MatchWorkflowTemporalMode.SelectedGoalRetrospective], false, 2, CommentatorPrompt.Version),
        Replay("named_player_context", ["current_player", "player_involvement", "coverage"]),
        Replay("selected_player_context", ["current_player", "player_involvement", "coverage"], requiresPlayer: true),
        Replay("coverage_and_limits", ["coverage"]),
        NoRead("clarify", [MatchWorkflowTemporalMode.ReplayAtCursor, MatchWorkflowTemporalMode.LiveNow, MatchWorkflowTemporalMode.HistoricalAtCursor]),
        NoRead("unsupported", [MatchWorkflowTemporalMode.ReplayAtCursor, MatchWorkflowTemporalMode.LiveNow, MatchWorkflowTemporalMode.HistoricalAtCursor]),
        NoRead("other_live_matches_stats", [MatchWorkflowTemporalMode.LiveNow])
    }.ToFrozenDictionary(workflow => workflow.Id, StringComparer.Ordinal);

    private static MatchWorkflowDefinition Replay(string id, ImmutableArray<string> tools, bool requiresPlayer = false) =>
        new(id, Version, tools, [MatchWorkflowTemporalMode.ReplayAtCursor], requiresPlayer, 1, CommentatorPrompt.Version);
    private static MatchWorkflowDefinition NoRead(string id, ImmutableArray<MatchWorkflowTemporalMode> modes) =>
        new(id, Version, [], modes, false, 0, CommentatorPrompt.Version);
}
