using Matchmate.Contracts;

namespace Matchmate.Core;

// Derived immutable documents. The journal remains the authoritative recovery and revision fence.
public sealed record FootballReadReference(string Path, string Hash, int Bytes);
public sealed record FootballReadCatalog(int Version, string DatasetId, string SourceFenceHash, FootballReadStream[] Streams);
public sealed record FootballReadStream(FootballDataset Dataset, FootballReadReference Index);
public sealed record FootballSeekPage(string PeriodId, long Minute, FootballReadReference Page);
public sealed record FootballReadIndex(int Version, FootballStream Stream, string ManifestHash, long Head, bool Closed,
    FootballReadReference Context, FootballSeekPage[] Seek);
public sealed record FootballReadMember(FootballStoredRecord Record, FootballCommitReference Payload);
public sealed record FootballReadWitness(string StreamId, FootballReadMember Member);
public sealed record FootballReadGroup(string RecordId, FootballReadMember[] Members, FootballReadWitness[] Witnesses);
public sealed record FootballReadPage(int Version, FootballStream Stream, string? PeriodId, long? Minute, FootballReadGroup[] Groups);
public sealed record FootballTrackingWindow(string PeriodId, long LocalMs, long SampleHoldMs);
public sealed record FootballReadPreparation(int Datasets, int Streams, int Published, int PageWriteCalls, long PageWriteBytes);
public interface IPreparedFootballStore
{
    // Explicit maintenance/ingest operation; never called by CatalogAsync or QueryAsync.
    Task<FootballReadPreparation> PrepareReadViewsAsync(CancellationToken token);
}
