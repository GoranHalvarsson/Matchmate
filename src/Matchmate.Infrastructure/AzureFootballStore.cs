using System.IO.Compression;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

// Shared local/Azure journal. Coordinates live in bounded immutable chunks; only compact headers are indexed in memory.
public sealed partial class AzureFootballStore(IReplayBlobStore blobs, IFootballIndexStore indexes, Func<DateTimeOffset>? utcNow = null) : IFootballStore, IPreparedFootballStore
{
    public const string Discriminator = "football-paged-index-v2";
    public const int MaxDatasets = 8, MaxStreamsPerDataset = 8, EntriesPerPage = 32, MaxPages = 256,
        MaxBatchesPerDataset = EntriesPerPage * MaxPages, MaxIndexBytes = 128_000, MaxArchivedBatchBytes = 514_000,
        MaxPageBytes = 512_000, MaxExpandedPageBytes = 4_000_000, PayloadCacheCapacity = 8, CasAttempts = 5;
    public const long MaxDatasetBytes = 1_000_000_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FootballLedger _ledger = new(retainTracking: false);
    private readonly Dictionary<string, VersionedFootballIndex> _roots = [];
    private readonly Dictionary<string, FootballJournalEntry> _commits = [];
    private readonly Dictionary<string, string> _entryHashes = [];
    private readonly Dictionary<string, FootballMetadataPage> _tails = [];
    private readonly Dictionary<string, string[]> _pageKeys = [];
    private readonly Dictionary<string, AcceptedFootballBatch> _payloads = [];
    private readonly Queue<string> _payloadOrder = [];
    public long PayloadReadBytes { get; private set; }
    public int PayloadReadCount { get; private set; }
    public long MetadataReadBytes { get; private set; }
    public int RetainedTrackingCount => _ledger.RetainedTrackingCount;
    public static string IndexId(string datasetId) => "football-paged-" + datasetId;
    public static string ManifestHash(FootballManifest manifest) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(manifest, FootballCodec.Json));
    public static string BlobPath(FootballStream stream, long sequence, string digest) =>
        $"football/v04-full/{stream.DatasetId}/{stream.DatasetVersion}/{stream.StreamId}/{sequence:D12}-{digest}.json.gz";
    private static string EntryKey(FootballStream stream, long sequence) => stream.DatasetId + "/" + stream.StreamId + "/" + sequence;
    private static string EntryHash(FootballJournalEntry entry) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(entry, FootballCodec.Json));
    public static string PagePath(string datasetId, int number, string hash) => $"football/v04-full/{datasetId}/metadata/{number:D6}-{hash}.json.gz";

    public async Task<bool> AppendAsync(FootballBatch batch, CancellationToken token)
    {
        FootballCodec.Validate(batch); await _gate.WaitAsync(token);
        try
        {
            for (var attempt = 0; attempt < CasAttempts; attempt++)
            {
                await RefreshAsync(token);
                var current = await indexes.ReadAsync(batch.Stream.DatasetId, token);
                if (current is not null) await ApplyRootAsync(current, token);
                var key = EntryKey(batch.Stream, batch.Sequence);
                if (_commits.TryGetValue(key, out var existing))
                {
                    if (existing.Metadata.Stream != batch.Stream || existing.Metadata.Digest != batch.Digest || ManifestHash(existing.Metadata.Manifest) != ManifestHash(batch.Manifest)) throw new RecordingValidationException("Committed sequence binding/content conflict.");
                    return false;
                }
                // Validate against the incremental dataset revision fence before creating any durable candidate.
                _ledger.Prepare(new AcceptedFootballBatch(batch, (utcNow ?? (() => DateTimeOffset.UtcNow))()), BlobPath(batch.Stream, batch.Sequence, batch.Digest));
                var archived = await ArchiveAsync(batch, token);
                var prepared = _ledger.Prepare(archived.Accepted, archived.Reference.BlobPath);
                var entry = new FootballJournalEntry(prepared.Metadata, archived.Reference);
                var old = current?.Value;
                var refs = (old?.Pages ?? []).ToList();
                FootballMetadataPage page;
                if (refs.Count > 0 && refs[^1].Count < EntriesPerPage)
                {
                    var tail = await LoadPageAsync(batch.Stream.DatasetId, refs[^1], token);
                    page = tail with { Entries = tail.Entries.Append(entry).ToArray() }; refs.RemoveAt(refs.Count - 1);
                }
                else page = new(batch.Stream.DatasetId, refs.Count, [entry]);
                var raw = JsonSerializer.SerializeToUtf8Bytes(page, FootballCodec.Json);
                if (raw.Length > MaxExpandedPageBytes) throw new RecordingValidationException("Compact metadata page capacity exceeded.");
                var compressed = Compress(raw); var hash = ReplayBatchCodec.Hash(compressed);
                if (compressed.Length > MaxPageBytes) throw new RecordingValidationException("Compressed metadata page capacity exceeded.");
                var pageRef = new FootballPageReference(page.Number, PagePath(batch.Stream.DatasetId, page.Number, hash), hash, compressed.Length, page.Entries.Length);
                refs.Add(pageRef);
                var head = new FootballStreamHead(batch.Stream, ManifestHash(batch.Manifest), batch.Sequence, batch.CloseStream);
                var heads = (old?.Streams ?? []).Where(s => s.Stream.StreamId != batch.Stream.StreamId).Append(head).OrderBy(s => s.Stream.StreamId, StringComparer.Ordinal).ToArray();
                var candidate = new FootballDatasetIndex(IndexId(batch.Stream.DatasetId), Discriminator, batch.Stream.DatasetId, heads, refs.ToArray(), (old?.CommitCount ?? 0) + 1, (old?.PayloadBytes ?? 0) + archived.Reference.Bytes);
                ValidateIndex(new(candidate, "candidate"));
                await blobs.PutImmutableAsync(pageRef.Path, compressed, token);
                candidate = candidate with { DerivedReadView = await BuildReadViewAsync(candidate, entry, token) };
                ValidateIndex(new(candidate, "candidate"));
                if (await indexes.CompareExchangeAsync(candidate, current?.ETag, token))
                {
                    _ledger.Commit(prepared); Remember(entry); _tails[pageRef.Path] = page;
                    _pageKeys[pageRef.Path] = page.Entries.Select(e => EntryKey(e.Metadata.Stream, e.Metadata.Sequence)).ToArray(); TrimTails(candidate);
                    _roots[batch.Stream.DatasetId] = new(candidate, "await-readback"); return true;
                }
                _readBuilders.Remove(batch.Stream.DatasetId);
                // No mutation of the in-memory ledger until CAS. Refresh committed pages and revalidate on 412.
            }
            throw new IOException("Dataset fence contention exhausted; retry without checkpoint.");
        }
        catch { _readBuilders.Clear(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<FootballLedger> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token); try { await RefreshAsync(token); return _ledger; } finally { _gate.Release(); }
    }
    public async Task<FootballDataset[]> CatalogAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token); try { return await ReadCatalogAsync(token); } finally { _gate.Release(); }
    }
    public async Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await QueryPreparedAsync(query, token); }
        finally { _gate.Release(); }
    }
    private async Task RefreshAsync(CancellationToken token)
    {
        var roots = await indexes.ListAsync(token);
        if (roots.Count > MaxDatasets || roots.Select(r => r.Value.DatasetId).Distinct().Count() != roots.Count) throw new RecordingValidationException("Dataset count/identity capacity exceeded.");
        if (_roots.Keys.Except(roots.Select(r => r.Value.DatasetId)).Any()) throw new RecordingValidationException("Committed dataset membership disappeared.");
        foreach (var root in roots.OrderBy(r => r.Value.DatasetId, StringComparer.Ordinal)) await ApplyRootAsync(root, token);
    }
    private async Task ApplyRootAsync(VersionedFootballIndex root, CancellationToken token)
    {
        ValidateIndex(root); var index = root.Value;
        if (_roots.TryGetValue(index.DatasetId, out var previous) && previous.ETag == root.ETag) return;
        if (previous is not null && (index.CommitCount < previous.Value.CommitCount || index.PayloadBytes < previous.Value.PayloadBytes)) throw new RecordingValidationException("Committed membership cannot rewind.");
        var seen = new HashSet<string>(); long bytes = 0;
        foreach (var reference in index.Pages)
        {
            // Immutable unchanged pages need neither Blob reads nor record revalidation.
            if (_pageKeys.TryGetValue(reference.Path, out var knownKeys))
            {
                foreach (var key in knownKeys) { if (!seen.Add(key)) throw new RecordingValidationException("Duplicate page membership."); bytes += _commits[key].Payload.Bytes; }
                continue;
            }
            var page = await LoadPageAsync(index.DatasetId, reference, token);
            foreach (var entry in page.Entries)
            {
                ValidateEntry(index.DatasetId, entry);
                var key = EntryKey(entry.Metadata.Stream, entry.Metadata.Sequence);
                if (!seen.Add(key)) throw new RecordingValidationException("Duplicate page membership.");
                bytes += entry.Payload.Bytes;
                if (_entryHashes.TryGetValue(key, out var priorHash))
                {
                    if (priorHash != EntryHash(entry)) throw new RecordingValidationException("Committed metadata changed.");
                }
                else { _ledger.AcceptMetadata(entry.Metadata); Remember(entry); }
            }
            _pageKeys[reference.Path] = page.Entries.Select(e => EntryKey(e.Metadata.Stream, e.Metadata.Sequence)).ToArray();
        }
        if (seen.Count != index.CommitCount || bytes != index.PayloadBytes || _commits.Any(c => c.Value.Metadata.Stream.DatasetId == index.DatasetId && !seen.Contains(c.Key))) throw new RecordingValidationException("Root does not bind complete committed membership.");
        foreach (var head in index.Streams)
        {
            var entries = seen.Select(k => _commits[k]).Where(e => e.Metadata.Stream.StreamId == head.Stream.StreamId).OrderBy(e => e.Metadata.Sequence).ToArray();
            if (entries.Length != head.Head || entries[^1].Metadata.CloseStream != head.Closed || entries.Any(e => e.Metadata.Stream != head.Stream || ManifestHash(e.Metadata.Manifest) != head.ManifestHash)) throw new RecordingValidationException("Root stream/head binding mismatch.");
        }
        if (seen.Select(k => _commits[k].Metadata.Stream.StreamId).Distinct().Count() != index.Streams.Length) throw new RecordingValidationException("Unbound metadata stream.");
        _roots[index.DatasetId] = root; TrimTails(index);
    }
    private void Remember(FootballJournalEntry entry)
    {
        var key = EntryKey(entry.Metadata.Stream, entry.Metadata.Sequence); _commits[key] = entry; _entryHashes[key] = EntryHash(entry);
        if (_readBuilders.TryGetValue(entry.Metadata.Stream.DatasetId, out var builder)) builder.Add(entry);
    }
    private void TrimTails(FootballDatasetIndex index)
    {
        var keep = index.Pages.LastOrDefault()?.Path;
        foreach (var path in _tails.Keys.Where(p => p.StartsWith($"football/v04-full/{index.DatasetId}/metadata/", StringComparison.Ordinal) && p != keep).ToArray()) _tails.Remove(path);
        var current = index.Pages.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var path in _pageKeys.Keys.Where(p => p.StartsWith($"football/v04-full/{index.DatasetId}/metadata/", StringComparison.Ordinal) && !current.Contains(p)).ToArray()) _pageKeys.Remove(path);
    }
    private async Task<FootballMetadataPage> LoadPageAsync(string dataset, FootballPageReference reference, CancellationToken token)
    {
        if (_tails.TryGetValue(reference.Path, out var cached)) return cached;
        var bytes = await blobs.ReadAsync(reference.Path, MaxPageBytes, token) ?? throw new RecordingValidationException("Committed metadata page missing.");
        MetadataReadBytes += bytes.Length;
        if (bytes.Length != reference.Bytes || ReplayBatchCodec.Hash(bytes) != reference.Hash) throw new RecordingValidationException("Metadata page hash mismatch.");
        var page = Deserialize<FootballMetadataPage>(FootballTransport.Expand(bytes, MaxExpandedPageBytes));
        if (page.DatasetId != dataset || page.Number != reference.Number || page.Entries is null || page.Entries.Length != reference.Count) throw new RecordingValidationException("Metadata page identity/count mismatch.");
        _tails[reference.Path] = page; return page;
    }
    private async Task<(AcceptedFootballBatch Accepted, FootballCommitReference Reference)> ArchiveAsync(FootballBatch batch, CancellationToken token)
    {
        var path = BlobPath(batch.Stream, batch.Sequence, batch.Digest);
        var bytes = await blobs.ReadAsync(path, MaxArchivedBatchBytes, token);
        if (bytes is null)
        {
            var accepted = new AcceptedFootballBatch(batch, (utcNow ?? (() => DateTimeOffset.UtcNow))());
            var proposed = Compress(JsonSerializer.SerializeToUtf8Bytes(accepted, FootballCodec.Json));
            if (proposed.Length > MaxArchivedBatchBytes) throw new RecordingValidationException("Archived batch capacity exceeded.");
            try { await blobs.PutImmutableAsync(path, proposed, token); bytes = proposed; }
            catch (RecordingValidationException) { bytes = await blobs.ReadAsync(path, MaxArchivedBatchBytes, token); if (bytes is null) throw; }
        }
        var stored = DecodeAccepted(bytes);
        if (stored.Batch.Stream != batch.Stream || stored.Batch.Sequence != batch.Sequence || stored.Batch.Digest != batch.Digest || ManifestHash(stored.Batch.Manifest) != ManifestHash(batch.Manifest)) throw new RecordingValidationException("Immutable batch binding conflict.");
        return (stored, new(batch.Sequence, batch.Digest, path, ReplayBatchCodec.Hash(bytes), bytes.Length, stored.ReceivedAtUtc));
    }
    private async Task<FootballRecord> ResolveAsync(FootballPayloadPointer pointer, CancellationToken token)
    {
        if (!_payloads.TryGetValue(pointer.Path, out var accepted))
        {
            var entry = _commits.Values.FirstOrDefault(c => c.Payload.BlobPath == pointer.Path) ?? throw new RecordingValidationException("Uncommitted payload is not queryable.");
            var reference = entry.Payload;
            var bytes = await blobs.ReadAsync(reference.BlobPath, MaxArchivedBatchBytes, token) ?? throw new RecordingValidationException("Committed payload missing.");
            PayloadReadBytes += bytes.Length; PayloadReadCount++;
            if (bytes.Length != reference.Bytes || ReplayBatchCodec.Hash(bytes) != reference.BlobHash) throw new RecordingValidationException("Payload hash mismatch.");
            accepted = DecodeAccepted(bytes);
            if (accepted.ReceivedAtUtc != reference.ReceivedAtUtc || accepted.Batch.Digest != reference.Digest || accepted.Batch.Stream != entry.Metadata.Stream || accepted.Batch.Sequence != reference.Sequence) throw new RecordingValidationException("Payload membership mismatch.");
            _payloads.Add(pointer.Path, accepted); _payloadOrder.Enqueue(pointer.Path);
            while (_payloadOrder.Count > PayloadCacheCapacity) _payloads.Remove(_payloadOrder.Dequeue());
        }
        if (accepted.Batch.Digest != pointer.Digest || pointer.RecordIndex < 0 || pointer.RecordIndex >= accepted.Batch.Records.Length) throw new RecordingValidationException("Payload record pointer mismatch.");
        var record = accepted.Batch.Records[pointer.RecordIndex];
        if (FootballCodec.ContentHash(record) != pointer.Fingerprint) throw new RecordingValidationException("Payload record fingerprint mismatch.");
        return record;
    }
    private static AcceptedFootballBatch DecodeAccepted(byte[] bytes)
    {
        var accepted = Deserialize<AcceptedFootballBatch>(FootballTransport.Expand(bytes, MaxArchivedBatchBytes)); FootballCodec.Validate(accepted.Batch);
        if (accepted.ReceivedAtUtc < DateTimeOffset.UnixEpoch) throw new RecordingValidationException("Invalid acceptance time."); return accepted;
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, FootballCodec.Json) ?? throw new RecordingValidationException("Missing journal document."); }
        catch (JsonException) { throw new RecordingValidationException("Malformed journal document."); }
    }
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(bytes); return output.ToArray();
    }
    private static void ValidateEntry(string dataset, FootballJournalEntry entry)
    {
        var meta = entry?.Metadata; var reference = entry?.Payload;
        if (meta?.Stream is null || meta.Manifest is null || reference is null || meta.Stream.DatasetId != dataset || meta.Sequence != reference.Sequence || meta.Digest != reference.Digest || meta.PayloadPath != reference.BlobPath
            || meta.ReceivedAtUtc != reference.ReceivedAtUtc || reference.BlobPath != BlobPath(meta.Stream, meta.Sequence, meta.Digest) || !HashValid(reference.BlobHash) || reference.Bytes is < 1 or > MaxArchivedBatchBytes) throw new RecordingValidationException("Invalid immutable payload membership.");
    }
    public static void ValidateIndex(VersionedFootballIndex root)
    {
        var index = root?.Value;
        if (index is null || index.Discriminator != Discriminator || !DatasetIdentity.IsValid(index.DatasetId) || index.Id != IndexId(index.DatasetId) || string.IsNullOrWhiteSpace(root!.ETag)
            || index.Streams is not { Length: > 0 and <= MaxStreamsPerDataset } || index.Pages is not { Length: > 0 and <= MaxPages } || index.CommitCount is < 1 or > MaxBatchesPerDataset || index.PayloadBytes is < 1 or > MaxDatasetBytes
            || index.Streams.Any(s => s?.Stream is null || s.Head is < 1 or > MaxBatchesPerDataset)
            || index.Pages.Any(p => p is null || p.Count is < 1 or > EntriesPerPage)) throw new RecordingValidationException("Invalid dataset fence/capacity.");
        if (index.Streams.Select(s => s.Stream.StreamId).Distinct().Count() != index.Streams.Length || index.Streams.Sum(s => s.Head) != index.CommitCount || index.Pages.Sum(p => p.Count) != index.CommitCount || JsonSerializer.SerializeToUtf8Bytes(index, FootballCodec.Json).Length > MaxIndexBytes) throw new RecordingValidationException("Fence count/capacity mismatch.");
        foreach (var head in index.Streams)
            if (head.Stream.DatasetId != index.DatasetId || !DatasetIdentity.IsValid(head.Stream.StreamId) || !DatasetIdentity.IsValid(head.Stream.DatasetVersion) || !DatasetIdentity.IsValid(head.Stream.FixtureId) || head.Stream.DeliveryMode is not ("live" or "replay") || !HashValid(head.ManifestHash) || head.Head < 1) throw new RecordingValidationException("Invalid stream head binding.");
        if (index.DerivedReadView is { } read) ValidateReadReference(index.DatasetId, read);
        for (var number = 0; number < index.Pages.Length; number++)
        {
            var page = index.Pages[number];
            if (page.Number != number || !HashValid(page.Hash) || page.Path != PagePath(index.DatasetId, number, page.Hash) || page.Bytes is < 1 or > MaxPageBytes || page.Count is < 1 or > EntriesPerPage || number < index.Pages.Length - 1 && page.Count != EntriesPerPage) throw new RecordingValidationException("Invalid metadata page reference.");
        }
    }
    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
