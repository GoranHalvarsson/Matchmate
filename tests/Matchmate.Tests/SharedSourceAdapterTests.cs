using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;

namespace Matchmate.Tests;

// Existing source artifacts remain local/unpublished; CI uses inline common-contract tests.
public sealed class SharedSourceAdapterTests
{
    [Fact]
    public async Task BothCompletedSourcesEncodeAndFeedOneOpenIncrementalLocalPipeline()
    {
        var root = LocalPaths.FindRoot();
        var australian = await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var brentford = await new BrentfordFootballSource(Path.Combine(root, ".local", "shared-inputs", "brentford-slice.json")).ReadAsync(default);
        Assert.Equal(67, australian.SelectMany(batch => batch.Records).Count(record => record.Payload is TrackingPayload));
        Assert.Contains("unsampled cursor is not evidence of a source gap", australian[0].Manifest.Normalization);
        using var directory = new TestDirectory(); var store = new LocalFootballStore(directory.Path);
        foreach (var batch in australian.Concat(brentford))
        {
            var encoded = FootballCodec.Encode(batch); Assert.InRange(encoded.Length, 1, FootballCodec.MaxBatchBytes);
            await store.AppendAsync(FootballCodec.Decode(encoded), default);
            if (batch.Sequence == 1)
            {
                var open = (await new LocalFootballStore(directory.Path).ReadAsync(default)).Catalog().Single(d => d.Stream.StreamId == batch.Stream.StreamId);
                Assert.False(open.Closed); Assert.NotNull(open.Fixture); Assert.NotNull(open.Schedule);
            }
        }
        var ledger = await new LocalFootballStore(directory.Path).ReadAsync(default); Assert.Equal(2, ledger.Catalog().Length);
        var aus = ledger.Catalog().Single(d => d.Manifest.DatasetId == australian[0].Manifest.DatasetId);
        var brent = ledger.Catalog().Single(d => d.Manifest.DatasetId == brentford[0].Manifest.DatasetId);
        FootballView At(FootballDataset dataset, long cursor) => ledger.Query(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId, cursor, "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision));
        var original = At(aus, 60000); var player = original.Frame!.Players.Single(p => p.SubjectId == "person-skillcorner-50999");
        Assert.Equal(6.56, player.XM!.Value, 7); Assert.Equal(36.45, player.YM!.Value, 7); Assert.Equal("estimated", player.Method); Assert.False(player.OriginalDetected);
        Assert.Equal(50.07, original.Frame.Ball.XM!.Value, 7); Assert.Equal(57.03, original.Frame.Ball.YM!.Value, 7);
        Assert.True(At(aus, 175900).TrackingGap); Assert.True(At(aus, 198800).TrackingGap); Assert.Null(At(aus, 100000).Frame);
        Assert.Empty(At(aus, 61800).Possessions); Assert.Single(At(aus, 65200).Possessions); Assert.Equal(5, At(aus, 167700).Possessions.Length);
        var half = At(brent, 2820000); Assert.Equal("period-pl-2645236-second", half.PeriodId); Assert.Equal(0, half.LocalMs);
        Assert.Equal(40, half.Dataset.People.Length); Assert.Equal(22, half.Frame!.ActiveSubjectIds.Length); Assert.False(half.TrackingGap);
        Assert.All(half.Frame.Players.Append(half.Frame.Ball), observation => { Assert.Equal("generated", observation.Method); Assert.Null(observation.ZM); });
        Assert.Empty(At(brent, 3779999).Events); Assert.Equal("goal-1", Assert.Single(At(brent, 3780000).Events).RecordId);
        var goal = Assert.Single(At(brent, 3780000).Events); Assert.Equal(900000, goal.FootballTime!.StartMs); Assert.Equal(960000, goal.FootballTime.EndMs); Assert.False(goal.FootballTime.EndInclusive);
        Assert.Equal("reported_minute", goal.FootballTime.Precision); Assert.Equal("real", goal.Evidence[0].Origin); Assert.Equal("reported", goal.Evidence[0].Method);
        Assert.DoesNotContain(At(brent, 4259999).Events, r => r.RecordId == "sub-5"); Assert.Contains(At(brent, 4260000).Events, r => r.RecordId == "sub-5");
        var before = At(brent, 4259900).Frame!.Players.Single(p => p.SlotId == "brentford-1"); var after = At(brent, 4260000).Frame!.Players.Single(p => p.SlotId == "brentford-1");
        Assert.EndsWith("kayode", before.SubjectId); Assert.EndsWith("hickey", after.SubjectId); Assert.Equal(22, At(brent, 4260000).Frame!.ActiveSubjectIds.Length);
        Assert.True(At(brent, 500000).TrackingGap); Assert.Null(At(brent, 500000).Frame);
        Assert.Throws<FootballQueryException>(() => ledger.Query(original.Context with { KnowledgeMode = "known_then", KnowledgeAsOfUtc = DateTimeOffset.UtcNow }));
        var context = brentford.SelectMany(b => b.Records).Single(r => r.Payload is FixturePayload);
        Assert.Equal("generated", context.Evidence.Single(e => e.EvidenceId == context.FieldEvidence["/payload/pitch"][0]).Method);
        Assert.Contains(brent.Manifest.Sources, s => s.SourceId == "authored-config" && s.ArtifactHash!.Length == 64);
        Assert.Contains(brent.Manifest.IdentityMappings, m => m.CanonicalId.EndsWith("kayode") && m.OriginalValue.GetString() == "brentford:kayode");
    }
}
