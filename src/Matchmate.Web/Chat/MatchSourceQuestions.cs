using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public enum MatchSourceQuestionId { Summary, Positions, Player, ReplayStatistics }
public sealed record MatchSourceQuestion(MatchSourceQuestionId Id, string Question, string WorkflowId);

// These are server-owned actions, not workflow arguments supplied by a user/model or precomputed UI answers.
public static class MatchSourceQuestions
{
    // Explicit UI action only; never a Decision-1 choice or a typed source-question alias.
    public const string CompareTeamsWorkflow = "compare_current_teams";
    public const string ExplainMomentWorkflow = "explain_current_moment";
    public static ImmutableArray<MatchSourceQuestion> All { get; } = [
        new(MatchSourceQuestionId.Summary, "What has happened so far?", "current_match_summary"),
        new(MatchSourceQuestionId.Positions, "Explain this moment", ExplainMomentWorkflow),
        new(MatchSourceQuestionId.Player, "Tell me about this player?", "selected_player_context"),
        new(MatchSourceQuestionId.ReplayStatistics, "Show replay statistics", "current_replay_statistics")];

    public static MatchSourceQuestion? Find(MatchSourceQuestionId id) => All.FirstOrDefault(question => question.Id == id);
    public static string DisplayQuestion(MatchSourceQuestionId id, MatchLanguage language) => ChatText.Get(language, id switch
    { MatchSourceQuestionId.Summary => "Question.Summary", MatchSourceQuestionId.Positions => "Question.Positions", MatchSourceQuestionId.Player => "Question.Player", MatchSourceQuestionId.ReplayStatistics => "Question.Statistics", _ => throw new ArgumentOutOfRangeException(nameof(id)) });
    public static MatchSourceQuestion? Exact(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return All.FirstOrDefault(question => MatchLanguages.Supported.Any(language => string.Equals(DisplayQuestion(question.Id, language), normalized, StringComparison.OrdinalIgnoreCase)));
    }

    internal static (string Answer, MatchChatEvidence[] Evidence) Format(MatchWorkflowBundle bundle) => FormatForQuestion(bundle, null);

    internal static (string Answer, MatchChatEvidence[] Evidence) FormatForQuestion(MatchWorkflowBundle bundle, string? question) => FormatForAudience(bundle, question, MatchAudienceMode.Fan);

    internal static (string Answer, MatchChatEvidence[] Evidence) FormatForAudience(MatchWorkflowBundle bundle, string? question, MatchAudienceMode mode) => FormatForLanguage(bundle, question, mode, MatchLanguage.English);

    internal static (string Answer, MatchChatEvidence[] Evidence) FormatForLanguage(MatchWorkflowBundle bundle, string? question, MatchAudienceMode mode, MatchLanguage language)
    {
        if (bundle.WorkflowId == ExplainMomentWorkflow) return MatchSnapshotExplanation.Format(bundle, language, mode);
        string T(string key, params object?[] args) => ChatText.Get(language, "Source." + key, args);
        mode = MatchAudiencePresentation.Normalize(mode);
        JsonDocument Item(string tool) => JsonDocument.Parse(bundle.Items.Single(item => item.Tool == tool).Text);
        using var coverageDocument = Item("coverage"); var coverage = coverageDocument.RootElement;
        string answer;
        switch (bundle.WorkflowId)
        {
            case "current_replay_statistics":
                using (var document = Item("illustration_statistics")) answer = MatchIllustrationStatistics.FormatForLanguage(document.RootElement, question, mode, language);
                break;
            case CompareTeamsWorkflow:
                using (var document = Item("reported_comparison")) answer = MatchReportedEventSummary.Format(document.RootElement, language, comparison: true);
                break;
            case "current_match_summary":
                using (var document = Item("match_summary"))
                {
                    var summary = document.RootElement;
                    if (summary.GetProperty("homeReportedGoals").ValueKind != JsonValueKind.Number
                        || summary.GetProperty("awayReportedGoals").ValueKind != JsonValueKind.Number)
                        answer = T("NoScore");
                    else
                    {
                        var home = Text(summary, "home"); var away = Text(summary, "away");
                        var homeGoals = summary.GetProperty("homeReportedGoals").GetInt32(); var awayGoals = summary.GetProperty("awayReportedGoals").GetInt32();
                        answer = homeGoals == awayGoals ? T("Level", home, away, homeGoals, awayGoals)
                            : homeGoals > awayGoals ? T("Lead", home, away, homeGoals, awayGoals) : T("Lead", away, home, awayGoals, homeGoals);
                    }
                    if (summary.TryGetProperty("reportedEvents", out var reports) && reports.GetProperty("reportsSupplied").GetBoolean())
                        answer += " " + MatchReportedEventSummary.Format(reports, language, comparison: false);
                }
                break;
            case "coverage_and_limits":
                var positions = Text(coverage, "origin") switch
                {
                    { } origin when origin.StartsWith("Generated illustration", StringComparison.Ordinal) => T("GeneratedPositions"),
                    { } origin when origin.StartsWith("Recorded source observations", StringComparison.Ordinal) => T("RecordedPositions"),
                    _ => T("UnknownPositions")
                };
                var gap = coverage.GetProperty("gap").GetBoolean()
                    ? T("Gap") : T("NoCarry");
                answer = $"{positions} {Ball(coverage, language)} {gap} {T("PositionLimits")}";
                break;
            case "selected_player_context":
                using (var document = Item("current_player"))
                {
                    var player = document.RootElement;
                    var involvement = bundle.Items.SingleOrDefault(item => item.Tool == "player_involvement");
                    using var involvementDocument = involvement is null ? null : JsonDocument.Parse(involvement.Text);
                    var details = involvementDocument?.RootElement ?? default;
                    var samePlayer = Text(details, "playerId") == bundle.Context.Query.SubjectId
                        && Text(details, "player") == Text(player, "selectedPlayer");
                    var team = samePlayer ? Text(details, "team") : null;
                    if (language != MatchLanguage.English)
                    {
                        var identityName = Text(player, "selectedPlayer");
                        var localizedIdentity = team is null ? identityName : T("PlayerIdentity", identityName, team);
                        localizedIdentity = player.TryGetProperty("shirt", out var shirtValue) && shirtValue.ValueKind == JsonValueKind.Number
                            ? T(team is null ? "PlayerShirtOnly" : "PlayerShirt", localizedIdentity, shirtValue.GetInt32()) : localizedIdentity + ".";
                        var hasPosition = Text(player, "availability") == "present" && Text(player, "xMetres") is not null && Text(player, "yMetres") is not null;
                        var positionKey = !hasPosition ? "PlayerNoPosition" : Text(player, "method") switch { "generated" => "PlayerGenerated", "observed" => "PlayerObserved", "estimated" => "PlayerEstimated", _ => "PlayerUnknown" };
                        answer = $"{localizedIdentity} {T(positionKey)} {T("PlayerLimited")}";
                        if (samePlayer && details.TryGetProperty("reviewedBrief", out var reviewed) && reviewed.TryGetProperty("observations", out var notes)
                            && notes.ValueKind == JsonValueKind.Array && notes.GetArrayLength() > 0) answer += " " + T("RawNotes");
                        break;
                    }
                    var shirt = player.TryGetProperty("shirt", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                    var identity = Text(player, "selectedPlayer") + (team is null ? "" : $" is listed with {team}")
                        + (shirt is null ? "" : $"{(team is null ? " wears" : ", wearing")} number {shirt}") + ".";
                    var present = Text(player, "availability") == "present" && Text(player, "xMetres") is not null && Text(player, "yMetres") is not null;
                    var position = !present ? "No current position is available here. That alone says nothing about whether they played."
                        : Text(player, "method") switch
                        {
                            "generated" => "The position shown is an illustration, not a recorded run or evidence of intent.",
                            "observed" => "A recorded position is available at this moment; a single point does not establish a run.",
                            "estimated" => "The current position is an estimate, not a verified movement.",
                            _ => "A position is shown, but its method is not specified. It does not establish a run."
                        };
                    answer = $"{identity} {position} This is a limited picture, not a full-match assessment.";
                    if (samePlayer && details.TryGetProperty("reviewedBrief", out var brief)
                        && brief.TryGetProperty("observations", out var observations) && observations.ValueKind == JsonValueKind.Array)
                    {
                        var observation = observations.EnumerateArray().FirstOrDefault();
                        if (Text(observation, "text") is { } text && Text(observation, "qualification") is { } qualification)
                        {
                            var note = PlayerEvidenceVoice.Display(text, qualification);
                            var candidate = $"{identity} One reviewed passage: {note.Text} {note.Qualification} {position} This is a limited picture, not a full-match assessment.";
                            if (candidate.Length <= MatchChatLimits.AnswerChars - (mode == MatchAudienceMode.Analyst ? 250 : 0)) answer = candidate;
                        }
                    }
                }
                break;
            default: throw new InvalidDataException("Unsupported source question.");
        }
        if (mode == MatchAudienceMode.Analyst) answer += MatchAudiencePresentation.SourceDetailForLanguage(bundle, language);
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > MatchChatLimits.AnswerChars) throw new InvalidDataException("Source answer budget exceeded.");
        return (answer, bundle.Items.Select(item => new MatchChatEvidence(item.Id, item.Tool, item.Text, item.NumericTokens.ToArray())).ToArray());
    }

    private static string? Text(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static string Ball(JsonElement coverage, MatchLanguage language) => ChatText.Get(language, "Source." + (Text(coverage, "ballAvailability") != "present" ? "NoBall"
        : Text(coverage, "ballMethod") switch { "observed" => "BallObserved", "estimated" => "BallEstimated", "generated" => "BallGenerated", _ => "BallUnknown" }));
}
