using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace Matchmate.Web.Chat;

public sealed class MatchChatDiagnosticsOptions
{
    public const string Section = "MatchChatDiagnostics";
    public bool Enabled { get; set; }
}

// Closed enums keep all telemetry values independent of conversation/provider/authentication payloads.
public enum MatchChatTimingStage
{
    ComponentTotal, BeforeSession, UiCommit, SessionTotal, Authentication, CommitAuthentication,
    RouterTotal, RouterCredential, RouterHttp, Executor, Retrieval, NarratorTotal, NarratorCredential,
    NarratorHttp, Validation
}
public enum MatchChatTimingOutcome { Success, Failure, Cancelled, Stale }
public enum MatchReplyRejection { ReplyBudget, MalformedJson, InvalidStructure, AnswerContent, UnsupportedMeasurement, UnknownEvidence, UnsupportedNumber, UnexpectedToolCall }

public sealed class MatchChatDiagnostics(IOptions<MatchChatDiagnosticsOptions> options, IHostEnvironment environment,
    ILogger<MatchChatDiagnostics> logger)
{
    public const string ActivitySourceName = "Matchmate.Chat.Timings";
    public const int MaxFileBytes = 262144;
    private static readonly ActivitySource Activities = new(ActivitySourceName);
    private readonly AsyncLocal<TraceContext?> _current = new();
    private readonly object _fileGate = new();
    private readonly string? _path = options.Value.Enabled && environment.IsDevelopment() ? WorkspaceFile(environment.ContentRootPath) : null;
    private sealed record TraceContext(string Id);

    public RequestTrace BeginRequest()
    {
        var previous = _current.Value;
        if (previous is not null) return new(null);
        _current.Value = new(Guid.NewGuid().ToString("N"));
        return new(() => _current.Value = previous);
    }

    public Timing Measure(MatchChatTimingStage stage) => new(this, _current.Value?.Id, stage);

    public async Task<T> TimeAsync<T>(MatchChatTimingStage stage, Func<Task<T>> work)
    {
        using var timing = Measure(stage);
        try { var result = await work(); timing.Complete(MatchChatTimingOutcome.Success); return result; }
        catch (OperationCanceledException) { timing.Complete(MatchChatTimingOutcome.Cancelled); throw; }
    }

    public TokenCredential ObserveCredential(TokenCredential credential) => new TimedCredential(credential, this);

    private void Write(string? traceId, MatchChatTimingStage stage, double durationMs, string outcome)
    {
        if (traceId is null) return;
        // Observability must not change a chat result, including when a logging provider or local disk fails.
        try { logger.LogInformation("Chat timing {ChatTraceId} {Stage} {DurationMs} {Outcome}", traceId, stage, durationMs, outcome); } catch { }
        if (_path is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new { traceId, stage = stage.ToString(), durationMs, outcome }) + "\n";
            lock (_fileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > MaxFileBytes) File.WriteAllText(_path, "");
                File.AppendAllText(_path, line);
            }
        }
        catch { } // Never expose exception messages or retry writes on the request path.
    }

    private static string? WorkspaceFile(string contentRoot)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(contentRoot)); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Matchmate.slnx"))) return Path.Combine(directory.FullName, ".local", "chat-timings.jsonl");
        }
        catch { }
        return null; // No arbitrary configured destination or hosted filesystem fallback.
    }

    public sealed class RequestTrace : IDisposable
    {
        private Action? _restore;
        internal RequestTrace(Action? restore) => _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    public sealed class Timing : IDisposable
    {
        private readonly MatchChatDiagnostics _diagnostics;
        private readonly string? _traceId;
        private readonly MatchChatTimingStage _stage;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private string _outcome = nameof(MatchChatTimingOutcome.Failure);
        private bool _disposed;
        internal Timing(MatchChatDiagnostics diagnostics, string? traceId, MatchChatTimingStage stage)
        {
            _diagnostics = diagnostics; _traceId = traceId; _stage = stage;
            _activity = Activities.StartActivity(stage.ToString(), ActivityKind.Internal);
            _activity?.SetTag("chat.trace_id", traceId);
        }
        public void Complete(MatchChatTimingOutcome outcome) => _outcome = outcome.ToString();
        public void Complete(MatchChatOutcome outcome) => _outcome = outcome.ToString();
        public void Complete(MatchReplyRejection reason) => _outcome = "Rejected" + reason;
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            var milliseconds = Math.Round(Stopwatch.GetElapsedTime(_start).TotalMilliseconds, 3);
            _activity?.SetTag("chat.outcome", _outcome); _activity?.Dispose();
            _diagnostics.Write(_traceId, _stage, milliseconds, _outcome);
        }
    }

    private sealed class TimedCredential(TokenCredential inner, MatchChatDiagnostics diagnostics) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            using var timing = diagnostics.Measure(MatchChatTimingStage.NarratorCredential);
            try { var result = inner.GetToken(requestContext, cancellationToken); timing.Complete(MatchChatTimingOutcome.Success); return result; }
            catch (OperationCanceledException) { timing.Complete(MatchChatTimingOutcome.Cancelled); throw; }
        }
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => await diagnostics.TimeAsync(MatchChatTimingStage.NarratorCredential, async () => await inner.GetTokenAsync(requestContext, cancellationToken));
    }
}

// Registered only on the existing provider clients. Includes transport-to-response-headers time;
// provider totals also include body reading/parsing and credential work.
public sealed class MatchChatTimingHandler(MatchChatDiagnostics diagnostics, MatchChatTimingStage stage) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timing = diagnostics.Measure(stage);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            timing.Complete(response.IsSuccessStatusCode ? MatchChatTimingOutcome.Success : MatchChatTimingOutcome.Failure);
            return response;
        }
        catch (OperationCanceledException) { timing.Complete(MatchChatTimingOutcome.Cancelled); throw; }
    }
}
