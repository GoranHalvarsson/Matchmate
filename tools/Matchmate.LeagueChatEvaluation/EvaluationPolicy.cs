using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;

namespace Matchmate.LeagueChatEvaluation;

public sealed record EvaluationCase(string Id, string Question, LeagueRoutingContext Context, LeagueQuestionPointer[] PreviousQuestions,
    bool HasNamedClubReference, string Expected, LeagueClarificationContext? Clarification = null, LeagueAcceptedTopic? AcceptedTopic = null);
public sealed record EvaluationFixture(int SchemaVersion, EvaluationCase[] Cases);
public sealed record EvaluationOptions(bool Live, string FixturePath, string OutputPath, string ExpectedHash, int Limit, string? Endpoint, string? Deployment, string? ReferenceBundle = null, string? ReferenceHash = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public static EvaluationOptions Parse(string[] args, string root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var live = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--live") { if (live) throw new ArgumentException("Duplicate live flag."); live = true; continue; }
            if (args[i] is not ("--fixtures" or "--fixture-sha256" or "--output" or "--endpoint" or "--deployment" or "--limit" or "--reference-bundle" or "--reference-sha256")
                || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i], args[++i]))
                throw new ArgumentException("Use --fixtures FILE [--fixture-sha256 SHA256] [--live --endpoint HTTPS --deployment NAME] [--reference-bundle .local/league-reference-v2/league-reference.json --reference-sha256 SHA256] [--limit 1..60] [--output .azure/FILE].");
        }
        var limit = 60;
        if (values.TryGetValue("--limit", out var text) && (!int.TryParse(text, out limit) || limit is < 1 or > 60)) throw new ArgumentException("Use a limit from1to60.");
        var fixture = Path.GetFullPath(values.GetValueOrDefault("--fixtures", "tests/Matchmate.Tests/Fixtures/league-routing-training-v1.json"), root);
        var output = Path.GetFullPath(values.GetValueOrDefault("--output", ".azure/league-routing-v2-evaluation.json"), root);
        var azure = Path.Combine(root, ".azure");
        if (!Inside(azure, output) || Path.GetExtension(output) != ".json" || Directory.Exists(output)
            || string.Equals(output, fixture, StringComparison.OrdinalIgnoreCase) || live && File.Exists(output))
            throw new ArgumentException("Choose a new output file inside the workspace .azure directory, distinct from the fixture.");
        string hash;
        if (values.TryGetValue("--fixture-sha256", out var supplied))
        {
            if (!Regex.IsMatch(supplied, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) || !Inside(azure, fixture) || Path.GetExtension(fixture) != ".json")
                throw new ArgumentException("Custom hash-pinned JSON fixtures must be inside workspace .azure.");
            hash = supplied.ToLowerInvariant();
        }
        else
        {
            var fixtures = Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures");
            if (!Inside(fixtures, fixture)) throw new ArgumentException("Custom fixtures require --fixture-sha256 inside workspace .azure.");
            hash = Path.GetFileName(fixture) switch
            {
                "league-routing-training-v1.json" => TrainingHash,
                "league-chat-tuning-v3.json" when !live => TuningHash,
                "league-chat-tuning-v2.json" when !live => "5e1f7bb6be81b68d1083163db3ca7c733c897690a6d6c3ac41b78c4da2acc0a2",
                "league-chat-heldout-v1.json" when !live => "11054509c9d1d2a72475e643ebe9cf712f9875fd8e55485a3dde766f34edd575",
                "league-chat-tuning-v1.json" when !live => "401d80ea5baec9dc11ff3d5d51e49870d95526559937abfb865f2718266dc774",
                _ => throw new ArgumentException("Legacy route-only fixtures are offline diagnostics; use training-v1 or a new explicit hash-pinned reliability fixture.")
            };
        }
        var endpoint = values.GetValueOrDefault("--endpoint"); var deployment = values.GetValueOrDefault("--deployment");
        if (live && (!AzureModelEndpoint.Valid(endpoint ?? "") || string.IsNullOrWhiteSpace(deployment) || deployment.Length > 128))
            throw new ArgumentException("Live routing requires explicit approved endpoint and deployment. No secret configuration is read.");
        var reference = values.GetValueOrDefault("--reference-bundle"); var referenceHash = values.GetValueOrDefault("--reference-sha256");
        if ((reference is null) != (referenceHash is null)) throw new ArgumentException("Supply both reference path and SHA-256.");
        if (reference is not null)
        {
            reference = Path.GetFullPath(reference, root);
            if (!string.Equals(reference, Path.GetFullPath(".local/league-reference-v2/league-reference.json", root), StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(referenceHash!, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("Use the existing pinned .local/league-reference-v2/league-reference.json and exact SHA-256.");
        }
        if (live && reference is null) throw new ArgumentException("End-to-end live evaluation requires a pinned reference bundle.");
        return new(live, fixture, output, hash, limit, endpoint, deployment, reference, referenceHash?.ToLowerInvariant());
    }
    public const string TrainingHash = "d5b6dbf7dae37a55f1e4937161e551781f8072147319bd837feb94dbf004e9f6";
    public const string TuningHash = "6df987ca180d900b81ce804ab2d828c826f2a79464e0624edef9c5dd2192840e";
    private static bool Inside(string directory, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    public byte[] ReadVerifiedBytes()
    {
        var bytes = File.ReadAllBytes(FixturePath);
        if (bytes.Length > 100000 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != ExpectedHash) throw new InvalidDataException("Frozen fixture SHA-256 mismatch or size cap exceeded. No inference performed.");
        return bytes;
    }
    public EvaluationFixture ReadFixture()
    {
        var bytes = ReadVerifiedBytes();
        var fixture = JsonSerializer.Deserialize<EvaluationFixture>(bytes, Json) ?? throw new InvalidDataException("Missing fixture.");
        if (fixture.SchemaVersion != 1 || fixture.Cases is not { Length: >= 1 and <= 60 } || fixture.Cases.Select(c => c.Id).Distinct().Count() != fixture.Cases.Length
            || fixture.Cases.Select(c => c.Question).Distinct().Count() != fixture.Cases.Length
            || fixture.Cases.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > 1000 || !LeagueWorkflowRegistry.Criteria.ContainsKey(c.Expected)
                || c.PreviousQuestions is not { Length: <= 8 } || c.PreviousQuestions.Any(p => p.Question is null || p.Question.Length > 1000)
                || c.Clarification?.OriginalQuestion.Length > 1000 || c.Context is null
                || c.AcceptedTopic is not null && !LeagueWorkflowRegistry.ValidTopic(c.AcceptedTopic)
                || !LeagueWorkflowExecutor.ValidContext(new("evaluation", c.Context.SeasonKey, c.Context.DateIndex, c.Context.Date, c.Context.SelectedClubId, c.Context.SelectedClubName))))
            throw new InvalidDataException("Invalid bounded routing fixtures.");
        return fixture;
    }
}

public sealed class EvaluationScore(int requested)
{
    private int _consecutiveFailures;
    public int Total { get; private set; }
    public int Passed { get; private set; }
    public bool StoppedForProviderFailure { get; private set; }
    public bool Completed => !StoppedForProviderFailure && Total == requested;
    public bool MeetsTarget => Completed && Passed >= Total * .9;
    public bool Register(string expected, MatchRoutingResult result)
    {
        if (StoppedForProviderFailure || Total >= requested) throw new InvalidOperationException("Evaluation already stopped.");
        var correct = result.Status is MatchRoutingStatus.Selected or MatchRoutingStatus.Clarify && Choice(result) == expected;
        Total++; if (correct) Passed++;
        _consecutiveFailures = result.Status is MatchRoutingStatus.Unavailable or MatchRoutingStatus.InvalidResponse ? _consecutiveFailures + 1 : 0;
        StoppedForProviderFailure = _consecutiveFailures == 3;
        return correct;
    }
    public static string? Choice(MatchRoutingResult result) => result.Status == MatchRoutingStatus.Clarify ? "clarify" : result.WorkflowId;
}



