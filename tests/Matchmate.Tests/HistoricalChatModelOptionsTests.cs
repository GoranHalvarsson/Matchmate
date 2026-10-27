using System.Net;
using System.Text.Json;
using Azure.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.HistoricalChat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatModelOptionsTests
{
    private static MatchChatOptions Shared() => new() { Enabled = true, Endpoint = "https://unit.openai.azure.com/", Deployment = "shared-narrator", ApiKey = "synthetic-key", TimeoutSeconds = 27 };
    private static HistoricalNarratorRequest Request() => new([new("system", HistoricalGuidePrompt.Version + "\n" + HistoricalGuidePrompt.Text), new("user", "Tell me about the result")], ["fixture", "form"]);
    private static string Completion() => JsonSerializer.Serialize(new { id = "synthetic-reply", @object = "chat.completion", created = 0, model = "synthetic", choices = new[] {
        new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = HistoricalChatFixtures.Reply("The result is recorded.", "fixture") } } } });
    private static void AssertWire(Sent sent, string deployment, string effort, int budget)
    {
        Assert.Equal("/openai/deployments/" + deployment + "/chat/completions", new Uri(sent.Uri).AbsolutePath);
        using var json = JsonDocument.Parse(sent.Body); var body = json.RootElement;
        Assert.Equal(effort, body.GetProperty("reasoning_effort").GetString());
        Assert.Equal(budget, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _)); Assert.False(body.TryGetProperty("tools", out _));
        var format = body.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(format.GetProperty("strict").GetBoolean()); Assert.Equal("historical_reply", format.GetProperty("name").GetString());
        Assert.Equal(new[] { "fixture", "form" }, format.GetProperty("schema").GetProperty("properties").GetProperty("evidenceIds").GetProperty("items").GetProperty("enum").EnumerateArray().Select(id => id.GetString()));
    }

    [Theory]
    [InlineData("absent", "shared-narrator", "none", 700)]
    [InlineData("empty", "shared-narrator", "none", 700)]
    [InlineData("deployment", "historical-trial", "none", 700)]
    [InlineData("effort", "shared-narrator", "low", 700)]
    [InlineData("budget", "shared-narrator", "none", 2048)]
    public async Task MissingOverridesIndependentlyRetainTheExistingWireDefaults(string profile, string deployment, string effort, int budget)
    {
        var historical = profile == "absent" ? null : new HistoricalReplyModelOptions();
        if (profile == "deployment") historical!.Deployment = deployment;
        if (profile == "effort") historical!.ReasoningEffort = effort;
        if (profile == "budget") historical!.MaxCompletionTokens = budget;
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, Options.Create(Shared()), new TestEnvironment(), historicalConfigured: historical is null ? null : Options.Create(historical));
        Assert.True(model.Configured); await model.CompleteAsync(Request(), default);
        AssertWire(Assert.Single(capture.Requests), deployment, effort, budget);
    }

    [Fact]
    public async Task HistoricalOverrideUsesItsOwnAliasLowEffortAndBudgetWithoutChangingSharedOptions()
    {
        var shared = Shared(); var before = JsonSerializer.Serialize(shared); var configured = Options.Create(shared);
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, configured, new TestEnvironment(), historicalConfigured: Options.Create(new HistoricalReplyModelOptions
            { Deployment = "historical-trial", ReasoningEffort = "low", MaxCompletionTokens = 2048 }));
        Assert.True(model.Configured); await model.CompleteAsync(Request(), default);
        AssertWire(Assert.Single(capture.Requests), "historical-trial", "low", 2048);
        Assert.Same(shared, configured.Value); Assert.Equal(before, JsonSerializer.Serialize(shared));
    }

    [Fact]
    public async Task EffectiveOptionsAreFrozenBeforeTheLazyClientIsCreated()
    {
        var shared = Shared(); var historical = new HistoricalReplyModelOptions { Deployment = "historical-trial", ReasoningEffort = "low", MaxCompletionTokens = 2048 };
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, Options.Create(shared), new TestEnvironment(), historicalConfigured: Options.Create(historical));
        shared.Deployment = "changed-shared"; shared.Enabled = false; shared.Endpoint = "https://untrusted.example/";
        historical.Deployment = "changed-profile"; historical.ReasoningEffort = "invalid"; historical.MaxCompletionTokens = 0;
        Assert.True(model.Configured); await model.CompleteAsync(Request(), default);
        var sent = Assert.Single(capture.Requests); Assert.Equal("unit.openai.azure.com", new Uri(sent.Uri).Host);
        AssertWire(sent, "historical-trial", "low", 2048);
    }

    [Theory]
    [InlineData("none")][InlineData("low")][InlineData("medium")][InlineData("high")][InlineData("xhigh")][InlineData("max")]
    public async Task AllowedEffortsAndBudgetBoundariesSurviveThePinnedSdkWire(string effort)
    {
        var budget = effort == "none" ? 128 : 8192;
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, Options.Create(Shared()), new TestEnvironment(), historicalConfigured: Options.Create(new HistoricalReplyModelOptions
            { ReasoningEffort = effort, MaxCompletionTokens = budget }));
        Assert.True(model.Configured); await model.CompleteAsync(Request(), default);
        AssertWire(Assert.Single(capture.Requests), "shared-narrator", effort, budget);
    }

    [Theory]
    [InlineData("blank-deployment")][InlineData("space-deployment")][InlineData("path-deployment")][InlineData("control-deployment")][InlineData("long-deployment")]
    [InlineData("blank-effort")][InlineData("unknown-effort")][InlineData("low-budget")][InlineData("high-budget")]
    public async Task ExplicitInvalidOverridesFailBeforeCredentialsOrNetwork(string invalid)
    {
        var historical = new HistoricalReplyModelOptions();
        switch (invalid)
        {
            case "blank-deployment": historical.Deployment = ""; break;
            case "space-deployment": historical.Deployment = " "; break;
            case "path-deployment": historical.Deployment = "../other"; break;
            case "control-deployment": historical.Deployment = "alias\n"; break;
            case "long-deployment": historical.Deployment = new string('a', 129); break;
            case "blank-effort": historical.ReasoningEffort = ""; break;
            case "unknown-effort": historical.ReasoningEffort = "unbounded"; break;
            case "low-budget": historical.MaxCompletionTokens = 127; break;
            case "high-budget": historical.MaxCompletionTokens = 8193; break;
        }
        var shared = Shared(); shared.ApiKey = ""; var credential = new ForbiddenCredential();
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, Options.Create(shared), new TestEnvironment(), credential, historicalConfigured: Options.Create(historical));
        Assert.False(model.Configured); await Assert.ThrowsAsync<InvalidOperationException>(() => model.CompleteAsync(Request(), default));
        Assert.Equal(0, credential.Calls); Assert.Empty(capture.Requests);
    }

    [Theory]
    [InlineData("disabled")][InlineData("endpoint")][InlineData("production-key")]
    public async Task AValidHistoricalProfileCannotBypassTheSharedAvailabilityAndConnectionGuards(string invalid)
    {
        var shared = Shared(); var environment = new TestEnvironment(); var credential = new ForbiddenCredential();
        if (invalid == "disabled") shared.Enabled = false;
        if (invalid == "endpoint") shared.Endpoint = "https://untrusted.example/";
        if (invalid == "production-key") environment.EnvironmentName = "Production";
        using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = new AzureHistoricalChatModel(http, Options.Create(shared), environment, credential, historicalConfigured: Options.Create(new HistoricalReplyModelOptions
            { Deployment = "historical-trial", ReasoningEffort = "low", MaxCompletionTokens = 2048 }));
        Assert.False(model.Configured); await Assert.ThrowsAsync<InvalidOperationException>(() => model.CompleteAsync(Request(), default));
        Assert.Equal(0, credential.Calls); Assert.Empty(capture.Requests);
    }

    [Fact]
    public void RegistrationWithoutConfigurationKeepsOptionalProfileDefaults()
    {
        using var provider = new ServiceCollection().AddHistoricalMatchChat().BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HistoricalReplyModelOptions>>().Value;
        Assert.Null(options.Deployment); Assert.Null(options.ReasoningEffort); Assert.Null(options.MaxCompletionTokens);
    }

    [Fact]
    public async Task ExplicitBindingReachesTheHistoricalClientAndLeavesSharedAndDecisionConfigurationSeparate()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MatchChat:Enabled"] = "true", ["MatchChat:Deployment"] = "shared-narrator", ["MatchChat:TimeoutSeconds"] = "27",
            ["MatchChat:Endpoint"] = "https://unit.openai.azure.com/", ["MatchChat:ApiKey"] = "synthetic-key",
            ["DecisionRouting:Deployment"] = "decision-unit", ["HistoricalReplyModel:Deployment"] = "historical-trial",
            ["HistoricalReplyModel:ReasoningEffort"] = "low", ["HistoricalReplyModel:MaxCompletionTokens"] = "2048"
        }).Build();
        var services = new ServiceCollection(); services.Configure<MatchChatOptions>(configuration.GetSection(MatchChatOptions.Section));
        services.Configure<DecisionRoutingOptions>(configuration.GetSection(DecisionRoutingOptions.Section)); services.AddHistoricalMatchChat(configuration);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment()); services.AddSingleton<MatchChatDiagnostics>();
        using var capture = new Capture(); services.AddHttpClient(nameof(IHistoricalChatModel)).ConfigurePrimaryHttpMessageHandler(() => capture);
        using var provider = services.BuildServiceProvider(); var historical = provider.GetRequiredService<IOptions<HistoricalReplyModelOptions>>().Value;
        var shared = provider.GetRequiredService<IOptions<MatchChatOptions>>().Value; var routing = provider.GetRequiredService<IOptions<DecisionRoutingOptions>>().Value;
        Assert.Equal("historical-trial", historical.Deployment); Assert.Equal("low", historical.ReasoningEffort); Assert.Equal(2048, historical.MaxCompletionTokens);
        Assert.Equal("shared-narrator", shared.Deployment); Assert.True(shared.Enabled); Assert.Equal(27, shared.TimeoutSeconds);
        Assert.Equal("decision-unit", routing.Deployment); Assert.Equal("shared-narrator", configuration["MatchChat:Deployment"]);
        await provider.GetRequiredService<IHistoricalChatModel>().CompleteAsync(Request(), default);
        AssertWire(Assert.Single(capture.Requests), "historical-trial", "low", 2048);
    }

    private sealed record Sent(string Uri, string Body);
    private sealed class Capture : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(token)));
            return new(HttpStatusCode.OK) { Content = new StringContent(Completion()) };
        }
    }
    private sealed class ForbiddenCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Invalid configuration must not request a token."); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(context, cancellationToken));
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Synthetic profile tests";
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
