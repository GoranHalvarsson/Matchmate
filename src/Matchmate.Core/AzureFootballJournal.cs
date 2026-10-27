using Matchmate.Contracts;
using System.Text.Json.Serialization;
namespace Matchmate.Core;

// Small Cosmos fence references immutable compact metadata pages, never tracking arrays.
public sealed record FootballCommitReference(long Sequence, string Digest, string BlobPath, string BlobHash, int Bytes, DateTimeOffset ReceivedAtUtc);
public sealed record FootballJournalEntry(FootballBatchMetadata Metadata, FootballCommitReference Payload);
public sealed record FootballMetadataPage(string DatasetId, int Number, FootballJournalEntry[] Entries);
public sealed record FootballPageReference(int Number, string Path, string Hash, int Bytes, int Count);
public sealed record FootballStreamHead(FootballStream Stream, string ManifestHash, long Head, bool Closed);
public sealed record FootballDatasetIndex(string Id, string Discriminator, string DatasetId, FootballStreamHead[] Streams,
    FootballPageReference[] Pages, int CommitCount, long PayloadBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FootballReadReference? DerivedReadView = null);
public sealed record VersionedFootballIndex(FootballDatasetIndex Value, string ETag);
public interface IFootballIndexStore
{
    Task<VersionedFootballIndex?> ReadAsync(string datasetId, CancellationToken token);
    Task<IReadOnlyList<VersionedFootballIndex>> ListAsync(CancellationToken token);
    Task<bool> CompareExchangeAsync(FootballDatasetIndex candidate, string? expectedETag, CancellationToken token);
}
