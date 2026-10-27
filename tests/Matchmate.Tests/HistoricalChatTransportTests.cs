using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatTransportTests
{
    private static DecisionRoutingOptions Routing() => new() { Enabled = true, Endpoint = "https://unit.services.ai.azure.com/", Deployment = "decision-unit", ApiKey = "synthetic-key" };
    private static MatchChatOptions Narration() => new() { Enabled = true, Endpoint = "https://unit.openai.azure.com/", Deployment = "narrator-unit", ApiKey = "synthetic-key" };
    private static HistoricalRoutingInput Input() => new("And Stoke?", HistoricalChatContext.From(HistoricalChatFixtures.Fixture()), [new("How were Southampton doing?", "southampton")], new("form", "southampton"));
    private static string Choice(string choice = "overview", double probability = .95) => JsonSerializer.Serialize(new { answers = new { workflow = new { type = "choice", choice,
        probabilities = HistoricalWorkflows.Criteria.Keys.ToDictionary(k => k, k => k == choice ? probability : (1 - probability) / (HistoricalWorkflows.Criteria.Count - 1)) } } });
    private static HistoricalNarratorRequest Request() => new([new("system", HistoricalGuidePrompt.Version + "\n" + HistoricalGuidePrompt.Text), new("user", "Tell me about the result")], ["fixture"]);
    private static string Completion() => JsonSerializer.Serialize(new { id = "synthetic-completion", @object = "chat.completion", created = 0, model = "synthetic", choices = new[] {
        new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = HistoricalChatFixtures.Reply("The result is recorded.", "fixture") } } } });
    [Fact]
    public async Task RouterUsesSingleBoundedChoiceAndIntentHistoryWithoutAssistantFacts()
    {
        using var handler = new Capture(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice()) }); using var http = new HttpClient(handler);
        var result = await new DecisionHistoricalWorkflowRouter(http, Options.Create(Routing()), new Environment()).RouteAsync(Input(), default);
        Assert.Equal("overview", result.WorkflowId); var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://unit.services.ai.azure.com/providers/microsoft/v1/systemone", sent.Uri);
        using var json = JsonDocument.Parse(sent.Body); var state = json.RootElement.GetProperty("state");
        Assert.Equal("form", state.GetProperty("AcceptedTopic").GetProperty("Workflow").GetString());
        Assert.Equal("How were Southampton doing?", state.GetProperty("PreviousQuestions")[0].GetProperty("Question").GetString());
        Assert.True(state.GetProperty("FixtureSelected").GetBoolean());
        Assert.Contains("END of this played date", state.GetProperty("SelectedContext").GetString());
        Assert.Contains("code reports when no earlier rank exists", state.GetProperty("SelectedContext").GetString());
        Assert.False(state.TryGetProperty("SeasonKey", out _)); Assert.False(state.TryGetProperty("Date", out _));
        Assert.Single(json.RootElement.GetProperty("questions").EnumerateObject()); Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain("HomeGoals", sent.Body); Assert.DoesNotContain("owner", sent.Body); Assert.DoesNotContain("Answer", state.GetRawText());
    }
    [Fact]
    public async Task SameIntentHasSameRouterStateAcrossSelectedFixtureDates()
    {
        using var handler = new Capture(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice()) }); using var http = new HttpClient(handler);
        var router = new DecisionHistoricalWorkflowRouter(http, Options.Create(Routing()), new Environment());
        var input = Input() with { Question = "What changed in the table?", PreviousQuestions = [], Topic = null };
        Assert.Equal(MatchRoutingStatus.Selected, (await router.RouteAsync(input, default)).Status);
        Assert.Equal(MatchRoutingStatus.Selected, (await router.RouteAsync(input with { Context = new("other-version", "2026-27", "brentford:chelsea", "2026-09-18") }, default)).Status);
        using var first = JsonDocument.Parse(handler.Requests[0].Body); using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(first.RootElement.GetProperty("state").GetRawText(), second.RootElement.GetProperty("state").GetRawText());
    }
    [Theory]
    [InlineData("overview")][InlineData("unsupported")][InlineData("clarify")]
    public void EveryFixedWorkflowHasCompleteProbabilityMap(string workflow) => Assert.Equal(workflow, DecisionHistoricalWorkflowRouter.Parse(Encoding.UTF8.GetBytes(Choice(workflow)), Routing()).WorkflowId);
    [Theory]
    [InlineData("form")][InlineData("table")][InlineData("meetings")][InlineData("ground")][InlineData("continue_topic")]
    public void RetiredOverlappingSubtopicsCannotBypassTheGroupedProviderAllowlist(string workflow) => Assert.Equal(MatchRoutingStatus.InvalidResponse,
        DecisionHistoricalWorkflowRouter.Parse(Encoding.UTF8.GetBytes(Choice(workflow)), Routing()).Status);
    [Theory]
    [InlineData("missing")][InlineData("extra")][InlineData("sum")][InlineData("negative")][InlineData("not-top")][InlineData("unknown-choice")][InlineData("argument")][InlineData("duplicate")][InlineData("nonfinite")]
    public void InvalidProviderChoicesFailClosed(string mutation)
    {
        var json = JsonNode.Parse(Choice())!; var answer = json["answers"]!["workflow"]!; var probabilities = answer["probabilities"]!.AsObject();
        switch (mutation)
        {
            case "missing": probabilities.Remove("unsupported"); break; case "extra": probabilities["query"] = 0; break;
            case "sum": probabilities["overview"] = .2; break; case "negative": probabilities["unsupported"] = -.1; break;
            case "not-top": answer["choice"] = "unsupported"; break; case "unknown-choice": answer["choice"] = "fetch_secret"; break;
            case "argument": answer["clubId"] = "injected"; break;
        }
        var body = json.ToJsonString();
        if (mutation == "duplicate") body = body.Replace("\"choice\":\"overview\"", "\"choice\":\"overview\",\"choice\":\"overview\"", StringComparison.Ordinal);
        if (mutation == "nonfinite") body = body.Replace("\"overview\":0.95", "\"overview\":1e999", StringComparison.Ordinal);
        Assert.Equal(MatchRoutingStatus.InvalidResponse, DecisionHistoricalWorkflowRouter.Parse(Encoding.UTF8.GetBytes(body), Routing()).Status);
    }
    [Fact]
    public void LowProbabilityAndOversizedOrInvalidShapesAbstain()
    {
        Assert.Equal(MatchRoutingStatus.Clarify, DecisionHistoricalWorkflowRouter.Parse(Encoding.UTF8.GetBytes(Choice(probability: .7)), Routing()).Status);
        foreach (var bytes in new[] { "{}"u8.ToArray(), "null"u8.ToArray(), new byte[DecisionHistoricalWorkflowRouter.ResponseBytes + 1] })
            Assert.Equal(MatchRoutingStatus.InvalidResponse, DecisionHistoricalWorkflowRouter.Parse(bytes, Routing()).Status);
    }
    [Fact]
    public async Task NarratorUsesItsOwnStrictCitationSchemaWithoutToolsOrRetries()
    {
        using var handler = new Capture(_ => new(HttpStatusCode.OK) { Content = new StringContent(Completion()) }); using var http = new HttpClient(handler);
        var result = await new AzureHistoricalChatModel(http, Options.Create(Narration()), new Environment()).CompleteAsync(Request(), default);
        Assert.Empty(result.Calls); using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body); var root = json.RootElement;
        Assert.False(root.TryGetProperty("tools", out _)); var schema = root.GetProperty("response_format").GetProperty("json_schema");
        Assert.Equal("historical_reply", schema.GetProperty("name").GetString()); Assert.True(schema.GetProperty("strict").GetBoolean());
        Assert.Equal("fixture", schema.GetProperty("schema").GetProperty("properties").GetProperty("evidenceIds").GetProperty("items").GetProperty("enum")[0].GetString());
        using var failure = new Capture(_ => new(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":{\"message\":\"synthetic\"}}") }); using var failed = new HttpClient(failure);
        await Assert.ThrowsAnyAsync<System.ClientModel.ClientResultException>(() => new AzureHistoricalChatModel(failed, Options.Create(Narration()), new Environment()).CompleteAsync(Request(), default)); Assert.Single(failure.Requests);
    }
    [Theory]
    [InlineData("disabled")][InlineData("endpoint")][InlineData("production-key")]
    public async Task DisabledInvalidOrProductionKeyNeverRequestsCredentialsOrNetwork(string mode)
    {
        var route = Routing(); var model = Narration(); var environment = new Environment(); var credential = new Credential();
        if (mode == "disabled") { route.Enabled = false; model.Enabled = false; }
        if (mode == "endpoint") route.Endpoint = model.Endpoint = "https://untrusted.example/";
        if (mode == "production-key") environment.EnvironmentName = "Production";
        using var handler = new Capture(_ => throw new InvalidOperationException("Must not send")); using var http = new HttpClient(handler);
        var router = new DecisionHistoricalWorkflowRouter(http, Options.Create(route), environment, credential);
        var narrator = new AzureHistoricalChatModel(http, Options.Create(model), environment, credential);
        Assert.False(router.Configured); Assert.False(narrator.Configured);
        Assert.Equal(MatchRoutingStatus.Unavailable, (await router.RouteAsync(Input(), default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => narrator.CompleteAsync(Request(), default)); Assert.Empty(handler.Requests); Assert.Equal(0, credential.Calls);
    }
    [Fact]
    public async Task AadUsesCognitiveAudienceAndRedirectsAndLargeRepliesDoNotRetry()
    {
        foreach (var status in new[] { HttpStatusCode.TemporaryRedirect, HttpStatusCode.OK })
        {
            using var handler = new Capture(_ => new(status) { Content = new StringContent(new string('x', DecisionHistoricalWorkflowRouter.ResponseBytes + 1)) }); using var http = new HttpClient(handler);
            var options = Routing(); options.ApiKey = ""; var credential = new Credential();
            var result = await new DecisionHistoricalWorkflowRouter(http, Options.Create(options), new Environment(), credential).RouteAsync(Input(), default);
            Assert.NotEqual(MatchRoutingStatus.Selected, result.Status); Assert.Single(handler.Requests); Assert.Equal(1, credential.Calls); Assert.Equal(DevelopmentModelCredential.CognitiveScope, Assert.Single(credential.Scopes!));
        }
    }
    private sealed record Sent(string Uri, string Body);
    private sealed class Capture(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token))); return send(request); }
    }
    private sealed class Credential : TokenCredential
    {
        public int Calls { get; private set; } public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) { Calls++; Scopes = context.Scopes; return new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1)); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(context, cancellationToken));
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Synthetic tests";
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
