using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Real inference evaluation only. No web host, mock provider, Azure writes, app configuration or secrets.
var modes = new[] { "--tuning", "--heldout", "--narrator" }.Where(args.Contains).ToArray();
if (modes.Length != 1) { Console.Error.WriteLine("Choose --tuning, --heldout or --narrator. Without --live, validates fixtures/context only."); return 2; }
var mode = modes[0][2..];
string? Value(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
var root = LocalPaths.FindRoot();
var preparationFlags = new[] { "--fixture", "--fixture-sha256", "--minimum-probability", "--minimum-margin" };
if (preparationFlags.Any(flag => args.Contains(flag) && (args.Count(a => a == flag) != 1 || Value(flag) is not { } value || value.StartsWith("--", StringComparison.Ordinal))))
{ Console.Error.WriteLine("Each fixture/threshold flag requires one explicit value. No inference started."); return 2; }
var customFixture = args.Contains("--fixture");
if (customFixture != args.Contains("--fixture-sha256") || mode == "narrator" && customFixture)
{ Console.Error.WriteLine("Custom fixtures require --fixture and --fixture-sha256 together, in tuning/heldout modes only."); return 2; }
var thresholdOverride = args.Contains("--minimum-probability") || args.Contains("--minimum-margin");
if (thresholdOverride && mode != "tuning") { Console.Error.WriteLine("Threshold overrides are calibration-only (--tuning); heldout uses production defaults."); return 2; }
var routerOptions = new DecisionRoutingOptions { Enabled = true, Endpoint = Value("--decision-endpoint") ?? "", Deployment = Value("--decision-deployment") ?? "" };
if (Value("--minimum-probability") is { } probability)
{
    if (!double.TryParse(probability, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed is <= 0.5 or > 1)
    { Console.Error.WriteLine("Invalid calibration probability."); return 2; }
    routerOptions.MinimumProbability = parsed;
}
if (Value("--minimum-margin") is { } margin)
{
    if (!double.TryParse(margin, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed is <= 0 or > 1)
    { Console.Error.WriteLine("Invalid calibration margin."); return 2; }
    routerOptions.MinimumMargin = parsed;
}
var fixtureName = mode == "heldout" ? "decision-routing-heldout-v1.json" : "decision-routing-tuning-v1.json";
var fixturePath = Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures", fixtureName);
var expectedHash = mode == "heldout" ? "19b81dbd3cc8e849fcaccfc562ad965c31b6a007ae08bdc69bbbd4f96c612283" : "84cac37582122a9b4bfd152439ec863a8ac77c8f51525c042694a6e5818092ad";
if (customFixture)
{
    fixturePath = Path.GetFullPath(Value("--fixture")!);
    if (!fixturePath.StartsWith(Path.Combine(root, ".azure") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !fixturePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    { Console.Error.WriteLine("Custom fixture must be a JSON file under workspace .azure/."); return 2; }
    expectedHash = Value("--fixture-sha256")!.ToLowerInvariant(); fixtureName = Path.GetFileName(fixturePath);
    if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) { Console.Error.WriteLine("Fixture SHA256 must contain 64 hexadecimal characters."); return 2; }
}
byte[] fixtureBytes;
try
{
    if (new FileInfo(fixturePath).Length > 262144) { Console.Error.WriteLine("Fixture exceeds the bounded file size."); return 2; }
    fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Fixture could not be read."); return 2; }
var fixtureHash = Convert.ToHexStringLower(SHA256.HashData(fixtureBytes));
if (fixtureHash != expectedHash) { Console.Error.WriteLine("Frozen fixture hash changed; stop and review labels. No inference started."); return 2; }
EvaluationCase[] cases;
try
{
    using var fixture = JsonDocument.Parse(fixtureBytes, new() { MaxDepth = 12 });
    if (customFixture && fixture.RootElement.GetProperty("cases").EnumerateArray().Any(c => !c.TryGetProperty("category", out _)
        || !c.TryGetProperty("dataset", out _) || !c.TryGetProperty("selected", out var selected) || selected.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
    { Console.Error.WriteLine("Custom cases require explicit category, dataset and selected context."); return 2; }
    cases = mode == "narrator" ? [new("N01", "Summarize this recording at the selected replay cursor.", "current_match_summary", "narrator", "australian", false),
        new("N02", "Summarize this recording at the selected replay cursor.", "current_match_summary", "narrator", "brentford", false)]
        : JsonSerializer.Deserialize<EvaluationCase[]>(fixture.RootElement.GetProperty("cases"), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })!;
}
catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
{ Console.Error.WriteLine("Malformed fixture schema. No credential lookup or inference started."); return 2; }
if (cases is null || cases.Length is < 1 or > 100 || cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 80
    || string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > MatchChatLimits.InputChars || c.Original?.Length > MatchChatLimits.InputChars
    || c.Expected is null || !DecisionWorkflowRouter.Criteria.ContainsKey(c.Expected) || string.IsNullOrWhiteSpace(c.Category) || c.Category.Length > 80
    || c.Dataset is not "australian" and not "brentford" || (c.GoalSelected || c.NamedHistory || c.NamedReference) && c.Dataset != "brentford"
    || c.NamedReference && (!c.NamedHistory || c.Original is not null || !MatchPlayerResolver.ReferenceQuestion(c.Question))) || cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length)
{ Console.Error.WriteLine("Invalid case identity/question/workflow/category/recording context. No inference started."); return 2; }
var availableCases = cases.Length;
if (Value("--limit") is { } limitText)
{
    if (!int.TryParse(limitText, out var limit) || limit < 1 || limit > cases.Length) { Console.Error.WriteLine("Invalid --limit."); return 2; }
    cases = cases.Take(limit).ToArray();
}
if (cases.Length > 100 || mode == "narrator" && cases.Length > 10 || cases.Any(c => !DecisionWorkflowRouter.Criteria.ContainsKey(c.Expected))) return 2;

// The same unchanged bounded adapters used by offline recording tests; never a replacement browser host.
var ledger = new FootballLedger();
IFootballSource[] sources = [new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")),
    new BrentfordFootballSource(Path.Combine(root, ".local", "shared-inputs", "brentford-slice.json"))];
foreach (var source in sources) foreach (var batch in await source.ReadAsync(default)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
MatchChatContext Context(EvaluationCase item)
{
    var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId == (item.Dataset == "australian" ? "australian-recorded" : "brentford-existing-illustration"));
    var subject = item.Selected ? item.Dataset == "australian" ? "person-skillcorner-50999" : "person-pl-reference-brentford-kayode" : null;
    return new(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
        item.Dataset == "australian" ? 60000 : 3780000, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision,
        dataset.HeadSequence, SubjectId: subject), dataset.HeadSequence, item.GoalSelected ? new("goal-1", 3780000, "61") : null);
}
var routingInputs = new Dictionary<string, MatchRoutingInput>(StringComparer.Ordinal);
foreach (var item in cases)
{
    var context = Context(item); var view = ledger.Query(context.Query);
    if (item.GoalSelected && (view.Dataset.Schedule!.Releases.Count(r => r.RecordId == "goal-1" && r.CursorMs == 3780000) != 1
        || !view.Events.Any(r => r.RecordId == "goal-1" && r.FootballTime?.ReportedClock == "61")))
    { Console.Error.WriteLine("Selected-goal fixture context unavailable. No inference started."); return 2; }
    var namedSubject = item.NamedHistory ? MatchPlayerResolver.Resolve(view.Dataset.People, new("Danny Welbeck")).SubjectId : null;
    if (item.NamedHistory && namedSubject is null)
    { Console.Error.WriteLine("Named-history roster context unavailable. No inference started."); return 2; }
    routingInputs.Add(item.Id, new(item.Question, context,
        item.NamedHistory ? [new("How did Danny Welbeck play in this match?", namedSubject)] : [],
        item.Original is null ? null : new(item.Original), item.NamedReference));
}
if (!args.Contains("--live")) { Console.WriteLine($"Validated {cases.Length}/{availableCases} {mode} contexts; fixture SHA256 {fixtureHash}; probability={routerOptions.MinimumProbability.ToString(CultureInfo.InvariantCulture)}, margin={routerOptions.MinimumMargin.ToString(CultureInfo.InvariantCulture)}. Zero credential lookups or inference requests."); return 0; }

var modelOptions = new MatchChatOptions { Enabled = true, Endpoint = Value("--narrator-endpoint") ?? "", Deployment = Value("--narrator-deployment") ?? "" };
var output = Path.GetFullPath(Value("--out") ?? Path.Combine(root, ".azure", $"chat-evaluation-{mode}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"));
if (!output.StartsWith(Path.Combine(root, ".azure") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("Output must be a JSON file under workspace .azure/."); return 2; }
if (string.Equals(output, fixturePath, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Output cannot overwrite its frozen fixture."); return 2; }
using var decisionTransport = new CaptureTransport(false, 100); using var narratorTransport = new CaptureTransport(true, 10);
using var decisionHttp = new HttpClient(decisionTransport); using var narratorHttp = new HttpClient(narratorTransport);
var credential = new AzureCliCredential(); var environment = new EvaluationEnvironment();
var router = new DecisionWorkflowRouter(decisionHttp, Options.Create(routerOptions), environment, credential);
var narrator = new AzureMatchChatModel(narratorHttp, Options.Create(modelOptions), environment, credential);
if (!router.Configured || mode == "narrator" && !narrator.Configured) { Console.Error.WriteLine("Supply valid Azure root endpoints/deployment names. No inference started."); return 2; }
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var rows = new List<object>(); var categoryScores = new Dictionary<string, List<bool>>(); var started = DateTimeOffset.UtcNow; var failed = false;
void Save() => File.WriteAllText(output, JsonSerializer.Serialize(new { mode, fixtureName, fixtureHash, availableCases, executedCases = rows.Count,
    partial = rows.Count < availableCases, started, savedAt = DateTimeOffset.UtcNow, routerOptions.MinimumProbability, routerOptions.MinimumMargin,
    thresholds = thresholdOverride ? "Explicit separate-calibration overrides; never used for heldout." : "Production defaults; heldout is not used for tuning.", decisionCalls = decisionTransport.Count,
    narratorCalls = narratorTransport.Count, categories = categoryScores.ToDictionary(p => p.Key, p => new { correct = p.Value.Count(v => v), total = p.Value.Count }), rows }, new JsonSerializerOptions { WriteIndented = true }));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
foreach (var item in cases)
{
    var context = Context(item); var clock = Stopwatch.StartNew(); MatchRoutingResult? route = null; object? execution = null; object? narration = null;
    double? retrievalMs = null, narratorMs = null; var requestStart = decisionTransport.Count; var narratorRequestStart = narratorTransport.Count;
    try
    {
        route = await router.RouteAsync(routingInputs[item.Id], cancellation.Token);
        var routeMs = clock.Elapsed.TotalMilliseconds;
        failed = route.Status is MatchRoutingStatus.Unavailable or MatchRoutingStatus.InvalidResponse;
        if (mode == "narrator" && route.Status == MatchRoutingStatus.Selected)
        {
            var snapshot = new MatchWorkflowSnapshot("local-evaluation", 1, context, route.WorkflowId == "other_live_matches_stats" ? MatchWorkflowTemporalMode.LiveNow : MatchWorkflowTemporalMode.ReplayAtCursor);
            var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query))); var retrieval = Stopwatch.StartNew();
            var result = await executor.ExecuteAsync(new(route.WorkflowId ?? "", MatchWorkflowRegistry.Version, snapshot), captured => captured == snapshot, cancellation.Token);
            retrievalMs = retrieval.Elapsed.TotalMilliseconds; execution = result.Status.ToString();
            if (result.Bundle is { } bundle && result.Status is MatchWorkflowStatus.Success or MatchWorkflowStatus.Partial)
            {
                var messages = new MatchModelMessage[] { new("system", CommentatorPrompt.Version + "\n" + CommentatorPrompt.Persona + "\n" + CommentatorPrompt.Grounding),
                    new("system", "Prepared app-computed evidence:\n" + JsonSerializer.Serialize(bundle)), new("user", item.Question) };
                var narrating = Stopwatch.StartNew(); var reply = await narrator.CompleteAsync(new(messages, bundle.Items.Select(item => item.Id)), cancellation.Token); narratorMs = narrating.Elapsed.TotalMilliseconds;
                if (reply.Calls.Length != 0) throw new InvalidDataException("Unexpected tool call.");
                var evidence = bundle.Items.ToDictionary(e => e.Id, e => new MatchChatEvidence(e.Id, e.Tool, e.Text, e.NumericTokens.ToArray()));
                var validated = MatchReplyValidation.Validate(reply.Content, evidence); narration = new { status = "grounding_checks_passed_not_truth_proof", validated.Answer, evidenceIds = validated.Evidence.Select(e => e.Id) };
            }
        }
        var chosen = route.Status == MatchRoutingStatus.Clarify ? "clarify" : route.WorkflowId;
        if (!categoryScores.TryGetValue(item.Category, out var scores)) categoryScores[item.Category] = scores = [];
        scores.Add(route.Status is MatchRoutingStatus.Selected or MatchRoutingStatus.Clarify && chosen == item.Expected);
        if (mode == "narrator" && narration is null) failed = true;
        rows.Add(new { item.Id, item.Category, item.Expected, chosen, status = route.Status.ToString(), context, routeMs, retrievalMs, narratorMs, execution, narration,
            response = decisionTransport.Count > requestStart ? decisionTransport.Last : null, narratorResponse = narratorTransport.Count > narratorRequestStart ? narratorTransport.Last : null });
        Console.WriteLine($"{item.Id}: {route.Status} {chosen ?? "no route"}");
        if (failed) { Save(); break; }
    }
    catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or InvalidOperationException or InvalidDataException or Azure.Identity.AuthenticationFailedException or System.ClientModel.ClientResultException or Azure.RequestFailedException)
    {
        rows.Add(new { item.Id, status = "failed", failureType = exception.GetType().Name, route, execution, retrievalMs, narratorMs,
            response = decisionTransport.Count > requestStart ? decisionTransport.Last : null, narratorResponse = narratorTransport.Count > narratorRequestStart ? narratorTransport.Last : null });
        failed = true; Save(); Console.Error.WriteLine($"{item.Id}: failed ({exception.GetType().Name}); no automatic retry."); break;
    }
    Save();
}
Save(); Console.WriteLine($"Saved {rows.Count}/{availableCases} results to {output}. Decision={decisionTransport.Count}; narrator={narratorTransport.Count}.");
return !failed && rows.Count == cases.Length ? 0 : 1;

sealed record EvaluationCase(string Id, string Question, string Expected, string Category = "tuning", string Dataset = "brentford", bool Selected = true, string? Original = null, bool GoalSelected = false, bool NamedHistory = false, bool NamedReference = false);
sealed class EvaluationEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development"; public string ApplicationName { get; set; } = "Matchmate.ChatEvaluation";
    public string ContentRootPath { get; set; } = "."; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
sealed class CaptureTransport(bool narrator, int maxCalls) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    public int Count { get; private set; } public object? Last { get; private set; }
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;
    private readonly Queue<(DateTimeOffset At, int Reserved)> _window = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Count >= maxCalls) throw new InvalidOperationException("Inference call budget exhausted.");
        var bodyBytes = await request.Content!.ReadAsByteArrayAsync(token); var reserved = narrator ? bodyBytes.Length + 1212 : 0;
        if (reserved > 30000) throw new InvalidDataException("Narrator request exceeds conservative thirty-thousand-TPM reservation.");
        while (true)
        {
            var now = DateTimeOffset.UtcNow; while (_window.TryPeek(out var entry) && now - entry.At >= TimeSpan.FromMinutes(1)) _window.Dequeue();
            var interval = TimeSpan.FromSeconds(narrator ? 10 : 2.2) - (now - _lastStart);
            if (_window.Count < 30 && _window.Sum(e => e.Reserved) + reserved <= 30000 && interval <= TimeSpan.Zero) break;
            var delay = _window.Count >= 30 || _window.Sum(e => e.Reserved) + reserved > 30000
                ? _window.Peek().At.AddMinutes(1) - now : interval;
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(50), token);
        }
        Count++; Last = null; _lastStart = DateTimeOffset.UtcNow; _window.Enqueue((_lastStart, reserved)); var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var response = await base.SendAsync(request, timeout.Token);
        await response.Content.LoadIntoBufferAsync(65536, timeout.Token); var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        object? sanitized = null;
        try { sanitized = Sanitize(JsonNode.Parse(bytes), 0); } catch (JsonException) { sanitized = new { nonJsonBodySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }; }
        Last = new { httpStatus = (int)response.StatusCode, elapsedMs = timer.Elapsed.TotalMilliseconds, body = sanitized };
        return response;
    }
    private static JsonNode? Sanitize(JsonNode? node, int depth)
    {
        if (depth > 12) return JsonValue.Create("[depth limit]");
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj.Take(40))
            {
                var key = pair.Key.ToLowerInvariant();
                var usageCount = key.EndsWith("tokens", StringComparison.Ordinal) && pair.Value is JsonValue value && value.TryGetValue<long>(out _);
                var sensitive = !usageCount && (key.Contains("key", StringComparison.Ordinal) || key.Contains("token", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal)
                    || key.Contains("authorization", StringComparison.Ordinal) || key is "request" or "prompt" or "input" or "messages" or "state" or "headers" or "credential");
                result[pair.Key[..Math.Min(pair.Key.Length, 160)]] = sensitive ? JsonValue.Create("[redacted]") : Sanitize(pair.Value, depth + 1);
            }
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Take(20).Select(item => Sanitize(item, depth + 1)).ToArray());
        if (node is JsonValue text && text.TryGetValue<string>(out var content)) return JsonValue.Create(content[..Math.Min(content.Length, 5000)]);
        return node?.DeepClone();
    }
}
