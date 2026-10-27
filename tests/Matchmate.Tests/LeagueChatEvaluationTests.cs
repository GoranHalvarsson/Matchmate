using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Matchmate.LeagueChatEvaluation;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatEvaluationTests
{
    [Fact]
    public async Task ValidDecisionDiagnosticsContainOnlyKnownScalarsAndPreserveExactResponseBytes()
    {
        var root = JsonNode.Parse(DecisionBody(.95))!;
        root["model"] = "private model sentinel"; root["usage"] = new JsonObject { ["tokens"] = 123, ["private"] = "private usage sentinel" };
        root["private root sentinel"] = "private response sentinel";
        var bytes = Encoding.UTF8.GetBytes(" \n" + root.ToJsonString() + "\r\n");
        using var inner = new DecisionHandler(bytes, false); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.services.ai.azure.com/") { Content = new StringContent("private question sentinel") };
        request.Headers.Authorization = new("Bearer", "private token sentinel");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Null(capture.Decision); Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        var diagnostic = Assert.IsType<EvaluationDecisionDiagnostics>(capture.Decision);
        Assert.Equal("club_context", diagnostic.Workflow.Choice); Assert.Equal(.95, diagnostic.Workflow.SelectedProbability); Assert.Equal(.9, diagnostic.Workflow.Confidence);
        Assert.Equal(.05 / 6, diagnostic.Workflow.RunnerUpProbability, 10); Assert.Equal(.95 - .05 / 6, diagnostic.Workflow.Margin, 10);
        Assert.Equal(LeagueWorkflowRegistry.TopicCriteria.Keys.Order(), diagnostic.Workflow.Probabilities.Keys.Order()); Assert.Equal(1, diagnostic.Workflow.Probabilities.Values.Sum(), 10);
        var receipt = JsonSerializer.Serialize(new { http = capture.Last, decision = capture.Decision });
        Assert.DoesNotContain("private", receipt); Assert.DoesNotContain("sentinel", receipt); Assert.Equal(1, inner.Calls);
    }
    [Theory]
    [InlineData(.95, MatchRoutingStatus.Selected)]
    [InlineData(.72, MatchRoutingStatus.Clarify)]
    public async Task ProductionRouterReadsTeeResponseAndKeepsItsOwnThresholdDecision(double selectedProbability, MatchRoutingStatus expected)
    {
        using var inner = new DecisionHandler(Encoding.UTF8.GetBytes(DecisionBody(selectedProbability)), false); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        var options = Options.Create(new DecisionRoutingOptions { Enabled = true, Endpoint = "https://unit.services.ai.azure.com/", Deployment = "synthetic-decision", ApiKey = "synthetic-key" });
        var router = new DecisionLeagueWorkflowRouter(http, options, new TestEnvironment());
        var result = await router.RouteAsync(new("Where do we stand?", new("2026-27", 0, null, "everton", "Everton"), []), default);
        Assert.Equal(expected, result.Status); Assert.Equal(selectedProbability, capture.Decision!.Workflow.SelectedProbability);
        Assert.Equal("club_context", capture.Decision.Workflow.Choice); Assert.Equal(1, inner.Calls);
    }
    [Theory]
    [InlineData("choice")] [InlineData("unknown-key")] [InlineData("missing-key")] [InlineData("negative")] [InlineData("range")]
    [InlineData("sum")] [InlineData("nonfinite")] [InlineData("string-probability")] [InlineData("confidence")]
    [InlineData("extra-secret")] [InlineData("duplicate")] [InlineData("text")] [InlineData("not-top")]
    public async Task MalformedOrUnexpectedResponseDataNeverBecomesDiagnostics(string mutation)
    {
        var node = JsonNode.Parse(DecisionBody(.95))!; var answer = node["answers"]!["workflow"]!; var probabilities = answer["probabilities"]!.AsObject();
        switch (mutation)
        {
            case "choice": answer["choice"] = "private response sentinel"; break;
            case "unknown-key": probabilities["private response sentinel"] = 0; break;
            case "missing-key": probabilities.Remove("league_context"); break;
            case "negative": probabilities["league_context"] = -.1; break;
            case "range": probabilities["club_context"] = 1.1; break;
            case "sum": probabilities["club_context"] = .3; break;
            case "string-probability": probabilities["league_context"] = "private response sentinel"; break;
            case "confidence": answer["confidence"] = 2; break;
            case "extra-secret": answer["private response sentinel"] = "private response sentinel"; break;
            case "not-top": answer["choice"] = "league_context"; break;
        }
        var body = node.ToJsonString();
        if (mutation == "duplicate") body = body.Replace("\"choice\":\"club_context\"", "\"choice\":\"club_context\",\"choice\":\"private response sentinel\"", StringComparison.Ordinal);
        if (mutation == "nonfinite") body = body.Replace("\"club_context\":0.95", "\"club_context\":1e999", StringComparison.Ordinal);
        if (mutation == "text") body = "\"private response sentinel\"";
        var bytes = Encoding.UTF8.GetBytes(body);
        using var inner = new DecisionHandler(bytes, false); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        using var response = await http.GetAsync("https://unit.services.ai.azure.com/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync()); Assert.Null(capture.Decision);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(new { http = capture.Last, decision = capture.Decision }));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task OversizeResponsesAreUnchangedAndProduceNoDiagnostics(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes(DecisionBody(.95) + new string(' ', DecisionLeagueWorkflowRouter.ResponseBytes));
        using var inner = new DecisionHandler(bytes, knownLength); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        using var response = await http.GetAsync("https://unit.services.ai.azure.com/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync()); Assert.Null(capture.Decision); Assert.Equal(200, capture.Last!.Status);
    }
    [Fact]
    public async Task ExactDiagnosticByteCapIsAcceptedAndLatePreviousContentCannotRepopulateNextCase()
    {
        var body = DecisionBody(.95); var bytes = Encoding.UTF8.GetBytes(body + new string(' ', DecisionLeagueWorkflowRouter.ResponseBytes - Encoding.UTF8.GetByteCount(body)));
        using var inner = new DecisionHandler(bytes, false); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        using (var response = await http.GetAsync("https://unit.services.ai.azure.com/", HttpCompletionOption.ResponseHeadersRead))
        { Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync()); Assert.NotNull(capture.Decision); }
        capture.BeginCase(); Assert.Null(capture.Decision); Assert.Null(capture.Last);
        using var next = await http.GetAsync("https://unit.services.ai.azure.com/", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await next.Content.ReadAsStreamAsync(); var prefix = new byte[1]; Assert.Equal(1, await stream.ReadAsync(prefix));
        capture.BeginCase(); await stream.CopyToAsync(Stream.Null);
        Assert.Null(capture.Decision); Assert.Null(capture.Last);
    }
    [Fact]
    public async Task PacingIsBeforeRequestAndKeepsMinimumGapAndFewerThanTwentyPerRollingMinute()
    {
        var now = DateTimeOffset.Parse("2026-10-23T12:00:00Z"); var requested = new List<DateTimeOffset>(); var waits = new List<TimeSpan>();
        var pacer = new EvaluationPacer(() => now, (delay, token) => { token.ThrowIfCancellationRequested(); waits.Add(delay); now += delay; return Task.CompletedTask; });
        for (var i = 0; i < 60; i++) { await pacer.WaitAsync(default); requested.Add(now); }
        Assert.Equal(59, waits.Count); Assert.All(waits, w => Assert.True(w >= TimeSpan.FromSeconds(3.2)));
        for (var i = 1; i < requested.Count; i++) Assert.True(requested[i] - requested[i - 1] >= TimeSpan.FromSeconds(3.2));
        Assert.All(requested, at => Assert.True(requested.Count(t => t <= at && t > at.AddMinutes(-1)) < 20));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacer.WaitAsync(cancelled.Token));
    }
    [Fact]
    public async Task HttpCaptureRetainsOnlyPublicStatusRetryAfterAndElapsedWithoutRetry()
    {
        using var inner = new StatusHandler(); using var capture = new EvaluationCaptureTransport(inner); using var http = new HttpClient(capture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.services.ai.azure.com/") { Content = new StringContent("never retain question or body") };
        request.Headers.Authorization = new("Bearer", "synthetic-secret"); using var response = await http.SendAsync(request);
        Assert.Equal(1, inner.Calls); Assert.Equal(429, capture.Last!.Status); Assert.Equal(11, capture.Last.RetryAfterSeconds);
        var json = JsonSerializer.Serialize(capture.Last); Assert.DoesNotContain("secret", json); Assert.DoesNotContain("question", json);
        using var parsed = JsonDocument.Parse(json); Assert.Equal(new[] { "Milliseconds", "RetryAfterSeconds", "Status" }, parsed.RootElement.EnumerateObject().Select(p => p.Name).Order());
        capture.BeginCase();
        var disabled = new DecisionLeagueWorkflowRouter(http, Options.Create(new DecisionRoutingOptions()), new TestEnvironment());
        var result = await disabled.RouteAsync(new("What happened here?", new("2026-27", 0, null, null, null), []), default);
        Assert.Equal(MatchRoutingStatus.Unavailable, result.Status); Assert.Null(capture.Last); Assert.Null(capture.Decision); Assert.Equal(1, inner.Calls);
    }
    [Fact]
    public void SemanticAbstentionsAndProviderFailuresStayDistinctAndThreeFailuresForceNonPass()
    {
        var score = new EvaluationScore(60);
        Assert.True(score.Register("clarify", new(MatchRoutingStatus.Clarify)));
        Assert.False(score.Register("named_club_form", new(MatchRoutingStatus.Clarify)));
        for (var i = 0; i < 51; i++) score.Register("club_context", new(MatchRoutingStatus.Selected, "club_context"));
        score.Register("league_context", new(MatchRoutingStatus.Unavailable)); score.Register("league_context", new(MatchRoutingStatus.InvalidResponse));
        Assert.False(score.StoppedForProviderFailure); score.Register("league_context", new(MatchRoutingStatus.Unavailable));
        Assert.True(score.StoppedForProviderFailure); Assert.False(score.Completed); Assert.False(score.MeetsTarget); Assert.Equal(52, score.Passed); Assert.Equal(56, score.Total);
        Assert.Throws<InvalidOperationException>(() => score.Register("clarify", new(MatchRoutingStatus.Clarify)));
    }
    [Theory]
    [InlineData("--limit", "oops")] [InlineData("--limit", "61")] [InlineData("--limit", "0")]
    [InlineData("--unknown", "x")] [InlineData("--fixture-sha256", "bad")]
    public void InvalidFlagsFailBeforeAnyInference(string flag, string value)
        => Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse([flag, value], Root()));
    [Fact]
    public void CustomFixtureRequiresExactHashUnderAzureAndOutputCannotReplaceItOrExistingEvidence()
    {
        var root = Root(); var azure = Path.Combine(root, ".azure"); Directory.CreateDirectory(azure);
        var path = Path.Combine(azure, "custom.json"); var output = Path.Combine(azure, "receipt.json");
        var bytes = File.ReadAllBytes(Path.Combine(HostingTestPaths.Root(), "tests", "Matchmate.Tests", "Fixtures", "league-chat-tuning-v2.json")); File.WriteAllBytes(path, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var options = EvaluationOptions.Parse(["--fixtures", path, "--fixture-sha256", hash, "--output", output], root);
        Assert.False(options.Live); Assert.Equal(20, options.ReadFixture().Cases.Length);
        Assert.Throws<InvalidDataException>(() => (options with { ExpectedHash = new string('0', 64) }).ReadFixture());
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--fixtures", path], root));
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--fixtures", path, "--fixture-sha256", hash, "--output", path], root));
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--fixtures", path, "--fixture-sha256", hash, "--output", Path.Combine(root, "outside.json")], root));
        File.WriteAllText(output, "existing receipt"); Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--live", "--output", output], root));
        Assert.Throws<ArgumentException>(() => EvaluationOptions.Parse(["--live", "--fixtures", "tests/Matchmate.Tests/Fixtures/league-chat-heldout-v1.json"], root));
    }
    [Fact]
    public void TuningV2RetainsOnlyOriginalTuningQuestionsAndUpdatesNamedWorkflowGolds()
    {
        var root = HostingTestPaths.Root(); var options = EvaluationOptions.Parse(["--fixtures", "tests/Matchmate.Tests/Fixtures/league-chat-tuning-v2.json"], root); var current = options.ReadFixture(); Assert.Equal(20, current.Cases.Length);
        var oldBytes = File.ReadAllBytes(Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures", "league-chat-tuning-v1.json"));
        var old = JsonSerializer.Deserialize<EvaluationFixture>(oldBytes, EvaluationOptions.Json)!;
        Assert.Equal(old.Cases.Select(c => c.Question), current.Cases.Select(c => c.Question));
        Assert.All(current.Cases, c => Assert.Contains(c.Expected, LeagueWorkflowRegistry.Criteria.Keys.AsEnumerable()));
        Assert.Equal(new[] { "league_table", "club_standing", "club_form", "date_changes", "ground_context", "coverage_limits", "other_season", "live_unavailable", "clarify", "unsupported", "named_club_standing", "named_club_form", "named_ground_context" }.Order(), current.Cases.Select(c => c.Expected).Distinct().Order());
        Assert.Equal("named_club_standing", current.Cases.Single(c => c.Id == "T-003").Expected);
    }
    private static string Root() => Path.Combine(HostingTestPaths.Root(), ".azure", "league-chat-r1-tests", Guid.NewGuid().ToString("N"));
    private static string DecisionBody(double probability) => JsonSerializer.Serialize(new { answers = new { workflow = new { type = "choice", choice = "club_context", confidence = .9,
        probabilities = LeagueWorkflowRegistry.TopicCriteria.Keys.ToDictionary(k => k, k => k == "club_context" ? probability : (1 - probability) / (LeagueWorkflowRegistry.TopicCriteria.Count - 1)) },
        club_binding = new { type = "choice", choice = "context", probabilities = new { context = .99, explicit_name = .01 } },
        time_intent = new { type = "choice", choice = "selected_view", probabilities = new { selected_view = .99, explicit_time_reference = .01 } } } });
    [Fact]
    public void TuningV3PinsTwentyOriginalRecordsAndFourApprovedContextScenarios()
    {
        var root = HostingTestPaths.Root(); var options = EvaluationOptions.Parse(["--fixtures", "tests/Matchmate.Tests/Fixtures/league-chat-tuning-v3.json"], root); var fixture = options.ReadFixture(); Assert.Equal(24, fixture.Cases.Length);
        using var old = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures", "league-chat-tuning-v2.json")));
        using var current = JsonDocument.Parse(File.ReadAllBytes(options.FixturePath));
        Assert.Equal(old.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.GetRawText()), current.RootElement.GetProperty("cases").EnumerateArray().Take(20).Select(c => c.GetRawText()));
        Assert.Equal(new LeagueAcceptedTopic("ground_context", "brentford"), fixture.Cases[20].AcceptedTopic); Assert.Equal(15, fixture.Cases[20].Context.DateIndex);
        Assert.Equal(new LeagueAcceptedTopic("club_form", "arsenal"), fixture.Cases[21].AcceptedTopic); Assert.Null(fixture.Cases[22].AcceptedTopic);
        Assert.Equal("named_club_standing", fixture.Cases[23].Expected);
    }
    private sealed class DecisionHandler(byte[] bytes, bool knownLength) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new UnseekableStream(bytes)) };
            response.Headers.TryAddWithoutValidation("x-private-sentinel", "private header sentinel"); return Task.FromResult(response);
        }
    }
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class StatusHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(11)); return Task.FromResult(response); }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Synthetic evaluator tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

