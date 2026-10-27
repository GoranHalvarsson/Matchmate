using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Experience;
using Xunit;

namespace Matchmate.Tests;

public sealed class MatchContextEnrichmentTests
{
    private static string Export(string name = "brentford-full-v2") => Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", name);
    private static readonly Lazy<Task<FootballLedger>> Enriched = new(async () => {
        var ledger = new FootballLedger();
        await foreach (var batch in new FullBrentfordFootballSource(Path.Combine(Export(), "manifest.json")).ReadBatchesAsync(default))
        {
            ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow));
            var dataset = Assert.Single(ledger.Catalog());
            if (dataset.Schedule is not null && ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)).Events.Length == 14) break;
        }
        return ledger;
    });

    [Fact]
    public async Task AllOfficialEventAttributesAndExplicitReviewedGoalAssociationsKeepAttributionAndUnknowns()
    {
        var ledger = await Enriched.Value; var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)); Assert.Equal(14, end.Events.Length);
        Assert.All(end.Events, record => { var details = Assert.IsType<MatchEventPayload>(record.Payload).Details!; Assert.NotEmpty(details);
            Assert.InRange(details.Length, 1, MatchEventDetailLimits.Count); Assert.All(details, detail => Assert.All(detail.EvidenceIds, id => Assert.Contains(record.Evidence, e => e.EvidenceId == id))); });
        var first = end.Events.Single(e => e.RecordId == "goal-1"); var goal = (MatchEventPayload)first.Payload;
        Assert.EndsWith("schuster", goal.AssistPersonId); Assert.Contains(goal.Details!, d => d.Kind == "finish" && d.Text.Contains("head") && d.Text.Contains("very close range"));
        Assert.Contains(goal.Details!, d => d.Kind == "build_up" && d.Text.Contains("Damsgaard") && d.Uncertainty!.Contains("does not independently"));
        var reviewed = Assert.Single(goal.Details!, d => d.Kind == "reviewed_observation"); Assert.Contains("unverified", reviewed.Uncertainty);
        Assert.Equal("observed", first.Evidence.Single(e => e.EvidenceId == reviewed.EvidenceIds[0]).Method);
        Assert.Equal("reported", first.Evidence.Single(e => e.EvidenceId == "club-sequence").Method);
        var second = (MatchEventPayload)end.Events.Single(e => e.RecordId == "goal-2").Payload;
        Assert.Null(second.AssistPersonId); Assert.Contains(second.Details!, d => d.Kind == "assist" && d.Text.Contains("No explicit"));
        Assert.Contains(second.Details!, d => d.Kind == "reported_minute_disagreement" && d.Text.Contains("80"));
        Assert.Contains(second.Details!, d => d.Kind == "reviewed_observation" && d.Uncertainty!.Contains("pass versus parry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(((MatchEventPayload)end.Events.Single(e => e.RecordId == "goal-3").Payload).Details!, d => d.Kind == "reviewed_observation" && d.Text.Contains("throw-in"));
        Assert.All(end.Events.Where(r => ((MatchEventPayload)r.Payload).EventKind != "goal"), r => Assert.DoesNotContain(((MatchEventPayload)r.Payload).Details!, d => d.Kind == "reviewed_observation"));
        Assert.Contains(end.Events, r => ((MatchEventPayload)r.Payload).Details!.Any(d => d.Kind == "reason" && d.Text.Contains("bad foul")));
        Assert.Equal(6, dataset.Manifest.Sources.Length); Assert.Equal(3, dataset.Manifest.ManifestRevision);
    }

    [Theory]
    [InlineData("current_match_events")] [InlineData("current_match_moment")]
    public async Task EarlyAndLateCursorEvidenceRetainsCountsAndBudgetsWithoutUnalignedNarrative(string workflow)
    {
        var ledger = await Enriched.Value; var dataset = Assert.Single(ledger.Catalog()); var executor = new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q)));
        var early = await executor.ExecuteAsync(Request(dataset, workflow, 3779999), _ => true);
        Assert.NotNull(early.Bundle); Assert.Empty(early.Bundle.EventProvenance); Assert.DoesNotContain("Damsgaard corner", JsonSerializer.Serialize(early.Bundle));
        var late = await executor.ExecuteAsync(Request(dataset, workflow, 5880000), _ => true);
        var raw = new MatchEvidenceProjection(ledger.Query(FullFootballSourceTests.Query(dataset, 5880000)));
        Assert.True(late.Bundle is not null, $"Status {late.Status}; event text {raw.Get("current_events").Text.Length} chars; summary {raw.Get("match_summary").Text.Length}; coverage {raw.Get("coverage").Text.Length}");
        var bundle = late.Bundle!;
        Assert.InRange(bundle.Items.Sum(i => i.Text.Length), 1, MatchWorkflowLimits.EvidenceChars); Assert.InRange(JsonSerializer.Serialize(bundle).Length, 1, MatchWorkflowLimits.BundleChars);
        using var events = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "current_events").Text); Assert.Equal(14, events.RootElement.GetProperty("eligibleTotal").GetInt32());
        Assert.Equal(workflow == "current_match_moment" ? 4 : 14, events.RootElement.GetProperty("shown").GetArrayLength());
        Assert.Equal(workflow == "current_match_moment" ? 10 : 0, events.RootElement.GetProperty("omitted").GetInt32());
        Assert.Contains("unaligned_source_media_narrative_not_supplied", bundle.Missingness);
        Assert.DoesNotContain("3715", JsonSerializer.Serialize(bundle)); Assert.DoesNotContain("Behind-goal replay of first goal", JsonSerializer.Serialize(bundle));
        Assert.DoesNotContain("completed_result", JsonSerializer.Serialize(bundle));
    }

    [Fact]
    public async Task SelectedGoalHasRichMechanismButNoOtherFutureEventAndSupportedAnswerPassesExistingGuard()
    {
        var ledger = await Enriched.Value; var dataset = Assert.Single(ledger.Catalog());
        var request = Request(dataset, "selected_goal_event", 3765000); request = request with { Snapshot = request.Snapshot with {
            Context = request.Snapshot.Context with { SelectedEvent = new("goal-1", 3780000, "61") }, TemporalMode = MatchWorkflowTemporalMode.SelectedGoalRetrospective } };
        var reads = 0; var result = await new MatchWorkflowExecutor((q, _) => { reads++; return Task.FromResult(ledger.Query(q)); }).ExecuteAsync(request, _ => true);
        var bundle = Assert.IsType<MatchWorkflowBundle>(result.Bundle); Assert.Equal(2, reads); Assert.Equal(4, bundle.Items.Length);
        var selected = bundle.Items.Single(i => i.Id == "selected_goal"); Assert.Contains("Jannik Schuster", selected.Text); Assert.Contains("Damsgaard corner", selected.Text); Assert.Contains("unverified", selected.Text);
        var json = JsonSerializer.Serialize(bundle); Assert.DoesNotContain("goal-2", json); Assert.DoesNotContain("goal-3", json); Assert.DoesNotContain("following a fast break", json);
        using var summary = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "match_summary").Text); Assert.Equal(0, summary.RootElement.GetProperty("homeReportedGoals").GetInt32());
        var supplied = bundle.Items.ToDictionary(i => i.Id, i => new MatchChatEvidence(i.Id, i.Tool, i.Text, i.NumericTokens.ToArray()));
        var answer = "The reports describe a Damsgaard corner, Schuster's header across goal and Anthony's headed finish. The reviewed footage does not independently establish the intermediate contact or defensive blame.";
        Assert.Equal(answer, MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer, evidenceIds = new[] { "selected_goal" } }), supplied).Answer);
        Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate("{\"answer\":\"The speed was 61 km/h.\",\"evidenceIds\":[\"selected_goal\"]}", supplied));
    }

    [Theory]
    [InlineData(61800, 0, false)] [InlineData(65200, 1, false)] [InlineData(175900, 5, true)]
    public async Task AustralianMomentUsesActualCurrentQualityAndOnlyCompletedPossessions(long cursor, int completed, bool gap)
    {
        var batches = await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var ledger = new FootballLedger(); foreach (var batch in batches) ledger.Accept(new(batch, DateTimeOffset.UtcNow)); var dataset = Assert.Single(ledger.Catalog());
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(Request(dataset, "current_match_moment", cursor), _ => true);
        var bundle = Assert.IsType<MatchWorkflowBundle>(result.Bundle); using var moment = JsonDocument.Parse(bundle.Items.Single(i => i.Id == "moment_context").Text);
        var root = moment.RootElement; Assert.Equal(gap, root.GetProperty("trackingGap").GetBoolean()); Assert.Equal(completed, root.GetProperty("eligibleCompletedPossessions").GetInt32());
        Assert.Equal(completed, root.GetProperty("completedPossessions").GetArrayLength()); Assert.InRange(root.GetProperty("players").GetArrayLength(), 0, MatchWorkflowLimits.TrackingPlayers);
        if (gap) Assert.Empty(root.GetProperty("players").EnumerateArray());
        Assert.Contains(root.GetProperty("ball").GetProperty("method").GetString(), new[] { "observed", "estimated", "unknown" });
        Assert.Contains("do not establish possession now", root.GetProperty("limitation").GetString()); Assert.DoesNotContain("generated", root.GetProperty("ball").GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("kind")] [InlineData("count")] [InlineData("text")] [InlineData("uncertainty")] [InlineData("evidence")]
    public void ContractRejectsUnboundedOrUnattributedClaims(string fault)
    {
        var fixture = SharedFootballTests.Fixture(); var detail = new MatchEventDetail("finish", "Reported header.", ["source"]);
        detail = fault switch { "kind" => detail with { Kind = "invented_tactic" }, "text" => detail with { Text = new('x', 321) },
            "uncertainty" => detail with { Uncertainty = new('x', 241) }, "evidence" => detail with { EvidenceIds = ["unknown-source"] }, _ => detail };
        var goal = SharedFootballTests.Goal(fixture); goal = goal with { Payload = ((MatchEventPayload)goal.Payload) with { Details = fault == "count" ? Enumerable.Repeat(detail, 7).ToArray() : [detail] } };
        Assert.Throws<RecordingValidationException>(() => FootballCodec.ValidateMetadataRecord(goal, fixture.Manifest));
    }

    [Fact]
    public void OptionalDetailsPreserveLegacyCanonicalPayloadBytesAndExistingExportFramesAreUnchanged()
    {
        const string legacy = "{\"kind\":\"match_event\",\"eventKind\":\"goal\",\"teamId\":\"team-home\",\"personId\":\"person-one\",\"assistPersonId\":null,\"decision\":\"confirmed\",\"ownGoal\":null,\"outgoingPersonId\":null,\"incomingPersonId\":null,\"groupId\":null}";
        var payload = JsonSerializer.Deserialize<FootballPayload>(legacy, FootballCodec.Json)!;
        Assert.Equal(legacy, JsonSerializer.Serialize(payload, FootballCodec.Json));
        using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export("brentford-full-v1"), "manifest.json")));
        using var enriched = JsonDocument.Parse(File.ReadAllText(Path.Combine(Export(), "manifest.json")));
        Assert.Equal(old.RootElement.GetProperty("chunks").GetRawText(), enriched.RootElement.GetProperty("chunks").GetRawText());
        Assert.Equal(ReplayBatchCodec.Hash(Encoding.UTF8.GetBytes(BrentfordFootballSource.EventNormalizationPolicy)), enriched.RootElement.GetProperty("hashes").GetProperty("normalization:event-context-v1").GetString());
    }

    [Theory]
    [InlineData("policy")] [InlineData("artifact")] [InlineData("source")]
    public async Task FullImportRejectsChangedEnrichmentPolicyArtifactOrArchivedSourceBeforeAnyBatch(string fault)
    {
        using var temp = new TestDirectory(); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Export(), "manifest.json")))!;
        foreach (var file in new[] { "official-record.json", "event-enrichment.json", "source-input-000.json", "source-input-001.json", "source-input-002.json" }) File.Copy(Path.Combine(Export(), file), Path.Combine(temp.Path, file));
        if (fault == "policy") manifest["normalizationPolicy"] = "wrong-policy";
        else await File.AppendAllTextAsync(Path.Combine(temp.Path, fault == "artifact" ? "event-enrichment.json" : "source-input-001.json"), " ");
        var path = Path.Combine(temp.Path, "manifest.json"); await File.WriteAllTextAsync(path, manifest.ToJsonString());
        await using var batches = new FullBrentfordFootballSource(path).ReadBatchesAsync(default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<RecordingValidationException>(async () => { await batches.MoveNextAsync(); });
    }

    private static MatchWorkflowRequest Request(FootballDataset dataset, string workflow, long cursor) => new(workflow, MatchWorkflowRegistry.Version,
        new("authenticated-owner", 1, new(FullFootballSourceTests.Query(dataset, cursor), dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor));
}
