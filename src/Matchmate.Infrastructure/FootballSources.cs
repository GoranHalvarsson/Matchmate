using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

internal static class FootballSourceMapping
{
    public static IdentityMapping Map(string canonical, string space, object original) => new(canonical, space, JsonSerializer.SerializeToElement(original));
    public static long Ms(double seconds) => checked((long)Math.Round(seconds * 1000));
    public static FootballRecord Record(FootballManifest manifest, string id, FootballPayload payload, FootballEvidence[] evidence,
        FootballTime? time = null, Dictionary<string, string[]>? fields = null) => new(FootballContract.Version, manifest.DatasetId, manifest.DatasetVersion,
            manifest.FixtureId, [], id, 1, null, "active", time, null, evidence, fields ?? [], payload);
    public static FootballEvidence Evidence(string id, string source, string locator, string method, string origin = "real", string? flags = null) =>
        new(id, source, locator, origin, method, method == "generated" ? "authored" : method == "reported" ? "reported" : "confirmed", null, flags);
}

public sealed class AustralianFootballSource(string trackingDirectory, string possessionsPath, bool fullCoverage = false) : IFootballSource
{
    public async Task<IReadOnlyList<FootballBatch>> ReadAsync(CancellationToken token)
    {
        var source = await new SkillCornerFileSource(trackingDirectory).ReadAsync(token);
        var document = source.Document; var fixtureId = "fixture-skillcorner-1874553"; var periodId = "period-skillcorner-1874553-first";
        string Person(int id) => "person-skillcorner-" + id;
        string Club(int id) => "team-skillcorner-" + id;
        const string ball = "ball-skillcorner-1874553-active";
        var mappings = new List<IdentityMapping> { FootballSourceMapping.Map(fixtureId, "skillcorner-match", document.MatchId), FootballSourceMapping.Map(periodId, "skillcorner-period", document.Period),
            FootballSourceMapping.Map(ball, "logical-active-ball", "physical identity unknown"), FootballSourceMapping.Map(Club(document.Home.Id), "skillcorner-team", document.Home.Id), FootballSourceMapping.Map(Club(document.Away.Id), "skillcorner-team", document.Away.Id) };
        mappings.AddRange(document.Players.Select(p => FootballSourceMapping.Map(Person(p.Id), "skillcorner-person", p.Id)));
        var manifest = new FootballManifest(FootballContract.Version, "australian-recorded", (fullCoverage ? "aus-full-v1-" : "aus-v04-") + source.Hash, fixtureId, 1, "open",
            [new("skillcorner-recording", "1874553-60-240.json @ " + document.Pin, source.Hash), new("skillcorner-possessions", "1874553-completed-events.json", ReplayBatchCodec.Hash(await BoundedFile.ReadAsync(possessionsPath, 64000, token)))], mappings.ToArray(),
            ["tracking_frame", "player_possession", "explicit-source-gaps", fullCoverage ? "complete-available-1801-frames" : "bounded-historical-slice"], ["retrospective_replay", "completed_reference"],
            fullCoverage ? "aus-fixed-top-left-full-v1: all 1801 available source frames and all 5 completed possessions, 60..240s inclusive at100ms; x=sourceX+length/2,y=width/2-sourceY; original gaps/estimates retained; no whole-match claim"
            : "aus-centre-to-fixed-top-left-v1: x=sourceX+length/2; y=width/2-sourceY; 67 selected samples from the unchanged 1801-frame source: bounded 60-65.5s window plus possession dependencies and gap samples; an unsampled cursor is not evidence of a source gap; no interpolation outside samples", []);
        FootballEvidence Evidence(string id, string method, string locator) => FootballSourceMapping.Evidence(id, "skillcorner-recording", locator, method);
        var records = new List<FootballRecord>
        {
            FootballSourceMapping.Record(manifest, "fixture-context", new FixturePayload(document.Label, document.Competition, new(Club(document.Home.Id), document.Home.Name), new(Club(document.Away.Id), document.Away.Name),
                new(document.Pitch.Length, document.Pitch.Width), [new(periodId, 1, "First half", 0, null)]), [Evidence("source", "reported", "source metadata")]),
            FootballSourceMapping.Record(manifest, "match-squad", new SquadPayload(document.Players.Select(p => new SquadPerson(Person(p.Id), Club(p.TeamId), p.Name, p.Number, "unknown")).ToArray()), [Evidence("source", "reported", "source roster/crosswalk")]),
            FootballSourceMapping.Record(manifest, "presentation-policy", new SchedulePayload("source-clock-replay", 1, [new(periodId, 60000, 240000, 60000)], [],
                [new("Recording start", 60000), new("Source gap 1", 175900), new("Source gap 2", 198800)], 100, "source_sample"), [Evidence("source", "computed", "identity mapping of source sample clock to replay cursor")])
        };
        var active = document.Players.Select(p => Person(p.Id)).ToArray();
        var representativeTimes = new HashSet<long> { 69200, 72800, 131300, 167700, 175900, 176000, 189800, 198800, 198900, 219500, 240000 };
        foreach (var frame in document.Frames.Where(f => fullCoverage || f.Clock <= 65.5 || representativeTimes.Contains(FootballSourceMapping.Ms(f.Clock))))
        {
            var locator = "frame " + frame.FrameId + " / " + frame.SourceTimestamp;
            var evidence = new[] { Evidence("observed", "observed", locator), Evidence("estimated", "estimated", locator), Evidence("unknown", "unknown", locator) };
            FootballObservation Observation(string subject, double? x, double? y, bool? detected, string? reason) => new(subject, null, x.HasValue && y.HasValue ? "present" : "missing",
                x + document.Pitch.Length / 2, y.HasValue ? document.Pitch.Width / 2 - y : null, null,
                detected == true ? "observed" : detected == false ? "estimated" : "unknown", "unknown", [detected == true ? "observed" : detected == false ? "estimated" : "unknown"], detected, reason);
            var players = frame.Players.Select(p => Observation(Person(p.PlayerId), p.X, p.Y, p.IsDetected, p.MissingReason)).ToArray();
            var fields = players.Select((p, i) => (Path: "/payload/players/" + i, p.EvidenceIds)).ToDictionary(p => p.Path, p => p.EvidenceIds);
            var ballPosition = Observation(ball, frame.Ball.X, frame.Ball.Y, frame.Ball.IsDetected, frame.Ball.MissingReason); fields["/payload/ball"] = ballPosition.EvidenceIds;
            var time = FootballSourceMapping.Ms(frame.Clock);
            records.Add(FootballSourceMapping.Record(manifest, "frame-" + frame.FrameId, new TrackingPayload(frame.FrameId.ToString(), frame.SourceTimestamp, players, ballPosition, active, frame.FrameState), evidence,
                new(periodId, time, time, "source_sample", true), fields));
        }
        using var possessions = JsonDocument.Parse(await BoundedFile.ReadAsync(possessionsPath, 64000, token));
        foreach (var entry in possessions.RootElement.GetProperty("annotations").EnumerateArray())
        {
            var id = entry.GetProperty("sourceId").GetString()!;
            var start = FootballSourceMapping.Ms(entry.GetProperty("start").GetDouble()); var end = FootballSourceMapping.Ms(entry.GetProperty("end").GetDouble());
            records.Add(FootballSourceMapping.Record(manifest, "possession-" + id.Replace('_', '-'), new PossessionPayload(Person(entry.GetProperty("actorId").GetInt32()), Club(entry.GetProperty("teamId").GetInt32()),
                entry.GetProperty("frameStart").ToString(), entry.GetProperty("frameEnd").ToString(), entry.GetProperty("startMatched").GetBoolean(), entry.GetProperty("endMatched").GetBoolean(), end, "frame-" + entry.GetProperty("frameEnd").GetInt32()),
                [FootballSourceMapping.Evidence("reported", "skillcorner-possessions", "source annotation " + id, "reported", flags: entry.GetRawText())], new(periodId, start, end, "source_interval", start == end ? true : null)));
        }
        foreach (var (start, end) in new[] { (175900L, 189800L), (198800L, 219500L) })
            records.Add(FootballSourceMapping.Record(manifest, "coverage-gap-" + start, new CoveragePayload(active, "missing", 100, "Explicit empty player source frames; independent ball retained"), [Evidence("source", "computed", "consecutive empty source samples")], new(periodId, start, end, "source_interval", true)));
        return FootballCodec.Batches(manifest, new(fullCoverage ? "run-aus-full-v1-" + source.Hash[..12] : "run-australian-v04", manifest.DatasetId, manifest.DatasetVersion, fixtureId, "replay"), records);
    }
}

public sealed class BrentfordFootballSource(string exportPath) : IFootballSource
{
    public const string EventNormalizationPolicy = "event-context-v1;optional-attributed-details;explicit-event-associations-only;no-media-clock-join";
    public const string SequenceNormalizationPolicy = "shared-event-sequence-v1;explicit-event-id;authored-layout-not-tracking;completed-possessions-only";
    public const string ParticipantEventNormalizationPolicy = "event-context-v2;accepted-goal-participants-v1;reported-actions-separate-from-native-points;no-assist-inference";
    public const string ParticipantSequenceNormalizationPolicy = "shared-event-sequence-v2;accepted-goal-participants-v1;authored-layout-not-tracking;no-cut-bridge";
    public async Task<IReadOnlyList<FootballBatch>> ReadAsync(CancellationToken token)
    {
        using var export = JsonDocument.Parse(await BoundedFile.ReadAsync(exportPath, 5_000_000, token)); var root = export.RootElement;
        var adapted = Adapt(root, false); return FootballCodec.Batches(adapted.Manifest, adapted.Stream, adapted.Records);
    }
    internal static (FootballManifest Manifest, FootballStream Stream, FootballRecord[] Records) Adapt(JsonElement root, bool fullCoverage)
    {
        if (root.GetProperty("exportVersion").GetInt32() != (fullCoverage ? 2 : 1)) throw new RecordingValidationException("Unsupported existing model export.");
        string Person(string id) => "person-pl-reference-" + id.Replace(':', '-');
        string Club(string id) => "team-pl-reference-" + id;
        const string fixtureId = "fixture-pl-2645236", first = "period-pl-2645236-first", second = "period-pl-2645236-second", ball = "ball-pl-2645236-active";
        var people = root.GetProperty("people").EnumerateArray().ToArray();
        var mappings = new List<IdentityMapping> { FootballSourceMapping.Map(fixtureId, "premier-league-match", "2645236"), FootballSourceMapping.Map(first, "authored-period", 1), FootballSourceMapping.Map(second, "authored-period", 2),
            FootballSourceMapping.Map(ball, "logical-active-ball", "physical identity unknown"), FootballSourceMapping.Map(Club("brentford"), "official-team", "brentford"), FootballSourceMapping.Map(Club("chelsea"), "official-team", "chelsea") };
        mappings.AddRange(people.Select(p => FootballSourceMapping.Map(Person(p.GetProperty("id").GetString()!), "official-person", p.GetProperty("id").GetString()!)));
        var enriched = root.TryGetProperty("enrichment", out var enrichment);
        var sequenced = root.TryGetProperty("sequences", out var sequences);
        var participants = enriched && enrichment.GetProperty("version").GetInt32() == 2;
        var generatedActions = root.TryGetProperty("generatedActions", out var actionReceipt);
        if (generatedActions && (!participants || !fullCoverage || actionReceipt.GetProperty("version").GetInt32() != 1
            || actionReceipt.GetProperty("file").GetString() != "generated-actions.json" || actionReceipt.GetProperty("policy").GetString() != FootballActionPolicy.GeneratedModelActionsV1
            || actionReceipt.GetProperty("count").GetInt32() is < 1 or > 2000
            || actionReceipt.GetProperty("sha256").GetString() != root.GetProperty("hashes").GetProperty("generated-actions.json").GetString()))
            throw new RecordingValidationException("Invalid generated model action receipt.");
        var contextPolicy = participants ? ParticipantEventNormalizationPolicy : EventNormalizationPolicy;
        var semanticPolicy = participants ? ParticipantSequenceNormalizationPolicy : SequenceNormalizationPolicy;
        var contextPath = $"data/match-enrichment/brentford-chelsea-2026-09-18-event-context-v{(participants ? 2 : 1)}.json";
        var sequencePath = $"data/match-enrichment/brentford-chelsea-2026-09-18-sequences-v{(participants ? 2 : 1)}.json";
        if (participants && (!fullCoverage || !sequenced)) throw new RecordingValidationException("Participant enrichment requires the complete shared sequence input.");
        if (sequenced != root.TryGetProperty("sequencePolicy", out _) || sequenced != root.GetProperty("hashes").TryGetProperty(participants ? "normalization:shared-event-sequence-v2" : "normalization:shared-event-sequence-v1", out _))
            throw new RecordingValidationException("Incomplete shared sequence policy/input receipt.");
        if (sequenced && (!fullCoverage || !enriched || sequences.GetProperty("version").GetInt32() != (participants ? 2 : 1)
            || sequences.GetProperty("fixtureId").GetString() != fixtureId || sequences.GetProperty("policy").GetString() != (participants ? "shared-event-sequence-v2" : "shared-event-sequence-v1")
            || root.GetProperty("sequencePolicy").GetString() != semanticPolicy)) throw new RecordingValidationException("Unknown shared sequence policy.");
        if (enriched && (enrichment.GetProperty("version").GetInt32() is not 1 and not 2 || enrichment.GetProperty("fixtureId").GetString() != fixtureId
            || root.GetProperty("normalizationPolicy").GetString() != contextPolicy)) throw new RecordingValidationException("Unknown event enrichment policy.");
        var generation = generatedActions ? ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(new { hashes = root.GetProperty("hashes"), policy = contextPolicy, sequencePolicy = semanticPolicy, actionPolicy = FootballActionPolicy.GeneratedModelActionsV1 }))
            : sequenced ? ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(new { hashes = root.GetProperty("hashes"), policy = contextPolicy, sequencePolicy = semanticPolicy }))
            : ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(new { hashes = root.GetProperty("hashes"), policy = EventNormalizationPolicy }));
        var version = (generatedActions ? "brent-full-v5-" : participants ? "brent-full-v4-" : sequenced ? "brent-full-v3-" : fullCoverage ? "brent-full-v2-" : "brent-v05-") + generation;
        var sources = new List<SourceReference> { new("official-record", "data/official-match-records/brentford-chelsea-2026-09-18.json", root.GetProperty("hashes").GetProperty("data/official-match-records/brentford-chelsea-2026-09-18.json").GetString()),
             new("existing-model", "prototypes/first-experience/pl-reconstructed-model.js", root.GetProperty("hashes").GetProperty("prototypes/first-experience/pl-reconstructed-model.js").GetString()),
             new("authored-config", "data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json", root.GetProperty("hashes").GetProperty("data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json").GetString()) };
        if (enriched)
        {
            sources.Add(new("event-enrichment", contextPath, root.GetProperty("hashes").GetProperty(contextPath).GetString()));
            sources.AddRange(enrichment.GetProperty("sources").EnumerateArray().Select(source => new SourceReference(source.GetProperty("id").GetString()!, source.GetProperty("path").GetString()!, source.GetProperty("sha256").GetString())));
            var associated = enrichment.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("recordId").GetString()).ToArray();
            if (associated.Length > 14 || associated.Distinct().Count() != associated.Length
                || associated.Any(id => !root.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("id").GetString() == id))) throw new RecordingValidationException("Invalid explicit enrichment associations.");
        }
        if (sequenced)
        {
            sources.Add(new("event-sequences", sequencePath, root.GetProperty("hashes").GetProperty(sequencePath).GetString()));
            var associated = sequences.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("recordId").GetString()).ToArray();
            if (associated.Length != 14 || associated.Distinct().Count() != 14 || associated.Any(id => !root.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("id").GetString() == id)))
                throw new RecordingValidationException("Shared sequences must explicitly associate all fourteen reports.");
        }
        if (generatedActions) sources.Add(new("generated-model-actions", "generated-actions.json", actionReceipt.GetProperty("sha256").GetString()));
        var manifest = new FootballManifest(FootballContract.Version, "brentford-existing-illustration", version, fixtureId, generatedActions ? 6 : participants ? 5 : sequenced ? 4 : enriched ? 3 : 2, "open", sources.ToArray(), mappings.ToArray(),
            new[] { "tracking_frame", "match_event", "attributed-event-details", "unaligned-commentary-not-exposed", fullCoverage ? "complete-existing-generated-100ms-model" : "sparse-generated-windows-only", fullCoverage ? "all-14-existing-selected-reports" : "selected-reported-events-only" }.Concat(sequenced ? ["shared-event-sequences-v1"] : Array.Empty<string>()).Concat(generatedActions ? [FootballActionPolicy.GeneratedModelActionsV1] : Array.Empty<string>()).ToArray(), ["retrospective_replay", "completed_reference"],
            contextPolicy + (fullCoverage ? "; all58801 generated frames0..5880s at100ms,40people/22active slots,14reports; authored firsthalf2820s; no sourceUTCavailability. Completed statistics/formations/result metadata remain in original officialJSON, not cursor projections."
            : "; 100ms samples only in declared tiny windows; authored 2820s first-half duration; no source UTC availability") + "; unaligned source-media narrative is unavailable at replay cursors." + (sequenced ? "; " + semanticPolicy : "")
            + (generatedActions ? "; generated-model-actions-v1: passes, goal shots and completed team-control spells describe the illustration only, never actual match statistics; unknown/restart/hold time excluded." : ""), []);
        var reported = FootballSourceMapping.Evidence("reported", "official-record", "existing official record", "reported");
        var generated = FootballSourceMapping.Evidence("generated", "existing-model", root.GetProperty("sampling").GetString()!, "generated", "synthetic");
        var authored = FootballSourceMapping.Evidence("authored", "authored-config", "pitch and authored period lengths/mapping", "generated", "synthetic");
        var squad = people.Select(p => new SquadPerson(Person(p.GetProperty("id").GetString()!), Club(p.GetProperty("team").GetString()!), p.GetProperty("name").GetString()!, p.GetProperty("shirt").GetInt32(), p.GetProperty("status").GetString()!)).ToArray();
        var releases = new List<PresentationRelease>(); var eventRecords = new List<FootballRecord>();
        var actionRecords = new List<FootballRecord>();
        if (root.TryGetProperty("generatedActionData", out var actionData))
        {
            if (!generatedActions || actionData.GetProperty("version").GetInt32() != 1 || actionData.GetProperty("fixtureId").GetString() != fixtureId
                || actionData.GetProperty("policy").GetString() != FootballActionPolicy.GeneratedModelActionsV1
                || actionData.GetProperty("durationMs").GetInt64() != 5880000 || actionData.GetProperty("halfMs").GetInt64() != 2820000
                || actionData.GetProperty("rows").GetArrayLength() != actionReceipt.GetProperty("count").GetInt32()) throw new RecordingValidationException("Generated action schema/binding mismatch.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? Optional(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
            foreach (var row in actionData.GetProperty("rows").EnumerateArray())
            {
                var id = row.GetProperty("recordId").GetString()!; var period = row.GetProperty("period").GetInt32();
                var start = row.GetProperty("startMs").GetInt64(); var end = row.GetProperty("endMs").GetInt64();
                var team = row.GetProperty("team").GetString()!; var person = Optional(row, "playerId"); var recipient = Optional(row, "recipientId"); var recipientTeam = Optional(row, "recipientTeam");
                bool Roster(string? player, string club) => player is null || people.Any(p => p.GetProperty("id").GetString() == player && p.GetProperty("team").GetString() == club);
                if (!seen.Add(id) || !id.StartsWith($"generated-action-v1-p{period}-", StringComparison.Ordinal)
                    || period is not 1 and not 2 || start < (period == 1 ? 0 : 2820000) || end <= start || end > (period == 1 ? 2820000 : 5880000)
                    || team is not "brentford" and not "chelsea" || !Roster(person, team)
                    || recipientTeam is not null && (recipientTeam is not "brentford" and not "chelsea" || !Roster(recipient, recipientTeam))) throw new RecordingValidationException("Generated action interval/identity/team mismatch.");
                var payload = new FootballActionPayload(row.GetProperty("actionKind").GetString()!, Club(team), person is null ? null : Person(person), recipient is null ? null : Person(recipient),
                    recipientTeam is null ? null : Club(recipientTeam), row.GetProperty("outcome").GetString()!, Optional(row, "fromSlotId"), Optional(row, "toSlotId"));
                var evidence = FootballSourceMapping.Evidence("generated-action", "generated-model-actions", "generated-actions.json / " + id + "; authored model action, not actual match evidence", "generated", "synthetic");
                var fields = new[] { "/footballTime", "/payload/actionKind", "/payload/teamId", "/payload/personId", "/payload/recipientId", "/payload/recipientTeamId", "/payload/outcome", "/payload/fromSlotId", "/payload/toSlotId" }
                    .ToDictionary(path => path, _ => new[] { evidence.EvidenceId });
                var offset = period == 1 ? 0 : 2820000;
                var record = FootballSourceMapping.Record(manifest, id, payload, [evidence], new(period == 1 ? first : second, start - offset, end - offset, "source_interval", false), fields)
                    with { EntityRefs = new[] { payload.TeamId, payload.PersonId, payload.RecipientId, payload.RecipientTeamId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray() };
                FootballCodec.ValidateMetadataRecord(record, manifest);
                actionRecords.Add(record); releases.Add(new(id, end));
            }
        }
        foreach (var entry in root.GetProperty("events").EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString()!; var kind = entry.GetProperty("kind").GetString()!;
            var time = entry.GetProperty("time"); var minute = time.GetProperty("minute").GetInt32();
            var added = time.TryGetProperty("added_minute", out var addedValue) && addedValue.ValueKind == JsonValueKind.Number ? addedValue.GetInt32() : 0;
            var eventPeriod = minute <= 45 ? 1 : 2;
            var release = (minute + added) * 60000L + (eventPeriod == 1 ? 0 : 120000);
            if (root.TryGetProperty("eventTimings", out var timings))
            {
                var mapping = timings.EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
                eventPeriod = mapping.GetProperty("period").GetInt32(); release = mapping.GetProperty("releaseMs").GetInt64();
            }
            var localEnd = release - (eventPeriod == 1 ? 0 : 2820000); var localStart = localEnd - 60000;
            string? GetPerson(string key) => entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Person(value.GetString()!) : null;
            string? assist = entry.TryGetProperty("assist", out var a) && a.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.String ? Person(p.GetString()!) : null;
            // A reported goal does not invent a source decision or exact second.
            var detail = new List<MatchEventDetail>();
            var eventEvidence = new List<FootballEvidence> { reported with { Locator = "event " + id + "; " + entry.GetProperty("evidence").GetProperty("source").GetString() + "; " + entry.GetProperty("evidence").GetProperty("locator").GetString(), OriginalFlags = entry.GetRawText() } };
            string Name(string? personId) => people.FirstOrDefault(p => p.GetProperty("id").GetString() == personId).GetProperty("name").GetString()!;
            if (entry.TryGetProperty("finish", out var finish))
            {
                detail.Add(new("finish", "Reported finish: " + finish.GetProperty("body_part").GetString()!.Replace('_', ' ') + "; " + finish.GetProperty("source_location_description").GetString() + ".", ["reported"]));
                if (finish.TryGetProperty("source_sequence", out var sequence) && sequence.ValueKind == JsonValueKind.String) detail.Add(new("build_up", "Reported sequence: " + sequence.GetString() + ".", ["reported"]));
            }
            if (entry.TryGetProperty("assist", out var assistEntry)) detail.Add(new("assist", assist is null ? "No explicit assist credit in the inspected goal entry."
                : "Explicitly credited assist: " + Name(assistEntry.GetProperty("player").GetString()) + "; " + assistEntry.GetProperty("evidence").GetProperty("locator").GetString() + ".", ["reported"]));
            if (entry.TryGetProperty("reason", out var reason)) detail.Add(new("reason", "Reported reason: " + reason.GetString() + ".", ["reported"]));
            if (kind == "substitution") detail.Add(new("substitution", Name(entry.GetProperty("on").GetString()) + " replaces " + Name(entry.GetProperty("off").GetString()) + ".", ["reported"], "Reported minute only; same-minute source order is not exact on-field order."));
            if (entry.TryGetProperty("disagreements", out var disagreements)) foreach (var disagreement in disagreements.EnumerateArray())
            {
                var claim = disagreement.GetProperty("evidence");
                eventEvidence.Add(reported with { EvidenceId = "reported-disagreement", Locator = claim.GetProperty("source").GetString() + "; " + claim.GetProperty("locator").GetString(), ClaimStatus = "disputed" });
                detail.Add(new("reported_minute_disagreement", "Retained report gives minute " + disagreement.GetProperty("value").GetString() + ".", ["reported-disagreement"], "The competition record supplies the selected reported minute; this differing club-report claim remains attributed."));
            }
            if (enriched) foreach (var association in enrichment.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("recordId").GetString() == id))
            {
                if (association.TryGetProperty("evidence", out var extraEvidence)) foreach (var source in extraEvidence.EnumerateArray())
                    eventEvidence.Add(new(source.GetProperty("evidenceId").GetString()!, source.GetProperty("sourceId").GetString()!, source.GetProperty("locator").GetString()!, "real", source.GetProperty("method").GetString()!, source.GetProperty("claimStatus").GetString()!));
                foreach (var claim in association.GetProperty("details").EnumerateArray())
                {
                    var evidenceId = claim.GetProperty("evidenceId").GetString()!;
                    eventEvidence.Add(new(evidenceId, claim.GetProperty("sourceId").GetString()!, claim.GetProperty("locator").GetString()!, "real", claim.GetProperty("method").GetString()!, claim.GetProperty("claimStatus").GetString()!));
                    detail.Add(new(claim.GetProperty("kind").GetString()!, claim.GetProperty("text").GetString()!, [evidenceId], claim.GetProperty("uncertainty").GetString()));
                }
            }
            MatchEventSequence? semantic = null;
            if (sequenced)
            {
                var sequence = sequences.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("recordId").GetString() == id);
                string? Actor(JsonElement step, string key) => step.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Person(step.GetProperty(key).GetString()!);
                semantic = new(sequence.GetProperty("version").GetInt32(), sequence.GetProperty("id").GetString()!, sequence.GetProperty("presentationLeadInMs").GetInt32(), sequence.GetProperty("presentationTailMs").GetInt32(),
                    sequence.GetProperty("steps").EnumerateArray().Select(step => new MatchEventStep(step.GetProperty("id").GetString()!, step.GetProperty("kind").GetString()!, Actor(step, "actorId"), Actor(step, "relatedActorId"),
                        step.GetProperty("presentationOffsetMs").GetInt32(), step.GetProperty("text").GetString()!, step.GetProperty("evidenceIds").EnumerateArray().Select(v => v.GetString()!).ToArray(), step.GetProperty("uncertainty").GetString())).ToArray());
                eventEvidence.Add(FootballSourceMapping.Evidence("sequence-layout", "event-sequences", "event " + id + "; authored schematic offsets and geometry only", "generated", "synthetic"));
                if (participants)
                {
                    var semanticEvidence = semantic.Steps.SelectMany(step => step.EvidenceIds).ToHashSet(StringComparer.Ordinal);
                    eventEvidence = eventEvidence.OrderByDescending(evidence => semanticEvidence.Contains(evidence.EvidenceId)).ToList();
                }
            }
            var groupId = sequenced && kind == "substitution" ? "substitutions-" + release : null;
            var payload = new MatchEventPayload(kind, Club(entry.GetProperty("team").GetString()!), GetPerson("player"), assist, null, null, GetPerson("off"), GetPerson("on"), groupId, detail.ToArray(), semantic);
            var fields = detail.Select((claim, index) => (Path: "/payload/details/" + index, claim.EvidenceIds)).ToDictionary(d => d.Path, d => d.EvidenceIds);
            if (semantic is not null)
            {
                fields["/payload/sequence/presentationLeadInMs"] = ["sequence-layout"];
                fields["/payload/sequence/presentationTailMs"] = ["sequence-layout"];
                foreach (var (step, index) in semantic.Steps.Select((step, index) => (step, index)))
                { fields["/payload/sequence/steps/" + index + "/text"] = step.EvidenceIds; fields["/payload/sequence/steps/" + index + "/presentationOffsetMs"] = ["sequence-layout"]; }
            }
            if (assist is not null) fields["/payload/assistPersonId"] = ["reported"];
            eventRecords.Add(FootballSourceMapping.Record(manifest, id, payload, eventEvidence.ToArray(), new(eventPeriod == 1 ? first : second, localStart, localEnd, "reported_minute", false, time.GetProperty("printed").GetString()), fields));
            releases.Add(new(id, release, groupId));
        }
        var records = new List<FootballRecord>
        {
            FootballSourceMapping.Record(manifest, "fixture-context", new FixturePayload("Brentford vs Chelsea", "Premier League", new(Club("brentford"), "Brentford"), new(Club("chelsea"), "Chelsea"), new(105, 68),
                [new(first, 1, "First half", 0, null), new(second, 2, "Second half", 2700000, null)]), [reported, authored], fields: new() { ["/payload/home"] = ["reported"], ["/payload/away"] = ["reported"], ["/payload/competition"] = ["reported"], ["/payload/pitch"] = ["authored"], ["/payload/periods"] = ["authored"] }),
            FootballSourceMapping.Record(manifest, "match-squad", new SquadPayload(squad), [reported]),
            FootballSourceMapping.Record(manifest, "presentation-policy", new SchedulePayload("existing-authored-minute-end", 1, [new(first, 0, 2820000, 0), new(second, 2820000, 5880000, 0)], releases.ToArray(),
                fullCoverage ? new[] { new PresentationMilestone("Opening", 0), new("Half-time · authored", 2820000) }.Concat(releases.Where(r => !r.RecordId.StartsWith("generated-action-", StringComparison.Ordinal)).Select(r => new PresentationMilestone(r.RecordId + " · authored release", r.CursorMs))).OrderBy(m => m.CursorMs).ToArray()
                : [new("Opening sample", 0), new("Half-time / second-half start · authored", 2820000), new("Reported goal 1 · authored release", 3780000), new("Reported substitution 5 · authored release", 4260000)], 100, "generated/authored; actual seconds and period duration unknown"), [generated])
        };
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            var cursor = FootballSourceMapping.Ms(frame.GetProperty("time").GetDouble()); var period = frame.GetProperty("period").GetInt32(); var local = period == 1 ? cursor : cursor - 2820000;
            var positions = frame.GetProperty("players").EnumerateArray().Select(p => new FootballObservation(Person(p.GetProperty("player").GetProperty("id").GetString()!), p.GetProperty("slotId").GetString(), "present",
                p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), null, "generated", "unknown", ["generated"])).ToArray();
            var sourceBall = frame.GetProperty("ball");
            records.Add(FootballSourceMapping.Record(manifest, "sample-" + cursor, new TrackingPayload("model-" + cursor, null, positions,
                new(ball, null, "present", sourceBall.GetProperty("x").GetDouble(), sourceBall.GetProperty("y").GetDouble(), null, "generated", "unknown", ["generated"]), positions.Select(p => p.SubjectId).ToArray(), fullCoverage ? "generated-complete-model" : "generated-sampled-window"), [generated],
                new(period == 1 ? first : second, local, local, "source_sample", true), new() { ["/payload/players"] = ["generated"], ["/payload/ball"] = ["generated"] }));
        }
        records.AddRange(eventRecords);
        records.AddRange(actionRecords);
        records.Add(FootballSourceMapping.Record(manifest, "tracking-coverage", new CoveragePayload([], fullCoverage ? "covered" : "not_collected", fullCoverage ? 100 : null, fullCoverage ? "Complete existing authored 100ms model; 22 active slots, not measured tracking or bench positions." : root.GetProperty("sampling").GetString()!), [generated]));
        return (manifest, new((generatedActions ? "run-brent-full-v5-" : participants ? "run-brent-full-v4-" : sequenced ? "run-brent-full-v3-" : fullCoverage ? "run-brent-full-v2-" : "run-brentford-v05-") + generation[..12], manifest.DatasetId, manifest.DatasetVersion, fixtureId, "replay"), records.ToArray());
    }
}
