using System.Text.Json;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.ReplayPublisher;
using Xunit;

namespace Matchmate.Tests;

public sealed class ReplayPublisherParityTests
{
    [Fact]
    public void ExportRequiresExplicitManifestWithoutAnOldVersionFallback()
        => Assert.Contains("explicit --brentford-manifest", Assert.Throws<ArgumentException>(() => ReplayExportOptions.Parse(["--export-local", "output"])).Message);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExportParsesTheExactManifestAndOptionalBrentfordOnlySelection(bool only)
    {
        var args = new List<string> { "--export-local", "output", "--brentford-manifest", "prepared/manifest.json" };
        if (only) args.AddRange(["--dataset", "brentford-existing-illustration"]);
        var options = ReplayExportOptions.Parse(args.ToArray());
        Assert.Equal(Path.GetFullPath("prepared/manifest.json"), options.BrentfordManifest);
        Assert.Equal(Path.GetFullPath("output"), options.OutputDirectory); Assert.Equal(only, options.BrentfordOnly);
    }

    [Theory]
    [InlineData("--brentford-manifest", "again.json")]
    [InlineData("--dataset", "unknown")]
    [InlineData("--unknown", "value")]
    [InlineData("--dataset", "--preflight")]
    public void InvalidOrRepeatedOptionsFailBeforeExport(string option, string value)
        => Assert.Throws<ArgumentException>(() => ReplayExportOptions.Parse(["--export-local", "out", "--brentford-manifest", "prepared.json", option, value]));

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task OfflinePreflightAcceptsCompleteCanonicalExportsAndReportsActualClosure(int version)
    {
        using var fixture = await ExportFixture.CreateAsync(version);
        var result = await ReplayExport.ValidateAsync(fixture.Directory);
        Assert.True(result.Readiness.OfflineValidated); Assert.False(result.Readiness.CloudWritesPerformed);
        Assert.Equal(version, result.Readiness.ExportVersion); Assert.Equal(64, result.Readiness.ExportHash.Length);
        var dataset = Assert.Single(result.Readiness.Datasets);
        Assert.True(dataset.Closed); Assert.Equal(0, dataset.PendingRecords); Assert.Equal(1, dataset.Head);
        Assert.Equal(1, dataset.Frames); Assert.Equal(fixture.Export.Datasets[0].Stream, dataset.Stream);
        Assert.Contains("tracking_frame", dataset.Capabilities);
    }

    [Theory]
    [InlineData("open")] [InlineData("counts")] [InlineData("head")] [InlineData("duplicate")]
    [InlineData("truncated")] [InlineData("archive")] [InlineData("transport")] [InlineData("actions")]
    public async Task PreflightRejectsIncompleteOrChangedExportsBeforeAnyCloudOperation(string change)
    {
        using var fixture = await ExportFixture.CreateAsync(2, close: change != "open");
        var export = fixture.Export;
        export = change switch
        {
            "counts" => export with { Datasets = [export.Datasets[0] with { Frames = 2 }] },
            "head" => export with { Datasets = [export.Datasets[0] with { Batches = 2 }] },
            "duplicate" => export with { Chunks = [export.Chunks[0], export.Chunks[0]], Datasets = [export.Datasets[0] with { Frames = 2 }] },
            "actions" => export with { Datasets = [export.Datasets[0] with { Actions = 1 }] },
            "truncated" => export with { Chunks = [] },
            _ => export
        };
        if (change == "archive") await File.WriteAllTextAsync(Path.Combine(fixture.Directory, fixture.Export.Artifacts[0].File), "changed");
        if (change == "transport") await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, fixture.Export.Chunks[0].File), [1, 2, 3]);
        await fixture.WriteAsync(export);
        await Assert.ThrowsAsync<RecordingValidationException>(() => ReplayExport.ValidateAsync(fixture.Directory));
    }

    [Fact]
    public async Task V2BrentfordRequiresItsPinnedSourceManifestArchive()
    {
        using var fixture = await ExportFixture.CreateAsync(2, brentford: true);
        var error = await Assert.ThrowsAsync<RecordingValidationException>(() => ReplayExport.ValidateAsync(fixture.Directory));
        Assert.Contains("source manifest archive", error.Message);
    }

    [Fact]
    public async Task ExportCannotWriteInsideItsPreparedSourceDirectory()
    {
        var input = Path.Combine(Path.GetTempPath(), "prepared-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<RecordingValidationException>(() => ReplayExport.ExportAsync(new(Path.Combine(input, "out"), Path.Combine(input, "manifest.json"), true), input));
        Assert.False(System.IO.Directory.Exists(input));
    }

    [Theory]
    [InlineData("../outside.batch")] [InlineData("../outside.json")]
    public async Task PreflightRejectsPathsOutsideTheExportDirectory(string file)
    {
        using var fixture = await ExportFixture.CreateAsync(2);
        var export = file.EndsWith(".batch", StringComparison.Ordinal) ? fixture.Export with { Chunks = [fixture.Export.Chunks[0] with { File = file }] }
            : fixture.Export with { Artifacts = [fixture.Export.Artifacts[0] with { File = file }] };
        await fixture.WriteAsync(export);
        await Assert.ThrowsAsync<RecordingValidationException>(() => ReplayExport.ValidateAsync(fixture.Directory));
    }

    [Theory]
    [InlineData("pass-slot")] [InlineData("goal-person")] [InlineData("goal-sequence")]
    public async Task ResignedSameCountTransportChangesFailTheDerivedCanonicalRecordBinding(string change)
    {
        using var fixture = await ExportFixture.CreateAsync(2, sourceBinding: true);
        var original = FootballTransport.Decode(await ReplayExport.ReadChunkAsync(fixture.Directory, fixture.Export.Chunks[0]), fixture.Export.Chunks[0].ContentType);
        var clean = await ReplayExport.ValidateAsync(fixture.Directory); // Complete, closed, valid transport/archive/count fixture.
        Assert.Equal(1, Assert.Single(clean.Readiness.Datasets).Actions);
        ValidateDerivedRecords(original.Records, original.Records);
        var records = original.Records.Select(record => record.Payload switch
        {
            FootballActionPayload pass when change == "pass-slot" => record with { Payload = pass with { FromSlotId = "slot-review-alt" } },
            MatchEventPayload goal when change == "goal-person" => record with { Payload = goal with { PersonId = "person-three" } },
            MatchEventPayload goal when change == "goal-sequence" => record with { Payload = goal with { Sequence = goal.Sequence! with { Steps = [goal.Sequence.Steps[0] with { Text = "Changed reported sequence description." }] } } },
            _ => record
        }).ToArray();
        var resigned = FootballCodec.Sign(original with { Records = records });
        var encoded = FootballTransport.Encode(resigned); var chunk = fixture.Export.Chunks[0] with
        { Hash = ReplayBatchCodec.Hash(encoded.Bytes), Bytes = encoded.Bytes.Length, Digest = resigned.Digest, ContentType = encoded.ContentType };
        await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, chunk.File), encoded.Bytes);
        var rewritten = fixture.Export with { Chunks = [chunk] }; await fixture.WriteAsync(rewritten);
        Assert.Equal(fixture.Export.Artifacts, rewritten.Artifacts); Assert.Equal(fixture.Export.Datasets, rewritten.Datasets);
        var countOnly = await ReplayExport.ValidateAsync(fixture.Directory); // Generic test stream still passes the prior transport/count gates.
        Assert.Equal(JsonSerializer.Serialize(clean.Readiness.Datasets, FootballCodec.Json), JsonSerializer.Serialize(countOnly.Readiness.Datasets, FootballCodec.Json));
        var decoded = FootballTransport.Decode(await ReplayExport.ReadChunkAsync(fixture.Directory, chunk), chunk.ContentType);
        var error = Assert.Throws<TargetInvocationException>(() => ValidateDerivedRecords(decoded.Records, original.Records));
        Assert.Contains("Canonical non-tracking record source binding mismatch", Assert.IsType<RecordingValidationException>(error.InnerException).Message);
    }

    private static void ValidateDerivedRecords(FootballRecord[] actual, FootballRecord[] derived)
        => typeof(ReplayExport).GetMethod("ValidateDerivedNonTrackingRecords", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [actual, derived]);

    private sealed class ExportFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "matchmate-publisher-test-" + Guid.NewGuid().ToString("N"));
        public FootballExport Export { get; private set; } = default!;
        public static async Task<ExportFixture> CreateAsync(int version, bool close = true, bool brentford = false, bool sourceBinding = false)
        {
            var result = new ExportFixture(); System.IO.Directory.CreateDirectory(result.Directory);
            var fixture = sourceBinding ? GeneratedIllustrationStatisticsTests.GeneratedFixture() : SharedFootballTests.Fixture();
            if (sourceBinding) fixture = fixture with { Manifest = fixture.Manifest with { Capabilities = fixture.Manifest.Capabilities.Append("shared-event-sequences-v1").ToArray() } };
            if (brentford) fixture = fixture with { Manifest = fixture.Manifest with { DatasetId = "brentford-existing-illustration" }, Stream = fixture.Stream with { DatasetId = "brentford-existing-illustration" } };
            var records = (sourceBinding ? GeneratedIllustrationStatisticsTests.GeneratedContext(fixture) : SharedFootballTests.Context(fixture)).Append(SharedFootballTests.Frame(fixture, 0)).ToList();
            if (sourceBinding)
            {
                records.Add(GeneratedIllustrationStatisticsTests.Action(fixture, "pass-one", "pass", 100, 500));
                var goal = SharedFootballTests.Goal(fixture);
                records.Add(goal with { Payload = ((MatchEventPayload)goal.Payload) with { Sequence = new(1, "sequence-test-goal", 0, 1000,
                    [new("finish", "reported_finish", "person-one", null, 0, "Reported finish.", ["source"])]) } });
            }
            var batch = SharedFootballTests.Batch(fixture, 1, records.ToArray(), close);
            var transport = FootballTransport.Encode(batch); var file = $"{batch.Stream.StreamId}--{batch.Sequence:D12}.batch";
            await File.WriteAllBytesAsync(Path.Combine(result.Directory, file), transport.Bytes);
            System.IO.Directory.CreateDirectory(Path.Combine(result.Directory, "sources"));
            byte[] artifact = [123, 125]; var hash = ReplayBatchCodec.Hash(artifact); var sourceFile = "sources/" + hash + "-receipt.json";
            await File.WriteAllBytesAsync(Path.Combine(result.Directory, sourceFile), artifact);
            result.Export = new(version, [new(batch.Stream, 1, 1, sourceBinding ? 1 : 0, 0, sourceBinding ? 1 : 0)], [new(file, transport.ContentType, ReplayBatchCodec.Hash(transport.Bytes), transport.Bytes.Length, batch.Stream, 1, batch.Digest)], [new(sourceFile, "test/receipt.json", hash, artifact.Length)]);
            await result.WriteAsync(result.Export); return result;
        }
        public Task WriteAsync(FootballExport export) => File.WriteAllBytesAsync(Path.Combine(Directory, "export.json"), JsonSerializer.SerializeToUtf8Bytes(export, FootballCodec.Json));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
