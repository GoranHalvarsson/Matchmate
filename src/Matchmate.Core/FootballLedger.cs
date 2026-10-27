using Matchmate.Contracts;
using System.Text.Json;

namespace Matchmate.Core;

public sealed record FootballRecordHeader(string DatasetId, string DatasetVersion, string? FixtureId, string RecordId, int Revision,
    int? SupersedesRevision, string State, FootballTime? Time, EffectiveTime? EffectiveTime, string RecordType,
    bool AvailabilityKnown, DateTimeOffset? AvailableAtUtc, string Fingerprint, int PayloadIndex);
public sealed record FootballStoredRecord(FootballRecordHeader Header, FootballRecord? NonTracking);
public sealed record FootballBatchMetadata(FootballManifest Manifest, FootballStream Stream, long Sequence, bool CloseStream,
    string Digest, DateTimeOffset ReceivedAtUtc, string PayloadPath, FootballStoredRecord[] Records);
public sealed record FootballPayloadPointer(string Path, int RecordIndex, string Digest, string Fingerprint);
public sealed record PreparedFootballBatch(FootballBatchMetadata Metadata, long BaseGeneration, FootballRecord[]? RetainedTracking, bool Duplicate);

// Compact archive/membership/time indexes. Tracking coordinates are retained only by the small pure-test/local compatibility mode.
public sealed class FootballLedger(bool retainTracking = true)
{
    private readonly record struct Key(string Dataset, string Version, string Record, int Revision);
    private sealed record Member(FootballStoredRecord Record, long Sequence, string PayloadPath, string Digest);
    private sealed class StreamState(FootballManifest manifest, FootballStream stream)
    {
        public readonly FootballManifest Manifest = manifest;
        public readonly FootballStream Stream = stream;
        public readonly Dictionary<string, SortedDictionary<int, Member>> Members = [];
        public readonly HashSet<string> NonTracking = [];
        public readonly Dictionary<string, SortedList<long, HashSet<string>>> Times = [];
        public readonly Dictionary<long, string> Digests = [];
        public long Head;
        public bool Closed;
    }
    private readonly Dictionary<Key, FootballStoredRecord> _archive = [];
    private readonly HashSet<(string Dataset, string Version, string Record)> _logical = [];
    private readonly Dictionary<Key, FootballRecord> _tracking = [];
    private readonly Dictionary<string, StreamState> _streams = [];
    private long _generation;
    public int CanonicalRecordCount => _archive.Count;
    public int RetainedTrackingCount => _tracking.Count;
    private static Key Identity(FootballRecordHeader header) => new(header.DatasetId, header.DatasetVersion, header.RecordId, header.Revision);
    public static FootballRecordHeader Header(FootballRecord record, int index)
    {
        var known = record.Evidence.All(e => e.AvailableAtUtc is not null);
        return new(record.DatasetId, record.DatasetVersion, record.FixtureId, record.RecordId, record.Revision, record.SupersedesRevision,
            record.RecordState, record.FootballTime, record.EffectiveTime, record.RecordType, known,
            known ? record.Evidence.Max(e => e.AvailableAtUtc) : null, FootballCodec.ContentHash(record), index);
    }
    public static FootballBatchMetadata Metadata(AcceptedFootballBatch accepted, string payloadPath)
    {
        FootballCodec.Validate(accepted.Batch);
        var records = accepted.Batch.Records.Select((record, index) =>
        {
            return new FootballStoredRecord(Header(record, index), record.Payload is TrackingPayload ? null : record);
        }).ToArray();
        return new(accepted.Batch.Manifest, accepted.Batch.Stream, accepted.Batch.Sequence, accepted.Batch.CloseStream, accepted.Batch.Digest,
            accepted.ReceivedAtUtc, payloadPath, records);
    }
    public PreparedFootballBatch Prepare(AcceptedFootballBatch accepted, string payloadPath = "")
    {
        var change = PrepareMetadata(Metadata(accepted, payloadPath));
        return change with { RetainedTracking = retainTracking ? accepted.Batch.Records.Where(r => r.Payload is TrackingPayload).ToArray() : null };
    }
    public PreparedFootballBatch PrepareMetadata(FootballBatchMetadata metadata)
    {
        if (metadata is null || metadata.Manifest is null || metadata.Stream is null || metadata.Records is null
            || metadata.Manifest.SchemaVersion != FootballContract.Version || metadata.Manifest.DatasetId != metadata.Stream.DatasetId
            || metadata.Manifest.DatasetVersion != metadata.Stream.DatasetVersion || metadata.Manifest.FixtureId != metadata.Stream.FixtureId
            || !DatasetIdentity.IsValid(metadata.Stream.StreamId) || metadata.Sequence < 1 || metadata.Records.Length > FootballCodec.MaxRecords
            || !ValidHash(metadata.Digest) || metadata.ReceivedAtUtc < DateTimeOffset.UnixEpoch) throw new RecordingValidationException("Invalid compact batch metadata.");
        FootballCodec.ValidateBinding(metadata.Manifest, metadata.Stream);
        _streams.TryGetValue(metadata.Stream.StreamId, out var stream);
        if (stream is not null)
        {
            if (stream.Stream != metadata.Stream || ManifestDigest(stream.Manifest) != ManifestDigest(metadata.Manifest)) throw new RecordingValidationException("Stream binding/manifest is immutable.");
            if (stream.Digests.TryGetValue(metadata.Sequence, out var digest))
            {
                if (digest != metadata.Digest) throw new RecordingValidationException("Conflicting stream sequence.");
                return new(metadata, _generation, null, true);
            }
            if (stream.Closed) throw new RecordingValidationException("Closed stream cannot advance.");
        }
        if (metadata.Sequence != (stream?.Head ?? 0) + 1) throw new RecordingValidationException("Delivery sequence must be contiguous; do not skip a failed batch.");
        var staged = new Dictionary<Key, string>();
        for (var index = 0; index < metadata.Records.Length; index++)
        {
            var record = metadata.Records[index]; var header = record?.Header;
            ValidateStoredRecord(record!, metadata.Manifest, metadata.Stream, index);
            var key = Identity(header!);
            if ((_archive.TryGetValue(key, out var prior) && prior.Header.Fingerprint != header!.Fingerprint)
                || staged.TryGetValue(key, out var fingerprint) && fingerprint != header!.Fingerprint) throw new RecordingValidationException("Canonical revision content conflict.");
            staged[key] = header!.Fingerprint;

        }
        return new(metadata, _generation, null, false);
    }
    private static void ValidateStoredRecord(FootballStoredRecord record, FootballManifest manifest, FootballStream bindingStream, int? expectedIndex = null)
    {
        var header = record?.Header;
            if (header is null || header.DatasetId != bindingStream.DatasetId || header.DatasetVersion != bindingStream.DatasetVersion
                || !DatasetIdentity.IsValid(header.RecordId) || header.Revision < 1 || header.PayloadIndex < 0 || header.PayloadIndex >= FootballCodec.MaxRecords || expectedIndex is int index && header.PayloadIndex != index || !ValidHash(header.Fingerprint)
                || header.State is not ("active" or "withdrawn") || (header.Revision == 1 ? header.SupersedesRevision is not null : header.SupersedesRevision != header.Revision - 1)
                || header.AvailabilityKnown != (header.AvailableAtUtc is not null)
                || (header.RecordType == "tracking_frame" ? record!.NonTracking is not null || header.FixtureId != bindingStream.FixtureId || header.Time?.StartMs is null || header.Time.EndMs is null || header.Time.StartMs < 0 || header.Time.EndMs < header.Time.StartMs
                    || !manifest.IdentityMappings.Any(m => m.CanonicalId == header.Time.PeriodId)
                    : record!.NonTracking is null || Header(record.NonTracking, header.PayloadIndex) != header)) throw new RecordingValidationException("Invalid compact canonical record metadata.");
        if (record!.NonTracking is not null) FootballCodec.ValidateMetadataRecord(record.NonTracking, manifest);
    }
    // A root-bound sparse read state includes complete member revision groups and canonical parent witnesses.
    // It uses the same selection/projection code as journal recovery; it does not replay delivery sequences.
    public static FootballLedger FromReadState(FootballDataset dataset, IReadOnlyList<FootballDataset> bindings, IEnumerable<FootballReadGroup> groups)
    {
        var ledger = new FootballLedger(false);
        var registry = bindings.ToDictionary(d => d.Stream.StreamId, StringComparer.Ordinal);
        if (!registry.TryGetValue(dataset.Stream.StreamId, out var selected) || selected != dataset) throw new RecordingValidationException("Unbound sparse stream.");
        foreach (var binding in bindings) FootballCodec.ValidateBinding(binding.Manifest, binding.Stream);
        var state = new StreamState(dataset.Manifest, dataset.Stream) { Head = dataset.HeadSequence, Closed = dataset.Closed };
        if (state.Head < 1) throw new RecordingValidationException("Invalid sparse stream head.");
        ledger._streams.Add(dataset.Stream.StreamId, state);
        void AddCanonical(FootballReadMember member, FootballDataset binding)
        {
            var header = member?.Record?.Header; var payload = member?.Payload;
            if (header is null || payload is null || payload.Sequence < 1 || payload.Sequence > binding.HeadSequence || !ValidHash(payload.Digest)
                || !ValidHash(payload.BlobHash) || payload.Bytes is < 1 or > 514_000 || payload.ReceivedAtUtc < DateTimeOffset.UnixEpoch
                || payload.BlobPath != $"football/v04-full/{binding.Stream.DatasetId}/{binding.Stream.DatasetVersion}/{binding.Stream.StreamId}/{payload.Sequence:D12}-{payload.Digest}.json.gz")
                throw new RecordingValidationException("Invalid sparse payload membership.");
            ValidateStoredRecord(member!.Record, binding.Manifest, binding.Stream);
            var key = Identity(header);
            if (ledger._archive.TryGetValue(key, out var prior) && prior.Header.Fingerprint != header.Fingerprint) throw new RecordingValidationException("Sparse canonical conflict.");
            ledger._archive.TryAdd(key, member.Record); ledger._logical.Add((key.Dataset, key.Version, key.Record));
        }
        foreach (var group in groups)
        {
            if (group?.Members is not { Length: > 0 } || group.Witnesses is null || !DatasetIdentity.IsValid(group.RecordId)) throw new RecordingValidationException("Invalid sparse revision group.");
            foreach (var witness in group.Witnesses)
            {
                if (witness is null || !registry.TryGetValue(witness.StreamId, out var binding)
                    || binding.Stream.DatasetId != dataset.Stream.DatasetId || binding.Stream.DatasetVersion != dataset.Stream.DatasetVersion) throw new RecordingValidationException("Unbound canonical witness.");
                AddCanonical(witness.Member, binding);
            }
            foreach (var member in group.Members)
            {
                AddCanonical(member, dataset);
                var record = member.Record; var header = record.Header;
                if (header.RecordId != group.RecordId) throw new RecordingValidationException("Sparse group identity mismatch.");
                if (!state.Members.TryGetValue(header.RecordId, out var revisions)) state.Members.Add(header.RecordId, revisions = []);
                var value = new Member(record, member.Payload.Sequence, member.Payload.BlobPath, member.Payload.Digest);
                if (revisions.TryGetValue(header.Revision, out var prior) && (prior.Record.Header != header || prior.Sequence != value.Sequence || prior.PayloadPath != value.PayloadPath || prior.Digest != value.Digest)) throw new RecordingValidationException("Sparse member conflict.");
                revisions.TryAdd(header.Revision, value);
            }
        }
        // Preserve delivery order for both nontracking records/equal-time events and tracking ties; witnesses have no membership.
        foreach (var member in state.Members.Values.SelectMany(revisions => revisions.Values).OrderBy(m => m.Sequence).ThenBy(m => m.Record.Header.PayloadIndex))
        {
            var header = member.Record.Header;
            if (header.RecordType != "tracking_frame") { state.NonTracking.Add(header.RecordId); continue; }
            if (!state.Times.TryGetValue(header.Time!.PeriodId, out var times)) state.Times.Add(header.Time.PeriodId, times = []);
            if (!times.TryGetValue(header.Time.StartMs!.Value, out var ids)) times.Add(header.Time.StartMs.Value, ids = []);
            ids.Add(header.RecordId);
        }
        return ledger;
    }
    public FootballTrackingWindow TrackingWindow(FootballQuery query)
    {
        var projection = Project(query);
        return new(projection.Period, projection.Local, projection.Dataset.Schedule!.SampleHoldMs);
    }
    public bool Commit(PreparedFootballBatch change)
    {
        if (change.Duplicate) return false;
        if (change.BaseGeneration != _generation) throw new RecordingValidationException("Staged metadata is stale; revalidate before commit.");
        var metadata = change.Metadata;
        if (!_streams.TryGetValue(metadata.Stream.StreamId, out var stream)) _streams.Add(metadata.Stream.StreamId, stream = new(metadata.Manifest, metadata.Stream));
        foreach (var record in metadata.Records)
        {
            var header = record.Header; var key = Identity(header);
            _archive.TryAdd(key, record); _logical.Add((key.Dataset, key.Version, key.Record));
            if (!stream.Members.TryGetValue(header.RecordId, out var revisions)) stream.Members[header.RecordId] = revisions = [];
            revisions.TryAdd(header.Revision, new(record, metadata.Sequence, metadata.PayloadPath, metadata.Digest));
            if (header.RecordType == "tracking_frame")
            {
                if (!stream.Times.TryGetValue(header.Time!.PeriodId, out var times)) stream.Times[header.Time.PeriodId] = times = [];
                if (!times.TryGetValue(header.Time.StartMs!.Value, out var ids)) times[header.Time.StartMs.Value] = ids = [];
                ids.Add(header.RecordId);
            }
            else stream.NonTracking.Add(header.RecordId);
        }
        if (retainTracking && change.RetainedTracking is not null)
            foreach (var record in change.RetainedTracking) _tracking.TryAdd(new(record.DatasetId, record.DatasetVersion, record.RecordId, record.Revision), record);
        stream.Digests.Add(metadata.Sequence, metadata.Digest); stream.Head = metadata.Sequence; stream.Closed = metadata.CloseStream; _generation++;
        return true;
    }
    public bool Accept(AcceptedFootballBatch accepted) => Commit(Prepare(accepted));
    public bool AcceptMetadata(FootballBatchMetadata metadata) => Commit(PrepareMetadata(metadata));
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string ManifestDigest(FootballManifest manifest) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(manifest, FootballCodec.Json));
    private bool Resolved(FootballRecordHeader record)
    {
        var previous = record.SupersedesRevision;
        while (previous is int revision)
        {
            if (!_archive.TryGetValue(new(record.DatasetId, record.DatasetVersion, record.RecordId, revision), out var parent)) return false;
            previous = parent.Header.SupersedesRevision;
        }
        return !_archive.TryGetValue(Identity(record), out var current) || current.NonTracking?.Payload is not PossessionPayload possession
            || _logical.Contains((record.DatasetId, record.DatasetVersion, possession.DependencyRecordId));
    }
    private Member? Select(StreamState stream, string id, FootballQuery? query)
    {
        foreach (var member in stream.Members[id].Reverse().Select(pair => pair.Value))
        {
            var header = member.Record.Header;
            if (member.Sequence > (query?.ApplicationSequence ?? long.MaxValue) || !Resolved(header)) continue;
            if (query?.KnowledgeMode == "known_then" && (!header.AvailabilityKnown || header.AvailableAtUtc > query.KnowledgeAsOfUtc)) continue;
            return member;
        }
        return null;
    }
    private FootballRecord[] NonTracking(StreamState stream, FootballQuery? query) => stream.NonTracking.Select(id => Select(stream, id, query))
        .Where(member => member?.Record.NonTracking is not null).Select(member => member!.Record.NonTracking!).ToArray();
    private FootballDataset Describe(StreamState stream, FootballQuery? query = null)
    {
        var active = NonTracking(stream, query).Where(r => r.RecordState == "active").ToArray();
        return new(stream.Manifest, stream.Stream, stream.Head, stream.Closed, active.Select(r => r.Payload).OfType<FixturePayload>().SingleOrDefault(),
            active.Select(r => r.Payload).OfType<SquadPayload>().SingleOrDefault()?.People ?? [], active.Select(r => r.Payload).OfType<SchedulePayload>().SingleOrDefault(),
            stream.NonTracking.Sum(id => stream.Members[id].Values.Count(m => m.Sequence <= (query?.ApplicationSequence ?? long.MaxValue) && !Resolved(m.Record.Header))));
    }
    public FootballDataset[] Catalog() => _streams.Values.OrderBy(s => s.Stream.StreamId, StringComparer.Ordinal).Select(s => Describe(s)).ToArray();
    private sealed record Projection(FootballDataset Dataset, FootballQuery Context, string Period, long Local, Member? Tracking,
        FootballRecord[] Events, FootballRecord[] Possessions, FootballRecord[] References, FootballIllustrationStatistics? IllustrationStatistics, string[]? GeneratedActionRecordIds, FootballStory? Story,
        string? Phase, long? PausedDisplayMs);
    private Projection Project(FootballQuery query)
    {
        if (query is null || !DatasetIdentity.IsValid(query.StreamId) || !DatasetIdentity.IsValid(query.DatasetId) || !DatasetIdentity.IsValid(query.DatasetVersion)
            || !DatasetIdentity.IsValid(query.FixtureId) || !DatasetIdentity.IsValid(query.PresentationPolicyId) || query.PresentationPolicyRevision <= 0 || string.IsNullOrWhiteSpace(query.KnowledgeMode)) throw new FootballQueryException("Complete canonical query context is required.");
        if (!_streams.TryGetValue(query.StreamId, out var stream)) throw new KeyNotFoundException("Unknown stream.");
        if (stream.Stream.DatasetId != query.DatasetId || stream.Stream.DatasetVersion != query.DatasetVersion || stream.Stream.FixtureId != query.FixtureId) throw new FootballQueryException("Dataset/version/fixture/stream mismatch.");
        if (query.SubjectId is not null && !stream.Manifest.IdentityMappings.Any(m => m.CanonicalId == query.SubjectId)) throw new FootballQueryException("Unknown subject in this dataset.");
        if (!stream.Manifest.QueryModes.Contains(query.KnowledgeMode) || query.KnowledgeMode == "known_then" && query.KnowledgeAsOfUtc is null) throw new FootballQueryException("This source does not support the requested knowledge mode/cutoff.");
        if (query.CursorMs < 0 || query.ApplicationSequence is < 1 || query.ApplicationSequence > stream.Head) throw new FootballQueryException("Invalid cursor/application boundary.");
        var selected = NonTracking(stream, query);
        // This is type membership at the selected application/knowledge fence, not a future event/outcome projection.
        var generatedActionIds = stream.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
            ? selected.Where(r => r.Payload is FootballActionPayload).Select(r => r.RecordId)
                .Order(StringComparer.Ordinal).Take(FootballActionPolicy.MaxGeneratedActionRecordIds + 1).ToArray() : null;
        if (generatedActionIds?.Length > FootballActionPolicy.MaxGeneratedActionRecordIds) generatedActionIds = null;
        var schedule = selected.Where(r => r.RecordState == "active").Select(r => r.Payload).OfType<SchedulePayload>().SingleOrDefault() ?? throw new FootballQueryException("Presentation dependencies are pending.");
        var fixture = selected.Where(r => r.RecordState == "active").Select(r => r.Payload).OfType<FixturePayload>().SingleOrDefault();
        if (fixture is not null && schedule.Segments.Any(s => !fixture.Periods.Any(p => p.Id == s.PeriodId))) throw new FootballQueryException("Presentation segment references an unknown football period.");
        if (schedule.PolicyId != query.PresentationPolicyId || schedule.PolicyRevision != query.PresentationPolicyRevision) throw new FootballQueryException("Presentation policy/revision mismatch.");
        var segment = schedule.Segments.FirstOrDefault(s => query.CursorMs >= s.CursorStartMs && query.CursorMs < s.CursorEndMs)
            ?? (query.CursorMs == schedule.Segments[^1].CursorEndMs ? schedule.Segments[^1] : null) ?? throw new FootballQueryException("Cursor outside presentation schedule.");
        var local = query.CursorMs - segment.CursorStartMs + segment.LocalStartMs;
        long? Cursor(FootballTime? time, bool end)
        {
            var value = end ? time?.EndMs : time?.StartMs;
            var part = schedule.Segments.FirstOrDefault(s => s.PeriodId == time?.PeriodId && FootballPresentationPolicy.IsPlay(s));
            return value is null || part is null ? null : part.CursorStartMs + value - part.LocalStartMs;
        }
        bool Eligible(string state, FootballTime? time, EffectiveTime? effective, string id, bool minuteEnd)
        {
            if (state != "active") return false;
            if (effective is { Basis: not "unknown" })
            {
                if (query.EffectiveDate is null || effective.From is not null && string.CompareOrdinal(query.EffectiveDate, effective.From) < 0
                    || effective.To is not null && (string.CompareOrdinal(query.EffectiveDate, effective.To) > 0 || effective.EndInclusive != true && query.EffectiveDate == effective.To)) return false;
            }
            var release = schedule.Releases.FirstOrDefault(r => r.RecordId == id);
            // Authored replay staging never supplies source event seconds or known-then availability.
            if (time is { Precision: "unknown" } && (release is null || query.KnowledgeMode is not ("retrospective_replay" or "completed_reference"))) return false;
            if (time is { Precision: not "unknown" } && (Cursor(time, minuteEnd) is not long cutoff || cutoff > query.CursorMs)) return false;
            return release is null || release.CursorMs <= query.CursorMs;
        }
        var eligible = selected.Where(r => Eligible(r.RecordState, r.FootballTime, r.EffectiveTime, r.RecordId, r.Payload is FootballActionPayload || r.Payload is MatchEventPayload && r.FootballTime?.Precision == "reported_minute")).ToArray();
        var dataset = Describe(stream, query) with { Fixture = eligible.Select(r => r.Payload).OfType<FixturePayload>().SingleOrDefault(), People = eligible.Select(r => r.Payload).OfType<SquadPayload>().SingleOrDefault()?.People ?? [], Schedule = schedule };
        Member? tracking = null;
        if (stream.Times.TryGetValue(segment.PeriodId, out var times))
        {
            var low = 0; var high = times.Count - 1; var position = -1;
            while (low <= high) { var middle = (low + high) / 2; if (times.Keys[middle] <= local) { position = middle; low = middle + 1; } else high = middle - 1; }
            for (; position >= 0 && local - times.Keys[position] <= schedule.SampleHoldMs && tracking is null; position--)
                foreach (var id in times.Values[position])
                {
                    var candidate = Select(stream, id, query); var header = candidate?.Record.Header;
                    if (header?.Time?.PeriodId == segment.PeriodId && header.Time.StartMs <= local && local - header.Time.StartMs <= schedule.SampleHoldMs
                        && Eligible(header.State, header.Time, header.EffectiveTime, header.RecordId, false)) { tracking = candidate; break; }
                }
        }
        return new(dataset, query, segment.PeriodId, local, tracking, eligible.Where(r => r.Payload is MatchEventPayload)
                .OrderBy(r => Cursor(r.FootballTime, false) ?? (r.FootballTime?.Precision == "unknown" ? schedule.Releases.FirstOrDefault(release => release.RecordId == r.RecordId)?.CursorMs : null)).ToArray(),
            eligible.Where(r => r.Payload is PossessionPayload p && p.EligibleAfterMs <= local && r.FootballTime!.PeriodId == segment.PeriodId
                && stream.Members.ContainsKey(p.DependencyRecordId) && Select(stream, p.DependencyRecordId, query) is not null).ToArray(), eligible.Where(r => r.Payload is ReferencePayload).ToArray(),
            FootballIllustrationStatisticsProjector.Project(dataset, eligible), generatedActionIds,
            FootballStoryProjector.Project(dataset, query, selected, eligible.Select(r => r.RecordId).ToHashSet(StringComparer.Ordinal)), segment.Phase, segment.PausedDisplayMs);
    }
    private static FootballView Finish(Projection projection, FootballRecord? resolved)
    {
        if (projection.Tracking is { } selected && (resolved is null || Header(resolved, selected.Record.Header.PayloadIndex) != selected.Record.Header || resolved.Payload is not TrackingPayload)) throw new RecordingValidationException("Lazy tracking payload does not match eligible canonical metadata.");
        var frame = resolved?.Payload as TrackingPayload; var gap = frame is null || frame.Players.All(p => p.Availability != "present");
        var goals = projection.Events.Select(r => (MatchEventPayload)r.Payload).Where(e => e.EventKind == "goal" && e.Decision == "confirmed").ToArray();
        return new(projection.Dataset, projection.Context, projection.Period, projection.Local, frame, projection.Events, projection.Possessions,
            goals.Count(g => g.TeamId == projection.Dataset.Fixture?.Home.Id), goals.Count(g => g.TeamId == projection.Dataset.Fixture?.Away.Id), gap,
            frame is null ? "No committed tracking sample at this cursor; positions are not carried across gaps." : gap ? "Explicit empty player frame; independent ball evidence is preserved." : "Source sample; no interpolation or measured movement statistics.", projection.References,
            IllustrationStatistics: projection.IllustrationStatistics, GeneratedActionRecordIds: projection.GeneratedActionRecordIds,
            Phase: projection.Phase, PausedDisplayMs: projection.PausedDisplayMs, Story: projection.Story);
    }
    public FootballView Query(FootballQuery query)
    {
        var projection = Project(query); FootballRecord? frame = null;
        if (projection.Tracking is { } member && !_tracking.TryGetValue(Identity(member.Record.Header), out frame)) throw new FootballQueryException("Use the asynchronous tracking payload resolver for this journal.");
        return Finish(projection, frame);
    }
    public async Task<FootballView> QueryAsync(FootballQuery query, Func<FootballPayloadPointer, CancellationToken, Task<FootballRecord>> resolver, CancellationToken token)
    {
        var projection = Project(query); FootballRecord? frame = null;
        if (projection.Tracking is { } member)
        {
            if (!_tracking.TryGetValue(Identity(member.Record.Header), out frame)) frame = await resolver(new(member.PayloadPath, member.Record.Header.PayloadIndex, member.Digest, member.Record.Header.Fingerprint), token);
        }
        return Finish(projection, frame);
    }
}
