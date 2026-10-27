using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Matchmate.Contracts;

namespace Matchmate.Core;

public interface IFootballStore
{
    Task<bool> AppendAsync(FootballBatch batch, CancellationToken token);
    Task<FootballLedger> ReadAsync(CancellationToken token);
    async Task<FootballDataset[]> CatalogAsync(CancellationToken token) => (await ReadAsync(token)).Catalog();
    async Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token) => (await ReadAsync(token)).Query(query);
}
public interface IFootballSource { Task<IReadOnlyList<FootballBatch>> ReadAsync(CancellationToken token); }
public interface IStreamingFootballSource { IAsyncEnumerable<FootballBatch> ReadBatchesAsync(CancellationToken token); }
public sealed class FootballQueryException(string message) : Exception(message);
public static class FootballCodec
{
    public const int MaxBatchBytes = 512_000, MaxRecords = 64;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 40 };
    public static string ContentHash(FootballRecord record) => ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(record, Json));
    public static FootballBatch Sign(FootballBatch batch) => batch with { Digest = ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(batch with { Digest = "" }, Json)) };
    public static byte[] Encode(FootballBatch batch) { Validate(batch); return JsonSerializer.SerializeToUtf8Bytes(batch, Json); }
    public static FootballBatch Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxBatchBytes) throw new RecordingValidationException("Invalid common batch size.");
        try { var batch = JsonSerializer.Deserialize<FootballBatch>(bytes, Json) ?? throw new RecordingValidationException("Missing common batch."); Validate(batch); return batch; }
        catch (JsonException) { throw new RecordingValidationException("Malformed common batch."); }
    }
    public static IReadOnlyList<FootballBatch> Batches(FootballManifest manifest, FootballStream stream, IEnumerable<FootballRecord> records)
    {
        var batches = new List<FootballBatch>(); var packer = new FootballBatchPacker(manifest, stream);
        foreach (var record in records)
        {
            if (packer.Add(record) is { } batch) batches.Add(batch);
        }
        batches.AddRange(packer.Complete());
        return batches;
    }
    public static void Validate(FootballBatch batch)
    {
        Require(batch?.Manifest is not null && batch.Stream is not null && batch.Records is not null, "Missing batch binding.");
        var manifest = batch!.Manifest; var stream = batch.Stream;
        ValidateBinding(manifest, stream);
        Require(batch.Sequence > 0 && batch.Records!.Length <= MaxRecords && Sign(batch).Digest == batch.Digest
            && JsonSerializer.SerializeToUtf8Bytes(batch, Json).Length <= MaxBatchBytes, "Invalid batch sequence/integrity/cap.");
        var identities = manifest.IdentityMappings.Select(m => m.CanonicalId).ToHashSet(StringComparer.Ordinal);
        foreach (var record in batch.Records) ValidateRecord(record, manifest, identities);
    }
    public static void ValidateBinding(FootballManifest manifest, FootballStream stream)
    {
        Require(manifest.SchemaVersion == FootballContract.Version && DatasetIdentity.IsValid(manifest.DatasetId)
            && DatasetIdentity.IsValid(manifest.DatasetVersion) && DatasetIdentity.IsValid(manifest.FixtureId)
            && manifest.ManifestRevision > 0 && manifest.Lifecycle is "open" or "closed"
            && manifest.Sources is { Length: > 0 } && manifest.IdentityMappings is { Length: > 0 }
            && manifest.QueryModes is { Length: > 0 } && manifest.IndependentDependencies is not null, "Invalid manifest.");
        Require(DatasetIdentity.IsValid(stream.StreamId) && stream.DatasetId == manifest.DatasetId && stream.DatasetVersion == manifest.DatasetVersion
            && stream.FixtureId == manifest.FixtureId && stream.DeliveryMode is "live" or "replay", "Invalid stream membership binding.");
        Require(manifest.Sources!.All(s => s is not null && DatasetIdentity.IsValid(s.SourceId) && !string.IsNullOrWhiteSpace(s.Locator))
            && manifest.Sources.Select(s => s.SourceId).Distinct().Count() == manifest.Sources.Length, "Invalid source registry.");
        Require(manifest.IdentityMappings!.All(m => m is not null && DatasetIdentity.IsValid(m.CanonicalId) && !string.IsNullOrWhiteSpace(m.Namespace)), "Invalid identity crosswalk.");
    }
    public static void ValidateMetadataRecord(FootballRecord record, FootballManifest manifest) => ValidateRecord(record, manifest, manifest.IdentityMappings.Select(m => m.CanonicalId).ToHashSet(StringComparer.Ordinal));
    private static void ValidateRecord(FootballRecord record, FootballManifest manifest, HashSet<string> identities)
    {
        Require(record is not null && record.SchemaVersion == manifest.SchemaVersion && record.DatasetId == manifest.DatasetId
            && record.DatasetVersion == manifest.DatasetVersion && DatasetIdentity.IsValid(record.RecordId)
            && record.Revision > 0 && (record.Revision == 1 ? record.SupersedesRevision is null : record.SupersedesRevision == record.Revision - 1)
            && record.RecordState is "active" or "withdrawn" && record.Payload is not null, "Invalid record/revision.");
        Require(record!.RecordState != "withdrawn" || !string.IsNullOrWhiteSpace(record.WithdrawalReason), "Withdrawal requires a reason.");
        Require(record.EntityRefs is not null && record.EntityRefs.All(identities.Contains), "Unknown entity reference.");
        if (record.Payload is ReferencePayload reference)
            Require(record.FixtureId is null && manifest.IndependentDependencies.Contains(reference.SubjectId) && identities.Contains(reference.SubjectId), "Independent reference scope must be declared.");
        else Require(record.FixtureId == manifest.FixtureId, "A match record requires its bound fixture.");
        Require(record.Evidence is { Length: > 0 } && record.Evidence.All(e => e is not null && DatasetIdentity.IsValid(e.EvidenceId)
            && manifest.Sources.Any(s => s.SourceId == e.SourceId) && !string.IsNullOrWhiteSpace(e.Locator)
            && e.Origin is "real" or "synthetic" && e.Method is "observed" or "reported" or "estimated" or "generated" or "computed" or "unknown"
            && e.ClaimStatus is "authored" or "confirmed" or "reported" or "disputed" or "interpretation"), "Invalid evidence.");
        var evidence = record.Evidence!.Select(e => e.EvidenceId).ToHashSet();
        Require(evidence.Count == record.Evidence.Length && record.FieldEvidence is not null && record.FieldEvidence.Values.All(ids => ids is { Length: > 0 } && ids.All(evidence.Contains)), "Invalid field evidence.");
        if (record.FootballTime is { } time)
            Require(identities.Contains(time.PeriodId) && time.Precision is "source_sample" or "source_exact" or "reported_minute" or "source_interval" or "presentation_sample" or "unknown"
                && (time.Precision == "unknown" ? time.StartMs is null && time.EndMs is null : time.StartMs >= 0 && time.EndMs >= time.StartMs), "Invalid period-local football time.");
        if (record.FootballTime?.Precision == "presentation_sample")
            Require(record.Payload is TrackingPayload && manifest.Capabilities.Contains(FootballPresentationPolicy.GeneratedMovement)
                && record.Evidence.All(e => e.Origin == "synthetic" && e.Method == "generated" && e.ClaimStatus == "authored"), "Presentation samples require authored generated tracking.");
        if (record.EffectiveTime is { } effective)
            Require(effective.Basis is "utc" or "calendar_date" or "unknown" && (effective.Basis != "unknown" || effective.From is null && effective.To is null), "Invalid effective time.");
        switch (record.Payload)
        {
            case FixturePayload fixture:
                Require(identities.Contains(fixture.Home.Id) && identities.Contains(fixture.Away.Id) && fixture.Pitch is not null
                    && double.IsFinite(fixture.Pitch.LengthM) && fixture.Pitch.LengthM > 0 && double.IsFinite(fixture.Pitch.WidthM) && fixture.Pitch.WidthM > 0
                    && fixture.Pitch.Basis == "top-left-metres-x-right-y-down" && fixture.Periods is { Length: > 0 }
                    && fixture.Periods.All(p => identities.Contains(p.Id)), "Invalid fixture/pitch/periods."); break;
            case SquadPayload squad:
                Require(squad.People is { Length: > 0 and <= 100 } && squad.People.All(p => p is not null && identities.Contains(p.PersonId) && identities.Contains(p.TeamId)
                    && !string.IsNullOrWhiteSpace(p.Name) && p.Selection is "starter" or "bench" or "unknown") && squad.People.Select(p => p.PersonId).Distinct().Count() == squad.People.Length, "Invalid squad."); break;
            case TrackingPayload frame:
                Require(record.FootballTime is not null && frame.Players is not null && frame.ActiveSubjectIds is { Length: > 0 and <= 100 }
                    && frame.ActiveSubjectIds.All(identities.Contains) && frame.ActiveSubjectIds.Distinct().Count() == frame.ActiveSubjectIds.Length
                    && frame.Players.All(p => p is not null && frame.ActiveSubjectIds.Contains(p.SubjectId))
                    && frame.Players.Select(p => p.SubjectId).Distinct().Count() == frame.Players.Length, "Invalid active frame subjects.");
                foreach (var observation in frame.Players!.Append(frame.Ball))
                {
                    Require(observation is not null && identities.Contains(observation.SubjectId) && observation.EvidenceIds is { Length: > 0 }
                        && observation.EvidenceIds.All(evidence.Contains) && observation.Method is "observed" or "estimated" or "generated" or "reported" or "unknown"
                        && observation.Quality is "usable" or "uncertain" or "invalid" or "unknown", "Invalid observation evidence/method.");
                    Require(observation!.Availability is "present" or "missing" or "not_in_frame" or "not_collected" or "unknown"
                        && (observation.Availability == "present" ? observation.XM is double x && double.IsFinite(x) && observation.YM is double y && double.IsFinite(y)
                            && (observation.ZM is null || double.IsFinite(observation.ZM.Value)) : observation.XM is null && observation.YM is null && observation.ZM is null), "Invalid observation missingness.");
                    Require(observation.EvidenceIds.All(id => record.Evidence.First(e => e.EvidenceId == id).Method == observation.Method), "Observation method contradicts evidence.");
                }
                break;
            case MatchEventPayload matchEvent:
                Require(record.FootballTime is not null && matchEvent.EventKind is "goal" or "substitution" or "yellow_card" or "red_card" or "var_decision" && identities.Contains(matchEvent.TeamId)
                    && new[] { matchEvent.PersonId, matchEvent.AssistPersonId, matchEvent.IncomingPersonId, matchEvent.OutgoingPersonId }.All(id => id is null || identities.Contains(id))
                    && (matchEvent.Decision is null or "unknown" or "confirmed" or "provisional" or "disallowed"
                        || matchEvent.EventKind == "red_card" && matchEvent.Decision is "straight_red" or "second_yellow"
                        || matchEvent.EventKind == "var_decision" && matchEvent.Decision is "no_penalty" or "card_upgraded"), "Invalid event.");
                Require(matchEvent.Details is null || matchEvent.Details.Length <= MatchEventDetailLimits.Count && matchEvent.Details.All(detail => detail is not null
                    && detail.Kind is "finish" or "assist" or "build_up" or "reason" or "substitution" or "reviewed_observation" or "reported_minute_disagreement"
                    && !string.IsNullOrWhiteSpace(detail.Text) && detail.Text.Length <= MatchEventDetailLimits.TextChars
                    && (detail.Uncertainty is null || detail.Uncertainty.Length <= MatchEventDetailLimits.UncertaintyChars)
                    && detail.EvidenceIds is { Length: > 0 and <= 3 } && detail.EvidenceIds.All(evidence.Contains)), "Invalid attributed event detail.");
                if (matchEvent.Sequence is { } sequence)
                {
                    Require(sequence.Version == 1 && DatasetIdentity.IsValid(sequence.Id)
                        && sequence.PresentationLeadInMs is >= 0 and <= MatchEventSequenceLimits.LeadInMs
                        && sequence.PresentationTailMs is >= 0 and <= MatchEventSequenceLimits.TailMs
                        && sequence.Steps is { Length: > 0 and <= MatchEventSequenceLimits.Steps }, "Invalid event sequence bounds.");
                    Require(sequence.Steps.All(step => step is not null && DatasetIdentity.IsValid(step.Id) && MatchEventSequenceLimits.EventKind(step.Kind)
                        && new[] { step.ActorId, step.RelatedActorId }.All(id => id is null || identities.Contains(id))
                        && step.PresentationOffsetMs >= -sequence.PresentationLeadInMs && step.PresentationOffsetMs <= sequence.PresentationTailMs
                        && !string.IsNullOrWhiteSpace(step.Text) && step.Text.Length <= MatchEventSequenceLimits.TextChars
                        && (step.Uncertainty is null || step.Uncertainty.Length <= MatchEventSequenceLimits.UncertaintyChars)
                        && step.EvidenceIds is { Length: > 0 and <= 3 } && step.EvidenceIds.All(evidence.Contains))
                        && sequence.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == sequence.Steps.Length
                        && sequence.Steps.Select(s => s.PresentationOffsetMs).SequenceEqual(sequence.Steps.Select(s => s.PresentationOffsetMs).Order()), "Invalid attributed event sequence.");
                    Require(matchEvent.EventKind switch {
                        "substitution" => sequence.PresentationLeadInMs == 0 && sequence.Steps is [{ Kind: "substitution", PresentationOffsetMs: 0 } sub]
                            && sub.ActorId == matchEvent.OutgoingPersonId && sub.RelatedActorId == matchEvent.IncomingPersonId,
                        "yellow_card" => sequence.PresentationLeadInMs == 0 && sequence.Steps is [{ Kind: "booking", PresentationOffsetMs: 0 } card] && card.ActorId == matchEvent.PersonId,
                        "red_card" => matchEvent.Decision is "straight_red" or "second_yellow" && sequence.PresentationLeadInMs == 0
                            && sequence.Steps is [{ Kind: "red_card", PresentationOffsetMs: 0, RelatedActorId: null } dismissal]
                            && dismissal.ActorId is not null && dismissal.ActorId == matchEvent.PersonId
                            && dismissal.EvidenceIds.All(id => record.Evidence.Single(e => e.EvidenceId == id).Method == "reported"),
                        _ => sequence.Steps.All(s => s.Kind is not "substitution" and not "booking" and not "red_card") }, "Sequence contradicts event type/participants.");
                    Require(sequence.Steps.All(step => step.Kind switch {
                        // Existing immutable imports may contain the earlier two-step goal representation.
                        "reported_assist" => matchEvent.EventKind == "goal" && step.ActorId is not null && step.RelatedActorId is not null
                            && step.ActorId == matchEvent.AssistPersonId && step.RelatedActorId == matchEvent.PersonId && step.ActorId != step.RelatedActorId
                            && step.EvidenceIds.All(id => record.Evidence.Single(e => e.EvidenceId == id).Method == "reported"),
                        "reported_finish" when sequence.Steps.Any(s => s.Kind == "reported_assist") => matchEvent.EventKind == "goal"
                            && step.ActorId is not null && step.ActorId == matchEvent.PersonId,
                        "release_pass" => matchEvent.EventKind == "goal" && step.ActorId is not null && step.RelatedActorId is not null && step.ActorId != step.RelatedActorId,
                        "rebound_finish" => matchEvent.EventKind == "goal" && step.ActorId is not null && step.ActorId == matchEvent.PersonId,
                        "shot" or "save" or "cross" or "deflection" => matchEvent.EventKind == "goal" && step.ActorId is not null,
                        _ => true }), "Invalid named sequence role.");
                }
                break;
            case PossessionPayload possession:
                Require(record.FootballTime is not null && identities.Contains(possession.PersonId) && identities.Contains(possession.TeamId) && possession.EligibleAfterMs >= 0
                    && DatasetIdentity.IsValid(possession.DependencyRecordId), "Invalid possession dependency."); break;
            case FootballActionPayload action:
                Require(manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
                    && record.FootballTime is { Precision: "source_interval", StartMs: not null, EndMs: not null, EndInclusive: false } actionTime
                    && actionTime.EndMs > actionTime.StartMs && record.EffectiveTime is null
                    && record.Evidence.All(e => e.Origin == "synthetic" && e.Method == "generated" && e.ClaimStatus == "authored"), "Invalid generated action provenance/interval.");
                Require(identities.Contains(action.TeamId)
                    && new[] { action.PersonId, action.RecipientId, action.RecipientTeamId }.All(id => id is null || identities.Contains(id))
                    && new[] { action.TeamId, action.PersonId, action.RecipientId, action.RecipientTeamId }.Where(id => id is not null).All(id => record.EntityRefs.Contains(id!))
                    && new[] { action.FromSlotId, action.ToSlotId }.All(id => id is null || DatasetIdentity.IsValid(id)), "Invalid generated action identity scope.");
                Require(action.ActionKind switch
                {
                    "pass" => action.PersonId is not null && action.RecipientId is not null && action.PersonId != action.RecipientId
                        && action.RecipientTeamId is not null && (action.Outcome == "completed" && action.RecipientTeamId == action.TeamId
                            || action.Outcome == "turnover" && action.RecipientTeamId != action.TeamId),
                    "shot" => action.PersonId is not null && action.Outcome == "goal" && action.RecipientId is null && action.RecipientTeamId is null,
                    "possession" => action.Outcome == "controlled" && action.PersonId is null && action.RecipientId is null && action.RecipientTeamId is null
                        && action.FromSlotId is null && action.ToSlotId is null,
                    _ => false
                }, "Invalid generated action kind/outcome/participants.");
                Require(new[] { "/footballTime", "/payload/actionKind", "/payload/teamId", "/payload/personId", "/payload/recipientId", "/payload/recipientTeamId", "/payload/outcome", "/payload/fromSlotId", "/payload/toSlotId" }
                    .All(record.FieldEvidence.ContainsKey), "Generated action requires attributed fields.");
                break;
            case SchedulePayload schedule:
                Require(DatasetIdentity.IsValid(schedule.PolicyId) && schedule.PolicyRevision > 0 && schedule.SampleHoldMs >= 0 && schedule.SampleHoldMs <= 1000
                    && schedule.Segments is { Length: > 0 } && schedule.Segments.All(s => identities.Contains(s.PeriodId) && s.CursorStartMs >= 0 && s.CursorEndMs > s.CursorStartMs && s.LocalStartMs >= 0)
                    && schedule.Releases is not null && schedule.Releases.All(r => DatasetIdentity.IsValid(r.RecordId) && r.CursorMs >= 0)
                    && schedule.Milestones is not null, "Invalid presentation schedule.");
                Require(schedule.Segments.All(s => s.Phase is null or "play" or "halftime_transition"
                    && (FootballPresentationPolicy.IsPlay(s) ? s.PausedDisplayMs is null : s.PausedDisplayMs >= 0))
                    && schedule.Segments.Zip(schedule.Segments.Skip(1)).All(pair => pair.First.CursorEndMs <= pair.Second.CursorStartMs), "Invalid presentation phase/order.");
                break;
            case CoveragePayload coverage: Require(coverage.SubjectIds is not null && coverage.SubjectIds.All(identities.Contains) && coverage.Status is "covered" or "missing" or "not_collected" or "unknown", "Invalid coverage."); break;
            case ReferencePayload: break;
            default: throw new RecordingValidationException("Unsupported typed football payload.");
        }
    }
    private static void Require([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new RecordingValidationException(message); }
}

public sealed class FootballBatchPacker(FootballManifest manifest, FootballStream stream)
{
    private readonly List<FootballRecord> _pending = [];
    private readonly int _overhead = JsonSerializer.SerializeToUtf8Bytes(new FootballBatch(manifest, stream, 1, [], false, new string('0', 64)), FootballCodec.Json).Length + 32;
    private int _bytes;
    private long _sequence;
    public FootballBatch? Add(FootballRecord record)
    {
        var length = JsonSerializer.SerializeToUtf8Bytes(record, FootballCodec.Json).Length + 1;
        if (_overhead + length > FootballCodec.MaxBatchBytes) throw new RecordingValidationException("One record exceeds the common batch cap.");
        FootballBatch? emitted = null;
        if (_pending.Count == FootballCodec.MaxRecords || _bytes + length + _overhead > FootballCodec.MaxBatchBytes) emitted = Flush(false);
        _pending.Add(record); _bytes += length; return emitted;
    }
    public IEnumerable<FootballBatch> Complete() { if (_pending.Count > 0) yield return Flush(false); yield return Flush(true); }
    private FootballBatch Flush(bool close)
    {
        var batch = FootballCodec.Sign(new(manifest, stream, ++_sequence, _pending.ToArray(), close, "")); FootballCodec.Validate(batch);
        _pending.Clear(); _bytes = 0; return batch;
    }
}
