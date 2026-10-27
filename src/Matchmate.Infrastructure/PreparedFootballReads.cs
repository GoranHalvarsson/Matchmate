using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public sealed partial class AzureFootballStore
{
    public const int ReadViewVersion = 1, MaxSeekPages = 4096, ReadCacheCapacity = 8;
    private readonly Dictionary<string, FootballDatasetIndex> _readerFences = [];
    private readonly Dictionary<string, (FootballReadReference Reference, object Document)> _readCache = [];
    private readonly Queue<string> _readOrder = [];
    private readonly Dictionary<string, PreparedReadBuilder> _readBuilders = [];
    public long ReadViewReadBytes { get; private set; }
    public int ReadViewReadCount { get; private set; }
    public int LastQueryHeaderCount { get; private set; }
    public int ReadViewWriteCount { get; private set; }
    public long ReadViewWriteBytes { get; private set; }
    public int RecoveredRecordCount => _ledger.CanonicalRecordCount;
    public static string SourceFenceHash(FootballDatasetIndex index) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(index with { DerivedReadView = null }, FootballCodec.Json));
    public static string ReadViewPath(string dataset, string hash) => $"football/v04-full/{dataset}/read-v1/{hash}.json.gz";
    private static void ValidateReadReference(string dataset, FootballReadReference reference)
    {
        if (reference is null || !HashValid(reference.Hash) || reference.Path != ReadViewPath(dataset, reference.Hash)
            || reference.Bytes is < 1 or > MaxPageBytes) throw new RecordingValidationException("Invalid prepared read reference.");
    }
    private async Task<FootballReadReference> PutReadAsync<T>(string dataset, T document, CancellationToken token)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(document, FootballCodec.Json);
        if (raw.Length > MaxExpandedPageBytes) throw new RecordingValidationException("Prepared read page expanded capacity exceeded.");
        var bytes = Compress(raw); var hash = ReplayBatchCodec.Hash(bytes);
        if (bytes.Length > MaxPageBytes) throw new RecordingValidationException("Prepared read page capacity exceeded.");
        var reference = new FootballReadReference(ReadViewPath(dataset, hash), hash, bytes.Length);
        await blobs.PutImmutableAsync(reference.Path, bytes, token); ReadViewWriteCount++; ReadViewWriteBytes += bytes.Length; return reference;
    }
    private async Task<T> LoadReadAsync<T>(string dataset, FootballReadReference reference, CancellationToken token) where T : class
    {
        ValidateReadReference(dataset, reference);
        if (_readCache.TryGetValue(reference.Path, out var cached))
        {
            if (cached.Reference != reference) throw new RecordingValidationException("Cached prepared reference length/hash mismatch.");
            return cached.Document as T ?? throw new RecordingValidationException("Prepared document type mismatch.");
        }
        var bytes = await blobs.ReadAsync(reference.Path, MaxPageBytes, token) ?? throw new RecordingValidationException("Prepared read page missing; run index preparation outside viewer requests.");
        ReadViewReadCount++; ReadViewReadBytes += bytes.Length;
        if (bytes.Length != reference.Bytes || ReplayBatchCodec.Hash(bytes) != reference.Hash) throw new RecordingValidationException("Prepared read page hash mismatch.");
        var value = Deserialize<T>(FootballTransport.Expand(bytes, MaxExpandedPageBytes));
        _readCache.Add(reference.Path, (reference, value)); _readOrder.Enqueue(reference.Path);
        while (_readOrder.Count > ReadCacheCapacity) _readCache.Remove(_readOrder.Dequeue());
        return value;
    }
    private async Task<FootballReadCatalog> CatalogDocumentAsync(VersionedFootballIndex root, CancellationToken token)
    {
        ValidateIndex(root); var index = root.Value;
        var reference = index.DerivedReadView ?? throw new RecordingValidationException("Prepared read index unavailable; run index preparation outside viewer requests.");
        var catalog = await LoadReadAsync<FootballReadCatalog>(index.DatasetId, reference, token);
        if (catalog.Version != ReadViewVersion || catalog.DatasetId != index.DatasetId || catalog.SourceFenceHash != SourceFenceHash(index)
            || catalog.Streams is null || catalog.Streams.Length != index.Streams.Length) throw new RecordingValidationException("Prepared catalog source fence mismatch.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in catalog.Streams)
        {
            var dataset = stream?.Dataset;
            if (dataset?.Manifest is null || dataset.Stream is null || !seen.Add(dataset.Stream.StreamId)) throw new RecordingValidationException("Invalid prepared catalog descriptor.");
            var head = index.Streams.SingleOrDefault(h => h.Stream.StreamId == dataset.Stream.StreamId);
            if (head is null || dataset.Stream != head.Stream || dataset.HeadSequence != head.Head || dataset.Closed != head.Closed
                || ManifestHash(dataset.Manifest) != head.ManifestHash) throw new RecordingValidationException("Prepared stream binding mismatch.");
            FootballCodec.ValidateBinding(dataset.Manifest, dataset.Stream); ValidateReadReference(index.DatasetId, stream!.Index);
        }
        ObserveReadFence(index);
        return catalog;
    }
    private void ObserveReadFence(FootballDatasetIndex index)
    {
        if (_readerFences.TryGetValue(index.DatasetId, out var previous))
        {
            if (index.CommitCount < previous.CommitCount || index.PayloadBytes < previous.PayloadBytes || index.Pages.Length < previous.Pages.Length
                || previous.Streams.Any(old => !index.Streams.Any(now => now.Stream == old.Stream && now.ManifestHash == old.ManifestHash
                    && now.Head >= old.Head && (!old.Closed || now.Closed && now.Head == old.Head)))
                || index.CommitCount == previous.CommitCount && SourceFenceHash(index) != SourceFenceHash(previous))
                throw new RecordingValidationException("Observed prepared source fence cannot rewind or lose membership.");
            for (var i = 0; i < previous.Pages.Length; i++)
                if (previous.Pages[i].Count == EntriesPerPage ? previous.Pages[i] != index.Pages[i] : index.Pages[i].Count < previous.Pages[i].Count)
                    throw new RecordingValidationException("Observed immutable source page membership changed.");
        }
        _readerFences[index.DatasetId] = index;
    }
    private async Task<FootballDataset[]> ReadCatalogAsync(CancellationToken token)
    {
        var roots = await indexes.ListAsync(token);
        if (roots.Count > MaxDatasets || roots.Select(r => r.Value.DatasetId).Distinct().Count() != roots.Count) throw new RecordingValidationException("Invalid catalog membership.");
        if (_readerFences.Keys.Except(roots.Select(r => r.Value.DatasetId)).Any()) throw new RecordingValidationException("Observed dataset disappeared.");
        var result = new List<FootballDataset>();
        foreach (var root in roots) result.AddRange((await CatalogDocumentAsync(root, token)).Streams.Select(s => s.Dataset));
        return result.OrderBy(d => d.Stream.StreamId, StringComparer.Ordinal).ToArray();
    }
    private async Task<FootballView> QueryPreparedAsync(FootballQuery query, CancellationToken token)
    {
        if (query is null || !DatasetIdentity.IsValid(query.DatasetId) || !DatasetIdentity.IsValid(query.StreamId)) throw new FootballQueryException("Complete canonical query context is required.");
        var root = await indexes.ReadAsync(query.DatasetId, token);
        if (root is null) { if (_readerFences.ContainsKey(query.DatasetId)) throw new RecordingValidationException("Observed dataset disappeared."); throw new KeyNotFoundException("Unknown dataset."); }
        var catalog = await CatalogDocumentAsync(root, token);
        var descriptor = catalog.Streams.SingleOrDefault(s => s.Dataset.Stream.StreamId == query.StreamId) ?? throw new KeyNotFoundException("Unknown stream.");
        var dataset = descriptor.Dataset;
        var index = await LoadReadAsync<FootballReadIndex>(query.DatasetId, descriptor.Index, token);
        if (index.Version != ReadViewVersion || index.Stream != dataset.Stream || index.ManifestHash != ManifestHash(dataset.Manifest)
            || index.Head != dataset.HeadSequence || index.Closed != dataset.Closed || index.Seek is null || index.Seek.Length > MaxSeekPages
            || index.Seek.Any(s => s is null) || index.Seek.Select(s => (s.PeriodId, s.Minute)).Distinct().Count() != index.Seek.Length) throw new RecordingValidationException("Prepared seek index binding mismatch.");
        ValidateReadReference(query.DatasetId, index.Context);
        foreach (var seek in index.Seek)
        {
            if (seek.Minute < 0 || !dataset.Manifest.IdentityMappings.Any(m => m.CanonicalId == seek.PeriodId)) throw new RecordingValidationException("Invalid prepared seek bucket.");
            ValidateReadReference(query.DatasetId, seek.Page);
        }
        var groups = new List<FootballReadGroup>();
        async Task AddPage(FootballReadReference reference, string? period, long? minute)
        {
            var page = await LoadReadAsync<FootballReadPage>(query.DatasetId, reference, token);
            if (page.Version != ReadViewVersion || page.Stream != dataset.Stream || page.PeriodId != period || page.Minute != minute
                || page.Groups is null || page.Groups.Length > MaxBatchesPerDataset * FootballCodec.MaxRecords
                || page.Groups.Any(g => g?.Members is not { Length: > 0 } || g.Witnesses is null || g.Members.Any(m => m?.Record?.Header is null || m.Payload is null))
                || page.Groups.Select(g => g.RecordId).Distinct().Count() != page.Groups.Length) throw new RecordingValidationException("Prepared page identity mismatch.");
            if (period is not null && page.Groups.Any(g => !g.Members.Any(m => m.Record.Header.RecordType == "tracking_frame"
                    && m.Record.Header.Time?.PeriodId == period && m.Record.Header.Time.StartMs / 60_000 == minute))) throw new RecordingValidationException("Prepared bucket/group mismatch.");
            groups.AddRange(page.Groups);
        }
        await AddPage(index.Context, null, null);
        var bindings = catalog.Streams.Select(s => s.Dataset).ToArray();
        var context = FootballLedger.FromReadState(dataset, bindings, groups);
        var window = context.TrackingWindow(query);
        // SampleHoldMs is validated by the shared schedule contract at <=1000ms, so at most two minute buckets.
        var first = Math.Max(0, window.LocalMs - window.SampleHoldMs) / 60_000; var last = window.LocalMs / 60_000;
        if (last - first > 1) throw new RecordingValidationException("Prepared seek window exceeds source contract.");
        foreach (var seek in index.Seek.Where(s => s.PeriodId == window.PeriodId && s.Minute >= first && s.Minute <= last)) await AddPage(seek.Page, seek.PeriodId, seek.Minute);
        var sparse = FootballLedger.FromReadState(dataset, bindings, groups); LastQueryHeaderCount = sparse.CanonicalRecordCount;
        var memberships = groups.SelectMany(g => g.Members).GroupBy(m => m.Payload.BlobPath).ToDictionary(g => g.Key, g => g.First().Payload, StringComparer.Ordinal);
        return await sparse.QueryAsync(query, async (pointer, cancellation) =>
        {
            if (!memberships.TryGetValue(pointer.Path, out var reference)) throw new RecordingValidationException("Unbound sparse payload pointer.");
            if (!_payloads.TryGetValue(pointer.Path, out var accepted))
            {
                var bytes = await blobs.ReadAsync(pointer.Path, MaxArchivedBatchBytes, cancellation) ?? throw new RecordingValidationException("Committed payload missing.");
                PayloadReadBytes += bytes.Length; PayloadReadCount++;
                if (bytes.Length != reference.Bytes || ReplayBatchCodec.Hash(bytes) != reference.BlobHash) throw new RecordingValidationException("Payload hash mismatch.");
                accepted = DecodeAccepted(bytes); _payloads.Add(pointer.Path, accepted); _payloadOrder.Enqueue(pointer.Path);
                while (_payloadOrder.Count > PayloadCacheCapacity) _payloads.Remove(_payloadOrder.Dequeue());
            }
            if (accepted.Batch.Stream != dataset.Stream || accepted.Batch.Sequence != reference.Sequence || accepted.ReceivedAtUtc != reference.ReceivedAtUtc
                || accepted.Batch.Digest != reference.Digest || ManifestHash(accepted.Batch.Manifest) != index.ManifestHash || accepted.Batch.Digest != pointer.Digest
                || pointer.RecordIndex < 0 || pointer.RecordIndex >= accepted.Batch.Records.Length) throw new RecordingValidationException("Prepared payload membership mismatch.");
            var record = accepted.Batch.Records[pointer.RecordIndex];
            if (FootballCodec.ContentHash(record) != pointer.Fingerprint) throw new RecordingValidationException("Payload record fingerprint mismatch.");
            return record;
        }, token);
    }
    public async Task<FootballReadPreparation> PrepareReadViewsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var roots = await indexes.ListAsync(token); var published = 0; var writes = ReadViewWriteCount; var bytes = ReadViewWriteBytes;
            if (roots.Count > MaxDatasets || roots.Select(r => r.Value.DatasetId).Distinct().Count() != roots.Count) throw new RecordingValidationException("Invalid preparation dataset membership.");
            foreach (var initial in roots)
            {
                var done = false;
                for (var attempt = 0; attempt < CasAttempts && !done; attempt++)
                {
                    var current = await indexes.ReadAsync(initial.Value.DatasetId, token) ?? throw new RecordingValidationException("Dataset disappeared during preparation.");
                    ValidateIndex(current);
                    if (current.Value.DerivedReadView is not null) { await CatalogDocumentAsync(current, token); done = true; continue; }
                    await ApplyRootAsync(current, token);
                    var reference = await BuildReadViewAsync(current.Value, null, token);
                    var candidate = current.Value with { DerivedReadView = reference };
                    ValidateIndex(new(candidate, "candidate"));
                    if (await indexes.CompareExchangeAsync(candidate, current.ETag, token)) { published++; _roots[candidate.DatasetId] = new(candidate, "await-readback"); done = true; }
                    else _readBuilders.Remove(candidate.DatasetId);
                }
                if (!done) throw new IOException("Read-index preparation contention exhausted.");
            }
            return new(roots.Count, roots.Sum(r => r.Value.Streams.Length), published, ReadViewWriteCount - writes, ReadViewWriteBytes - bytes);
        }
        catch { _readBuilders.Clear(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<FootballReadReference> BuildReadViewAsync(FootballDatasetIndex candidate, FootballJournalEntry? addition, CancellationToken token)
    {
        if (!_readBuilders.TryGetValue(candidate.DatasetId, out var builder))
        {
            builder = new(candidate.DatasetId);
            foreach (var entry in _commits.Values.Where(e => e.Metadata.Stream.DatasetId == candidate.DatasetId)) builder.Add(entry);
            _readBuilders.Add(candidate.DatasetId, builder);
        }
        if (addition is not null) builder.Add(addition);
        return await builder.PublishAsync(candidate, PutReadAsync, token);
    }
}
