using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

// Ingest-only mutable bookkeeping. Successful appends rewrite affected immutable pages, not the recording.
internal sealed class PreparedReadBuilder(string datasetId)
{
    private readonly record struct GroupKey(string Stream, string Record);
    private sealed class StreamState(FootballManifest manifest, FootballStream stream)
    {
        public readonly FootballManifest Manifest = manifest;
        public readonly FootballStream Stream = stream;
        public readonly Dictionary<string, SortedDictionary<int, FootballReadMember>> Groups = [];
        public readonly HashSet<string> NonTracking = [];
        public readonly Dictionary<(string Period, long Minute), HashSet<string>> Buckets = [];
        public readonly Dictionary<(string Period, long Minute), FootballReadReference> Pages = [];
        public readonly HashSet<(string Period, long Minute)> Dirty = [];
        public FootballReadReference? Context;
        public bool ContextDirty = true;
        public long Head;
        public bool Closed;
    }
    private readonly Dictionary<string, StreamState> _streams = [];
    private readonly Dictionary<(string Version, string Record), SortedDictionary<int, FootballReadWitness>> _canonical = [];
    private readonly Dictionary<(string Version, string Record), HashSet<GroupKey>> _dependents = [];
    public void Add(FootballJournalEntry entry)
    {
        var metadata = entry.Metadata;
        if (metadata.Stream.DatasetId != datasetId) throw new RecordingValidationException("Read builder dataset mismatch.");
        if (!_streams.TryGetValue(metadata.Stream.StreamId, out var stream)) _streams.Add(metadata.Stream.StreamId, stream = new(metadata.Manifest, metadata.Stream));
        if (metadata.Sequence <= stream.Head) return;
        if (metadata.Sequence != stream.Head + 1) throw new RecordingValidationException("Read builder sequence gap.");
        foreach (var record in metadata.Records)
        {
            var header = record.Header; var member = new FootballReadMember(record, entry.Payload);
            var logical = (header.DatasetVersion, header.RecordId);
            if (!_canonical.TryGetValue(logical, out var canonical)) _canonical.Add(logical, canonical = []);
            canonical.TryAdd(header.Revision, new(metadata.Stream.StreamId, member));
            if (!stream.Groups.TryGetValue(header.RecordId, out var revisions)) stream.Groups.Add(header.RecordId, revisions = []);
            revisions.TryAdd(header.Revision, member); // Keep earliest delivery membership, as FootballLedger.Commit does.
            if (header.RecordType == "tracking_frame")
            {
                var bucket = (header.Time!.PeriodId, header.Time.StartMs!.Value / 60_000);
                if (!stream.Buckets.TryGetValue(bucket, out var ids)) stream.Buckets.Add(bucket, ids = []);
                ids.Add(header.RecordId);
            }
            else { stream.NonTracking.Add(header.RecordId); stream.ContextDirty = true; }
            if (record.NonTracking?.Payload is PossessionPayload possession)
            {
                var dependency = (header.DatasetVersion, possession.DependencyRecordId);
                if (!_dependents.TryGetValue(dependency, out var dependents)) _dependents.Add(dependency, dependents = []);
                dependents.Add(new(metadata.Stream.StreamId, header.RecordId));
            }
            // A newly arrived canonical parent can resolve another stream's previously pending revision.
            foreach (var affected in _streams.Values.Where(s => s.Stream.DatasetVersion == header.DatasetVersion && s.Groups.ContainsKey(header.RecordId))) Mark(affected, header.RecordId);
            if (_dependents.TryGetValue(logical, out var waiting)) foreach (var affected in waiting) _streams[affected.Stream].ContextDirty = true;
        }
        stream.Head = metadata.Sequence; stream.Closed = metadata.CloseStream;
    }
    private static void Mark(StreamState stream, string id)
    {
        foreach (var member in stream.Groups[id].Values)
        {
            if (member.Record.Header.RecordType == "tracking_frame") stream.Dirty.Add((member.Record.Header.Time!.PeriodId, member.Record.Header.Time.StartMs!.Value / 60_000));
            else stream.ContextDirty = true;
        }
    }
    private FootballReadGroup Group(StreamState stream, string id)
    {
        var members = stream.Groups[id].Values.ToArray(); var witnesses = new Dictionary<(string, int), FootballReadWitness>();
        var included = members.Select(m => (m.Record.Header.RecordId, m.Record.Header.Revision)).ToHashSet();
        void Parents(FootballRecordHeader header)
        {
            var parent = header.SupersedesRevision;
            while (parent is int revision && _canonical.TryGetValue((header.DatasetVersion, header.RecordId), out var revisions) && revisions.TryGetValue(revision, out var witness))
            {
                var key = (header.RecordId, revision);
                if (!included.Contains(key)) witnesses.TryAdd(key, witness);
                parent = witness.Member.Record.Header.SupersedesRevision;
            }
        }
        foreach (var member in members)
        {
            Parents(member.Record.Header);
            if (member.Record.NonTracking?.Payload is PossessionPayload possession
                && _canonical.TryGetValue((stream.Stream.DatasetVersion, possession.DependencyRecordId), out var dependency))
            {
                var witness = dependency.First().Value; var h = witness.Member.Record.Header;
                witnesses.TryAdd((h.RecordId, h.Revision), witness); Parents(h);
            }
        }
        return new(id, members, witnesses.Values.OrderBy(w => w.Member.Record.Header.RecordId, StringComparer.Ordinal).ThenBy(w => w.Member.Record.Header.Revision).ToArray());
    }
    private FootballReadGroup[] Context(StreamState stream)
    {
        var ids = stream.NonTracking.ToHashSet();
        foreach (var dependency in ids.SelectMany(id => stream.Groups[id].Values).Select(m => m.Record.NonTracking?.Payload).OfType<PossessionPayload>().Select(p => p.DependencyRecordId).ToArray())
            if (stream.Groups.ContainsKey(dependency)) ids.Add(dependency);
        return ids.Order(StringComparer.Ordinal).Select(id => Group(stream, id)).ToArray();
    }
    public async Task<FootballReadReference> PublishAsync(FootballDatasetIndex fence,
        Func<string, object, CancellationToken, Task<FootballReadReference>> put, CancellationToken token)
    {
        if (fence.Streams.Length != _streams.Count || fence.Streams.Any(h => !_streams.TryGetValue(h.Stream.StreamId, out var s) || s.Stream != h.Stream
            || s.Head != h.Head || s.Closed != h.Closed || AzureFootballStore.ManifestHash(s.Manifest) != h.ManifestHash)) throw new RecordingValidationException("Read builder fence mismatch.");
        var bindings = _streams.Values.Select(s => new FootballDataset(s.Manifest, s.Stream, s.Head, s.Closed, null, [], null, 0)).ToArray();
        var descriptors = new List<FootballReadStream>();
        foreach (var stream in _streams.Values.OrderBy(s => s.Stream.StreamId, StringComparer.Ordinal))
        {
            var context = Context(stream);
            if (stream.ContextDirty || stream.Context is null)
            {
                stream.Context = await put(datasetId, new FootballReadPage(AzureFootballStore.ReadViewVersion, stream.Stream, null, null, context), token);
                stream.ContextDirty = false;
            }
            foreach (var bucket in stream.Dirty.OrderBy(b => b.Period, StringComparer.Ordinal).ThenBy(b => b.Minute))
            {
                var groups = stream.Buckets[bucket].Order(StringComparer.Ordinal).Select(id => Group(stream, id)).ToArray();
                stream.Pages[bucket] = await put(datasetId, new FootballReadPage(AzureFootballStore.ReadViewVersion, stream.Stream, bucket.Period, bucket.Minute, groups), token);
            }
            stream.Dirty.Clear();
            if (stream.Pages.Count > AzureFootballStore.MaxSeekPages) throw new RecordingValidationException("Prepared seek page capacity exceeded.");
            var binding = bindings.Single(b => b.Stream == stream.Stream);
            var descriptor = FootballLedger.FromReadState(binding, bindings, context).Catalog().Single();
            var seek = stream.Pages.OrderBy(p => p.Key.Period, StringComparer.Ordinal).ThenBy(p => p.Key.Minute).Select(p => new FootballSeekPage(p.Key.Period, p.Key.Minute, p.Value)).ToArray();
            var index = new FootballReadIndex(AzureFootballStore.ReadViewVersion, stream.Stream, AzureFootballStore.ManifestHash(stream.Manifest), stream.Head, stream.Closed, stream.Context, seek);
            descriptors.Add(new(descriptor, await put(datasetId, index, token)));
        }
        return await put(datasetId, new FootballReadCatalog(AzureFootballStore.ReadViewVersion, datasetId, AzureFootballStore.SourceFenceHash(fence), descriptors.ToArray()), token);
    }
}
