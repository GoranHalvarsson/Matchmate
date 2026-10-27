using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Matchmate.Core;

// Generator code is text, normalized to UTF8 without BOM and LF. Recording artifact hashes stay raw.
public static class GeneratedDistanceSourceFingerprint
{
    public static ImmutableArray<string> InputPaths { get; } = [
        "global.json", "Directory.Build.props", "tools/Matchmate.GeneratedDistance/Matchmate.GeneratedDistance.csproj",
        "tools/Matchmate.GeneratedDistance/Program.cs", "src/Matchmate.Contracts/FootballContracts.cs",
        "src/Matchmate.Contracts/GeneratedDistanceContracts.cs", "src/Matchmate.Core/GeneratedDistanceSourceFingerprint.cs",
        "src/Matchmate.Core/GeneratedDistanceCalculator.cs", "src/Matchmate.Core/GeneratedDistanceProjection.cs",
        "src/Matchmate.Core/FootballPipeline.cs", "src/Matchmate.Core/FootballTransport.cs", "src/Matchmate.Core/ReplayPublication.cs",
        "src/Matchmate.Infrastructure/PreparedGeneratedDistanceStore.cs", "src/Matchmate.Infrastructure/FullFootballSources.cs",
        "src/Matchmate.Infrastructure/FootballSources.cs", "src/Matchmate.Infrastructure/LiverpoolFootballSource.cs"];

    public static string Compute(IEnumerable<(string Path, string Text)> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, text) in sources)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.Replace('\\', '/') + "\n"));
            var normalized = text.StartsWith('\uFEFF') ? text[1..] : text;
            hash.AppendData(Encoding.UTF8.GetBytes(normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
