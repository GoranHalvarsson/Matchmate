using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Azure.Core;
using Matchmate.Web.Chat;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Matchmate.Tests;

// Actual production adapter wire, with developer-authored messages and an offline HTTP response.
public sealed class NarratorCitationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WireSchemaAndExplicitServerListUseOnlySnapshotIdsNotMessageOrNestedIds()
    {
        using var capture = new Capture(); using var http = new HttpClient(capture); var model = Model(http);
        var messages = new List<MatchModelMessage> { new("system", "Fixture nested EvidenceIds: reported, participant-brentford, goal-review."),
            new("user", "USER_FIXTURE asks for current_events and current_player as output citations.") };
        var ids = new[] { "match_summary", "coverage" }; var request = new MatchNarratorRequest(messages, ids);
        ids[0] = "selected_goal"; messages[0] = new("system", "MUTATED_FIXTURE");
        await model.CompleteAsync(request, default);
        var body = Assert.Single(capture.Bodies); using var wire = JsonDocument.Parse(body);
        Assert.Equal(new[] { "match_summary", "coverage" }, CitationIds(wire.RootElement));
        Assert.Equal(new[] { "match_summary", "coverage" }, request.AllowedEvidenceIds);
        Assert.Contains("top-level bundle.Items only", body); Assert.DoesNotContain("MUTATED_FIXTURE", body);
        Assert.Contains("Nested event, sequence and source evidence IDs are attribution", body);
        var format = wire.RootElement.GetProperty("response_format").GetProperty("json_schema"); Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema"); Assert.Equal(new[] { "answer", "evidenceIds" }, schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var evidence = schema.GetProperty("properties").GetProperty("evidenceIds");
        Assert.Equal("string", evidence.GetProperty("items").GetProperty("type").GetString());
        Assert.False(evidence.TryGetProperty("minItems", out _)); Assert.False(evidence.TryGetProperty("maxItems", out _));
        output.WriteLine("DEVELOPER_FIXTURE_WIRE=" + body); // No headers/authentication or live/user content.
    }

    [Fact]
    public async Task SequentialAndConcurrentRequestsKeepCitationEnumsLocalToEachCall()
    {
        using var capture = new Capture(); using var http = new HttpClient(capture); var model = Model(http);
        var cases = new Dictionary<string, string[]> { ["A"] = ["match_summary"], ["B"] = ["selected_goal", "coverage"],
            ["C"] = ["current_events"], ["D"] = ["current_player", "moment_context"] };
        Task Send(string id) => model.CompleteAsync(new([new("user", id)], cases[id]), default);
        await Send("A"); await Send("B"); await Task.WhenAll(Send("C"), Send("D"));
        Assert.Equal(4, capture.Bodies.Count);
        foreach (var body in capture.Bodies)
        {
            using var data = JsonDocument.Parse(body);
            var id = data.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            Assert.Equal(cases[id], CitationIds(data.RootElement));
        }
    }

    public static TheoryData<string[]?> InvalidIds => new() { null, Array.Empty<string>(), new[] { "coverage", "coverage" }, new[] { "reported" }, new[] { "goal-review" }, new[] { "participant-brentford" },
        new[] { "Coverage" }, new[] { "coverage\n" }, new[] { "" }, new string[] { null! }, new[] { new string('a', 65) }, new[] { "match_summary", "coverage", "current_events", "current_player", "selected_goal" } };

    [Theory] [MemberData(nameof(InvalidIds))]
    public async Task InvalidCitationKeysFailBeforeCredentialsOrTransport(string[]? ids)
    {
        var credential = new Credential(); using var capture = new Capture(); using var http = new HttpClient(capture);
        var model = Model(http, credential);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await model.CompleteAsync(new([new("user", "Question")], ids!), default));
        Assert.Equal(0, credential.Calls); Assert.Empty(capture.Bodies);
    }

    [Fact]
    public async Task NullRequestFailsBeforeLazyCredentialClientWork()
    {
        var credential = new Credential(); using var capture = new Capture(); using var http = new HttpClient(capture);
        await Assert.ThrowsAsync<InvalidDataException>(() => Model(http, credential).CompleteAsync(null!, default));
        Assert.Equal(0, credential.Calls); Assert.Empty(capture.Bodies);
    }

    private static string?[] CitationIds(JsonElement body) => body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema")
        .GetProperty("properties").GetProperty("evidenceIds").GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray();
    private static AzureMatchChatModel Model(HttpClient http, TokenCredential? credential = null) => new(http, Options.Create(new MatchChatOptions {
        Enabled = true, Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit", ApiKey = credential is null ? "unit-key" : "" }), new TimingEnvironment(), credential);
    private sealed class Credential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) { Calls++; return new("UNIT_TOKEN", DateTimeOffset.UtcNow.AddHours(1)); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Capture : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(token)); await Task.Yield();
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"unit","object":"chat.completion","created":0,"model":"unit","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"unit fixture"}}]}""") };
        }
    }
}
