using System.Text.Json;

namespace Matchmate.Web.HistoricalChat;

public enum HistoricalChatStage { Authentication, Routing, Retrieval, EvidencePreparation, Narration, ReplyValidation, Commit }
public enum HistoricalChatFailureReason
{
    Unknown, RoutingFailure, InvalidRoutingResponse, RetrievalFailure, EvidencePreparationFailure, NarrationFailure,
    AuthenticationFailure, CommitFailure, Cancelled, Timeout, UnsupportedDetail, Venue, UncitedIdentity,
    UnsupportedNumber, UnknownEvidence, InvalidReplyJson, InvalidReplyStructure, ReplyBudget, InvalidAnswerContent,
    UnsupportedMeasurement, UnexpectedToolCall, IncompleteNarratorReply, ProviderRateLimited, ProviderAuthentication,
    DetailMechanismQualifier, DetailForbiddenNoun, DetailForbiddenAction, AggregateScopeMissing, AggregateCitationMissing,
    AggregateSubjectUnbound, AggregateTeamPredicateMismatch, AggregateValueMismatch, AggregateGerundWithoutComma,
    AggregateFiniteTotalPhrase, AggregateFiniteGrammarMismatch, AggregateGerundGrammarMismatch
}

internal static class HistoricalChatDiagnostics
{
    public static void Record(ILogger<HistoricalChatSession>? logger, HistoricalChatStage stage, HistoricalChatFailureReason reason)
    {
        // Never attach an exception or payload, and never let a logging provider affect a chat result.
        try { logger?.LogWarning(new EventId(7301, "HistoricalChatFailure"), "Historical chat failure {Stage} {Reason}", stage, reason); } catch { }
    }

    public static HistoricalChatFailureReason Classify(HistoricalChatStage stage, Exception error)
    {
        try
        {
            if (stage == HistoricalChatStage.ReplyValidation)
            {
                if (error is JsonException) return HistoricalChatFailureReason.InvalidReplyJson;
                if (error is not InvalidDataException) return HistoricalChatFailureReason.Unknown;
                return error.Message switch
                {
                    "Unsupported historical match detail." => HistoricalChatFailureReason.UnsupportedDetail,
                    "Historical rejection: DetailMechanismQualifier." => HistoricalChatFailureReason.DetailMechanismQualifier,
                    "Historical rejection: DetailForbiddenNoun." => HistoricalChatFailureReason.DetailForbiddenNoun,
                    "Historical rejection: DetailForbiddenAction." => HistoricalChatFailureReason.DetailForbiddenAction,
                    "Historical rejection: AggregateScopeMissing." => HistoricalChatFailureReason.AggregateScopeMissing,
                    "Historical rejection: AggregateCitationMissing." => HistoricalChatFailureReason.AggregateCitationMissing,
                    "Historical rejection: AggregateSubjectUnbound." => HistoricalChatFailureReason.AggregateSubjectUnbound,
                    "Historical rejection: AggregateTeamPredicateMismatch." => HistoricalChatFailureReason.AggregateTeamPredicateMismatch,
                    "Historical rejection: AggregateValueMismatch." => HistoricalChatFailureReason.AggregateValueMismatch,
                    "Historical rejection: AggregateGerundWithoutComma." => HistoricalChatFailureReason.AggregateGerundWithoutComma,
                    "Historical rejection: AggregateFiniteTotalPhrase." => HistoricalChatFailureReason.AggregateFiniteTotalPhrase,
                    "Historical rejection: AggregateFiniteGrammarMismatch." => HistoricalChatFailureReason.AggregateFiniteGrammarMismatch,
                    "Historical rejection: AggregateGerundGrammarMismatch." => HistoricalChatFailureReason.AggregateGerundGrammarMismatch,
                    "Season ground cannot establish the fixture venue." => HistoricalChatFailureReason.Venue,
                    "Uncited club identity." => HistoricalChatFailureReason.UncitedIdentity,
                    "Unsupported numeric claim." => HistoricalChatFailureReason.UnsupportedNumber,
                    "Unknown evidence reference." => HistoricalChatFailureReason.UnknownEvidence,
                    "A structured, evidence-supported answer is required." => HistoricalChatFailureReason.InvalidReplyStructure,
                    "Reply budget exceeded." => HistoricalChatFailureReason.ReplyBudget,
                    "Unsupported answer content." => HistoricalChatFailureReason.InvalidAnswerContent,
                    "Unsupported measurement claim." => HistoricalChatFailureReason.UnsupportedMeasurement,
                    "Historical narrator attempted a tool call." => HistoricalChatFailureReason.UnexpectedToolCall,
                    _ => HistoricalChatFailureReason.Unknown
                };
            }
            if (stage == HistoricalChatStage.Narration && error is InvalidDataException
                && error.Message == "Incomplete, filtered or unexpected tool-call narrator reply.") return HistoricalChatFailureReason.IncompleteNarratorReply;
            if (error is InvalidDataException && stage is HistoricalChatStage.Retrieval or HistoricalChatStage.EvidencePreparation)
                return stage == HistoricalChatStage.Retrieval ? HistoricalChatFailureReason.RetrievalFailure : HistoricalChatFailureReason.EvidencePreparationFailure;
            if (stage is HistoricalChatStage.Routing or HistoricalChatStage.Narration)
            {
                if (error is Azure.Identity.AuthenticationFailedException) return HistoricalChatFailureReason.ProviderAuthentication;
                var status = error switch
                {
                    System.ClientModel.ClientResultException client => client.Status,
                    Azure.RequestFailedException azure => azure.Status,
                    HttpRequestException http => (int?)http.StatusCode,
                    _ => null
                };
                if (status is 401 or 403) return HistoricalChatFailureReason.ProviderAuthentication;
                if (status == 429) return HistoricalChatFailureReason.ProviderRateLimited;
            }
            if (error is HttpRequestException or IOException or JsonException or System.ClientModel.ClientResultException
                or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
                return stage switch
                {
                    HistoricalChatStage.Authentication => HistoricalChatFailureReason.AuthenticationFailure,
                    HistoricalChatStage.Routing => HistoricalChatFailureReason.RoutingFailure,
                    HistoricalChatStage.Retrieval => HistoricalChatFailureReason.RetrievalFailure,
                    HistoricalChatStage.EvidencePreparation => HistoricalChatFailureReason.EvidencePreparationFailure,
                    HistoricalChatStage.Narration => HistoricalChatFailureReason.NarrationFailure,
                    HistoricalChatStage.Commit => HistoricalChatFailureReason.CommitFailure,
                    _ => HistoricalChatFailureReason.Unknown
                };
        }
        catch { } // Even a nonstandard exception's Message getter must not affect request handling.
        return HistoricalChatFailureReason.Unknown;
    }
}
