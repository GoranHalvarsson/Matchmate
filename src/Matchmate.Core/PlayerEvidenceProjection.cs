using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matchmate.Contracts;

namespace Matchmate.Core;

// Validated immutable prepared state. No journal, file, media or network work happens while projecting a view.
public sealed class PlayerEvidenceProjection
{
    private readonly PreparedPlayerEvidencePack _pack;
    private readonly string _hash;
    public PlayerEvidenceProjection(PreparedPlayerEvidencePack pack, string sha256)
    {
        ValidatePack(pack); Require(Hash(sha256), "Invalid prepared pack fingerprint.");
        _pack = pack; _hash = sha256;
    }
    public FootballView Attach(FootballView view)
    {
        if (!Matches(_pack.Binding, view)) return view;
        var observations = _pack.Observations.Where(o => Eligible(o, view)).ToImmutableArray();
        var used = observations.SelectMany(o => o.Evidence).Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal);
        var sources = _pack.Sources.Where(s => used.Contains(s.SourceId)).Append(new SourceReference(_pack.Id,
            "prepared-player-evidence:" + _pack.Id, _hash)).ToImmutableArray();
        return view with { PlayerEvidence = new(1, _pack.Id, _hash, _pack.Binding, sources, observations, _pack.Coverage) };
    }
    public static PlayerEvidenceView? ForSubject(FootballView view, string subject)
    {
        if (view.PlayerEvidence is not { } evidence) return null;
        ValidateBinding(evidence.Binding);
        Require(evidence.Version == 1 && DatasetIdentity.IsValid(evidence.PackId) && Hash(evidence.PackSha256)
            && Matches(evidence.Binding, view) && Text(evidence.Coverage, PlayerEvidenceLimits.CoverageChars), "Unbound prepared player evidence.");
        ValidateSources(evidence.Sources);
        Require(evidence.Sources.Count(s => s.SourceId == evidence.PackId && s.ArtifactHash == evidence.PackSha256) == 1,
            "Missing prepared source fingerprint.");
        ValidateCollisions(evidence.Binding.SourcePins, evidence.Sources);
        ValidateObservations(evidence.Observations, view.Dataset.People.Select(p => p.PersonId).ToHashSet(StringComparer.Ordinal),
            evidence.Binding.PeriodIds, evidence.Sources);
        Require(evidence.Observations.All(o => Eligible(o, view)), "Future prepared player evidence.");
        var selected = evidence.Observations.Where(o => o.PersonId == subject).ToImmutableArray();
        var used = selected.SelectMany(o => o.Evidence).Select(e => e.SourceId).Append(evidence.PackId).ToHashSet(StringComparer.Ordinal);
        return evidence with { Observations = selected, Sources = evidence.Sources.Where(s => used.Contains(s.SourceId)).ToImmutableArray() };
    }
    public static string RosterFingerprint(IEnumerable<SquadPerson> people)
    {
        var canonical = string.Join('\n', people.OrderBy(p => p.PersonId, StringComparer.Ordinal).Select(p => string.Join('\u001f',
            p.PersonId, p.TeamId, p.Name, p.Shirt?.ToString(CultureInfo.InvariantCulture) ?? "null", p.Selection)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    public static void ValidatePack(PreparedPlayerEvidencePack pack)
    {
        if (pack is null) throw new InvalidDataException("Missing prepared player pack.");
        ValidateBinding(pack.Binding); ValidateSources(pack.Sources);
        Require(pack.Version == 1 && DatasetIdentity.IsValid(pack.Id) && Text(pack.Coverage, PlayerEvidenceLimits.CoverageChars)
            && !pack.Roster.IsDefaultOrEmpty && pack.Roster.Length <= PlayerEvidenceLimits.People
            && pack.Roster.All(p => p is not null && DatasetIdentity.IsValid(p.PersonId) && DatasetIdentity.IsValid(p.TeamId)
                && Text(p.Name, 180) && p.Shirt is null or >= 0 and <= 999 && p.Selection is "starter" or "bench" or "unknown")
            && pack.Roster.Select(p => p.PersonId).Distinct(StringComparer.Ordinal).Count() == pack.Roster.Length,
            "Invalid prepared player roster/pack.");
        Require(RosterFingerprint(pack.Roster) == pack.Binding.RosterSha256 && !pack.Sources.Any(s => s.SourceId == pack.Id)
            && !pack.Binding.SourcePins.Any(s => s.SourceId == pack.Id), "Prepared roster/fingerprint collision.");
        ValidateCollisions(pack.Binding.SourcePins, pack.Sources);
        ValidateObservations(pack.Observations, pack.Roster.Select(p => p.PersonId).ToHashSet(StringComparer.Ordinal), pack.Binding.PeriodIds, pack.Sources);
        Require(JsonSerializer.SerializeToUtf8Bytes(pack).Length <= PlayerEvidenceLimits.PackBytes, "Prepared pack exceeds byte budget.");
    }
    private static void ValidateBinding(PlayerEvidenceBinding binding)
    {
        if (binding is null) throw new InvalidDataException("Missing prepared player binding.");
        ValidateSources(binding.SourcePins);
        Require(new[] { binding.DatasetId, binding.DatasetVersion, binding.StreamId, binding.FixtureId }.All(DatasetIdentity.IsValid)
            && binding.HeadSequence > 0 && binding.ManifestRevision > 0 && Hash(binding.RosterSha256)
            && binding.SourcePins.All(s => Hash(s.ArtifactHash))
            && !binding.ContextSourceIds.IsDefaultOrEmpty && binding.ContextSourceIds.Length <= PlayerEvidenceLimits.Sources
            && binding.ContextSourceIds.All(id => binding.SourcePins.Any(s => s.SourceId == id))
            && binding.ContextSourceIds.Distinct(StringComparer.Ordinal).Count() == binding.ContextSourceIds.Length
            && !binding.PeriodIds.IsDefaultOrEmpty && binding.PeriodIds.Length <= 8 && binding.PeriodIds.All(DatasetIdentity.IsValid)
            && binding.PeriodIds.Distinct(StringComparer.Ordinal).Count() == binding.PeriodIds.Length, "Invalid prepared binding.");
    }
    private static void ValidateSources(ImmutableArray<SourceReference> sources) => Require(!sources.IsDefaultOrEmpty
        && sources.Length <= PlayerEvidenceLimits.Sources && sources.All(s => s is not null && DatasetIdentity.IsValid(s.SourceId)
            && Text(s.Locator, PlayerEvidenceLimits.LocatorChars) && (s.ArtifactHash is null || Hash(s.ArtifactHash)))
        && sources.Select(s => s.SourceId).Distinct(StringComparer.Ordinal).Count() == sources.Length, "Invalid prepared source registry.");
    private static void ValidateCollisions(ImmutableArray<SourceReference> pins, ImmutableArray<SourceReference> sources) =>
        Require(sources.All(s => pins.All(p => p.SourceId != s.SourceId || p == s)), "Conflicting prepared source identifiers.");
    private static void ValidateObservations(ImmutableArray<PlayerEvidenceObservation> observations, HashSet<string> people,
        ImmutableArray<string> periods, ImmutableArray<SourceReference> sources)
    {
        Require(!observations.IsDefault && observations.Length <= PlayerEvidenceLimits.Observations
            && observations.All(o => o is not null && DatasetIdentity.IsValid(o.Id))
            && observations.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() == observations.Length, "Invalid prepared observations.");
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in observations)
        {
            Require(people.Contains(o.PersonId) && o.FootballTime is { } time && periods.Contains(time.PeriodId)
                && ((time.StartMs is >= 0 && time.EndMs is >= 0 && time.EndMs >= time.StartMs
                        && time.Precision is "reported_minute" or "source_sample" or "source_interval"
                        && (time.ReportedClock is null || Text(time.ReportedClock, 180)) && o.EligibleAfterMs >= time.EndMs)
                    || (time.StartMs is null && time.EndMs is null && time.Precision == "unknown"
                        && Text(time.ReportedClock, 180) && o.EligibleAfterMs > 0))
                && Text(o.Text, PlayerEvidenceLimits.TextChars)
                && Text(o.Qualification, PlayerEvidenceLimits.QualificationChars)
                && !o.RelatedPersonIds.IsDefault && o.RelatedPersonIds.Length <= 8 && o.RelatedPersonIds.All(people.Contains)
                && o.RelatedPersonIds.Distinct(StringComparer.Ordinal).Count() == o.RelatedPersonIds.Length
                && !o.Evidence.IsDefaultOrEmpty && o.Evidence.Length <= PlayerEvidenceLimits.Evidence, "Invalid prepared observation scope.");
            foreach (var e in o.Evidence)
                Require(e is not null && DatasetIdentity.IsValid(e.EvidenceId) && evidenceIds.Add(e.EvidenceId)
                    && sources.Any(s => s.SourceId == e.SourceId) && Text(e.Locator, PlayerEvidenceLimits.LocatorChars)
                    && e.Origin == "real" && e.Method is "reported" or "observed" && e.ClaimStatus is "reported" or "confirmed"
                    && e.AvailableAtUtc is null && (e.OriginalFlags is null || Text(e.OriginalFlags, PlayerEvidenceLimits.LocatorChars)),
                    "Invalid prepared attribution/availability.");
        }
    }
    private static bool Matches(PlayerEvidenceBinding binding, FootballView view)
    {
        var d = view.Dataset; var q = view.Context;
        return d is { Closed: true, PendingRecords: 0, Manifest: not null, Stream: not null,
                Fixture.Periods.Length: > 0, Schedule.Segments.Length: > 0 } && q is not null
            && d.Schedule.Segments.Length <= 64 && d.Schedule.Segments.All(s => s is not null && s.CursorStartMs >= 0
                && s.CursorEndMs >= s.CursorStartMs && s.LocalStartMs >= 0)
            && d.Fixture.Periods.All(p => p is not null)
            && d.Schedule.Segments.Any(s => q.CursorMs >= s.CursorStartMs && q.CursorMs <= s.CursorEndMs)
            && d.Manifest.SchemaVersion == FootballContract.Version
            && q.KnowledgeMode == "retrospective_replay" && q.KnowledgeAsOfUtc is null && q.EffectiveDate is null
            && q.DatasetId == binding.DatasetId && q.DatasetVersion == binding.DatasetVersion && q.StreamId == binding.StreamId && q.FixtureId == binding.FixtureId
            && d.Manifest.DatasetId == binding.DatasetId && d.Manifest.DatasetVersion == binding.DatasetVersion && d.Manifest.FixtureId == binding.FixtureId
            && d.Stream.DatasetId == binding.DatasetId && d.Stream.DatasetVersion == binding.DatasetVersion && d.Stream.FixtureId == binding.FixtureId
            && d.Stream.StreamId == binding.StreamId && d.Stream.DeliveryMode == "replay" && d.HeadSequence == binding.HeadSequence
            && (q.ApplicationSequence ?? d.HeadSequence) == binding.HeadSequence && d.Manifest.ManifestRevision == binding.ManifestRevision
            && q.PresentationPolicyId == d.Schedule.PolicyId && q.PresentationPolicyRevision == d.Schedule.PolicyRevision
            && d.People is { Length: > 0 } && d.People.All(p => p is not null && p.Name is not null) && RosterFingerprint(d.People) == binding.RosterSha256
            && d.Manifest.Sources is not null && d.Manifest.Sources.Length == binding.SourcePins.Length
            && binding.SourcePins.All(p => d.Manifest.Sources.Count(s => s == p) == 1)
            && binding.PeriodIds.Order(StringComparer.Ordinal).SequenceEqual(d.Fixture.Periods.Select(p => p.Id).Order(StringComparer.Ordinal));
    }
    private static bool Eligible(PlayerEvidenceObservation observation, FootballView view)
    {
        if (observation.FootballTime.Precision == "unknown")
        {
            // An untimed report has no invented contact clock. Its retrospective release must be the
            // actual bound schedule's period endpoint, with one unambiguous mapping to the cursor.
            var schedule = view.Dataset.Schedule!.Segments.OrderBy(s => s.CursorStartMs).ToArray();
            if (schedule.Any(s => s.CursorEndMs <= s.CursorStartMs || s.LocalStartMs > long.MaxValue - (s.CursorEndMs - s.CursorStartMs))
                || schedule.Zip(schedule.Skip(1)).Any(pair => pair.First.CursorEndMs > pair.Second.CursorStartMs)) return false;
            var period = schedule.Where(s => s.PeriodId == observation.FootballTime.PeriodId).ToArray();
            if (period.Length == 0 || period.Zip(period.Skip(1)).Any(pair =>
                    pair.First.LocalStartMs + (pair.First.CursorEndMs - pair.First.CursorStartMs) > pair.Second.LocalStartMs)) return false;
            var end = period.Max(s => s.LocalStartMs + (s.CursorEndMs - s.CursorStartMs));
            if (observation.EligibleAfterMs != end || view.Context.CursorMs < period.Max(s => s.CursorEndMs)) return false;
        }
        var segments = view.Dataset.Schedule!.Segments.Where(s => s.PeriodId == observation.FootballTime.PeriodId
            && observation.EligibleAfterMs >= s.LocalStartMs && observation.EligibleAfterMs <= s.LocalStartMs + s.CursorEndMs - s.CursorStartMs).ToArray();
        return segments.Length == 1 && view.Context.CursorMs >= segments[0].CursorStartMs + observation.EligibleAfterMs - segments[0].LocalStartMs;
    }
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Text(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) return false;
        try { _ = text.Normalize(); return true; } catch (ArgumentException) { return false; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
