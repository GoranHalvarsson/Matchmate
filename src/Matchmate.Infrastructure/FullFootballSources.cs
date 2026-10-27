using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public sealed class FullAustralianFootballSource(string trackingDirectory, string possessionsPath) : IStreamingFootballSource
{
    public async IAsyncEnumerable<FootballBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken token)
    {
        foreach (var batch in await new AustralianFootballSource(trackingDirectory, possessionsPath, fullCoverage: true).ReadAsync(token)) { token.ThrowIfCancellationRequested(); yield return batch; }
    }
}
public sealed class FullBrentfordFootballSource(string manifestPath) : IStreamingFootballSource
{
    public async IAsyncEnumerable<FootballBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken token)
    {
        var manifestBytes = await BoundedFile.ReadAsync(manifestPath, 2_000_000, token);
        using var manifest = JsonDocument.Parse(manifestBytes); var root = manifest.RootElement;
        if (root.GetProperty("exportVersion").GetInt32() != 2 || !root.GetProperty("fullCoverage").GetBoolean()
            || root.GetProperty("frameCount").GetInt32() != 58801 || root.GetProperty("startMs").GetInt64() != 0 || root.GetProperty("endMs").GetInt64() != 5880000
            || root.GetProperty("cadenceMs").GetInt32() != 100 || root.GetProperty("people").GetArrayLength() != 40 || root.GetProperty("events").GetArrayLength() != 14)
            throw new RecordingValidationException("Full existing model coverage receipt mismatch.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var official = await BoundedFile.ReadAsync(Path.Combine(directory, "official-record.json"), 1_000_000, token);
        if (ReplayBatchCodec.Hash(official) != root.GetProperty("hashes").GetProperty("data/official-match-records/brentford-chelsea-2026-09-18.json").GetString()) throw new RecordingValidationException("Archived original official record hash mismatch.");
        using var original = JsonDocument.Parse(official);
        if (!JsonNode.DeepEquals(JsonNode.Parse(original.RootElement.GetProperty("events").GetRawText()), JsonNode.Parse(root.GetProperty("events").GetRawText()))
            || !JsonNode.DeepEquals(JsonNode.Parse(original.RootElement.GetProperty("players").GetRawText()), JsonNode.Parse(root.GetProperty("people").GetRawText())))
            throw new RecordingValidationException("Exported event/roster input differs from its archived original.");
        if (root.TryGetProperty("enrichment", out var enrichment))
        {
            var participant = enrichment.GetProperty("version").GetInt32() == 2;
            var contextPath = $"data/match-enrichment/brentford-chelsea-2026-09-18-event-context-v{(participant ? 2 : 1)}.json";
            var policy = participant ? BrentfordFootballSource.ParticipantEventNormalizationPolicy : BrentfordFootballSource.EventNormalizationPolicy;
            var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, "event-enrichment.json"), 64000, token);
            if (ReplayBatchCodec.Hash(bytes) != root.GetProperty("hashes").GetProperty(contextPath).GetString()
                || !JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(enrichment.GetRawText()))
                || root.GetProperty("normalizationPolicy").GetString() != policy
                || root.GetProperty("hashes").GetProperty(participant ? "normalization:event-context-v2" : "normalization:event-context-v1").GetString() != ReplayBatchCodec.Hash(Encoding.UTF8.GetBytes(policy)))
                throw new RecordingValidationException("Enrichment input/policy fingerprint mismatch.");
            var receipts = root.GetProperty("sourceReceipts");
            if (receipts.GetArrayLength() != enrichment.GetProperty("sources").GetArrayLength() + 1 || receipts.GetArrayLength() > 4) throw new RecordingValidationException("Enrichment source receipts mismatch.");
            var expectedInputs = enrichment.GetProperty("sources").EnumerateArray().Append(enrichment.GetProperty("unalignedTimeline")).ToArray(); var index = 0;
            foreach (var receipt in receipts.EnumerateArray())
            {
                var file = receipt.GetProperty("file").GetString();
                var expected = expectedInputs[index];
                if (file != $"source-input-{index++:D3}.json" || receipt.GetProperty("sourcePath").GetString() != expected.GetProperty("path").GetString()
                    || receipt.GetProperty("sha256").GetString() != expected.GetProperty("sha256").GetString()) throw new RecordingValidationException("Invalid enrichment source receipt path/pin.");
                var source = await BoundedFile.ReadAsync(Path.Combine(directory, file), 1_000_000, token);
                var hash = ReplayBatchCodec.Hash(source);
                if (hash != receipt.GetProperty("sha256").GetString() || hash != root.GetProperty("hashes").GetProperty(receipt.GetProperty("sourcePath").GetString()!).GetString()) throw new RecordingValidationException("Archived enrichment source hash mismatch.");
                if (participant && expected.TryGetProperty("id", out var sourceId) && sourceId.GetString() == "goal-participants")
                {
                    using var curated = JsonDocument.Parse(source); var participantRoot = curated.RootElement;
                    if (participantRoot.GetProperty("version").GetInt32() != 1 || participantRoot.GetProperty("fixtureId").GetString() != "fixture-pl-2645236"
                        || participantRoot.GetProperty("policy").GetString() != "accepted-goal-participants-v1"
                        || participantRoot.GetProperty("roster").GetProperty("sha256").GetString() != ReplayBatchCodec.Hash(official))
                        throw new RecordingValidationException("Curated participant fixture/roster binding mismatch.");
                }
            }
        }
        if (root.TryGetProperty("sequences", out var sequences))
        {
            var participant = sequences.GetProperty("version").GetInt32() == 2;
            var contextPath = $"data/match-enrichment/brentford-chelsea-2026-09-18-event-context-v{(participant ? 2 : 1)}.json";
            var sequencePath = $"data/match-enrichment/brentford-chelsea-2026-09-18-sequences-v{(participant ? 2 : 1)}.json";
            var policy = participant ? BrentfordFootballSource.ParticipantSequenceNormalizationPolicy : BrentfordFootballSource.SequenceNormalizationPolicy;
            var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, "event-sequences.json"), 64000, token);
            if (ReplayBatchCodec.Hash(bytes) != root.GetProperty("hashes").GetProperty(sequencePath).GetString()
                || !JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(sequences.GetRawText()))
                || root.GetProperty("sequencePolicy").GetString() != policy
                || root.GetProperty("hashes").GetProperty(participant ? "normalization:shared-event-sequence-v2" : "normalization:shared-event-sequence-v1").GetString() != ReplayBatchCodec.Hash(Encoding.UTF8.GetBytes(policy)))
                throw new RecordingValidationException("Shared sequence input/policy fingerprint mismatch.");
            var inputs = sequences.GetProperty("sources").EnumerateArray().ToArray();
            if (inputs.Length != 2 || inputs[0].GetProperty("path").GetString() != "data/official-match-records/brentford-chelsea-2026-09-18.json"
                || inputs[1].GetProperty("path").GetString() != contextPath
                || inputs.Any(input => input.GetProperty("sha256").GetString() != root.GetProperty("hashes").GetProperty(input.GetProperty("path").GetString()!).GetString()))
                throw new RecordingValidationException("Shared sequence source pins mismatch.");
        }
        JsonNode? generatedActionData = null;
        if (root.TryGetProperty("generatedActions", out var actions))
        {
            if (actions.GetProperty("version").GetInt32() != 1 || actions.GetProperty("file").GetString() != "generated-actions.json"
                || actions.GetProperty("policy").GetString() != FootballActionPolicy.GeneratedModelActionsV1 || actions.GetProperty("count").GetInt32() is < 1 or > 2000)
                throw new RecordingValidationException("Invalid generated action artifact receipt.");
            var bytes = await BoundedFile.ReadAsync(Path.Combine(directory, "generated-actions.json"), 2_000_000, token);
            if (ReplayBatchCodec.Hash(bytes) != actions.GetProperty("sha256").GetString()
                || actions.GetProperty("sha256").GetString() != root.GetProperty("hashes").GetProperty("generated-actions.json").GetString()) throw new RecordingValidationException("Generated action artifact hash mismatch.");
            using var data = JsonDocument.Parse(bytes); var content = data.RootElement;
            if (content.GetProperty("version").GetInt32() != 1 || content.GetProperty("fixtureId").GetString() != "fixture-pl-2645236"
                || content.GetProperty("policy").GetString() != FootballActionPolicy.GeneratedModelActionsV1 || content.GetProperty("durationMs").GetInt64() != 5880000
                || content.GetProperty("halfMs").GetInt64() != 2820000 || content.GetProperty("rows").GetArrayLength() != actions.GetProperty("count").GetInt32())
                throw new RecordingValidationException("Generated action artifact schema mismatch.");
            generatedActionData = JsonNode.Parse(bytes);
        }
        var header = new JsonObject();
        foreach (var property in root.EnumerateObject().Where(p => p.Name is "exportVersion" or "hashes" or "sampling" or "people" or "events" or "sources" or "eventTimings" or "enrichment" or "normalizationPolicy" or "sequences" or "sequencePolicy" or "generatedActions")) header[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        long expectedMs = 0, rawBytes = 0, compressedBytes = 0; var count = 0; FootballBatchPacker? packer = null; var first = true; var chunkNumber = 0;
        foreach (var chunk in root.GetProperty("chunks").EnumerateArray())
        {
            var filename = chunk.GetProperty("file").GetString();
            if (filename != $"frames-{chunkNumber++:D6}.json.gz") throw new RecordingValidationException("Export chunk order/name mismatch.");
            var compressed = await BoundedFile.ReadAsync(Path.Combine(directory, filename), 1_000_000, token);
            if (compressed.Length != chunk.GetProperty("bytes").GetInt32() || ReplayBatchCodec.Hash(compressed) != chunk.GetProperty("sha256").GetString()) throw new RecordingValidationException("Full export chunk integrity mismatch.");
            var raw = FootballTransport.Expand(compressed, 2_000_000);
            if (raw.Length != chunk.GetProperty("rawBytes").GetInt32() || ReplayBatchCodec.Hash(raw) != chunk.GetProperty("rawSha256").GetString()) throw new RecordingValidationException("Full export raw hash mismatch.");
            rawBytes += raw.Length; compressedBytes += compressed.Length;
            using var frames = JsonDocument.Parse(raw);
            if (frames.RootElement.GetArrayLength() != chunk.GetProperty("count").GetInt32() || Math.Round(chunk.GetProperty("startMs").GetDouble()) != expectedMs) throw new RecordingValidationException("Incomplete full export chunk.");
            foreach (var frame in frames.RootElement.EnumerateArray())
            {
                if (FootballSourceMapping.Ms(frame.GetProperty("time").GetDouble()) != expectedMs || frame.GetProperty("period").GetInt32() != (expectedMs < 2820000 ? 1 : 2)
                    || frame.GetProperty("players").GetArrayLength() != 22) throw new RecordingValidationException("Full export has missing frame/period/slots.");
                expectedMs += 100; count++;
            }
            if (Math.Round(chunk.GetProperty("endMs").GetDouble()) != expectedMs - 100) throw new RecordingValidationException("Export chunk endpoint mismatch.");
            var current = (JsonObject)header.DeepClone(); current["frames"] = JsonNode.Parse(raw);
            if (first && generatedActionData is not null) current["generatedActionData"] = generatedActionData;
            using var source = JsonDocument.Parse(current.ToJsonString()); var adapted = BrentfordFootballSource.Adapt(source.RootElement, true);
            packer ??= new(adapted.Manifest, adapted.Stream);
            var records = first ? adapted.Records : adapted.Records.Where(record => record.Payload is TrackingPayload);
            foreach (var record in records) if (packer.Add(record) is { } batch) yield return batch;
            first = false;
        }
        if (count != 58801 || expectedMs != 5880100 || rawBytes != root.GetProperty("rawBytes").GetInt64() || compressedBytes != root.GetProperty("compressedBytes").GetInt64())
            throw new RecordingValidationException("Full model export was truncated or its size receipt is invalid.");
        foreach (var batch in packer!.Complete()) yield return batch;
    }
}
