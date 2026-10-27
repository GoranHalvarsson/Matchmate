using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Matchmate.LeagueChatEvaluation;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var options = EvaluationOptions.Parse(args, Directory.GetCurrentDirectory());
var bytes = options.ReadVerifiedBytes();
using var schema = JsonDocument.Parse(bytes);
if (schema.RootElement.GetProperty("schemaVersion").GetInt32() == 1)
{
    if (options.Live) throw new InvalidDataException("Legacy route-only fixtures are offline diagnostics only.");
    var old = options.ReadFixture();
    Console.WriteLine(JsonSerializer.Serialize(new { mode = "legacy_offline_validation_only", fixtureSha256 = options.ExpectedHash, cases = old.Cases.Length, liveCalls = 0 }));
    return;
}
var fixture = ReliabilityFixtures.Read(bytes);
var reference = options.ReferenceBundle is null ? null : ReliabilityRunner.ReadReference(options.ReferenceBundle, options.ReferenceHash!);
if (reference is not null) foreach (var item in fixture.Cases) _ = reference.Context(item); // All source contexts pass before credentials or calls.
if (!options.Live)
{
    if (reference is not null)
        foreach (var item in fixture.Cases)
        {
            var plan = await reference.ExecuteExpectedPlanAsync(item);
            if (!plan.Usable) throw new InvalidDataException("Offline expected plan does not match pinned source: " + item.Id + "; outcome=" + plan.Execution?.Outcome + "; subject=" + plan.Execution?.ResolvedClubId + "; evidence=" + plan.EvidenceCorrect);
        }
    Console.WriteLine(JsonSerializer.Serialize(new { mode = reference is null ? "offline_schema_validation_only" : "offline_expected_plan_validation_only",
        fixtureSha256 = options.ExpectedHash, referenceSha256 = options.ReferenceHash, cases = fixture.Cases.Length, liveCalls = 0 }));
    return;
}
using var handler = new EvaluationCaptureTransport(new HttpClientHandler { AllowAutoRedirect = false });
using var http = new HttpClient(handler);
var configured = Options.Create(new DecisionRoutingOptions { Enabled = true, Endpoint = options.Endpoint!, Deployment = options.Deployment!, TimeoutSeconds = 15 });
var router = new DecisionLeagueWorkflowRouter(http, configured, new EvaluationEnvironment(), new DevelopmentModelCredential(new DefaultAzureCredential()));
var pacer = new EvaluationPacer(); var results = new List<object>(); var score = new ReliabilityScore(fixture.Cases.Length, options.Limit);
foreach (var item in fixture.Cases.Take(options.Limit))
{
    await pacer.WaitAsync(default); // Queue wait precedes the router's15second timeout.
    handler.BeginCase(); var watch = Stopwatch.StartNew();
    var route = await router.RouteAsync(reference!.Input(item), default);
    var observation = await reference.ExecuteAsync(item, route, handler.Decision);
    score.Register(item, observation);
    results.Add(new { caseId = item.Id, expected = item.Expected, selectedWorkflow = route.WorkflowId, status = route.Status.ToString(),
        actualOutcome = observation.Execution?.Outcome.ToString(), actualSubject = observation.Execution?.ResolvedClubId,
        evidenceIds = observation.Execution?.Evidence.Select(e => e.Id), observation.Reads, observation.ExactWorkflow,
        observation.FamilyCorrect, observation.OutcomeCorrect, observation.SubjectCorrect, observation.EvidenceCorrect,
        observation.BindingCorrect, observation.TimeScopeCorrect, observation.ResolvedTimeScope, observation.TimeIntentCorrect, observation.Usable, observation.UnsafeSubject, observation.UnsafeTime, observation.UnsafeUnexpectedAnswer,
        milliseconds = watch.ElapsedMilliseconds, http = handler.Last, decision = handler.Decision });
    if (score.Stopped) break;
}
var receipt = new { mode = "explicit_live_router_and_pinned_domain_execution", fixtureSha256 = options.ExpectedHash, referenceSha256 = options.ReferenceHash,
    timeDiagnostics = "Original semantic-time gold is compared to code resolution; binary model intent has separate optional gold and raw diagnostics.",
    registry = LeagueWorkflowRegistry.Version, endpoint = options.Endpoint, deployment = options.Deployment, fixtureCases = score.FixtureCases, requestedCases = score.Requested, resultKind = score.ResultKind,
    score.Total, score.Usable, score.RawExact, score.Supported, score.SupportedUsable, score.UnsafeAnswers, score.ProviderFailures, score.InvalidResponses, score.UnavailableResponses, score.SemanticErrors, score.ExecutedRequested, score.Completed, score.SampleQualityMet, score.CalibrationQualityMet,
    stopReason = score.Stopped ? "three_consecutive_unavailable_or_invalid_provider_responses" : null,
    accuracyPercent = score.Total == 0 ? 0 : 100d * score.Usable / score.Total,
    supportedAccuracyPercent = score.Supported == 0 ? 0 : 100d * score.SupportedUsable / score.Supported,
    targetPercent = 95, meetsTarget = score.MeetsTarget, freshGateComposition = score.FreshGateComposition, results };
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
await using (var output = new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write))
    await JsonSerializer.SerializeAsync(output, receipt, new JsonSerializerOptions(EvaluationOptions.Json) { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { fixtureSha256 = options.ExpectedHash, receipt.Total, receipt.Usable, receipt.accuracyPercent,
    receipt.supportedAccuracyPercent, receipt.resultKind, receipt.ExecutedRequested, receipt.Completed, receipt.CalibrationQualityMet, receipt.stopReason, receipt.meetsTarget, receipt = options.OutputPath }));
if (!receipt.meetsTarget && !receipt.CalibrationQualityMet) Environment.ExitCode = 3;

internal sealed class EvaluationEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Matchmate.LeagueChatEvaluation";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
