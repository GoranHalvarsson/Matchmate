namespace Matchmate.ReplayPublisher;

public sealed record ReplayExportOptions(string OutputDirectory, string BrentfordManifest, bool BrentfordOnly)
{
    public static ReplayExportOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "--export-local" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Use --export-local DIRECTORY --brentford-manifest FILE [--dataset brentford-existing-illustration].");
        string? manifest = null, dataset = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Every export option requires one value.");
            if (args[index] == "--brentford-manifest" && manifest is null) manifest = args[index + 1];
            else if (args[index] == "--dataset" && dataset is null) dataset = args[index + 1];
            else throw new ArgumentException("Unknown or repeated export option: " + args[index]);
        }
        if (manifest is null) throw new ArgumentException("An explicit --brentford-manifest FILE is required. No old-version fallback is selected.");
        if (dataset is not null && dataset != "brentford-existing-illustration") throw new ArgumentException("The optional --dataset must be brentford-existing-illustration; omit it to export both recordings.");
        return new(Path.GetFullPath(args[1]), Path.GetFullPath(manifest), dataset is not null);
    }
}
