namespace Matchmate.Web.HistoricalChat;

// Optional historical narrator profile; shared connection, availability and timeout settings remain in MatchChat.
public sealed class HistoricalReplyModelOptions
{
    public const string Section = "HistoricalReplyModel";
    public string? Deployment { get; set; }
    public string? ReasoningEffort { get; set; }
    public int? MaxCompletionTokens { get; set; }
}
