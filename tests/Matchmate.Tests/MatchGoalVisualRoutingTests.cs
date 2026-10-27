using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

// Captured-score regression only: original bodies/hashes remain frozen; the new option is explicitly zero-filled, never live inference.
public sealed class MatchGoalVisualRoutingTests(ITestOutputHelper output)
{
    private static JsonDocument Captured() => JsonDocument.Parse(File.ReadAllText(Path.Combine(LocalPaths.FindRoot(),
        "tests", "Matchmate.Tests", "Fixtures", "goal-visual-routing-captured-v1.json")));
    public static IEnumerable<object[]> CapturedIds()
    {
        using var data = Captured();
        return data.RootElement.GetProperty("cases").EnumerateArray().Select(r => new object[] { r.GetProperty("id").GetString()! }).ToArray();
    }

    [Theory] [MemberData(nameof(CapturedIds))]
    public async Task R_GV_001_CapturedScoresWithZeroNewOptionPreserveTenOutcomesAndClarifyTwoPreconditions(string id)
    {
        using var data = Captured(); var metadata = data.RootElement.GetProperty("metadata");
        Assert.Contains("not fresh inference", metadata.GetProperty("kind").GetString());
        Assert.Equal("f7792ed09938a3453cc3d0aa319492da7696e6b5b0149f0377101e6de3eae58d", metadata.GetProperty("sourceResult").GetProperty("sha256").GetString());
        Assert.Equal(10, metadata.GetProperty("sourceResult").GetProperty("originalCorrect").GetInt32());
        Assert.Equal(12, metadata.GetProperty("sourceResult").GetProperty("originalDecisionCalls").GetInt32());
        var row = data.RootElement.GetProperty("cases").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        var context = DecodeLegacyContext(row.GetProperty("context"));
        var question = row.GetProperty("question").GetString()!; var body = row.GetProperty("responseBody").GetString()!;
        Assert.Equal(row.GetProperty("responseBodySha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
        var prerequisite = context.SelectedEvent is null && MatchEventVisual.Intent(question) is not null;
        var options = Enabled(); options.ApiKey = "unit-only-key";
        Assert.Equal(metadata.GetProperty("minimumProbability").GetDouble(), options.MinimumProbability);
        Assert.Equal(metadata.GetProperty("minimumMargin").GetDouble(), options.MinimumMargin);
        using var transport = new CaptureTransport(CurrentEnvelope(body), (HttpStatusCode)row.GetProperty("httpStatus").GetInt32());
        using var http = new HttpClient(transport); var credential = new ForbiddenCredential();
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new TestEnvironment(), credential).RouteAsync(new(question, context, []), default);
        var chosen = result.Status == MatchRoutingStatus.Clarify ? "clarify" : result.WorkflowId;
        Assert.Equal(row.GetProperty("expected").GetString(), chosen); Assert.Equal(0, credential.Calls);
        if (prerequisite)
        {
            Assert.Equal(MatchRoutingStatus.Clarify, result.Status); Assert.Null(result.WorkflowId); Assert.Equal(1, transport.Calls);
            Assert.Equal("current_match_moment", row.GetProperty("originalChosen").GetString());
        }
        else
        {
            Assert.Equal(MatchRoutingStatus.Selected, result.Status); Assert.Equal(row.GetProperty("originalChosen").GetString(), result.WorkflowId);
            Assert.Equal(1, transport.Calls); using var sent = JsonDocument.Parse(transport.Body!);
            Assert.Equal(question, sent.RootElement.GetProperty("state").GetProperty("Question").GetString());
            Assert.Equal(context, JsonSerializer.Deserialize<MatchChatContext>(sent.RootElement.GetProperty("state").GetProperty("Context").GetRawText()));
            var workflow = sent.RootElement.GetProperty("questions").GetProperty("workflow");
            Assert.Equal(DecisionWorkflowRouter.Instructions, workflow.GetProperty("instructions").GetString());
            Assert.Equal(11, workflow.GetProperty("criteria").EnumerateObject().Count());
            foreach (var criterion in DecisionWorkflowRouter.Criteria) Assert.Equal(criterion.Value, workflow.GetProperty("criteria").GetProperty(criterion.Key).GetString());
        }
        output.WriteLine($"{id}: captured-score regression with zero-filled statistics option -> {chosen}; capture transport calls={transport.Calls}; credential calls={credential.Calls}; new inference=0; mandatory={row.GetProperty("mandatory").GetBoolean()}");
    }

    [Theory] [InlineData("Show me how this goal happened")] [InlineData("Draw a diagram of the goal I am asking about.")]
    public async Task MissingSelectionStillClarifiesAfterOneValidDecision(string question)
    {
        var response = JsonSerializer.Serialize(new { answers = new { workflow = new { type = "choice", choice = "current_match_moment",
            probabilities = DecisionWorkflowRouter.Criteria.Keys.ToDictionary(id => id, id => id == "current_match_moment" ? 1d : 0d) } } });
        using var transport = new CaptureTransport(response); using var http = new HttpClient(transport); var credential = new ForbiddenCredential(); var options = Enabled(); options.ApiKey = "unit-only-key";
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new TestEnvironment(), credential).RouteAsync(Input(question), default);
        Assert.Equal(MatchRoutingStatus.Clarify, result.Status); Assert.Null(result.WorkflowId); Assert.Equal(1, transport.Calls); Assert.Equal(0, credential.Calls);
        using var sent = JsonDocument.Parse(transport.Body!); Assert.Equal(question, sent.RootElement.GetProperty("state").GetProperty("Question").GetString());
    }

    [Theory] [InlineData("Show the current score")] [InlineData("Show how many goals have been reported")]
    public async Task OrdinaryShowScoreQuestionStillUsesDecisionTransport(string question)
    {
        using var data = Captured(); var row = data.RootElement.GetProperty("cases").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "GV-10");
        using var transport = new CaptureTransport(CurrentEnvelope(row.GetProperty("responseBody").GetString()!)); using var http = new HttpClient(transport);
        var options = Enabled(); options.ApiKey = "unit-only-key";
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new TestEnvironment()).RouteAsync(Input(question), default);
        Assert.Equal(MatchRoutingStatus.Selected, result.Status); Assert.Equal("current_match_summary", result.WorkflowId); Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task MissingSelectionPreconditionHonorsCancellationBeforeClarificationOrExternalAccess()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var transport = new CaptureTransport("unreachable"); using var http = new HttpClient(transport); var credential = new ForbiddenCredential();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DecisionWorkflowRouter(http, Options.Create(Enabled()), new TestEnvironment(), credential)
            .RouteAsync(Input("Show me how this goal happened"), cancellation.Token));
        Assert.Equal(0, transport.Calls); Assert.Equal(0, credential.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExistingConfigurationAndInputValidationStillPrecedeVisualPrecondition(bool invalidInput)
    {
        var options = Enabled(); options.Enabled = invalidInput;
        using var transport = new CaptureTransport("unreachable"); using var http = new HttpClient(transport); var credential = new ForbiddenCredential();
        var input = Input("Show me how this goal happened"); if (invalidInput) input = input with { PreviousQuestions = default };
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new TestEnvironment(), credential).RouteAsync(input, default);
        Assert.Equal(invalidInput ? MatchRoutingStatus.InvalidResponse : MatchRoutingStatus.Unavailable, result.Status);
        Assert.Equal(0, transport.Calls); Assert.Equal(0, credential.Calls);
    }

    private static MatchRoutingInput Input(string question) => new(question, new(SharedFootballTests.Query(SharedFootballTests.Fixture(), 0), 2), []);
    // The old ten-option response is no longer a complete map. Preserve all captured scores and add only the new option at zero.
    private static string CurrentEnvelope(string captured)
    {
        var node = JsonNode.Parse(captured)!; node["answers"]!["workflow"]!["probabilities"]!["current_replay_statistics"] = 0d;
        return node.ToJsonString();
    }
    // Frozen D099 captures keep their original SelectedGoal JSON. Adapt only their trusted locator shape in this decoder.
    private static MatchChatContext DecodeLegacyContext(JsonElement value)
    {
        var context = JsonSerializer.Deserialize<MatchChatContext>(value.GetRawText())!;
        return value.TryGetProperty("SelectedGoal", out var legacy) && legacy.ValueKind != JsonValueKind.Null
            ? context with { SelectedEvent = new(legacy.GetProperty("RecordId").GetString()!, legacy.GetProperty("ReleaseCursorMs").GetInt64(), legacy.GetProperty("ReportedClock").GetString()!, "goal") }
            : context;
    }
    private static DecisionRoutingOptions Enabled() => new() { Enabled = true, Endpoint = "https://unit-resource.services.ai.azure.com/", Deployment = "decision-unit" };
    private sealed class CaptureTransport(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Body = await request.Content!.ReadAsStringAsync(token); return new(status) { Content = new StringContent(response) }; }
    }
    private sealed class ForbiddenCredential : TokenCredential
    {
        public int Calls;
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token) { Calls++; throw new InvalidOperationException("Credential access is forbidden for a missing required selection."); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) { Calls++; throw new InvalidOperationException("Credential access is forbidden for a missing required selection."); }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Unit";
        public string ContentRootPath { get; set; } = "."; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
