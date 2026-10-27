using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.LeagueChat;

public sealed record LeagueChatContext(string Version, string SeasonKey, int DateIndex, string? Date, string? SelectedClubId, string? SelectedClubName)
{
    public static LeagueChatContext From(LeagueSeasonSnapshot snapshot) => new(snapshot.Version, snapshot.Season.SeasonKey, snapshot.DateIndex,
        snapshot.Date, snapshot.SelectedClub?.Club.Id, snapshot.SelectedClub?.Club.Name);
}
public sealed record LeagueRoutingContext(string SeasonKey, int DateIndex, string? Date, string? SelectedClubId, string? SelectedClubName);
public sealed record LeagueQuestionPointer(string Question, string? SubjectId);
// No football payload, assistant answer, identity, credential or user profile is sent to Decision-1.
public sealed record LeagueClarificationContext(string OriginalQuestion);
public sealed record LeagueAcceptedTopic(string WorkflowId, string? SubjectId);
public sealed record LeagueRoutingInput(string Question, LeagueRoutingContext Context, ImmutableArray<LeagueQuestionPointer> PreviousQuestions, bool HasNamedClubReference = false, LeagueClarificationContext? Clarification = null, LeagueAcceptedTopic? AcceptedTopic = null);
public interface ILeagueWorkflowRouter
{
    bool Configured { get; }
    Task<MatchRoutingResult> RouteAsync(LeagueRoutingInput input, CancellationToken token);
}
public interface ILeagueChatModel
{
    bool Configured { get; }
    Task<MatchModelReply> CompleteAsync(LeagueNarratorRequest request, CancellationToken token);
}
public sealed class LeagueNarratorRequest
{
    public ImmutableArray<MatchModelMessage> Messages { get; }
    public ImmutableArray<string> AllowedEvidenceIds { get; }
    public LeagueNarratorRequest(IEnumerable<MatchModelMessage> messages, IEnumerable<string> allowedEvidenceIds)
    {
        var supplied = messages?.Take(9).ToImmutableArray() ?? [];
        AllowedEvidenceIds = allowedEvidenceIds?.Take(5).ToImmutableArray() ?? [];
        if (supplied.Length is < 1 or > 7 || supplied[0] is null || supplied[0].Role != "system" || supplied[0].Content is null || !supplied[0].Content.StartsWith(LeagueGuidePrompt.Version + "\n", StringComparison.Ordinal)
            || supplied.Any(m => m is null || m.Role is not ("system" or "user") || m.Content is null || m.Content.Length > 32000 || m.Calls is { Length: > 0 } || m.ToolCallId is not null)
            || AllowedEvidenceIds.Length is < 1 or > 4 || AllowedEvidenceIds.Distinct(StringComparer.Ordinal).Count() != AllowedEvidenceIds.Length
            || AllowedEvidenceIds.Any(id => !LeagueWorkflowRegistry.EvidenceIds.Contains(id))) throw new InvalidDataException("A league-bound narrator request is required.");
        Messages = supplied.Insert(1, new("system", "Server-owned top-level allowed output evidenceIds: " + JsonSerializer.Serialize(AllowedEvidenceIds) + ". Nested source IDs are not output citations."));
    }
}
public sealed record LeagueChatTurn(string Question, string Answer, LeagueChatContext Context, string? SubjectName,
    MatchChatEvidence[] Evidence, bool SourceOnly, MatchChatOutcome Outcome = MatchChatOutcome.Answered, string? ResolvedClubId = null, string? OriginalQuestion = null, MatchLanguage Language = MatchLanguage.English)
{ public Guid Id { get; } = Guid.NewGuid(); }
public sealed record LeagueWorkflowResult(MatchChatOutcome Outcome, string Message, ImmutableArray<MatchChatEvidence> Evidence, string? ResolvedClubId = null, string? SubjectName = null, string? CanonicalWorkflow = null, bool IsContinuation = false);
public sealed record LeagueSourceQuestion(string Id, string Question, string WorkflowId);
