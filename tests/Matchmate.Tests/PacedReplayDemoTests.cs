using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.ReplayPublisher;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class PacedReplayDemoTests
{
    [Fact]
    public async Task CompletedActionsAndTrackingAdvanceThroughTheRealLedgerWithoutFutureReports()
    {
        using var fixture = await DemoFixture.CreateAsync();
        var sourceBefore = Snapshot(fixture.Source);
        var result = await fixture.ExportAsync();
        Assert.True(result.Readiness.OfflineValidated); Assert.False(result.Readiness.CloudWritesPerformed);
        Assert.Equal(PacedReplayDemo.DatasetId, result.Plan.Stream.DatasetId); Assert.NotEqual(fixture.Fixture.Stream, result.Plan.Stream);
        Assert.Equal(120000, result.Readiness.ScheduledDelayMs);
        var first = result.Readiness.Progress[0]; Assert.Equal(1, first.Head); Assert.Equal(0, first.AcceptedActions);
        Assert.Equal(0, first.PassAttempts); Assert.Null(first.TrackingThroughMs); Assert.False(first.Closed);
        var early = result.Readiness.Progress.First(p => p.ThroughMs == 5000);
        var later = result.Readiness.Progress.First(p => p.ThroughMs == 10000);
        Assert.Equal(1, early.PassAttempts); Assert.Equal(2, later.PassAttempts);
        Assert.Equal(0, early.ControlledMs); Assert.Equal(100000, result.Readiness.Progress[^1].ControlledMs);
        Assert.True(early.TrackingThroughMs < later.TrackingThroughMs); Assert.False(early.FixedCursorTrackingAvailable);
        Assert.True(result.Readiness.Progress[^1].FixedCursorTrackingAvailable); Assert.True(result.Readiness.Progress[^1].Closed);
        Assert.All(result.Readiness.Progress, p => Assert.True(p.DuplicateStable));
        var batches = await Batches(fixture.Output, result.Manifest);
        var rows = batches.SelectMany(b => b.Records).ToArray();
        Assert.DoesNotContain(rows, r => r.Payload is MatchEventPayload or PossessionPayload);
        Assert.Equal(61, rows.Count(r => r.Payload is TrackingPayload)); Assert.Equal(3, rows.Count(r => r.Payload is FootballActionPayload));
        Assert.All(rows.Where(r => r.FootballTime is not null), r => Assert.True(r.FootballTime!.EndMs <= 300000 && r.FootballTime.PeriodId == "period-one"));
        Assert.All(rows, r => { Assert.Equal(result.Plan.Stream.DatasetId, r.DatasetId); Assert.Equal(result.Plan.Stream.DatasetVersion, r.DatasetVersion); Assert.StartsWith("paced-", r.RecordId); });
        var schedule = rows.Select(r => r.Payload).OfType<SchedulePayload>().Single();
        Assert.Equal(rows.Where(r => r.Payload is FootballActionPayload).Select(r => r.RecordId).Order(), schedule.Releases.Select(r => r.RecordId).Order());
        Assert.All(schedule.Releases, release => Assert.Equal(rows.Single(r => r.RecordId == release.RecordId).FootballTime!.EndMs, release.CursorMs));
        Assert.Contains("Synthetic", rows.Select(r => r.Payload).OfType<FixturePayload>().Single().Label);
        var roster = rows.Single(r => r.Payload is SquadPayload);
        Assert.Equal("reported", Assert.Single(roster.Evidence).Method); Assert.Equal("real", Assert.Single(roster.Evidence).Origin);
        Assert.Equal("One", ((SquadPayload)roster.Payload).People[0].Name);
        Assert.Equal(sourceBefore, Snapshot(fixture.Source)); // Every accepted input remains byte-identical.
        Assert.True((await ReplayExport.ValidateAsync(fixture.Source)).Readiness.OfflineValidated);
    }

    [Fact]
    public async Task ExportAndTransportRetriesHaveIdenticalBytesIdsAndDigests()
    {
        using var fixture = await DemoFixture.CreateAsync(); var first = await fixture.ExportAsync(); var before = Snapshot(fixture.Output);
        var second = await fixture.ExportAsync(); Assert.Equal(before, Snapshot(fixture.Output));
        Assert.Equal(first.Readiness.ExportHash, second.Readiness.ExportHash);
        var sent = new List<string>(); var waits = new List<double>();
        for (var retry = 0; retry < 2; retry++)
            await PacedReplayDemo.PublishAsync(fixture.Output, 100, (batch, transport, _) =>
            {
                Assert.Equal(batch.Digest, FootballTransport.Decode(transport.Bytes, transport.ContentType).Digest);
                sent.Add(PacedReplayDemo.MessageId(batch)); return Task.CompletedTask;
            }, delay: (duration, _) => { waits.Add(duration.TotalMilliseconds); return Task.CompletedTask; });
        Assert.Equal(sent.Take(sent.Count / 2), sent.Skip(sent.Count / 2)); Assert.Equal(120, waits.Count); Assert.All(waits, w => Assert.Equal(100, w));
    }

    [Fact]
    public async Task ValidlyResignedSameCountActionTamperFailsPinnedCanonicalBinding()
    {
        using var fixture = await DemoFixture.CreateAsync(); var result = await fixture.ExportAsync();
        var batches = await Batches(fixture.Output, result.Manifest); var target = batches.First(b => b.Records.Any(r => r.Payload is FootballActionPayload { ActionKind: "pass" }));
        var modified = FootballCodec.Sign(target with { Records = target.Records.Select(r => r.Payload is FootballActionPayload { ActionKind: "pass" } action
            ? r with { Payload = action with { FromSlotId = "different-valid-slot" } } : r).ToArray() });
        var encoded = FootballTransport.Encode(modified); var chunk = result.Manifest.Chunks.Single(c => c.Sequence == target.Sequence);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Output, chunk.File), encoded.Bytes);
        var export = result.Manifest with { Chunks = result.Manifest.Chunks.Select(c => c.Sequence == chunk.Sequence
            ? c with { ContentType = encoded.ContentType, Bytes = encoded.Bytes.Length, Hash = ReplayBatchCodec.Hash(encoded.Bytes), Digest = modified.Digest } : c).ToArray() };
        await File.WriteAllBytesAsync(Path.Combine(fixture.Output, "export.json"), JsonSerializer.SerializeToUtf8Bytes(export, FootballCodec.Json));
        Assert.True((await ReplayExport.ValidateAsync(fixture.Output)).Readiness.OfflineValidated); // Counts and transport alone cannot detect this.
        var error = await Assert.ThrowsAsync<RecordingValidationException>(() => PacedReplayDemo.ValidateAsync(fixture.Output));
        Assert.Contains("canonical record binding", error.Message);
    }

    [Fact]
    public async Task AStageCannotReleaseAnActionBeforeItsCompletion()
    {
        using var fixture = await DemoFixture.CreateAsync(); var result = await fixture.ExportAsync();
        var plan = result.Plan with { Stages = result.Plan.Stages.Select((s, index) => index == 1 ? s with { ThroughMs = 0 } : s).ToArray() };
        await File.WriteAllBytesAsync(Path.Combine(fixture.Output, PacedReplayDemo.PlanFile), JsonSerializer.SerializeToUtf8Bytes(plan, FootballCodec.Json));
        await Assert.ThrowsAsync<RecordingValidationException>(() => PacedReplayDemo.ValidateAsync(fixture.Output));
    }

    [Fact]
    public async Task CancellationStopsBeforeFirstSendAndDuringAWaitWithoutSkippingAhead()
    {
        using var fixture = await DemoFixture.CreateAsync(); await fixture.ExportAsync(); var sent = 0;
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PacedReplayDemo.PublishAsync(fixture.Output, 100,
            (_, _, _) => { sent++; return Task.CompletedTask; }, token: before.Token));
        Assert.Equal(0, sent);
        using var during = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PacedReplayDemo.PublishAsync(fixture.Output, 100,
            (_, _, _) => { sent++; return Task.CompletedTask; }, token: during.Token,
            delay: (_, token) => { during.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(1, sent); // Only the metadata header was sent.
    }

    [Fact]
    public async Task CancelledExportAndNestedOutputCannotWriteAcceptedInputs()
    {
        using var fixture = await DemoFixture.CreateAsync(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExportAsync(cancellation.Token));
        Assert.False(Directory.Exists(fixture.Output));
        await Assert.ThrowsAsync<RecordingValidationException>(() => PacedReplayDemo.ExportAsync(new(Path.Combine(fixture.Source, "nested"), fixture.Source, fixture.Fixture.Manifest.DatasetId)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Source, "nested")));
    }

    [Fact]
    public async Task NonGeneratedPositionsFailInsteadOfRelabelingRealMovement()
    {
        using var fixture = await DemoFixture.CreateAsync(generated: false);
        await Assert.ThrowsAsync<RecordingValidationException>(() => fixture.ExportAsync()); Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void ExplicitPacedOptionsKeepDefaultPublishingSeparate()
    {
        var export = PacedReplayExportOptions.Parse(["--export-paced-demo", "out", "--source-export", "source"]);
        Assert.Equal("brentford-existing-illustration", export.SourceDataset);
        var publish = PacedReplayPublishOptions.Parse(["--publish-paced", "out", "--delay-ms", "2500", "--dry-run"]);
        Assert.Equal(2500, publish.DelayMs); Assert.True(publish.DryRun);
        Assert.Equal(2000, PacedReplayPublishOptions.Parse(["--publish-paced", "out"]).DelayMs);
        Assert.Throws<ArgumentException>(() => PacedReplayExportOptions.Parse(["--export-paced-demo", "out"]));
        Assert.Throws<ArgumentException>(() => PacedReplayPublishOptions.Parse(["--publish", "out"]));
        Assert.Throws<ArgumentException>(() => PacedReplayPublishOptions.Parse(["--publish-paced", "--dry-run", "out"]));
    }

    [Theory]
    [InlineData("--delay-ms", "0")] [InlineData("--delay-ms", "2501")] [InlineData("--delay-ms", "-1")]
    [InlineData("--delay-ms", "NaN")] [InlineData("--unknown", "value")] [InlineData("--delay-ms", "--dry-run")]
    public void InvalidPacedOptionsFailBeforeAnyPublish(string option, string value)
        => Assert.Throws<ArgumentException>(() => PacedReplayPublishOptions.Parse(["--publish-paced", "out", option, value]));

    private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + ReplayBatchCodec.Hash(File.ReadAllBytes(path))).ToArray();
    private static async Task<FootballBatch[]> Batches(string directory, FootballExport export)
    {
        var result = new List<FootballBatch>();
        foreach (var chunk in export.Chunks) result.Add(FootballTransport.Decode(await ReplayExport.ReadChunkAsync(directory, chunk), chunk.ContentType));
        return result.ToArray();
    }
    private sealed class DemoFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "paced-demo-test-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "source");
        public string Output => Path.Combine(_root, "demo");
        public TestFixture Fixture { get; } = GeneratedIllustrationStatisticsTests.GeneratedFixture();
        public Task<ValidatedPacedDemo> ExportAsync(CancellationToken token = default) => PacedReplayDemo.ExportAsync(new(Output, Source, Fixture.Manifest.DatasetId), token);
        public static async Task<DemoFixture> CreateAsync(bool generated = true)
        {
            var fixture = new DemoFixture(); Directory.CreateDirectory(fixture.Source); var model = fixture.Fixture;
            var context = GeneratedIllustrationStatisticsTests.GeneratedContext(model);
            context[1] = context[1] with { Evidence = [context[1].Evidence[0] with { Origin = "real", Method = "reported", ClaimStatus = "reported" }] };
            context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Segments = [new("period-one", 0, 400000, 0), new("period-two", 400000, 800000, 0)] } };
            var records = context.ToList();
            for (long time = 0; time <= 305000; time += 5000)
            {
                var frame = Frame(model, time); var payload = (TrackingPayload)frame.Payload;
                records.Add(generated ? frame with { Evidence = [frame.Evidence[0] with { Origin = "synthetic", Method = "generated", ClaimStatus = "authored" }],
                    Payload = payload with { Players = payload.Players.Select(p => p with { Method = "generated" }).ToArray(), Ball = payload.Ball with { Method = "generated" } } } : frame);
            }
            records.AddRange([
                GeneratedIllustrationStatisticsTests.Action(model, "early-pass", "pass", 1000, 4000),
                GeneratedIllustrationStatisticsTests.Action(model, "later-pass", "pass", 5000, 9000),
                GeneratedIllustrationStatisticsTests.Action(model, "control", "possession", 0, 100000, outcome: "controlled"),
                GeneratedIllustrationStatisticsTests.Action(model, "crosses-cutoff", "pass", 299000, 300001),
                GeneratedIllustrationStatisticsTests.Action(model, "future-pass", "pass", 300000, 301000),
                GeneratedIllustrationStatisticsTests.Action(model, "second-half-pass", "pass", 1000, 4000, period: "period-two"),
                Goal(model) with { Evidence = [Goal(model).Evidence[0] with { Origin = "real" }] }]);
            var batches = FootballCodec.Batches(model.Manifest, model.Stream, records); var chunks = new List<ExportChunk>();
            foreach (var batch in batches)
            {
                var transport = FootballTransport.Encode(batch); var file = $"{batch.Stream.StreamId}--{batch.Sequence:D12}.batch";
                await File.WriteAllBytesAsync(Path.Combine(fixture.Source, file), transport.Bytes);
                chunks.Add(new(file, transport.ContentType, ReplayBatchCodec.Hash(transport.Bytes), transport.Bytes.Length, batch.Stream, batch.Sequence, batch.Digest));
            }
            byte[] archive = [123, 125]; Directory.CreateDirectory(Path.Combine(fixture.Source, "sources"));
            await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "sources", "test.json"), archive);
            var export = new FootballExport(2, [new(model.Stream, batches.Count, records.Count(r => r.Payload is TrackingPayload), 1, 0, 6)],
                chunks.ToArray(), [new("sources/test.json", "test/source.json", ReplayBatchCodec.Hash(archive), archive.Length)]);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Source, "export.json"), JsonSerializer.SerializeToUtf8Bytes(export, FootballCodec.Json));
            return fixture;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
