using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

/// <summary>Upstream normalization of the frozen local illustration into the common football pipeline.</summary>
public sealed class LiverpoolFootballSource(string manifestPath) : IStreamingFootballSource
{
    public const string DatasetId = "brentford-liverpool-illustration";
    public const string ManifestHash = "f068a719fb27ba0c47acb1f5d342613578534a4eb06fdf1eee00aae45d4e87a0";
    public const string FactsHash = "d5a15af25696db2cd0c983d1869f6950074f6371d7689a8610a3f198775532cf";
    public const string GoalReviewHash = "45d0952330c2a49611648bd6adaff87854d6c1178591eaf9aa1f43d78b256c4a";
    public const string Normalization = "liverpool-generated-v2-ms-v1;generated-presentation-sample-not-football-seconds;round-seconds-to-nearest-ms-ties-away-from-zero;coalesce-equal-rounded-ms-latest-original-t-wins;35201-input-32701-output-2500-collisions;2-football-periods;60s-paused-halftime;reported-minute-intervals-and-null-exact-seconds-availability-retained;authored-releases-not-known-then;inactive-transitions-excluded;no-measured-movement";
    public const string SequencePolicy = "liverpool-reviewed-goal-sequences-v1;three-reported-steps-per-goal;names-actions-from-pinned-club-report-review-not-pixel-identity-or-asr-only;authored-offsets-minus12000-minus6000-zero-from-generated-goal-window;shared-schematic-geometry-generated-not-tracking;exact-contacts-source-seconds-and-availability-unknown";
    public const string CardPolicy = "reported-card-sequences-v1;six-bookings-two-dismissals;one-reported-actor-at-authored-release;reason-source-or-explicitly-unavailable;no-tackle-referee-or-dismissed-player-position-inferred";
    public const string ActionPolicy = "liverpool-generated-actions-v1;existing-pinned-authored-actions-only;pass-and-illustrated-goal-pass-as-pass;illustrated-finish-as-generated-goal-shot;carry-only-contributes-control;contiguous-same-team-control-spells;lead-in-hold-restart-reposition-halftime-excluded;participant-active-through-entire-interval;second-local-presentation-minus3420000;completed-end-release;never-actual-match-statistics";
    private const string FixtureId = "fixture-pl-2645256", First = "period-pl-2645256-first", Second = "period-pl-2645256-second", Ball = "ball-pl-2645256-active";
    private static string Person(string id) => "person-pl-reference-" + id.Replace(':', '-');
    private static string Team(string id) => "team-pl-reference-" + id;
    private static long Milliseconds(double seconds) => checked((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetString() : null;

    public async IAsyncEnumerable<FootballBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken token)
    {
        var manifestBytes = await BoundedFile.ReadAsync(manifestPath, 64000, token);
        if (ReplayBatchCodec.Hash(manifestBytes) != ManifestHash) throw new RecordingValidationException("Liverpool frozen manifest hash mismatch.");
        using var manifestDocument = JsonDocument.Parse(manifestBytes); var root = manifestDocument.RootElement;
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var goalReviewPath = Path.GetFullPath(Path.Combine(directory, "..", "..", "brentford-liverpool-goal-review", "goal-review.v1.json"));
        var goalReviewBytes = await BoundedFile.ReadAsync(goalReviewPath, 64000, token);
        if (ReplayBatchCodec.Hash(goalReviewBytes) != GoalReviewHash) throw new RecordingValidationException("Liverpool accepted goal review hash mismatch.");
        using var goalReviewDocument = JsonDocument.Parse(goalReviewBytes); var goalReview = goalReviewDocument.RootElement;
        if (goalReview.GetProperty("version").GetInt32() != 1 || goalReview.GetProperty("artifact_kind").GetString() != "source_backed_goal_sequence_review"
            || goalReview.GetProperty("fixture_id").GetString() != root.GetProperty("fixtureId").GetString()
            || goalReview.GetProperty("source_pins").GetProperty("sources/official-match-facts.json").GetProperty("sha256").GetString() != FactsHash
            || goalReview.GetProperty("goals").GetArrayLength() != 3)
            throw new RecordingValidationException("Liverpool goal review fixture/facts binding mismatch.");
        var reviewedGoals = goalReview.GetProperty("goals").EnumerateArray().ToDictionary(g => g.GetProperty("id").GetString()!, StringComparer.Ordinal);
        async Task<byte[]> Artifact(JsonElement receipt, int cap)
        {
            var file = receipt.GetProperty("file").GetString()!;
            if (Path.GetFileName(file) != file) throw new RecordingValidationException("Liverpool artifact must be adjacent to its manifest.");
            var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, file), cap, token);
            if (bytes.Length != receipt.GetProperty("bytes").GetInt32() || ReplayBatchCodec.Hash(bytes) != receipt.GetProperty("sha256").GetString())
                throw new RecordingValidationException("Liverpool artifact integrity mismatch.");
            return bytes;
        }
        var factsBytes = await Artifact(root.GetProperty("source"), 100000);
        if (ReplayBatchCodec.Hash(factsBytes) != FactsHash) throw new RecordingValidationException("Liverpool official facts hash mismatch.");
        using var factsDocument = JsonDocument.Parse(factsBytes); var facts = factsDocument.RootElement;
        using var stagingDocument = JsonDocument.Parse(await Artifact(root.GetProperty("generatedEventStaging"), 64000));
        using var participationDocument = JsonDocument.Parse(await Artifact(root.GetProperty("generatedParticipation"), 64000));
        using var actionDocument = JsonDocument.Parse(await Artifact(root.GetProperty("generatedBallActions"), 400000));
        using var configDocument = JsonDocument.Parse(await Artifact(root.GetProperty("config"), 64000));
        var config = configDocument.RootElement;
        if (config.GetProperty("version").GetInt32() != 1 || config.GetProperty("seed").GetInt32() != 2645256)
            throw new RecordingValidationException("Liverpool generated configuration binding mismatch.");
        var people = facts.GetProperty("players").EnumerateArray().ToArray();
        var events = facts.GetProperty("events").EnumerateArray().ToArray();
        var stages = stagingDocument.RootElement.GetProperty("events").EnumerateArray().ToDictionary(e => e.GetProperty("sourceEventId").GetString()!, StringComparer.Ordinal);
        if (people.Length != 40 || events.Length != 23 || stages.Count != 23 || facts.GetProperty("fixture").GetProperty("officialMatchId").GetString() != "2645256")
            throw new RecordingValidationException("Liverpool fixture/roster/event binding mismatch.");
        var mappings = new List<IdentityMapping>
        {
            FootballSourceMapping.Map(FixtureId, "premier-league-match", "2645256"),
            FootballSourceMapping.Map(First, "authored-period", 1), FootballSourceMapping.Map(Second, "authored-period", 2),
            FootballSourceMapping.Map(Ball, "logical-active-ball", "physical identity unknown"),
            FootballSourceMapping.Map(Team("brentford"), "official-team", "brentford"), FootballSourceMapping.Map(Team("liverpool"), "official-team", "liverpool")
        };
        mappings.AddRange(people.Select(p => FootballSourceMapping.Map(Person(p.GetProperty("id").GetString()!), "official-person", p.GetProperty("id").GetString()!)));
        var sources = facts.GetProperty("sources").EnumerateArray().Select(s => new SourceReference(s.GetProperty("id").GetString()!, s.GetProperty("url").GetString()!, FactsHash))
            .Append(new("liverpool-generated-v2", "dataset-v2/manifest.json; generated, not measured", ManifestHash))
            .Append(new("liverpool-generated-config", "dataset-v2/authored-config.json; version 1; seed 2645256; shared schematic layout is generated", root.GetProperty("config").GetProperty("sha256").GetString()))
            .Append(new("generated-model-actions", "dataset-v2/generated-ball-actions.json; existing authored actions, not actual match statistics", root.GetProperty("generatedBallActions").GetProperty("sha256").GetString()))
            .Append(new("liverpool-goal-review", "brentford-liverpool-goal-review/goal-review.v1.json; reviewed Brentford FC report claims; names/actions reported, not observed contacts", GoalReviewHash)).ToArray();
        var version = "liverpool-generated-v2-cards-v1-actions-v1-" + ManifestHash[..12] + "-" + GoalReviewHash[..12];
        var manifest = new FootballManifest(FootballContract.Version, DatasetId, version, FixtureId, 5, "open", sources, mappings.ToArray(),
            ["tracking_frame", "match_event", FootballPresentationPolicy.GeneratedMovement, "complete-generated-200ms-illustration", "paused-halftime-transition", "shared-event-sequences-v1", FootballActionPolicy.GeneratedModelActionsV1],
            ["retrospective_replay", "completed_reference"], Normalization + ";" + SequencePolicy + ";" + CardPolicy + ";" + ActionPolicy, []);
        var stream = new FootballStream("run-" + version, DatasetId, manifest.DatasetVersion, FixtureId, "replay");
        var packer = new FootballBatchPacker(manifest, stream);
        FootballEvidence Generated(string locator) => FootballSourceMapping.Evidence("generated", "liverpool-generated-v2", locator, "generated", "synthetic");
        FootballRecord Record(string id, FootballPayload payload, FootballEvidence[] evidence, FootballTime? time = null, Dictionary<string, string[]>? fields = null)
            => FootballSourceMapping.Record(manifest, id, payload, evidence, time, fields);
        var fixtureEvidence = FootballSourceMapping.Evidence("reported", "pl-overview", "fixture header; source conflicts retained in original flags", "reported",
            flags: facts.GetProperty("sourceConflicts").GetRawText());
        var actions = NormalizeActions(manifest, actionDocument.RootElement, participationDocument.RootElement, people);
        var releases = events.Select(e => new PresentationRelease(e.GetProperty("id").GetString()!, Milliseconds(stages[e.GetProperty("id").GetString()!].GetProperty("stageSeconds").GetDouble())))
            .Concat(actions.Select(r => new PresentationRelease(r.RecordId, r.FootballTime!.EndMs!.Value + (r.FootballTime.PeriodId == Second ? 3420000 : 0)))).ToArray();
        var metadata = new List<FootballRecord>
        {
            Record("fixture-context", new FixturePayload("Brentford – Liverpool · 17 October 2026", "Premier League", new(Team("brentford"), "Brentford"), new(Team("liverpool"), "Liverpool"),
                new(105, 68), [new(First, 1, "First half", 0, 3360000), new(Second, 2, "Second half", 2700000, 3120000)]), [fixtureEvidence, Generated("authored pitch and half lengths; not verified whistles")],
                fields: new() { ["/payload/periods"] = ["generated"], ["/payload/pitch"] = ["generated"], ["/payload/label"] = ["reported"] }),
            Record("match-squad", new SquadPayload(people.Select(p => new SquadPerson(Person(p.GetProperty("id").GetString()!), Team(p.GetProperty("team").GetString()!), p.GetProperty("name").GetString()!,
                p.GetProperty("shirt").ValueKind == JsonValueKind.Null ? null : p.GetProperty("shirt").GetInt32(), p.GetProperty("status").GetString()!)).ToArray()),
                [FootballSourceMapping.Evidence("reported", "pl-lineups", "fixture-specific starting XI and substitutes; full 40-person roster", "reported")]),
            Record("presentation-policy", new SchedulePayload("liverpool-generated-staging-ms-v1", 1,
                [new(First, 0, 3360000, 0), new(First, 3360000, 3420000, 3360000, "halftime_transition", 3360000), new(Second, 3420000, 6540000, 0)], releases,
                [new("Generated first half", 0), new("Generated halftime transition; clock paused", 3360000), new("Generated second half", 3420000), new("End of illustration", 6540000)], 200,
                "authored_generated_staging; not source seconds or availability; " + Normalization), [Generated("authored-config.json timeline; generated-event-staging.json; rounded-ms adapter policy")])
        };
        foreach (var e in events)
        {
            var id = e.GetProperty("id").GetString()!; var kind = e.GetProperty("kind").GetString()!; var time = e.GetProperty("time");
            var phase = time.GetProperty("phase").GetString(); var period = phase == "second_half" ? Second : First;
            var minute = time.GetProperty("minute").GetInt32() + (time.GetProperty("added_minute").ValueKind == JsonValueKind.Null ? 0 : time.GetProperty("added_minute").GetInt32());
            var end = minute * 60000L - (period == Second ? 2700000 : 0);
            var footballTime = phase == "half_time" ? new FootballTime(period, null, null, "unknown", null, time.GetProperty("printed").GetString())
                : new FootballTime(period, end - 60000, end, "reported_minute", true, time.GetProperty("printed").GetString());
            var origin = e.GetProperty("evidence");
            var evidence = new List<FootballEvidence> { FootballSourceMapping.Evidence("reported", origin.GetProperty("source").GetString()!, origin.GetProperty("locator").GetString()!, "reported", flags: e.GetRawText()) };
            string? assist = null;
            if (e.TryGetProperty("assist", out var credited) && Text(credited, "player") is { } assistId)
            {
                assist = Person(assistId); var attribution = credited.GetProperty("evidence");
                evidence.Add(FootballSourceMapping.Evidence("assist", attribution.GetProperty("source").GetString()!, attribution.GetProperty("locator").GetString()!, "reported"));
            }
            var details = new List<MatchEventDetail>();
            if (Text(e, "reason") is { } reason) details.Add(new("reason", reason, ["reported"]));
            if (Text(e, "participantQualification") is { } qualification) details.Add(new("reason", qualification, ["reported"]));
            if (Text(e, "relatedEvent") is { } related) details.Add(new("reason", "Related reported event: " + related + "; review decision, not an additional dismissal.", ["reported"]));
            if (e.TryGetProperty("classificationEvidence", out var classification))
                evidence.Add(FootballSourceMapping.Evidence("classification", classification.GetProperty("source").GetString()!, classification.GetProperty("locator").GetString()!, "reported"));
            var fields = new Dictionary<string, string[]> { ["/footballTime"] = ["reported"], ["/payload"] = ["reported"] };
            if (assist is not null) fields["/payload/assistPersonId"] = ["assist"];
            if (kind == "red_card") fields["/payload/decision"] = [evidence.Any(v => v.EvidenceId == "classification") ? "classification" : "reported"];
            MatchEventSequence? sequence = null;
            if (kind is "yellow_card" or "red_card")
            {
                var card = kind == "yellow_card" ? "yellow card" : Text(e, "dismissalType") == "straight_red" ? "straight-red dismissal" : "second-yellow dismissal";
                var cardReason = Text(e, "reason"); var text = cardReason is null ? $"Reported {card}; reason not supplied." : $"Reported {card}: {cardReason}.";
                var witnesses = evidence.Any(v => v.EvidenceId == "classification") ? new[] { "reported", "classification" } : ["reported"];
                sequence = new(1, "liverpool-card-" + id, 0, 15000,
                    [new("card", kind == "yellow_card" ? "booking" : "red_card", Person(Text(e, "player")!), null, 0, text, witnesses,
                        "Reported card only; no tackle or referee location is reconstructed. Exact source seconds are unknown.")]);
                evidence.Add(FootballSourceMapping.Evidence("card-layout", "liverpool-generated-config", "authored release display only; the card annotation is not a foul or referee location", "generated", "synthetic"));
                fields["/payload/sequence/presentationLeadInMs"] = ["card-layout"];
                fields["/payload/sequence/presentationTailMs"] = ["card-layout"];
                fields["/payload/sequence/steps/0/text"] = witnesses;
                fields["/payload/sequence/steps/0/presentationOffsetMs"] = ["card-layout"];
            }
            if (kind == "goal")
            {
                var scorer = Text(e, "player")!; var assister = e.GetProperty("assist").GetProperty("player").GetString()!;
                var reviewed = reviewedGoals[id]; var steps = reviewed.GetProperty("steps").EnumerateArray().ToArray();
                var stepKinds = id switch { "goal-1" => new[] { "release_pass", "cross", "header_finish" }, "goal-2" => ["release_pass", "advance", "shot"], "goal-3" => ["cross", "headed_assist", "shot"], _ => throw new RecordingValidationException("Unexpected Liverpool reviewed goal.") };
                if (reviewed.GetProperty("reported_minute").GetString() != footballTime.ReportedClock || reviewed.GetProperty("scorer_id").GetString() != scorer
                    || reviewed.GetProperty("official_assist_id").GetString() != assister || steps.Length != 3
                    || Text(steps[^1], "actor_id") != scorer || !steps.Any(s => Text(s, "actor_id") == assister)
                    || steps.Where((step, index) => step.GetProperty("order").GetInt32() != index + 1 || Text(step, "provenance") != "reported"
                        || Text(step, "report_source") != "brentford-report" || string.IsNullOrWhiteSpace(Text(step, "report_locator"))
                        || step.GetProperty("available_at").ValueKind != JsonValueKind.Null || step.GetProperty("match_clock").ValueKind != JsonValueKind.Null
                        || step.GetProperty("position").ValueKind != JsonValueKind.Null).Any())
                    throw new RecordingValidationException("Liverpool goal review participant/time/provenance mismatch.");
                var release = stages[id].GetProperty("stageSeconds").GetDouble();
                var passes = actionDocument.RootElement.GetProperty("actions").EnumerateArray().Where(a => Text(a, "kind") == "illustrated_goal_pass"
                    && a.GetProperty("endSeconds").GetDouble() == release - 6 && Text(a, "fromPlayerId") == assister && Text(a, "toPlayerId") == scorer).ToArray();
                var finishes = actionDocument.RootElement.GetProperty("actions").EnumerateArray().Where(a => Text(a, "kind") == "illustrated_finish"
                    && a.GetProperty("startSeconds").GetDouble() == release - 6 && a.GetProperty("endSeconds").GetDouble() == release && Text(a, "fromPlayerId") == scorer).ToArray();
                if (passes.Length != 1 || finishes.Length != 1 || assist is null) throw new RecordingValidationException("Liverpool goal window/credited participant binding mismatch.");
                var reportUrl = goalReview.GetProperty("sources")[0].GetProperty("url").GetString();
                evidence.Add(FootballSourceMapping.Evidence("goal-report", "liverpool-goal-review", reportUrl + "; " + string.Join("; ", steps.Select(s => Text(s, "report_locator")))
                    + "; named actions are reported; pixel-only named contacts and exact times are not established", "reported"));
                evidence.Add(FootballSourceMapping.Evidence("sequence-layout", "liverpool-generated-config", "version 1; seed 2645256; generated shared schematic geometry; presentation lead-in 15000ms and tail 5000ms", "generated", "synthetic"));
                evidence.Add(FootballSourceMapping.Evidence("sequence-window", "liverpool-generated-v2", $"generated-ball-actions.json pinned by frozen manifest; {id} window stages at {release - 12:R}s, {release - 6:R}s, {release:R}s; timing anchors only, participants/actions come from the report review; never actual contact times", "generated", "synthetic"));
                sequence = new(1, "liverpool-sequence-" + id, 15000, 5000,
                    steps.Select((step, index) => new MatchEventStep("reported-step-" + (index + 1), stepKinds[index], Person(Text(step, "actor_id")!),
                        Text(step, "receiver_id") is { } receiver ? Person(receiver) : null, -12000 + index * 6000,
                        "Club report: " + Text(step, "label") + ".", ["goal-report"],
                        "Named action is reported; geometry and offsets are generated. Pixels do not independently identify each contact; exact source seconds are unknown.")).ToArray());
                fields["/payload/sequence/presentationLeadInMs"] = ["sequence-layout"];
                fields["/payload/sequence/presentationTailMs"] = ["sequence-layout"];
                foreach (var (step, index) in sequence.Steps.Select((step, index) => (step, index)))
                {
                    fields[$"/payload/sequence/steps/{index}/text"] = step.EvidenceIds;
                    fields[$"/payload/sequence/steps/{index}/presentationOffsetMs"] = ["sequence-window"];
                }
            }
            metadata.Add(Record(id, new MatchEventPayload(kind, Team(e.GetProperty("team").GetString()!), Text(e, "player") is { } player ? Person(player) : null, assist,
                kind == "goal" ? "confirmed" : Text(e, "dismissalType") ?? Text(e, "decision"),
                OutgoingPersonId: Text(e, "off") is { } off ? Person(off) : null, IncomingPersonId: Text(e, "on") is { } on ? Person(on) : null,
                Details: details.Count == 0 ? null : details.ToArray(), Sequence: sequence), evidence.ToArray(), footballTime, fields));
        }
        foreach (var record in metadata.Concat(actions)) if (packer.Add(record) is { } batch) yield return batch;
        FootballRecord Frame(JsonElement frame, long ms)
        {
            var seconds = frame.GetProperty("t").GetDouble();
            var evidence = new[] { Generated("generated frame t=" + seconds.ToString("R", CultureInfo.InvariantCulture) + "; latest-original-t rounded-ms sample; inactiveTransitions omitted") };
            var players = frame.GetProperty("players").EnumerateArray().Select(p => new FootballObservation(Person(p[1].GetString()!), p[0].GetString(), "present", p[2].GetDouble(), p[3].GetDouble(), null, "generated", "usable", ["generated"])).ToArray();
            var ball = frame.GetProperty("ball");
            var local = ms < 3420000 ? ms : ms - 3420000;
            return Record("frame-" + ms.ToString("D7", CultureInfo.InvariantCulture), new TrackingPayload("generated-t-" + seconds.ToString("R", CultureInfo.InvariantCulture), null, players,
                new(Ball, null, "present", ball[0].GetDouble(), ball[1].GetDouble(), null, "generated", "usable", ["generated"]), players.Select(p => p.SubjectId).ToArray(), "generated"), evidence,
                new(ms < 3420000 ? First : Second, local, local, "presentation_sample", true), new() { ["/footballTime"] = ["generated"], ["/payload/players"] = ["generated"], ["/payload/ball"] = ["generated"] });
        }
        JsonElement? pending = null; long pendingMs = -1, inputCount = 0, outputCount = 0, collisions = 0, rawBytes = 0, compressedBytes = 0; double lastSeconds = -1;
        foreach (var chunk in root.GetProperty("chunks").EnumerateArray())
        {
            var compressed = await Artifact(chunk, 500000); var raw = FootballTransport.Expand(compressed, 3000000);
            if (raw.Length != chunk.GetProperty("rawBytes").GetInt32() || ReplayBatchCodec.Hash(raw) != chunk.GetProperty("rawSha256").GetString()) throw new RecordingValidationException("Liverpool raw chunk integrity mismatch.");
            rawBytes += raw.Length; compressedBytes += compressed.Length;
            using var document = JsonDocument.Parse(raw); var rows = document.RootElement;
            if (rows.GetArrayLength() != chunk.GetProperty("frameCount").GetInt32()) throw new RecordingValidationException("Liverpool chunk frame count mismatch.");
            foreach (var frame in rows.EnumerateArray())
            {
                token.ThrowIfCancellationRequested(); var seconds = frame.GetProperty("t").GetDouble(); var ms = Milliseconds(seconds);
                if (!double.IsFinite(seconds) || seconds <= lastSeconds || ms < 0 || ms > 6540000 || ms < pendingMs) throw new RecordingValidationException("Liverpool frame ordering/timing mismatch.");
                lastSeconds = seconds; inputCount++;
                if (pending is not null && ms != pendingMs)
                {
                    if (pendingMs != outputCount * 200) throw new RecordingValidationException("Liverpool rounded sample coverage mismatch.");
                    if (packer.Add(Frame(pending.Value, pendingMs)) is { } batch) yield return batch;
                    outputCount++;
                }
                else if (pending is not null) collisions++;
                pending = frame.Clone(); pendingMs = ms;
            }
        }
        if (pending is null || pendingMs != 6540000 || inputCount != 35201 || outputCount != 32700 || collisions != 2500
            || rawBytes != root.GetProperty("totals").GetProperty("rawFrameBytes").GetInt64() || compressedBytes != root.GetProperty("totals").GetProperty("compressedFrameBytes").GetInt64())
            throw new RecordingValidationException("Liverpool complete export/quantization receipt mismatch.");
        if (packer.Add(Frame(pending.Value, pendingMs)) is { } finalBatch) yield return finalBatch;
        foreach (var batch in packer.Complete()) yield return batch;
    }

    // Normalize only explicitly authored contacts. Coordinates never infer a pass, shot or control.
    private static FootballRecord[] NormalizeActions(FootballManifest manifest, JsonElement data, JsonElement participation, JsonElement[] people)
    {
        if (data.GetProperty("version").GetInt32() != 1 || Text(data, "provenance") != "generated"
            || data.GetProperty("actualPassShotPossessionStatistics").GetBoolean() || data.GetProperty("actions").GetArrayLength() != 1236
            || participation.GetProperty("version").GetInt32() != 1)
            throw new RecordingValidationException("Liverpool generated action/lifecycle binding mismatch.");
        var roster = people.ToDictionary(p => Text(p, "id")!, p => Text(p, "team")!, StringComparer.Ordinal);
        var lifecycle = participation.GetProperty("players").EnumerateArray().ToDictionary(p => Text(p, "playerId")!, StringComparer.Ordinal);
        string Slot(string player, string team, long start, long end)
        {
            if (!roster.TryGetValue(player, out var actualTeam) || team != actualTeam || !lifecycle.TryGetValue(player, out var row)
                || Text(row, "team") != team || Text(row, "slotId") is not { } slot || row.GetProperty("entrySeconds").ValueKind != JsonValueKind.Number
                || Milliseconds(row.GetProperty("entrySeconds").GetDouble()) > start
                || row.GetProperty("exitSeconds").ValueKind == JsonValueKind.Number && Milliseconds(row.GetProperty("exitSeconds").GetDouble()) < end)
                throw new RecordingValidationException("Liverpool generated action participant is inactive or has no slot.");
            return slot; // Same opaque role-slot identity as the unchanged tracking frames.
        }
        var records = new List<FootballRecord>();
        long controlStart = -1, controlEnd = -1, previousEnd = -1; string? controlTeam = null, controlPeriod = null;
        void Add(string kind, string team, long start, long end, string locator, string? person = null, string? recipient = null,
            string? recipientTeam = null, string outcome = "controlled", string? fromSlot = null, string? toSlot = null)
        {
            var period = start < 3360000 ? First : Second; var offset = period == First ? 0 : 3420000;
            var id = $"generated-action-v1-p{(period == First ? 1 : 2)}-{kind}-{start:D7}-{end:D7}";
            var payload = new FootballActionPayload(kind, Team(team), person is null ? null : Person(person), recipient is null ? null : Person(recipient),
                recipientTeam is null ? null : Team(recipientTeam), outcome, fromSlot, toSlot);
            var evidence = FootballSourceMapping.Evidence("generated-action", "generated-model-actions", "generated-ball-actions.json / " + locator
                + "; authored generated illustration only, not actual match evidence; " + ActionPolicy, "generated", "synthetic");
            var fields = new[] { "/footballTime", "/payload/actionKind", "/payload/teamId", "/payload/personId", "/payload/recipientId", "/payload/recipientTeamId", "/payload/outcome", "/payload/fromSlotId", "/payload/toSlotId" }
                .ToDictionary(path => path, _ => new[] { evidence.EvidenceId });
            var record = FootballSourceMapping.Record(manifest, id, payload, [evidence], new(period, start - offset, end - offset, "source_interval", false), fields)
                with { EntityRefs = new[] { payload.TeamId, payload.PersonId, payload.RecipientId, payload.RecipientTeamId }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray() };
            FootballCodec.ValidateMetadataRecord(record, manifest); records.Add(record);
        }
        void FlushControl()
        {
            if (controlTeam is not null) Add("possession", controlTeam, controlStart, controlEnd, $"contiguous explicit control [{controlStart},{controlEnd})ms");
            controlTeam = null;
        }
        foreach (var (row, index) in data.GetProperty("actions").EnumerateArray().Select((row, index) => (row, index)))
        {
            var start = Milliseconds(row.GetProperty("startSeconds").GetDouble()); var end = Milliseconds(row.GetProperty("endSeconds").GetDouble());
            if (start < previousEnd || end <= start || start < 0 || end > 6540000 || Text(row, "provenance") != "generated")
                throw new RecordingValidationException("Liverpool generated action order/provenance mismatch.");
            previousEnd = end;
            var kind = Text(row, "kind");
            if (kind is not ("carry" or "pass" or "illustrated_goal_pass" or "illustrated_finish")) { FlushControl(); continue; }
            if (!(start < 3360000 && end <= 3360000 || start >= 3420000))
                throw new RecordingValidationException("Liverpool generated action crosses a playing-period boundary.");
            var player = Text(row, "fromPlayerId")!;
            if (!roster.TryGetValue(player, out var team)) throw new RecordingValidationException("Liverpool generated action actor is unknown.");
            var fromSlot = Slot(player, team, start, end); var recipient = Text(row, "toPlayerId");
            string? recipientTeam = null, toSlot = null;
            if (recipient is not null)
            {
                if (!roster.TryGetValue(recipient, out recipientTeam)) throw new RecordingValidationException("Liverpool generated action recipient is unknown.");
                toSlot = Slot(recipient, recipientTeam, start, end);
            }
            if (kind == "carry" && recipient != player) throw new RecordingValidationException("Liverpool carry participant mismatch.");
            var period = start < 3360000 ? First : Second;
            if (controlTeam != team || controlEnd != start || controlPeriod != period) FlushControl();
            if (controlTeam is null) { controlStart = start; controlTeam = team; controlPeriod = period; }
            controlEnd = end;
            if (kind is "pass" or "illustrated_goal_pass") Add("pass", team, start, end, "actions/" + index, player, recipient, recipientTeam,
                team == recipientTeam ? "completed" : "turnover", fromSlot, toSlot);
            else if (kind == "illustrated_finish") Add("shot", team, start, end, "actions/" + index, player, outcome: "goal", fromSlot: fromSlot);
        }
        FlushControl();
        if (records.Count != 727 || records.Count(r => r.Payload is FootballActionPayload { ActionKind: "pass" }) != 592
            || records.Count(r => r.Payload is FootballActionPayload { ActionKind: "shot" }) != 3)
            throw new RecordingValidationException("Liverpool generated action normalization receipt mismatch.");
        return records.ToArray();
    }
}
