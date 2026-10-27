using System.Diagnostics;
using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Matchmate.Web.LeagueChat;

namespace Matchmate.LeagueChatEvaluation;

// CLI-only, sequential pacing before RouteAsync starts its own15second timeout.
public sealed class EvaluationPacer(Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Queue<DateTimeOffset> _requests = new();
    private DateTimeOffset? _last;
    public async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); var now = _clock();
            while (_requests.TryPeek(out var first) && now - first >= TimeSpan.FromMinutes(1)) _requests.Dequeue();
            var wait = _last is { } last ? last.AddSeconds(3.2) - now : TimeSpan.Zero;
            if (_requests.Count >= 19) wait = Max(wait, _requests.Peek().AddMinutes(1) - now);
            if (wait <= TimeSpan.Zero) { _last = now; _requests.Enqueue(now); return; }
            await _delay(wait, token);
        }
    }
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
public sealed record EvaluationHttpReceipt(int? Status, double? RetryAfterSeconds, long Milliseconds);
public sealed record EvaluationDecisionDiagnostics(LeagueDecisionAnswer Workflow, LeagueDecisionAnswer ClubBinding, LeagueDecisionAnswer TimeIntent);
public static class EvaluationDecisionParser
{
    public static EvaluationDecisionDiagnostics? Parse(ReadOnlyMemory<byte> bytes)
    {
        var parsed = DecisionLeagueWorkflowRouter.ParseChoices(bytes);
        return parsed is null ? null : new(parsed.Workflow, parsed.ClubBinding, parsed.TimeIntent);
    }
}
// No request, raw body, headers, arbitrary response strings, tokens or credentials enter receipts.
public sealed class EvaluationCaptureTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private long _generation;
    public EvaluationHttpReceipt? Last { get; private set; }
    public EvaluationDecisionDiagnostics? Decision { get; private set; }
    public void BeginCase() { _generation++; Last = null; Decision = null; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var watch = Stopwatch.StartNew(); var generation = ++_generation; Last = null; Decision = null;
        try
        {
            var response = await base.SendAsync(request, token);
            var retry = response.Headers.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
            if (generation == _generation) Last = new((int)response.StatusCode, seconds is { } value ? Math.Clamp(value, 0, 86400) : null, watch.ElapsedMilliseconds);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is not > DecisionLeagueWorkflowRouter.ResponseBytes)
                response.Content = new DiagnosticContent(response.Content, diagnostics =>
                {
                    if (generation != _generation) return;
                    Decision = diagnostics;
                    if (Last is { } receipt) Last = receipt with { Milliseconds = watch.ElapsedMilliseconds };
                });
            return response;
        }
        finally { if (generation == _generation) Last ??= new(null, null, watch.ElapsedMilliseconds); }
    }
    // Tee only bytes the caller reads. The router receives the original byte sequence even when
    // the response is malformed, truncated, or too large; diagnostics never consume ahead of it.
    private sealed class DiagnosticContent : HttpContent
    {
        private readonly HttpContent _original;
        private readonly Action<EvaluationDecisionDiagnostics?> _complete;
        public DiagnosticContent(HttpContent original, Action<EvaluationDecisionDiagnostics?> complete)
        {
            _original = original; _complete = complete;
            foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = _original.Headers.ContentLength ?? 0; return _original.Headers.ContentLength.HasValue; }
        private async Task<Stream> Open(CancellationToken token) => new DiagnosticStream(await _original.ReadAsStreamAsync(token), _complete);
        protected override Task<Stream> CreateContentReadStreamAsync() => Open(default);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Open(token);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        { await using var source = await Open(token); await source.CopyToAsync(stream, token); }
        protected override void Dispose(bool disposing) { if (disposing) _original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class DiagnosticStream(Stream original, Action<EvaluationDecisionDiagnostics?> complete) : Stream
    {
        private byte[]? _buffer = new byte[DecisionLeagueWorkflowRouter.ResponseBytes];
        private int _count;
        private bool _completed;
        private void Capture(ReadOnlySpan<byte> bytes)
        {
            if (_completed) return;
            if (bytes.Length == 0)
            {
                _completed = true; complete(_buffer is null ? null : EvaluationDecisionParser.Parse(_buffer.AsMemory(0, _count)));
                _buffer = null; return;
            }
            if (_buffer is null) return;
            if (_count + bytes.Length > _buffer.Length) { _buffer = null; return; }
            bytes.CopyTo(_buffer.AsSpan(_count)); _count += bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) { var read = original.Read(buffer, offset, count); if (count != 0) Capture(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await original.ReadAsync(buffer, token); if (buffer.Length != 0) Capture(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => original.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _buffer = null; if (disposing) original.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { _buffer = null; await original.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}

