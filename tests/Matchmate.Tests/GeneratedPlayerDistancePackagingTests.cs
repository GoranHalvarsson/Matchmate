using System.Xml.Linq;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;

namespace Matchmate.Tests;

public sealed class GeneratedPlayerDistancePackagingTests
{
    [Fact, Trait("Finding", "F-GD-001")]
    public void ContainersIncludeBothPreparedResourcesAndKeepRawDataOutsideTheBuildContext()
    {
        var root = LocalPaths.FindRoot();
        var rules = File.ReadAllLines(Path.Combine(root, ".dockerignore")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        Assert.Equal("**", rules[0]);
        Assert.Equal(new[] { "!global.json", "!Directory.Build.props", "!src/", "!src/**", "!data/", "!data/match-enrichment/",
            "!data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json", "!data/generated-replay-distance/",
            "!data/generated-replay-distance/brentford-full-v5-distance-v2.gz", "!data/generated-replay-distance/liverpool-actions-v1-distance-v1.gz" }.Order(StringComparer.Ordinal),
            rules.Where(l => l.StartsWith('!')).Order(StringComparer.Ordinal));
        Assert.Contains("data/*", rules);
        var resources = new[] { "data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json", "data/generated-replay-distance/brentford-full-v5-distance-v2.gz",
            "data/generated-replay-distance/liverpool-actions-v1-distance-v1.gz" };
        foreach (var resource in resources)
        {
            Assert.True(File.Exists(Path.Combine(root, resource)));
            var directory = resource[..(resource.LastIndexOf('/') + 1)];
            Assert.True(Array.IndexOf(rules, directory + "*") < Array.IndexOf(rules, "!" + resource));
            Assert.Contains(directory + "*", rules); // Sibling raw data/files are excluded, with one exact exception.
        }
        var embedded = XDocument.Load(Path.Combine(root, "src/Matchmate.Infrastructure/Matchmate.Infrastructure.csproj"))
            .Descendants("EmbeddedResource").Select(e => e.Attribute("Include")!.Value.Replace("../../", "", StringComparison.Ordinal)).ToArray();
        Assert.Equal(resources.Order(StringComparer.Ordinal), embedded.Order(StringComparer.Ordinal));
        foreach (var app in new[] { "Matchmate.Api", "Matchmate.Worker" })
        {
            var docker = File.ReadAllLines(Path.Combine(root, "src", app, "Dockerfile"));
            Assert.Equal(resources.Select(resource => "COPY " + resource + " " + resource[..(resource.LastIndexOf('/') + 1)]),
                docker.Where(l => l.StartsWith("COPY data/", StringComparison.Ordinal)));
        }
    }

    [Fact, Trait("Finding", "F-GD-002")]
    public void GeneratorTextFingerprintIsInvariantToCheckoutNewlinesAndIncludesItsOwnHelper()
    {
        var root = LocalPaths.FindRoot();
        var inputs = GeneratedDistanceSourceFingerprint.InputPaths.Select(path => (Path: path, Text: File.ReadAllText(Path.Combine(root, path)))).ToArray();
        Assert.Contains("src/Matchmate.Core/GeneratedDistanceSourceFingerprint.cs", inputs.Select(i => i.Path));
        var lf = inputs.Select(i => (i.Path, i.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))).ToArray();
        var crlf = lf.Select(i => (i.Path, i.Item2.Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        Assert.Equal(GeneratedDistanceSourceFingerprint.Compute(lf), GeneratedDistanceSourceFingerprint.Compute(crlf));
        Assert.Equal(GeneratedDistanceSourceFingerprint.Compute(inputs), GeneratedDistanceSourceFingerprint.Compute(lf));
        Assert.Equal(GeneratedDistanceSourceFingerprint.Compute([("a.cs", "rörelse\n")]),
            GeneratedDistanceSourceFingerprint.Compute([("a.cs", "\uFEFFrörelse\r\n")]));
        Assert.NotEqual(GeneratedDistanceSourceFingerprint.Compute(lf), GeneratedDistanceSourceFingerprint.Compute(lf.Append(("changed.cs", "changed\n"))));
        Assert.Contains("src/Matchmate.Infrastructure/LiverpoolFootballSource.cs", inputs.Select(i => i.Path));
        foreach (var (name, sourceHash) in new[] { ("brentford-full-v5-distance-v2.gz", "ce79cf8f307fbc596396a78edcf3e9c9f8fb41798a0b73ad06d7e4cd2465e6de"),
            ("liverpool-actions-v1-distance-v1.gz", LiverpoolFootballSource.ManifestHash) })
        {
            var path = Path.Combine(root, "data/generated-replay-distance", name);
            using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new BinaryReader(gzip);
            Assert.Equal("MMGD0001"u8.ToArray(), reader.ReadBytes(8));
            using var header = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
            Assert.Equal(GeneratedDistanceSourceFingerprint.Compute(lf), header.RootElement.GetProperty("generatorSha256").GetString());
            Assert.Equal(sourceHash, header.RootElement.GetProperty("sourceManifestSha256").GetString());
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(path, ".receipt.json")));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), receipt.RootElement.GetProperty("packSha256").GetString());
            Assert.Equal(GeneratedDistanceSourceFingerprint.Compute(lf), receipt.RootElement.GetProperty("generatorSha256").GetString());
            using var prefix = new MemoryStream(); gzip.CopyTo(prefix);
            var chelsea = name.StartsWith("brentford", StringComparison.Ordinal);
            Assert.Equal(chelsea ? 2_587_422 : 1_402_419, prefix.Length);
            Assert.Equal(chelsea ? "aa8d95bc3311d418caf0ba7bbdd1c497dce21e3c32b8e81a64ef9e33cf139917"
                : "85d1f8ab7a9cc74e3b866ddbc4237a1ef733ff3f58374891f8742f78e798b618", Convert.ToHexStringLower(SHA256.HashData(prefix.ToArray())));
            // Header bindings change with the canonical revision; every per-player prefix byte stays identical.
            var priorName = chelsea ? "brentford-full-v5-distance-v1.gz" : "liverpool-cards-v1-distance-v1.gz";
            using var prior = File.OpenRead(Path.Combine(root, "data/generated-replay-distance", priorName));
            using var oldGzip = new GZipStream(prior, CompressionMode.Decompress); using var oldReader = new BinaryReader(oldGzip);
            Assert.Equal("MMGD0001"u8.ToArray(), oldReader.ReadBytes(8)); oldReader.ReadBytes(oldReader.ReadInt32());
            using var oldPrefix = new MemoryStream(); oldGzip.CopyTo(oldPrefix);
            Assert.Equal(oldPrefix.ToArray(), prefix.ToArray());
        }
    }
}
