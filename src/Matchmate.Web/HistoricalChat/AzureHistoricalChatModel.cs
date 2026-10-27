using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

using Matchmate.Web.Chat;

namespace Matchmate.Web.HistoricalChat;

public sealed class AzureHistoricalChatModel : IHistoricalChatModel
{
    private readonly Lazy<ChatClient> _client;
    private readonly string _reasoningEffort;
    private readonly int _maxCompletionTokens;
    public bool Configured { get; }

    public AzureHistoricalChatModel(HttpClient http, IOptions<MatchChatOptions> configured, IHostEnvironment environment,
        TokenCredential? credential = null, MatchChatDiagnostics? diagnostics = null, IOptions<HistoricalReplyModelOptions>? historicalConfigured = null)
    {
        var shared = configured.Value;
        var historical = historicalConfigured?.Value;
        // Freeze one effective profile for validation and lazy client creation without changing another narrator's options.
        var effective = new MatchChatOptions { Enabled = shared.Enabled, Endpoint = shared.Endpoint, ApiKey = shared.ApiKey,
            Deployment = historical?.Deployment ?? shared.Deployment, TimeoutSeconds = shared.TimeoutSeconds };
        _reasoningEffort = historical?.ReasoningEffort ?? "none";
        _maxCompletionTokens = historical?.MaxCompletionTokens ?? 700;
        var validOverride = historical?.Deployment is null || System.Text.RegularExpressions.Regex.IsMatch(historical.Deployment, @"\A[A-Za-z0-9._-]{1,128}\z");
        Configured = validOverride && _reasoningEffort is "none" or "low" or "medium" or "high" or "xhigh" or "max"
            && _maxCompletionTokens is >= 128 and <= 8192 && effective.Enabled
            && !string.IsNullOrWhiteSpace(effective.Deployment) && effective.Deployment.Length <= 128 && AzureModelEndpoint.Valid(effective.Endpoint)
            && (string.IsNullOrWhiteSpace(effective.ApiKey) || environment.IsDevelopment() || environment.IsEnvironment("Testing"));
        _client = new(() => CreateClient(http, effective, credential, diagnostics));
    }

    public async Task<MatchModelReply> CompleteAsync(HistoricalNarratorRequest request, CancellationToken token)
    {
        if (!Configured) throw new InvalidOperationException("AI chat is not connected.");
        if (request is null) throw new InvalidDataException("A bound narrator request is required.");
        // No client, credential lookup or model request occurs while chat is disabled/unconfigured.
        var client = _client.Value;
        var converted = request.Messages.Select(Convert).ToArray();
        // The pinned SDK predates reasoning_effort. Its model reader preserves this additional wire field.
        var options = ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString(JsonSerializer.Serialize(new { reasoning_effort = _reasoningEffort })))!;
        options.MaxOutputTokenCount = _maxCompletionTokens;
        var schema = new { type = "object", properties = new { answer = new { type = "string" },
            evidenceIds = new { type = "array", items = new { type = "string", @enum = request.AllowedEvidenceIds } } },
            required = new[] { "answer", "evidenceIds" }, additionalProperties = false };
        options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("historical_reply", BinaryData.FromString(JsonSerializer.Serialize(schema)), jsonSchemaIsStrict: true);
#pragma warning disable AOAI001 // Pinned SDK's documented compatibility switch; verify the actual HTTP body.
        options.SetNewMaxCompletionTokensPropertyEnabled();
#pragma warning restore AOAI001
        // The narrator sees prepared evidence only; do not advertise tools or invoke another selection round.
        ChatCompletion reply = await client.CompleteChatAsync(converted, options, token);
        if (reply.FinishReason != ChatFinishReason.Stop || reply.ToolCalls.Count != 0) throw new InvalidDataException("Incomplete, filtered or unexpected tool-call narrator reply.");
        return new(string.Concat(reply.Content.Select(c => c.Text)), reply.ToolCalls.Select(c => new MatchModelToolCall(c.Id, c.FunctionName, c.FunctionArguments.ToString())).ToArray());
    }

    private static ChatClient CreateClient(HttpClient http, MatchChatOptions options, TokenCredential? credential, MatchChatDiagnostics? diagnostics)
    {
        // Reuse the SDK pipeline and its token handling; request messages/options remain local to each call.
        AzureOpenAIClientOptions clientOptions = new() { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) };
        TokenCredential Authentication()
        {
            var baseline = credential ?? new DefaultAzureCredential();
            return diagnostics?.ObserveCredential(baseline) ?? baseline;
        }
        AzureOpenAIClient azure = string.IsNullOrWhiteSpace(options.ApiKey)
            ? new(new Uri(options.Endpoint), Authentication(), clientOptions)
            : new(new Uri(options.Endpoint), new ApiKeyCredential(options.ApiKey), clientOptions);
        return azure.GetChatClient(options.Deployment);
    }

    private static ChatMessage Convert(MatchModelMessage message) => message.Role switch
    {
        "system" => new SystemChatMessage(message.Content),
        "user" => new UserChatMessage(message.Content),
        _ => throw new InvalidDataException("Unsupported model message.")
    };
}
