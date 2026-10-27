using System.Text.Json;
using Matchmate.Contracts;

namespace Matchmate.Core;

// The ledger supplies all latest application/knowledge-selected records, including inactive and
// unreleased actions. Filtering those first could silently publish a truncated control spell.
public static class FootballStoryProjector
{
    public const int MaxDurationMs = 60000, MaxActions = 12;
    private const string Policy = "completed-generated-control-spell-v1";

    public static FootballStory? Project(FootballDataset dataset, FootballQuery query,
        IEnumerable<FootballRecord> selectedRecords, IReadOnlySet<string> eligibleRecordIds)
    {
        // Control/action rows do not declare their dependencies. Only a complete closed head can
        // establish full membership; an open/earlier/known-then selection could omit an unseen child.
        if (!dataset.Closed || dataset.PendingRecords != 0 || query.ApplicationSequence is not null && query.ApplicationSequence != dataset.HeadSequence
            || query.KnowledgeMode is not ("retrospective_replay" or "completed_reference")
            || query.KnowledgeAsOfUtc is not null || query.EffectiveDate is not null
            || !dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
            || dataset.Fixture is not { } fixture || dataset.Schedule is not { } schedule || dataset.People.Length == 0
            || dataset.Manifest.DatasetId != query.DatasetId || dataset.Manifest.DatasetVersion != query.DatasetVersion
            || dataset.Manifest.FixtureId != query.FixtureId || dataset.Stream.DatasetId != query.DatasetId
            || dataset.Stream.DatasetVersion != query.DatasetVersion || dataset.Stream.FixtureId != query.FixtureId
            || dataset.Stream.StreamId != query.StreamId || schedule.PolicyId != query.PresentationPolicyId
            || schedule.PolicyRevision != query.PresentationPolicyRevision || query.CursorMs < 0
            || dataset.HeadSequence < 1 || query.ApplicationSequence is < 1 || query.ApplicationSequence > dataset.HeadSequence
            || !dataset.Manifest.QueryModes.Contains(query.KnowledgeMode)
            || query.KnowledgeMode == "known_then" && query.KnowledgeAsOfUtc is null
            || fixture.Home.Id == fixture.Away.Id || fixture.Periods.Select(p => p.Id).Distinct().Count() != fixture.Periods.Length
            || dataset.People.Select(p => p.PersonId).Distinct().Count() != dataset.People.Length) return null;

        var actions = selectedRecords.Where(r => r.Payload is FootballActionPayload).ToArray();
        if (actions.Length > FootballActionPolicy.MaxGeneratedActionRecordIds
            || actions.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != actions.Length) return null;
        var teams = new[] { fixture.Home.Id, fixture.Away.Id };
        var people = dataset.People.ToDictionary(p => p.PersonId, p => p.TeamId, StringComparer.Ordinal);
        bool Roster(string? person, string team) => person is null || people.TryGetValue(person, out var own) && own == team;
        var intervals = new Dictionary<string, (long Start, long End)>(StringComparer.Ordinal);
        foreach (var record in actions)
        {
            // Reuse the canonical schema validator and canonical record hash, rather than a parallel codec.
            try { FootballCodec.ValidateMetadataRecord(record, dataset.Manifest); }
            catch (RecordingValidationException) { return null; }
            var action = (FootballActionPayload)record.Payload;
            var time = record.FootballTime!;
            var parts = schedule.Segments.Where(s => s.PeriodId == time.PeriodId && FootballPresentationPolicy.IsPlay(s)).ToArray();
            if (parts.Length != 1 || !fixture.Periods.Any(p => p.Id == time.PeriodId)
                || !teams.Contains(action.TeamId) || !Roster(action.PersonId, action.TeamId)
                || action.RecipientTeamId is not null && (!teams.Contains(action.RecipientTeamId) || !Roster(action.RecipientId, action.RecipientTeamId))
                || record.Conflicts is { Length: > 0 }) return null;
            var part = parts[0];
            var start = time.StartMs!.Value; var end = time.EndMs!.Value;
            if (part.CursorStartMs < 0 || part.CursorEndMs <= part.CursorStartMs || part.LocalStartMs < 0
                || start < part.LocalStartMs || end - part.LocalStartMs > part.CursorEndMs - part.CursorStartMs
                || start - part.LocalStartMs > long.MaxValue - part.CursorStartMs
                || end - part.LocalStartMs > long.MaxValue - part.CursorStartMs) return null;
            var cursorStart = part.CursorStartMs + start - part.LocalStartMs;
            var cursorEnd = part.CursorStartMs + end - part.LocalStartMs;
            var releases = schedule.Releases.Where(r => r.RecordId == record.RecordId).ToArray();
            if (releases.Length > 1 || releases.Any(r => r.CursorMs < cursorEnd || r.CursorMs > schedule.Segments[^1].CursorEndMs)) return null;
            intervals.Add(record.RecordId, (cursorStart, cursorEnd));
        }

        var controls = actions.Where(r => ((FootballActionPayload)r.Payload).ActionKind == "possession")
            .OrderBy(r => intervals[r.RecordId].Start).ThenBy(r => r.RecordId, StringComparer.Ordinal).ToArray();
        var children = actions.Where(r => ((FootballActionPayload)r.Payload).ActionKind != "possession")
            .OrderBy(r => intervals[r.RecordId].Start).ThenBy(r => r.RecordId, StringComparer.Ordinal).ToArray();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var passages = new List<FootballStoryPassage>();
        string? scheduleFingerprint = null;
        long previousEnd = -1;
        foreach (var control in controls)
        {
            var action = (FootballActionPayload)control.Payload; var time = control.FootballTime!;
            var (start, end) = intervals[control.RecordId];
            if (start < previousEnd || end - start > MaxDurationMs) return null;
            previousEnd = end;
            var members = children.Where(r => r.FootballTime!.PeriodId == time.PeriodId
                && ((FootballActionPayload)r.Payload).TeamId == action.TeamId
                && intervals[r.RecordId].Start >= start && intervals[r.RecordId].End <= end).ToArray();
            if (members.Length > MaxActions) return null;
            long memberEnd = start;
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i]; var value = (FootballActionPayload)member.Payload; var interval = intervals[member.RecordId];
                if (!assigned.Add(member.RecordId) || interval.Start < memberEnd
                    || (value.Outcome == "turnover" || value.ActionKind == "shot") && (i != members.Length - 1 || interval.End != end)) return null;
                memberEnd = interval.End;
            }
            var dependencies = new[] { control }.Concat(members).ToArray();
            // Eligibility remains the ledger's authority. Recheck completion/release defensively here too.
            if (dependencies.Any(r => r.RecordState != "active" || !eligibleRecordIds.Contains(r.RecordId)
                || intervals[r.RecordId].End > query.CursorMs
                || schedule.Releases.Any(release => release.RecordId == r.RecordId && release.CursorMs > query.CursorMs))) continue;
            var payloads = members.Select(r => (FootballActionPayload)r.Payload).ToArray();
            var ending = payloads.LastOrDefault() is { } last && last.ActionKind == "shot" ? "shot"
                : payloads.LastOrDefault()?.Outcome == "turnover" ? "turnover"
                : fixture.Periods.Single(p => p.Id == time.PeriodId).ActualDurationMs == time.EndMs ? "period_end" : "control_end";
            var evidence = dependencies.Select(r => new FootballStoryEvidence(r.RecordId, r.Revision, FootballCodec.ContentHash(r),
                r.Evidence.Select(e => e.EvidenceId).Order(StringComparer.Ordinal).ToArray())).ToArray();
            var passage = new FootballStoryPassage("passage:" + control.RecordId, time.PeriodId, action.TeamId, start, end,
                payloads.Count(p => p.ActionKind == "pass"), payloads.Count(p => p.ActionKind == "pass" && p.Outcome == "completed"),
                payloads.Count(p => p.Outcome == "turnover"), payloads.Count(p => p.ActionKind == "shot"), ending, evidence, "");
            scheduleFingerprint ??= ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(schedule, FootballCodec.Json));
            var fingerprint = ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                Policy, query.DatasetId, query.DatasetVersion, query.StreamId, query.FixtureId,
                query.PresentationPolicyId, query.PresentationPolicyRevision,
                ScheduleFingerprint = scheduleFingerprint,
                Passage = passage
            }, FootballCodec.Json));
            passages.Add(passage with { Fingerprint = fingerprint });
        }
        return assigned.Count == children.Length ? new(passages.ToArray()) : null;
    }
}
