using System.Text.Json;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public enum MatchAudienceMode { Fan, Analyst }

// Trusted, ephemeral Web presentation only. Never a query, routing hint, evidence source or permission.
public static class MatchAudiencePresentation
{
    public const string Version = "match-audience-v1";
    public static MatchAudienceMode Normalize(MatchAudienceMode mode) => mode == MatchAudienceMode.Analyst ? mode : MatchAudienceMode.Fan;
    public static string Label(MatchAudienceMode mode) => Normalize(mode) == MatchAudienceMode.Analyst ? "Analyst" : "Fan";
    public static MatchModelMessage NarratorStyle(MatchAudienceMode mode) => new("system", Version + "\n"
        + (Normalize(mode) == MatchAudienceMode.Analyst
            ? "Audience: Analyst. Answer the question with relevant supported figures, comparisons and method or coverage context. Explain football interpretation as qualified interpretation. Keep the answer focused; do not list unrelated statistics or raw source fields."
            : "Audience: Fan. Give a short, useful football explanation in plain language. Lead with what matters to the question, with only the supported figures and qualifications needed to understand it. Avoid jargon and long data inventories.")
        + " This changes presentation only. The existing grounding, evidence, citation, numeric, temporal and uncertainty rules retain full authority; never invent a figure or turn generated illustration into actual observation.");

    internal static string SourceDetail(MatchWorkflowBundle bundle) => SourceDetailForLanguage(bundle, MatchLanguage.English);
    internal static string SourceDetailForLanguage(MatchWorkflowBundle bundle, MatchLanguage language)
    {
        if (bundle.WorkflowId == "current_match_summary") return ChatText.Get(language, "Source.MethodSummary");
        if (bundle.WorkflowId == "coverage_and_limits") return ChatText.Get(language, "Source.MethodCoverage");
        if (bundle.WorkflowId != "selected_player_context") return "";
        using var player = JsonDocument.Parse(bundle.Items.Single(item => item.Tool == "current_player").Text);
        var method = player.RootElement.TryGetProperty("method", out var supplied) && supplied.ValueKind == JsonValueKind.String ? supplied.GetString() : null;
        return ChatText.Get(language, "Source." + (method switch { "generated" => "MethodPlayerGenerated", "estimated" => "MethodPlayerEstimated", "observed" => "MethodPlayerObserved", _ => "MethodPlayerUnknown" }));
    }
}
