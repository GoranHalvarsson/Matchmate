using System.Collections.Immutable;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

public sealed class MatchWorkflowTests
{
    // The unchanged recording adapters and ledger, not fabricated product data, establish these boundaries.
    private static readonly Lazy<Task<FootballLedger>> Recordings = new(async () =>
    {
        var root = LocalPaths.FindRoot(); var ledger = new FootballLedger();
        var australian = await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default);
        var brentford = await new BrentfordFootballSource(Path.Combine(root, ".local", "shared-inputs", "brentford-slice.json")).ReadAsync(default);
        foreach (var batch in australian.Concat(brentford)) ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow));
        return ledger;
    });

    [Theory]
    [InlineData("australian-recorded", 60000, "person-skillcorner-50999", "estimated")]
    [InlineData("brentford-existing-illustration", 3780000, "person-pl-reference-brentford-kayode", "generated")]
    public async Task ActualRecordingsReturnBoundedPlayerMethodsSourcesAndNoRosterOrSchedule(string datasetId, long cursor, string person, string method)
    {
        var ledger = await Recordings.Value;
        var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId.StartsWith(datasetId, StringComparison.Ordinal));
        var query = Query(dataset, cursor) with { SubjectId = person }; var request = Request("selected_player_context", query, dataset.HeadSequence);
        var reads = 0; var executor = new MatchWorkflowExecutor((q, _) => { reads++; Assert.Equal(query with { ApplicationSequence = dataset.HeadSequence }, q); return Task.FromResult(ledger.Query(q)); });
        var result = await executor.ExecuteAsync(request, snapshot => snapshot == request.Snapshot);
        Assert.Equal(MatchWorkflowStatus.Success, result.Status); Assert.Equal(1, reads);
        var bundle = Assert.IsType<MatchWorkflowBundle>(result.Bundle); Assert.Equal(request.WorkflowId, bundle.WorkflowId); Assert.Equal(MatchWorkflowRegistry.Version, bundle.Version);
        Assert.Equal(cursor, bundle.Context.Query.CursorMs); Assert.Equal(dataset.HeadSequence, bundle.Context.HeadSequence); Assert.Equal("replay", bundle.DeliveryMode);
        Assert.Equal(new[] { "current_player", "player_involvement", "coverage" }, bundle.Items.Select(item => item.Id)); Assert.Contains(method, Assert.Single(bundle.Items, item => item.Tool == "current_player").Text);
        Assert.All(bundle.Sources, source => Assert.Equal(64, source.ArtifactHash!.Length));
        Assert.Contains("tracking_record_provenance_not_exposed_by_view", bundle.Missingness);
        var json = JsonSerializer.Serialize(bundle); Assert.InRange(json.Length, 1, MatchWorkflowLimits.BundleChars);
        Assert.DoesNotContain("Milestones", json); Assert.DoesNotContain("Releases", json); Assert.DoesNotContain("Segments", json);
        Assert.DoesNotContain("People", json); Assert.DoesNotContain("IdentityMappings", json); Assert.DoesNotContain("OwnerKey", json);
        Assert.DoesNotContain("SourceTimestamp", json); Assert.DoesNotContain("Frame", json);
    }

    [Fact]
    public async Task RealReportedMinuteAndRewindPreserveEligibleEventProvenanceAndCounts()
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.Capabilities.Contains("match_event"));
        var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query)));
        async Task<MatchWorkflowBundle> At(long cursor)
        {
            var result = await executor.ExecuteAsync(Request("current_match_events", Query(dataset, cursor), dataset.HeadSequence), _ => true);
            Assert.Equal(MatchWorkflowStatus.Success, result.Status); return result.Bundle!;
        }
        var before = await At(3779999); Assert.Empty(before.EventProvenance); Assert.DoesNotContain("goal-1", JsonSerializer.Serialize(before));
        var after = await At(3780000); var provenance = Assert.Single(after.EventProvenance);
        Assert.Equal("goal-1", provenance.RecordId); Assert.Equal(1, provenance.Revision); Assert.Equal("real", provenance.Origin); Assert.Equal("reported", provenance.Method);
        Assert.Equal("reported_minute", provenance.FootballTime!.Precision); Assert.Equal(960000, provenance.FootballTime.EndMs);
        Assert.Contains("\"homeReportedGoals\":1", Assert.Single(after.Items, item => item.Tool == "match_summary").Text);
        Assert.Empty((await At(3779999)).EventProvenance);
    }

    [Fact]
    public async Task ActualAustralianMissingFeedAndGapNeverInventScorePlayerOrBall()
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId == "australian-recorded");
        var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query)));
        var query = Query(dataset, 175900) with { SubjectId = "person-skillcorner-50999" };
        var player = await executor.ExecuteAsync(Request("selected_player_context", query, dataset.HeadSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Partial, player.Status); Assert.Contains("selected_player_position_missing", player.Bundle!.Missingness);
        Assert.Contains("tracking_gap", player.Bundle.Missingness); Assert.Contains("\"xMetres\":null", player.Bundle.Items[0].Text);
        var coverage = await executor.ExecuteAsync(Request("coverage_and_limits", query, dataset.HeadSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, coverage.Status); Assert.Contains("event_reports_not_supplied", coverage.Bundle!.Missingness);
        var summary = await executor.ExecuteAsync(Request("current_match_summary", query, dataset.HeadSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Partial, summary.Status); Assert.Contains("\"homeReportedGoals\":null", summary.Bundle!.Items[0].Text);
        var events = await executor.ExecuteAsync(Request("current_match_events", query, dataset.HeadSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Unavailable, events.Status); Assert.Null(events.Bundle);
        Assert.Equal("This recording does not supply reported match events.", events.Message);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("CURRENT_MATCH_SUMMARY")] [InlineData("current_match_summary;coverage")]
    [InlineData("https://outside.example/")] [InlineData("match_summary")] [InlineData("current_match_summary ")]
    public async Task ForgedRoutesNeverRead(string route)
    {
        var executor = NoRead(); var result = await executor.ExecuteAsync(Request(route), _ => true);
        Assert.Equal(MatchWorkflowStatus.InvalidRequest, result.Status); Assert.Null(result.Bundle);
    }

    [Theory]
    [InlineData("version")] [InlineData("owner")] [InlineData("generation")] [InlineData("cursor")]
    [InlineData("head")] [InlineData("application")] [InlineData("policy")] [InlineData("knowledge")]
    [InlineData("dataset")] [InlineData("subject")] [InlineData("date")] [InlineData("time")]
    public async Task InvalidSnapshotsCannotGrantRetrieval(string field)
    {
        var request = Request("current_match_summary"); var snapshot = request.Snapshot; var context = snapshot.Context; var query = context.Query;
        request = field switch
        {
            "version" => request with { Version = "future-v9" },
            "owner" => request with { Snapshot = snapshot with { OwnerKey = "" } },
            "generation" => request with { Snapshot = snapshot with { Generation = -1 } },
            "head" => request with { Snapshot = snapshot with { Context = context with { HeadSequence = 0 } } },
            "time" => request with { Snapshot = snapshot with { TemporalMode = MatchWorkflowTemporalMode.LiveNow } },
            _ => request with { Snapshot = snapshot with { Context = context with { Query = field switch
            {
                "cursor" => query with { CursorMs = -1 }, "application" => query with { ApplicationSequence = 100 },
                "policy" => query with { PresentationPolicyRevision = 0 }, "knowledge" => query with { KnowledgeMode = "known_then" },
                "dataset" => query with { DatasetId = "../outside" }, "subject" => query with { SubjectId = "person from url" },
                _ => query with { EffectiveDate = "2026-02-31" }
            } } } }
        };
        Assert.Equal(MatchWorkflowStatus.InvalidRequest, (await NoRead().ExecuteAsync(request, _ => true)).Status);
    }

    [Theory]
    [InlineData("clarify", MatchWorkflowStatus.Clarify)] [InlineData("unsupported", MatchWorkflowStatus.Unsupported)]
    [InlineData("other_live_matches_stats", MatchWorkflowStatus.Unavailable)] [InlineData("selected_player_context", MatchWorkflowStatus.Clarify)]
    public async Task NoReadRoutesAndMissingSelectionHaveDistinctOutcomes(string route, MatchWorkflowStatus status)
    {
        var request = Request(route); if (route == "other_live_matches_stats") request = request with { Snapshot = request.Snapshot with { TemporalMode = MatchWorkflowTemporalMode.LiveNow } };
        var result = await NoRead().ExecuteAsync(request, _ => true); Assert.Equal(status, result.Status); Assert.Null(result.Bundle);
        if (route == "other_live_matches_stats") Assert.Equal("The requested data capability is unavailable. Recordings cannot substitute for live games.", result.Message);
    }

    [Fact]
    public async Task UnresolvableSelectedPersonClarifiesInsteadOfRetrievingAnotherPlayer()
    {
        var view = UnitView(); var query = view.Context with { SubjectId = "person-not-in-roster" }; var reads = 0;
        var executor = new MatchWorkflowExecutor((q, _) => { reads++; Assert.Equal(query.SubjectId, q.SubjectId); return Task.FromResult(view with { Context = q }); });
        var result = await executor.ExecuteAsync(Request("selected_player_context", query, view.Dataset.HeadSequence), _ => true);
        Assert.Equal(MatchWorkflowStatus.Clarify, result.Status); Assert.Equal(1, reads); Assert.Null(result.Bundle);
    }

    [Theory]
    [InlineData("owner")] [InlineData("generation")] [InlineData("cursor")] [InlineData("head")]
    [InlineData("subject")] [InlineData("version")] [InlineData("fixture")]
    public async Task AuthoritativeContextChangesDiscardLateReadsAndNeverCrossOwners(string change)
    {
        var view = UnitView(); var request = Request("current_match_summary", view.Context, view.Dataset.HeadSequence); var current = request.Snapshot;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var completion = new TaskCompletionSource<FootballView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MatchWorkflowExecutor((_, _) => { entered.SetResult(); return completion.Task; });
        var pending = executor.ExecuteAsync(request, captured => captured == current); await entered.Task;
        current = change switch
        {
            "owner" => current with { OwnerKey = "another-authenticated-owner" }, "generation" => current with { Generation = current.Generation + 1 },
            "head" => current with { Context = current.Context with { HeadSequence = current.Context.HeadSequence + 1 } },
            _ => current with { Context = current.Context with { Query = change switch
            {
                "cursor" => current.Context.Query with { CursorMs = 1000 }, "subject" => current.Context.Query with { SubjectId = "person-two" },
                "version" => current.Context.Query with { DatasetVersion = "another-version" }, _ => current.Context.Query with { FixtureId = "another-fixture" }
            } } }
        };
        completion.SetResult(view); var result = await pending; Assert.Equal(MatchWorkflowStatus.Stale, result.Status); Assert.Null(result.Bundle);
        Assert.Equal(MatchWorkflowStatus.Stale, (await NoRead().ExecuteAsync(request, captured => captured == current)).Status);
    }

    [Theory]
    [InlineData("context")] [InlineData("manifest")] [InlineData("stream")] [InlineData("head")] [InlineData("policy")] [InlineData("delivery")]
    public async Task ReturnedContextDomainHeadAndPolicyMustMatchSnapshot(string field)
    {
        var view = UnitView(); var request = Request("current_match_summary", view.Context, view.Dataset.HeadSequence);
        var wrong = field switch
        {
            "context" => view with { Context = view.Context with { CursorMs = 1000 } },
            "manifest" => view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { FixtureId = "another-fixture" } } },
            "stream" => view with { Dataset = view.Dataset with { Stream = view.Dataset.Stream with { DatasetVersion = "another-version" } } },
            "head" => view with { Dataset = view.Dataset with { HeadSequence = view.Dataset.HeadSequence + 1 } },
            "policy" => view with { Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with { PolicyRevision = 2 } } },
            _ => view with { Dataset = view.Dataset with { Stream = view.Dataset.Stream with { DeliveryMode = "live" } } }
        };
        var result = await new MatchWorkflowExecutor((_, _) => Task.FromResult(wrong)).ExecuteAsync(request, _ => true);
        Assert.Equal(MatchWorkflowStatus.Stale, result.Status); Assert.Null(result.Bundle);
    }

    [Fact]
    public async Task LedgerCorrectionCannotLeakIntoEarlierApplicationRevision()
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(fixture); var goal = SharedFootballTests.Goal(fixture);
        SharedFootballTests.Add(ledger, fixture, 2, goal);
        var request = Request("current_match_events", SharedFootballTests.Query(fixture, 80000), 2);
        SharedFootballTests.Add(ledger, fixture, 3, goal with { Revision = 2, SupersedesRevision = 1, Payload = ((MatchEventPayload)goal.Payload) with { Decision = "disallowed" } });
        var executor = new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q)));
        Assert.Equal(MatchWorkflowStatus.Stale, (await executor.ExecuteAsync(request, _ => true)).Status);
        var oldBoundary = request with { Snapshot = request.Snapshot with { Context = request.Snapshot.Context with { HeadSequence = 3, Query = request.Snapshot.Context.Query with { ApplicationSequence = 2 } } } };
        var earlier = (await executor.ExecuteAsync(oldBoundary, _ => true)).Bundle!; Assert.Equal(1, Assert.Single(earlier.EventProvenance).Revision);
        var corrected = (await executor.ExecuteAsync(request with { Snapshot = request.Snapshot with { Context = request.Snapshot.Context with { HeadSequence = 3 } } }, _ => true)).Bundle!;
        Assert.Equal(2, Assert.Single(corrected.EventProvenance).Revision); Assert.Contains("disallowed", corrected.Items[0].Text);
    }

    [Fact]
    public async Task CancellationStopsWaitingEvenIfReadIgnoresTokenAndLateResultIsDiscarded()
    {
        using var cancellation = new CancellationTokenSource(); var view = UnitView(); var request = Request("current_match_summary", view.Context, view.Dataset.HeadSequence);
        var completion = new TaskCompletionSource<FootballView>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new MatchWorkflowExecutor((_, _) => { entered.SetResult(); return completion.Task; });
        var pending = executor.ExecuteAsync(request, _ => true, cancellation.Token); await entered.Task; cancellation.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(MatchWorkflowStatus.Cancelled, result.Status); Assert.Null(result.Bundle);
        completion.SetResult(view); Assert.Null(result.Bundle);
        Assert.Equal(MatchWorkflowStatus.Cancelled, (await NoRead().ExecuteAsync(request, _ => true, cancellation.Token)).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReadFaultsAndIndependentReadCancellationAreSanitizedWithoutSummaryFallback(bool cancelled)
    {
        Exception exception = cancelled ? new OperationCanceledException("private cancellation trace") : new HttpRequestException("private endpoint and credential trace");
        var executor = new MatchWorkflowExecutor((_, _) => Task.FromException<FootballView>(exception));
        var result = await executor.ExecuteAsync(Request("current_match_events"), _ => true);
        Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle);
        Assert.DoesNotContain("private", result.Message); Assert.DoesNotContain("goals", result.Message); Assert.DoesNotContain("Unit", result.Message);
    }

    [Theory]
    [InlineData("view")] [InlineData("dataset")] [InlineData("manifest")] [InlineData("stream")] [InlineData("events")]
    public async Task MalformedApiViewsFailClosedWithoutThrowing(string field)
    {
        var view = UnitView(); var malformed = field switch
        {
            "view" => null!, "dataset" => view with { Dataset = null! },
            "manifest" => view with { Dataset = view.Dataset with { Manifest = null! } },
            "stream" => view with { Dataset = view.Dataset with { Stream = null! } },
            _ => view with { Events = null! }
        };
        var result = await new MatchWorkflowExecutor((_, _) => Task.FromResult(malformed)).ExecuteAsync(Request("current_match_summary"), _ => true);
        Assert.Equal(MatchWorkflowStatus.RetrievalFailure, result.Status); Assert.Null(result.Bundle);
    }

    [Fact]
    public async Task RegistryAndReturnedBundlesCannotBeMutatedOrShareMutableArrays()
    {
        var workflow = MatchWorkflowRegistry.Workflows["current_match_summary"];
        Assert.Equal(13, MatchWorkflowRegistry.Workflows.Count); Assert.Equal(1, workflow.MaxReads);
        Assert.Equal(2, MatchWorkflowRegistry.Workflows[MatchSourceQuestions.ExplainMomentWorkflow].MaxReads);
        Assert.Equal(2, MatchWorkflowRegistry.Workflows["selected_goal_event"].MaxReads);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, MatchWorkflowDefinition>)MatchWorkflowRegistry.Workflows).Remove(workflow.Id));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)workflow.Tools)[0] = "outside-tool");
        var view = UnitView(); var request = Request("current_match_summary", view.Context, view.Dataset.HeadSequence);
        var bundle = (await new MatchWorkflowExecutor((_, _) => Task.FromResult(view)).ExecuteAsync(request, _ => true)).Bundle!;
        var sourceId = bundle.Sources[0].SourceId; view.Dataset.Manifest.Sources[0] = view.Dataset.Manifest.Sources[0] with { SourceId = "mutated" };
        Assert.Equal(sourceId, bundle.Sources[0].SourceId); Assert.Throws<NotSupportedException>(() => ((IList<MatchWorkflowItem>)bundle.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)bundle.Items[0].NumericTokens).Clear());
    }

    [Fact]
    public async Task EvidenceBudgetsBoundEventFanoutAndRejectOversizedData()
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(fixture); var goal = SharedFootballTests.Goal(fixture);
        SharedFootballTests.Add(ledger, fixture, 2, Enumerable.Range(0, 25).Select(i => goal with { RecordId = "goal-" + i }).ToArray());
        var view = ledger.Query(SharedFootballTests.Query(fixture, 80000) with { ApplicationSequence = 2 }); var request = Request("current_match_events", view.Context, 2);
        var executor = new MatchWorkflowExecutor((_, _) => Task.FromResult(view)); var bounded = await executor.ExecuteAsync(request, _ => true);
        Assert.Equal(MatchWorkflowStatus.Partial, bounded.Status); Assert.Equal(MatchWorkflowLimits.Events, bounded.Bundle!.EventProvenance.Length);
        Assert.Contains("events_truncated", bounded.Bundle.Missingness); Assert.Equal(3, bounded.Bundle.Items.Length);
        Assert.True(bounded.Bundle.Items.Sum(item => item.Text.Length) <= MatchWorkflowLimits.EvidenceChars);
        view = view with { Events = view.Events.Select(record => record with { Evidence = Enumerable.Range(0, 3).Select(i => record.Evidence[0] with
            { EvidenceId = new string('e', 180), SourceId = new string('s', 180), Origin = new string('o', 180), Method = new string('m', 180) }).ToArray() }).ToArray() };
        var oversized = await executor.ExecuteAsync(request, _ => true); Assert.Equal(MatchWorkflowStatus.RetrievalFailure, oversized.Status); Assert.Null(oversized.Bundle);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelectedRealGoalDuringLeadInIsExplicitAndNeverImportsLaterEventsScoreOrPositions(bool atRelease)
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.Capabilities.Contains("match_event"));
        var end = ledger.Query(Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
        var highlight = Matchmate.Web.Experience.ExperiencePresentation.Highlights(end).First(h => h.Kind == "goal");
        var query = Query(dataset, highlight.CursorMs - (atRelease ? 0 : 15000)); var selection = new MatchSelectedEvent(highlight.RecordId, highlight.CursorMs, highlight.ReportedClock);
        var request = Request("selected_goal_event", query, dataset.HeadSequence); request = request with { Snapshot = request.Snapshot with {
            Context = request.Snapshot.Context with { SelectedEvent = selection }, TemporalMode = MatchWorkflowTemporalMode.SelectedGoalRetrospective } };
        var reads = new List<FootballQuery>(); var executor = new MatchWorkflowExecutor((q, _) => { reads.Add(q); return Task.FromResult(ledger.Query(q)); });
        var result = await executor.ExecuteAsync(request, _ => true); Assert.Equal(MatchWorkflowStatus.Success, result.Status);
        Assert.Equal(atRelease ? 1 : 2, reads.Count); Assert.All(reads, q => Assert.Equal(dataset.HeadSequence, q.ApplicationSequence));
        Assert.Equal(query.CursorMs, reads[0].CursorMs); if (!atRelease) Assert.Equal(selection.ReleaseCursorMs, reads[1].CursorMs);
        var bundle = result.Bundle!; Assert.Equal(selection, bundle.Context.SelectedEvent); Assert.Equal(query.CursorMs, bundle.Context.Query.CursorMs);
        Assert.Equal(selection.RecordId, Assert.Single(bundle.EventProvenance).RecordId); Assert.Equal(4, bundle.Items.Length);
        var selected = Assert.Single(bundle.Items, i => i.Id == "selected_goal");
        using var data = JsonDocument.Parse(selected.Text); Assert.Equal("retrospective_selected_goal", data.RootElement.GetProperty("scope").GetString());
        Assert.Equal(atRelease, data.RootElement.GetProperty("alreadyEligibleAtCurrentCursor").GetBoolean());
        Assert.Equal(atRelease ? 1 : 0, JsonDocument.Parse(bundle.Items.Single(i => i.Id == "match_summary").Text).RootElement.GetProperty("homeReportedGoals").GetInt32());
        var json = JsonSerializer.Serialize(bundle);
        Assert.DoesNotContain("Segments", json); Assert.DoesNotContain("Releases", json); Assert.DoesNotContain("People", json);
        foreach (var later in end.Events.Where(r => r.RecordId != selection.RecordId)) Assert.DoesNotContain(later.RecordId, json);
        var ordinary = await executor.ExecuteAsync(Request("current_match_summary", query, dataset.HeadSequence) with { Snapshot = request.Snapshot with { TemporalMode = MatchWorkflowTemporalMode.ReplayAtCursor } }, _ => true);
        Assert.Null(ordinary.Bundle!.Context.SelectedEvent); if (!atRelease) Assert.DoesNotContain(selection.RecordId, JsonSerializer.Serialize(ordinary.Bundle));
    }

    [Theory]
    [InlineData("missing")] [InlineData("mode")] [InlineData("id")] [InlineData("clock")] [InlineData("negative")]
    public async Task InvalidSelectedGoalRequestsRejectBeforeReads(string field)
    {
        var request = SelectedRequest(); var context = request.Snapshot.Context;
        request = request with { Snapshot = request.Snapshot with { Context = context with { SelectedEvent = field switch {
            "missing" => null, "id" => context.SelectedEvent! with { RecordId = "../private" }, "clock" => context.SelectedEvent! with { ReportedClock = "" },
            "negative" => context.SelectedEvent! with { ReleaseCursorMs = -1 }, _ => context.SelectedEvent } },
            TemporalMode = field == "mode" ? MatchWorkflowTemporalMode.ReplayAtCursor : MatchWorkflowTemporalMode.SelectedGoalRetrospective } };
        Assert.Equal(MatchWorkflowStatus.InvalidRequest, (await NoRead().ExecuteAsync(request, _ => true)).Status);
    }

    [Theory]
    [InlineData("release")] [InlineData("duplicate")] [InlineData("type")] [InlineData("record-domain")] [InlineData("reported-clock")]
    [InlineData("head")] [InlineData("returned-query")]
    public async Task SelectedGoalMustMatchScheduleGoalIdentityAndFreshReadContext(string fault)
    {
        var (ledger, request) = SelectedUnit(); var reads = 0;
        var executor = new MatchWorkflowExecutor((query, _) => {
            reads++; var view = ledger.Query(query);
            if (fault is "release" or "duplicate") return Task.FromResult(view with { Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with {
                Releases = fault == "duplicate" ? [new("goal-one", 60000), new("goal-one", 60000)] : [new("goal-one", 60001)] } } });
            if (reads == 2) view = fault switch {
                "type" => view with { Events = view.Events.Select(r => r with { Payload = new MatchEventPayload("card", "team-home") }).ToArray() },
                "record-domain" => view with { Events = view.Events.Select(r => r with { DatasetVersion = "another-version" }).ToArray() },
                "reported-clock" => view with { Events = view.Events.Select(r => r with { FootballTime = r.FootballTime! with { ReportedClock = "99" } }).ToArray() },
                "head" => view with { Dataset = view.Dataset with { HeadSequence = 99 } },
                "returned-query" => view with { Context = query with { CursorMs = query.CursorMs + 1 } }, _ => view };
            return Task.FromResult(view);
        });
        var result = await executor.ExecuteAsync(request, _ => true);
        Assert.Equal(fault is "head" or "returned-query" ? MatchWorkflowStatus.Stale : MatchWorkflowStatus.InvalidRequest, result.Status);
        Assert.Null(result.Bundle); Assert.Equal(fault is "release" or "duplicate" ? 1 : 2, reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelectedSecondReadDiscardsCancellationOrChangedContext(bool cancel)
    {
        var (ledger, request) = SelectedUnit(); using var cancellation = new CancellationTokenSource(); var current = true; var reads = 0;
        var executor = new MatchWorkflowExecutor((query, _) => { reads++; if (reads == 2) { if (cancel) cancellation.Cancel(); else current = false; } return Task.FromResult(ledger.Query(query)); });
        var result = await executor.ExecuteAsync(request, _ => current, cancellation.Token);
        Assert.Equal(cancel ? MatchWorkflowStatus.Cancelled : MatchWorkflowStatus.Stale, result.Status); Assert.Null(result.Bundle); Assert.Equal(2, reads);
    }

    private static MatchWorkflowRequest SelectedRequest() => Request("selected_goal_event", SharedFootballTests.Query(SharedFootballTests.Fixture(), 45000)) with {
        Snapshot = new("authenticated-owner-key", 1, new(SharedFootballTests.Query(SharedFootballTests.Fixture(), 45000), 2, new("goal-one", 60000, "1")), MatchWorkflowTemporalMode.SelectedGoalRetrospective) };
    private static (FootballLedger Ledger, MatchWorkflowRequest Request) SelectedUnit()
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = new FootballLedger();
        SharedFootballTests.Add(ledger, fixture, 1, SharedFootballTests.Context(fixture).Select(r => r.Payload is SchedulePayload schedule
            ? r with { Payload = schedule with { Releases = [new("goal-one", 60000)] } } : r).ToArray());
        SharedFootballTests.Add(ledger, fixture, 2, SharedFootballTests.Goal(fixture) with { RecordId = "goal-one", FootballTime = SharedFootballTests.Goal(fixture).FootballTime! with { ReportedClock = "1" } }); return (ledger, SelectedRequest());
    }

    private static MatchWorkflowExecutor NoRead() => new((_, _) => throw new InvalidOperationException("No read was permitted."));
    private static FootballView UnitView()
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(fixture);
        SharedFootballTests.Add(ledger, fixture, 2, SharedFootballTests.Frame(fixture, 0));
        return ledger.Query(SharedFootballTests.Query(fixture, 0) with { ApplicationSequence = 2 });
    }
    private static FootballQuery Query(FootballDataset dataset, long cursor) => FullFootballSourceTests.Query(dataset, cursor);
    private static MatchWorkflowRequest Request(string route, FootballQuery? query = null, long head = 2) =>
        new(route, MatchWorkflowRegistry.Version, new("authenticated-owner-key", 1, new(query ?? UnitView().Context, head), MatchWorkflowTemporalMode.ReplayAtCursor));
}
