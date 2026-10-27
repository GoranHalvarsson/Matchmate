using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Matchmate.Web.Chat;

public enum MatchRoutingStatus { Selected, Clarify, Unavailable, InvalidResponse }
public sealed record MatchQuestionPointer(string Question, string? SelectedSubjectId, string? OriginalQuestion = null);
public sealed record MatchClarificationContext(string OriginalQuestion);
// No authenticated owner, roster, schedule, evidence or previous assistant facts cross this boundary.
// Named-reference authority comes from live session state, never from question-history text.
public sealed record MatchRoutingInput(string Question, MatchChatContext Context,
    ImmutableArray<MatchQuestionPointer> PreviousQuestions, MatchClarificationContext? Clarification = null, bool HasNamedPlayerReference = false);
public sealed record MatchRoutingResult(MatchRoutingStatus Status, string? WorkflowId = null);
public interface IMatchWorkflowRouter
{
    bool Configured { get; }
    Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token);
}

public sealed class DecisionRoutingOptions
{
    public const string Section = "DecisionRouting";
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;
    // Selected on separate 28-case calibration; fresh held-out evaluation is pending, not an accuracy guarantee.
    // Enabled remains an explicit activation opt-in.
    public double MinimumProbability { get; set; } = 0.80;
    public double MinimumMargin { get; set; } = 0.20;
}

public static class AzureModelEndpoint
{
    public static bool Valid(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" && uri.AbsolutePath == "/"
        && Regex.IsMatch(uri.Host, @"\A[a-z0-9][a-z0-9-]{0,62}\.(?:services\.ai|cognitiveservices|openai)\.azure\.com\z", RegexOptions.CultureInvariant);
}
