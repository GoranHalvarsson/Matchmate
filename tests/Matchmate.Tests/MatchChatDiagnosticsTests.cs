using System.Net;
using System.Text.Json;
using Azure.Core;
using Matchmate.Web.Chat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class MatchChatDiagnosticsTests
{
    [Theory]
    [InlineData(MatchReplyRejection.MalformedJson)] [InlineData(MatchReplyRejection.InvalidStructure)]
    [InlineData(MatchReplyRejection.AnswerContent)] [InlineData(MatchReplyRejection.UnknownEvidence)]
    [InlineData(MatchReplyRejection.UnsupportedNumber)] [InlineData(MatchReplyRejection.UnsupportedMeasurement)]
    [InlineData(MatchReplyRejection.ReplyBudget)] [InlineData(MatchReplyRejection.UnexpectedToolCall)]
    public void RejectionCategoryUsesSameFourSafeFieldsWithoutPayload(MatchReplyRejection reason)
    {
        using var workspace = new Workspace(); var diagnostics = Create(new(), workspace.Root, enabled: true);
        using (diagnostics.BeginRequest()) using (var timing = diagnostics.Measure(MatchChatTimingStage.Validation)) timing.Complete(reason);
        var text = File.ReadAllText(workspace.Output); using var document = JsonDocument.Parse(text); var data = document.RootElement;
        Assert.Equal(new[] { "traceId", "stage", "durationMs", "outcome" }, data.EnumerateObject().Select(p => p.Name));
        Assert.True(Guid.TryParseExact(data.GetProperty("traceId").GetString(), "N", out _));
        Assert.Equal("Validation", data.GetProperty("stage").GetString()); Assert.Equal("Rejected" + reason, data.GetProperty("outcome").GetString());
        Assert.DoesNotContain("answer", text); Assert.DoesNotContain("question", text); Assert.DoesNotContain("evidence", text);
    }

    [Fact]
    public async Task ConcurrentRequestsStaySeparateAndNestedStagesShareCorrelation()
    {
        var log = new TimingLog(); var diagnostics = Create(log);
        async Task Request()
        {
            using var trace = diagnostics.BeginRequest();
            using var nested = diagnostics.BeginRequest();
            await diagnostics.TimeAsync(MatchChatTimingStage.Authentication, async () => { await Task.Yield(); return 1; });
            using var total = diagnostics.Measure(MatchChatTimingStage.SessionTotal); total.Complete(MatchChatOutcome.Answered);
        }
        await Task.WhenAll(Task.Run(Request), Task.Run(Request));
        var groups = log.Records.GroupBy(r => r.TraceId).ToArray(); Assert.Equal(2, groups.Length);
        Assert.All(groups, group => { Assert.True(Guid.TryParseExact(group.Key, "N", out _)); Assert.Equal(2, group.Count()); });
        Assert.All(log.Records, record => Assert.True(record.DurationMs >= 0));
        // Finishing a request restores the context; unrelated work has no trace or log entry.
        using (var outside = diagnostics.Measure(MatchChatTimingStage.Validation)) outside.Complete(MatchChatTimingOutcome.Success);
        Assert.Equal(4, log.Records.Count);
    }

    [Theory]
    [InlineData("Development", true, true)] [InlineData("Development", false, false)]
    [InlineData("Production", true, false)] [InlineData("Testing", true, false)]
    public void FileSinkRequiresDevelopmentAndExplicitOptIn(string environment, bool enabled, bool expected)
    {
        using var workspace = new Workspace(); var diagnostics = Create(new(), workspace.Root, environment, enabled);
        using (diagnostics.BeginRequest()) using (var timing = diagnostics.Measure(MatchChatTimingStage.SessionTotal)) timing.Complete(MatchChatOutcome.Disconnected);
        Assert.Equal(expected, File.Exists(workspace.Output));
        if (!expected) return;
        using var document = JsonDocument.Parse(File.ReadAllText(workspace.Output)); var data = document.RootElement;
        Assert.Equal(new[] { "traceId", "stage", "durationMs", "outcome" }, data.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Disconnected", data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task FileIsBoundedAndExceptionsNeverBecomePayloads()
    {
        using var workspace = new Workspace(); Directory.CreateDirectory(Path.GetDirectoryName(workspace.Output)!);
        File.WriteAllText(workspace.Output, new string('x', MatchChatDiagnostics.MaxFileBytes));
        var diagnostics = Create(new(), workspace.Root, enabled: true);
        using (diagnostics.BeginRequest())
            await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.TimeAsync<int>(MatchChatTimingStage.RouterTotal,
                () => throw new InvalidOperationException("SECRET_QUESTION_TOKEN_EMAIL_AND_EXCEPTION")));
        Assert.InRange(new FileInfo(workspace.Output).Length, 1, MatchChatDiagnostics.MaxFileBytes);
        var text = File.ReadAllText(workspace.Output); Assert.DoesNotContain("SECRET", text);
        using var document = JsonDocument.Parse(text); Assert.Equal("Failure", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task BrokenDiskAndThrowingLoggerCannotChangeResultOrCancellation()
    {
        using var workspace = new Workspace(); File.WriteAllText(Path.Combine(workspace.Root, ".local"), "blocks directory creation");
        var diagnostics = Create(new() { Throw = true }, workspace.Root, enabled: true);
        using var trace = diagnostics.BeginRequest();
        Assert.Equal(42, await diagnostics.TimeAsync(MatchChatTimingStage.Retrieval, () => Task.FromResult(42)));
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.TimeAsync(MatchChatTimingStage.RouterTotal, () => Task.FromCanceled<int>(token.Token)));
    }

    [Fact]
    public async Task ActualNarratorCredentialAndTransportAreCorrelatedWithoutLoggingBodiesOrTokens()
    {
        var log = new TimingLog(); var diagnostics = Create(log); var credential = new Credential();
        using var transport = new MatchChatTimingHandler(diagnostics, MatchChatTimingStage.NarratorHttp) { InnerHandler = new ReplyHandler() };
        using var http = new HttpClient(transport);
        var narrator = new AzureMatchChatModel(http, Options.Create(new MatchChatOptions
        { Enabled = true, Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit" }), new TimingEnvironment(), credential, diagnostics);
        using (diagnostics.BeginRequest())
        {
            var result = await narrator.CompleteAsync(new([new("user", "SECRET_QUESTION")], ["coverage"]), default);
            Assert.Equal("SECRET_ANSWER", result.Content);
        }
        Assert.Equal(1, credential.Calls);
        Assert.Contains(log.Records, r => r.Stage == MatchChatTimingStage.NarratorCredential.ToString() && r.Outcome == "Success");
        Assert.Contains(log.Records, r => r.Stage == MatchChatTimingStage.NarratorHttp.ToString() && r.Outcome == "Success");
        Assert.Single(log.Records.Select(r => r.TraceId).Distinct()); Assert.DoesNotContain("SECRET", string.Join("\n", log.Messages));
    }

    internal static MatchChatDiagnostics Create(TimingLog log, string? root = null, string environment = "Development", bool enabled = false)
        => new(Options.Create(new MatchChatDiagnosticsOptions { Enabled = enabled }), new TimingEnvironment { ContentRootPath = root ?? ".", EnvironmentName = environment }, log);

    private sealed class Credential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) { Calls++; return new("SECRET_TOKEN", DateTimeOffset.UtcNow.AddHours(1)); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class ReplyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"id":"unit","object":"chat.completion","created":0,"model":"unit","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"SECRET_ANSWER"}}]}""") });
    }
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "matchmate-timings-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, ".local", "chat-timings.jsonl");
        public Workspace() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "Matchmate.slnx"), ""); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}

internal sealed class TimingEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Unit";
    public string ContentRootPath { get; set; } = "."; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
internal sealed class TimingLog : ILogger<MatchChatDiagnostics>
{
    internal sealed record Record(string TraceId, string Stage, double DurationMs, string Outcome);
    public System.Collections.Concurrent.ConcurrentBag<Record> Records { get; } = [];
    public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
    public Action<Record>? OnRecord { get; set; }
    public bool Throw { get; set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (Throw) throw new IOException("SECRET_LOGGING_EXCEPTION");
        var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value);
        var record = new Record((string)fields["ChatTraceId"]!, fields["Stage"]!.ToString()!, (double)fields["DurationMs"]!, (string)fields["Outcome"]!);
        Records.Add(record); OnRecord?.Invoke(record);
        Messages.Add(formatter(state, exception)); Assert.Null(exception);
    }
}
