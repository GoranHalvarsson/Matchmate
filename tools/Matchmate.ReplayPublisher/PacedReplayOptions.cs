using System.Globalization;
using Matchmate.Core;

namespace Matchmate.ReplayPublisher;

public sealed record PacedReplayExportOptions(string OutputDirectory, string SourceDirectory, string SourceDataset)
{
    public static PacedReplayExportOptions Parse(string[] args)
    {
        var values = PacedReplayOptions.Values(args, "--export-paced-demo", ["--source-export", "--source-dataset"]);
        if (!values.TryGetValue("--source-export", out var source)) throw new ArgumentException("An explicit --source-export DIRECTORY is required.");
        var dataset = values.GetValueOrDefault("--source-dataset", "brentford-existing-illustration");
        if (!DatasetIdentity.IsValid(dataset)) throw new ArgumentException("Invalid source dataset identity.");
        return new(Path.GetFullPath(args[1]), Path.GetFullPath(source), dataset);
    }
}

public sealed record PacedReplayPublishOptions(string Directory, int DelayMs, bool DryRun)
{
    public const int DefaultDelayMs = 2000, MinDelayMs = 100, MaxDelayMs = 2500;
    public static PacedReplayPublishOptions Parse(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Use --publish-paced DIRECTORY [--delay-ms VALUE] [--dry-run].");
        var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
        if (args.Count(a => a == "--dry-run") > 1) throw new ArgumentException("Repeated --dry-run option.");
        var values = PacedReplayOptions.Values(args.Where(a => a != "--dry-run").ToArray(), "--publish-paced", ["--delay-ms"]);
        var delay = DefaultDelayMs;
        if (values.TryGetValue("--delay-ms", out var value) && !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out delay))
            throw new ArgumentException("--delay-ms requires an integer.");
        ValidateDelay(delay);
        return new(Path.GetFullPath(args[1]), delay, dryRun);
    }
    public static void ValidateDelay(int delay)
    {
        if (delay is < MinDelayMs or > MaxDelayMs) throw new ArgumentException($"--delay-ms must be {MinDelayMs}–{MaxDelayMs}; the 60 staged waits total at most 150 seconds.");
    }
}

internal static class PacedReplayOptions
{
    public static Dictionary<string, string> Values(string[] args, string command, string[] allowed)
    {
        if (args.Length < 2 || args[0] != command || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Use {command} DIRECTORY with explicit options.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !allowed.Contains(args[index]) || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Unknown, repeated or missing paced option value.");
        return result;
    }
}
