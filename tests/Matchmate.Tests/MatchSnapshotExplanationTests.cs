using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Xunit;

namespace Matchmate.Tests;

public sealed partial class MatchConversationTests
{
    private static readonly Lazy<Task<FootballLedger>> SnapshotChelsea = new(() => ReadSnapshotSource(false));
    private static readonly Lazy<Task<FootballLedger>> SnapshotLiverpool = new(() => ReadSnapshotSource(true));
    private static async Task<FootballLedger> ReadSnapshotSource(bool liverpool)
    {
        IStreamingFootballSource source = liverpool
            ? new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output/brentford-liverpool-generated/dataset-v2/manifest.json"))
            : new FullBrentfordFootballSource(Path.Combine(LocalPaths.FindRoot(), ".local/shared-inputs/brentford-full-v5/manifest.json"));
        var ledger = new FootballLedger();
        await foreach (var batch in source.ReadBatchesAsync(default)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
        return ledger;
    }
    private static FootballView SnapshotEnd(FootballLedger ledger)
    {
        var dataset = Assert.Single(ledger.Catalog());
        return ledger.Query(FullFootballSourceTests.Query(dataset, dataset.Schedule!.Segments[^1].CursorEndMs));
    }
    private static MatchSelectedEvent SnapshotSelection(FootballView end, FootballRecord record) => new(record.RecordId,
        end.Dataset.Schedule!.Releases.Single(r => r.RecordId == record.RecordId).CursorMs,
        record.FootballTime!.ReportedClock!, ((MatchEventPayload)record.Payload).EventKind);
    private static MatchWorkflowRequest SnapshotRequest(FootballView view, MatchSelectedEvent? selection = null) => new(
        MatchSourceQuestions.ExplainMomentWorkflow, MatchWorkflowRegistry.Version,
        new("snapshot-owner", 1, new(view.Context, view.Dataset.HeadSequence, selection), MatchWorkflowTemporalMode.ReplayAtCursor));
    private static MatchSnapshotData SnapshotData(MatchWorkflowResult result) => JsonSerializer.Deserialize<MatchSnapshotData>(
        Assert.Single(Assert.IsType<MatchWorkflowBundle>(result.Bundle).Items).Text)!;

    public static IEnumerable<object[]> SnapshotGoals => from liverpool in new[] { false, true }
        from goal in new[] { 0, 1, 2 } from phase in new[] { -15000L, 0L, 30000L }
        select new object[] { liverpool, goal, phase };

    [Theory, MemberData(nameof(SnapshotGoals))]
    public async Task SnapshotAllSixRealGoalsUseActualDisplayedStagingAndOnlyTheirOwnReports(bool liverpool, int goalIndex, long phase)
    {
        var ledger = await (liverpool ? SnapshotLiverpool : SnapshotChelsea).Value;
        var end = SnapshotEnd(ledger); var record = end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "goal" }).ElementAt(goalIndex);
        var selected = SnapshotSelection(end, record);
        var view = ledger.Query(end.Context with { CursorMs = selected.ReleaseCursorMs + phase });
        using var h = new Harness(actual: view); h.Api.View = view;
        h.Api.Wait = (query, _) => { h.Api.View = ledger.Query(query); return Task.CompletedTask; };
        h.SetContext(new(view.Context, view.Dataset.HeadSequence, selected)); h.Router.Configured = h.Model.Configured = false;
        var samples = new Dictionary<string, string>();
        foreach (var language in MatchLanguages.Supported)
        {
            h.Session.SetLanguage(language);
            var result = await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.Positions);
            Assert.True(result.Accepted, result.Message);
            var turn = h.Session.Turns[^1]; Assert.True(turn.EvidenceOnly); Assert.True(turn.SelectedEventScope); Assert.Null(turn.EventVisual);
            var data = JsonSerializer.Deserialize<MatchSnapshotData>(Assert.Single(turn.Evidence).Text)!;
            var scenes = MatchPitchScene.Select(view, selected, view.Dataset.HeadSequence, end.Events);
            var mask = MatchPitchScene.Mask(view, view.Dataset.HeadSequence, end.Events, scenes);
            var pitch = MatchPitchPresenter.Project(view, scenes, true, mask);
            Assert.Equal(pitch.Path.Length > 0, data.Path); Assert.Equal(pitch.ActiveStep?.Kind, data.ActiveKind);
            Assert.Equal(phase <= 0, data.SelectedOverlay); Assert.Equal(phase > 0, !data.SelectedOverlay);
            Assert.Equal(record.Payload is MatchEventPayload { Sequence: { } sequence } ? sequence.Steps.Select(s => s.Kind) : [], data.SelectedSteps.Select(s => s.Kind));
            var expectedActors = ((MatchEventPayload)record.Payload).Sequence!.Steps.Select(s =>
                s.ActorId is null ? null : end.Dataset.People.Single(p => p.PersonId == s.ActorId).Name);
            Assert.Equal(expectedActors, data.SelectedSteps.Select(s => s.Actor));
            foreach (var actor in expectedActors.OfType<string>()) Assert.Contains(actor, turn.Answer);
            Assert.DoesNotContain(language == MatchLanguage.Swedish ? "Klubbrapport:" : language == MatchLanguage.Spanish ? "Informe del club:" : "Club report:", turn.Answer);
            if (!liverpool && language == MatchLanguage.Swedish && goalIndex == 0)
            {
                Assert.Contains("Mikkel Damsgaard slår hörnan.", turn.Answer);
                Assert.Contains("Jannik Schuster nickar tvärs över målet.", turn.Answer);
                Assert.Contains("Jaidon Anthony nickar från mycket nära håll.", turn.Answer);
                Assert.DoesNotContain("sekvens: slår", turn.Answer);
            }
            Assert.All(end.Events.Where(r => r.RecordId != selected.RecordId), r => Assert.DoesNotContain(r.RecordId, turn.Evidence[0].Text));
            Assert.InRange(turn.Answer.Length, 1, MatchChatLimits.AnswerChars);
            var sections = turn.Answer.Split("\n\n", StringSplitOptions.None);
            Assert.Equal(4, sections.Length); Assert.All(sections, section => Assert.False(string.IsNullOrWhiteSpace(section)));
            Assert.Equal(SnapshotText.Get(language, "Selected", ReplayText.Kind(language, "goal"), selected.ReportedClock), sections[0]);
            Assert.StartsWith(SnapshotText.Get(language, "ReportedSequence", "").TrimEnd() + "\n1. ", sections[1]);
            var reportedLines = sections[1].Split('\n'); Assert.Equal(data.SelectedSteps.Length + 1, reportedLines.Length);
            for (var stepIndex = 0; stepIndex < data.SelectedSteps.Length; stepIndex++) Assert.StartsWith($"{stepIndex + 1}. ", reportedLines[stepIndex + 1]);
            Assert.StartsWith(SnapshotText.Get(language, "OnPitch") + "\n", sections[2]);
            Assert.Equal(SnapshotText.Get(language, "Generated"), sections[3]);
            Assert.DoesNotContain("<", turn.Answer); Assert.DoesNotContain("```", turn.Answer);
            Assert.DoesNotContain("release_pass", turn.Answer); Assert.DoesNotContain("reported_finish", turn.Answer);
            if (pitch.Frame?.Ball is { Availability: "present", XM: { } x, YM: { } y } && !mask)
            {
                Assert.Equal(x < end.Dataset.Fixture!.Pitch.LengthM / 3 ? "Left" : x > end.Dataset.Fixture.Pitch.LengthM * 2 / 3 ? "Right" : "Middle", data.BallThird);
                Assert.Equal(y < end.Dataset.Fixture.Pitch.WidthM / 3 ? "Top" : y > end.Dataset.Fixture.Pitch.WidthM * 2 / 3 ? "Bottom" : "Central", data.BallBand);
            }
            if (phase <= 0) Assert.NotEqual(JsonSerializer.Serialize(view.Frame), JsonSerializer.Serialize(pitch.Frame));
            samples[language.ToString()] = turn.Answer;
        }
        Assert.Empty(h.Model.Messages); Assert.Empty(h.Router.Inputs);
        Assert.All(h.Api.Queries, q => Assert.Equal(view.Dataset.HeadSequence, q.ApplicationSequence));
        if (goalIndex == (liverpool ? 1 : 0) && phase == -15000)
        {
            var directory = Path.Combine(LocalPaths.FindRoot(), ".local/verification/snapshot-explanation");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, liverpool ? "sample-liverpool.json" : "sample-chelsea.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotBookingsSubstitutionsAndDismissalsStayEligibleAndNonSpatial(bool liverpool)
    {
        var ledger = await (liverpool ? SnapshotLiverpool : SnapshotChelsea).Value; var end = SnapshotEnd(ledger);
        foreach (var record in end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: not "goal" } e && MatchEventVisual.SupportedKind(e.EventKind)))
        {
            var selected = SnapshotSelection(end, record);
            var executor = new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query)));
            var view = ledger.Query(end.Context with { CursorMs = selected.ReleaseCursorMs });
            var result = await executor.ExecuteAsync(SnapshotRequest(view, selected), _ => true);
            Assert.True(result.Status == MatchWorkflowStatus.Success, selected.RecordId + ": " + result.Status + " " + result.Message);
            var data = SnapshotData(result); Assert.Equal(((MatchEventPayload)record.Payload).Sequence is not null, data.SelectedOverlay); Assert.False(data.Path);
            var payload = (MatchEventPayload)record.Payload;
            if (payload.EventKind == "substitution")
            {
                Assert.Equal(end.Dataset.People.Single(p => p.PersonId == payload.OutgoingPersonId).Name, data.SelectedSteps[0].Actor);
                Assert.Equal(end.Dataset.People.Single(p => p.PersonId == payload.IncomingPersonId).Name, data.SelectedSteps[0].RelatedActor);
            }
            if (payload.EventKind == "red_card")
            {
                Assert.True(data.DismissalAnnotation);
                var name = end.Dataset.People.Single(p => p.PersonId == payload.PersonId).Name;
                Assert.DoesNotContain(name, data.Rings); Assert.DoesNotContain(name, data.Nearby);
            }
            foreach (var language in MatchLanguages.Supported)
            {
                var formatted = FormatSnapshot(result.Bundle!, language);
                Assert.Contains(SnapshotText.Get(language, "NonSpatial"), formatted);
                Assert.InRange(formatted.Length, 1, 1800);
            }
            var before = ledger.Query(end.Context with { CursorMs = selected.ReleaseCursorMs - 1 });
            Assert.Equal(MatchWorkflowStatus.Unavailable, (await executor.ExecuteAsync(SnapshotRequest(before, selected), _ => true)).Status);
        }
    }

    private static string FormatSnapshot(MatchWorkflowBundle bundle, MatchLanguage language) =>
        ((string Answer, MatchChatEvidence[] Evidence))typeof(MatchSourceQuestions).GetMethod("FormatForLanguage", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [bundle, null, MatchAudienceMode.Fan, language])! is var result ? result.Answer : "";

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotUnselectedFutureGoalWindowIsNeutralAndHasNoHiddenBallOrActors(bool liverpool)
    {
        var ledger = await (liverpool ? SnapshotLiverpool : SnapshotChelsea).Value; var end = SnapshotEnd(ledger);
        var goal = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "goal" }); var selection = SnapshotSelection(end, goal);
        var view = ledger.Query(end.Context with { CursorMs = selection.ReleaseCursorMs - 5000 });
        var result = await new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query))).ExecuteAsync(SnapshotRequest(view), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, result.Status); var data = SnapshotData(result);
        Assert.True(data.Neutral); Assert.Null(data.BallThird); Assert.Null(data.BallBand); Assert.Empty(data.Nearby); Assert.Empty(data.Rings);
        Assert.Empty(data.SelectedSteps); Assert.Null(data.ActiveActor); Assert.False(data.Path);
        Assert.All(MatchLanguages.Supported, language => Assert.Equal(SnapshotText.Get(language, "Neutral"), FormatSnapshot(result.Bundle!, language)));
    }

    [Theory] [InlineData("ordinary")] [InlineData("player-gap")] [InlineData("ball-missing")] [InlineData("no-frame")]
    [InlineData("unknown-name")] [InlineData("unknown-method")] [InlineData("duplicate-id")]
    public async Task SnapshotOrdinaryMomentUsesPresentDisplayedBallAndNearestKnownPlayersWithIndependentMissingness(string state)
    {
        using var h = new Harness(); var original = h.Api.View;
        var person = original.Dataset.People[0];
        var frame = original.Frame!;
        frame = frame with { Ball = frame.Ball with { Availability = "present", XM = 90, YM = 10, Method = state == "unknown-method" ? "unknown" : "observed", Quality = "usable" },
            Players = [frame.Players[0] with { SubjectId = person.PersonId, Availability = state == "player-gap" ? "missing" : "present", XM = state == "player-gap" ? null : 89, YM = state == "player-gap" ? null : 11, Method = "observed", Quality = "usable" }] };
        if (state == "ball-missing") frame = frame with { Ball = frame.Ball with { Availability = "missing", XM = null, YM = null } };
        var view = original with { Frame = state == "no-frame" ? null : frame, TrackingGap = state == "player-gap" };
        if (state == "unknown-name") view = view with { Dataset = view.Dataset with { People = [] } };
        if (state == "duplicate-id") view = view with { Dataset = view.Dataset with { People = [person, person] } };
        h.Api.View = view;
        var result = await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.Positions);
        if (state == "duplicate-id") { Assert.False(result.Accepted); Assert.Empty(h.Session.Turns); return; }
        Assert.True(result.Accepted, result.Message);
        var turn = Assert.Single(h.Session.Turns); var data = JsonSerializer.Deserialize<MatchSnapshotData>(Assert.Single(turn.Evidence).Text)!;
        Assert.Equal(state == "no-frame", data.NoFrame);
        if (state is "ball-missing" or "no-frame") { Assert.Null(data.BallThird); Assert.Empty(data.Nearby); }
        else
        {
            Assert.Equal("Right", data.BallThird); Assert.Equal("Top", data.BallBand);
            if (state is "unknown-name" or "player-gap") Assert.Empty(data.Nearby); else Assert.Equal(person.Name, Assert.Single(data.Nearby));
        }
        Assert.Equal(state == "unknown-method", data.UnknownMethod);
        Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    [Theory] [InlineData("head")] [InlineData("fixture")] [InlineData("version")] [InlineData("policy")]
    [InlineData("revision")] [InlineData("missing")] [InlineData("duplicate")] [InlineData("current-revision")]
    public async Task SnapshotMismatchedEndMetadataAndInvalidIndexesFailClosed(string change)
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger); var view = ledger.Query(end.Context with { CursorMs = 1000 });
        var reads = 0;
        var result = await new MatchWorkflowExecutor((query, _) => {
            reads++; var current = ledger.Query(query); if (reads == 1) return Task.FromResult(change == "current-revision" ? current with { Events = [end.Events[0] with { Revision = 999 }] } : current);
            current = change switch {
                "head" => current with { Dataset = current.Dataset with { HeadSequence = current.Dataset.HeadSequence + 1 } },
                "fixture" => current with { Context = current.Context with { FixtureId = "wrong-fixture" } },
                "version" => current with { Dataset = current.Dataset with { Manifest = current.Dataset.Manifest with { DatasetVersion = "wrong-version" } } },
                "policy" => current with { Dataset = current.Dataset with { Schedule = current.Dataset.Schedule! with { PolicyRevision = current.Dataset.Schedule.PolicyRevision + 1 } } },
                "revision" => current with { Events = [end.Events[0] with { Revision = 0 }, .. end.Events.Skip(1)] },
                "missing" => current with { Events = end.Events.Skip(1).ToArray() },
                "duplicate" => current with { Events = [end.Events[0], .. end.Events] }, _ => current };
            return Task.FromResult(current);
        }).ExecuteAsync(SnapshotRequest(view), _ => true);
        Assert.Contains(result.Status, new[] { MatchWorkflowStatus.Stale, MatchWorkflowStatus.RetrievalFailure }); Assert.Null(result.Bundle);
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task SnapshotExactLocalizedLabelsBypassModelsWhileDecisionCannotSelectCodeOwnedWorkflow(MatchLanguage language)
    {
        using var h = new Harness(); h.Session.SetLanguage(language); h.Model.Configured = h.Router.Configured = false;
        Assert.True((await h.Ask(MatchSourceQuestions.DisplayQuestion(MatchSourceQuestionId.Positions, language))).Accepted);
        Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
        h.Session.NewChat(); h.Model.Configured = h.Router.Configured = true; h.Router.Route = MatchSourceQuestions.ExplainMomentWorkflow;
        var reads = h.Api.Queries.Count; Assert.Equal(MatchChatOutcome.RoutingUnavailable, (await h.Ask("Explain things near the ball please")).Outcome);
        Assert.Equal(reads, h.Api.Queries.Count); Assert.DoesNotContain(MatchSourceQuestions.ExplainMomentWorkflow, DecisionWorkflowRouter.Criteria.Keys);
    }

    [Theory] [InlineData("cancel")] [InlineData("rewind")] [InlineData("head")] [InlineData("readiness")]
    public async Task SnapshotPendingReplyCannotCommitAfterAcceptedContextOrReadinessChanges(string change)
    {
        using var h = new Harness(); h.SetContext(h.Context with { Query = h.Context.Query with { CursorMs = 1000 } });
        using var component = Workspace(h); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Api.Wait = (_, _) => { entered.TrySetResult(); return release.Task; };
        var pending = WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Positions); await entered.Task;
        if (change == "readiness") { WorkspaceSet(component, "SnapshotReady", false); component.Apply(); }
        else if (change == "cancel") h.Session.Cancel();
        else h.SetContext(change == "head" ? h.Context with { HeadSequence = h.Context.HeadSequence + 1 } : h.Context with { Query = h.Context.Query with { CursorMs = 0 } });
        release.TrySetResult(); await pending; Assert.Empty(h.Session.Turns); Assert.Empty(h.Model.Messages);
    }

    [Fact]
    public async Task SnapshotReadinessGuardsChipAndTypedAliasWithoutBlockingOtherSourceActions()
    {
        using var h = new Harness(); using var component = Workspace(h); WorkspaceSet(component, "SnapshotReady", false); component.Apply();
        await WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Positions); Assert.Empty(h.Api.Queries);
        typeof(MatchConversation).GetField("_question", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, "Explain this moment");
        await WorkspaceInvoke(component, "SendAsync", (object)null!); Assert.Empty(h.Api.Queries);
        await WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Summary); Assert.Single(h.Api.Queries);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotOrdinaryRealFramesIdentifyTheThreeNearestPresentNamesWithoutPossessionClaim(bool liverpool)
    {
        var ledger = await (liverpool ? SnapshotLiverpool : SnapshotChelsea).Value; var end = SnapshotEnd(ledger);
        var view = ledger.Query(end.Context with { CursorMs = 10000 });
        var result = await new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query))).ExecuteAsync(SnapshotRequest(view), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, result.Status); var data = SnapshotData(result); Assert.False(data.Neutral);
        var scenes = MatchPitchScene.Select(view, null, view.Dataset.HeadSequence, end.Events);
        var pitch = MatchPitchPresenter.Project(view, scenes, true, MatchPitchScene.Mask(view, view.Dataset.HeadSequence, end.Events, scenes));
        var ball = pitch.Frame!.Ball;
        var expected = pitch.Frame.Players.Where(p => p.Availability == "present" && view.Dataset.People.Any(person => person.PersonId == p.SubjectId))
            .OrderBy(p => Math.Pow(p.XM!.Value - ball.XM!.Value, 2) + Math.Pow(p.YM!.Value - ball.YM!.Value, 2))
            .ThenBy(p => p.SubjectId, StringComparer.Ordinal).Take(3).Select(p => view.Dataset.People.Single(person => person.PersonId == p.SubjectId).Name);
        Assert.Equal(expected, data.Nearby); Assert.Equal(3, data.Nearby.Length);
        foreach (var language in MatchLanguages.Supported)
        {
            var answer = FormatSnapshot(result.Bundle!, language);
            Assert.Contains(SnapshotText.Get(language, "Nearby", string.Join(", ", data.Nearby)), answer);
            var sections = answer.Split("\n\n", StringSplitOptions.None); Assert.Equal(2, sections.Length);
            Assert.StartsWith(SnapshotText.Get(language, "OnPitch") + "\n", sections[0]);
            Assert.Equal(SnapshotText.Get(language, "Generated"), sections[1]);
            Assert.DoesNotContain(SnapshotText.Get(language, "ReportedSequence", "").TrimEnd(), answer);
            Assert.InRange(answer.Length, 1, MatchChatLimits.AnswerChars);
        }
    }

    [Fact]
    public void SnapshotResourcesHaveMatchingKeysPlaceholdersAndLocalizedNewLabels()
    {
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(LocalPaths.FindRoot(), $"src/Matchmate.Web/Localization/SnapshotText{suffix}.resx"))
            .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
        var neutral = Read("");
        foreach (var suffix in new[] { ".sv", ".es" })
        {
            var translated = Read(suffix); Assert.Equal(neutral.Keys.Order(), translated.Keys.Order());
            foreach (var key in neutral.Keys)
                Assert.Equal(Regex.Matches(neutral[key], @"\{\d+\}").Select(m => m.Value).Order(), Regex.Matches(translated[key], @"\{\d+\}").Select(m => m.Value).Order());
        }
        Assert.Equal("Explain this moment", MatchSourceQuestions.DisplayQuestion(MatchSourceQuestionId.Positions, MatchLanguage.English));
        Assert.Equal("Förklara det här ögonblicket", MatchSourceQuestions.DisplayQuestion(MatchSourceQuestionId.Positions, MatchLanguage.Swedish));
        Assert.Equal("Explica este momento", MatchSourceQuestions.DisplayQuestion(MatchSourceQuestionId.Positions, MatchLanguage.Spanish));
    }

    [Theory] [InlineData("complete")] [InlineData("missing-incoming")] [InlineData("missing-outgoing")]
    [InlineData("participant-only")] [InlineData("future")] [InlineData("wrong-team")]
    public async Task SnapshotEligibleLiverpoolSubstitutionWithoutSequenceRetainsOnlyExplicitIdentityAndNoOverlay(string state)
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger);
        var report = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "substitution", Sequence: null });
        var selected = SnapshotSelection(end, report); var payload = (MatchEventPayload)report.Payload;
        var participant = payload.OutgoingPersonId;
        var changed = report with { Payload = state switch {
            "missing-incoming" => payload with { IncomingPersonId = null },
            "missing-outgoing" => payload with { OutgoingPersonId = null },
            "participant-only" => payload with { OutgoingPersonId = null, IncomingPersonId = null, PersonId = participant },
            "wrong-team" => payload with { TeamId = end.Dataset.Fixture!.Home.Id == payload.TeamId ? end.Dataset.Fixture.Away.Id : end.Dataset.Fixture.Home.Id }, _ => payload } };
        var cursor = selected.ReleaseCursorMs - (state == "future" ? 1 : 0);
        var view = ledger.Query(end.Context with { CursorMs = cursor });
        var result = await new MatchWorkflowExecutor((query, _) => {
            var bound = ledger.Query(query);
            return Task.FromResult(bound with { Events = bound.Events.Select(r => r.RecordId == changed.RecordId ? changed : r).ToArray() });
        }).ExecuteAsync(SnapshotRequest(view, selected), _ => true);
        if (state is "future" or "wrong-team") { Assert.Equal(MatchWorkflowStatus.Unavailable, result.Status); Assert.Null(result.Bundle); return; }
        Assert.Equal(MatchWorkflowStatus.Success, result.Status); var data = SnapshotData(result); var step = Assert.Single(data.SelectedSteps);
        Assert.False(data.SelectedOverlay); Assert.False(data.Path); Assert.False(data.ChangeMarker); Assert.Empty(data.Rings);
        if (state == "missing-incoming") Assert.Null(step.RelatedActor);
        if (state == "missing-outgoing") Assert.Null(step.Actor);
        if (state == "participant-only") { Assert.Equal("substitution_participant", step.Kind); Assert.Equal(end.Dataset.People.Single(p => p.PersonId == participant).Name, step.Actor); }
        foreach (var language in MatchLanguages.Supported)
        {
            var answer = FormatSnapshot(result.Bundle!, language); Assert.Contains(SnapshotText.Get(language, "OutsideOverlay"), answer);
            if (state.StartsWith("missing-", StringComparison.Ordinal)) Assert.Contains(SnapshotText.Get(language, "UnknownActor"), answer);
        }
    }

    [Fact]
    public async Task SnapshotUnsupportedVarSelectionIsRejectedBeforeAnyRead()
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger);
        var reports = end.Events.Where(r => r.Payload is MatchEventPayload { EventKind: "var_decision" }).ToArray();
        Assert.Equal(2, reports.Length);
        foreach (var report in reports)
        {
            var reads = 0;
            var result = await new MatchWorkflowExecutor((query, _) => { reads++; return Task.FromResult(ledger.Query(query)); })
                .ExecuteAsync(SnapshotRequest(end, SnapshotSelection(end, report)), _ => true);
            Assert.Equal(MatchWorkflowStatus.InvalidRequest, result.Status); Assert.Equal(0, reads); Assert.Null(result.Bundle);
        }
    }

    [Theory]
    [InlineData("sub-2", -1)] [InlineData("sub-2", 0)] [InlineData("sub-2", 1)]
    [InlineData("sub-3", -1)] [InlineData("sub-3", 0)] [InlineData("sub-3", 1)]
    [InlineData("sub-4", -1)] [InlineData("sub-4", 0)] [InlineData("sub-4", 1)]
    public async Task SnapshotUnknownHalftimeEndpointsStayUnknownAndOnlyReleasedCurrentReportsCanAnswer(string id, long offset)
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger); var report = end.Events.Single(r => r.RecordId == id);
        Assert.Equal("unknown", report.FootballTime!.Precision); Assert.Null(report.FootballTime.StartMs); Assert.Null(report.FootballTime.EndMs);
        var selected = SnapshotSelection(end, report); var cursor = selected.ReleaseCursorMs + offset;
        var view = ledger.Query(end.Context with { CursorMs = cursor });
        var result = await new MatchWorkflowExecutor((query, _) => Task.FromResult(ledger.Query(query))).ExecuteAsync(SnapshotRequest(view, selected), _ => true);
        if (offset < 0) { Assert.Equal(MatchWorkflowStatus.Unavailable, result.Status); Assert.Null(result.Bundle); return; }
        Assert.Equal(MatchWorkflowStatus.Success, result.Status); var data = SnapshotData(result);
        Assert.False(data.SelectedOverlay); Assert.False(data.Path); Assert.False(data.ChangeMarker);
        Assert.All(result.Bundle!.EventProvenance, p => { Assert.Equal("unknown", p.FootballTime!.Precision); Assert.Null(p.FootballTime.StartMs); Assert.Null(p.FootballTime.EndMs); });
        var payload = (MatchEventPayload)report.Payload;
        foreach (var language in MatchLanguages.Supported)
        {
            var answer = FormatSnapshot(result.Bundle, language);
            Assert.Contains(end.Dataset.People.Single(p => p.PersonId == payload.OutgoingPersonId).Name, answer);
            Assert.Contains(end.Dataset.People.Single(p => p.PersonId == payload.IncomingPersonId).Name, answer);
            Assert.Contains(SnapshotText.Get(language, "OutsideOverlay"), answer);
            Assert.InRange(answer.Length, 1, MatchChatLimits.AnswerChars);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotInjectedReportCannotBypassFutureAuthoredReleaseOrKnownMinuteCutoff(bool unknownTime)
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger);
        var report = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "substitution", Sequence: null }
            && r.FootballTime!.Precision == (unknownTime ? "unknown" : "reported_minute"));
        var selected = SnapshotSelection(end, report); var cursor = selected.ReleaseCursorMs - 1;
        var view = ledger.Query(end.Context with { CursorMs = cursor });
        var result = await new MatchWorkflowExecutor((query, _) => {
            var bound = ledger.Query(query);
            // A faulty API placing the report in the early view still cannot defeat either guard.
            return Task.FromResult(bound.Events.Any(r => r.RecordId == report.RecordId) ? bound : bound with { Events = [.. bound.Events, report] });
        }).ExecuteAsync(SnapshotRequest(view, selected), _ => true);
        Assert.Equal(MatchWorkflowStatus.Unavailable, result.Status); Assert.Null(result.Bundle);
        if (unknownTime) return;
        // Even an incorrectly early authored release cannot authorize a known football cutoff.
        var release = selected with { ReleaseCursorMs = cursor };
        result = await new MatchWorkflowExecutor((query, _) => {
            var bound = ledger.Query(query);
            var schedule = bound.Dataset.Schedule! with { Releases = bound.Dataset.Schedule!.Releases.Select(r => r.RecordId == report.RecordId ? r with { CursorMs = cursor } : r).ToArray() };
            return Task.FromResult(bound with { Dataset = bound.Dataset with { Schedule = schedule }, Events = bound.Events.Any(r => r.RecordId == report.RecordId) ? bound.Events : [.. bound.Events, report] });
        }).ExecuteAsync(SnapshotRequest(view, release), _ => true);
        Assert.Equal(MatchWorkflowStatus.Unavailable, result.Status); Assert.Null(result.Bundle);
    }

    [Theory] [InlineData("no-frame")] [InlineData("neutral")]
    public async Task SnapshotEmptyPitchStatesOmitIrrelevantHeadingsAndNumberedSections(string state)
    {
        var ledger = await SnapshotLiverpool.Value; var end = SnapshotEnd(ledger);
        var goal = end.Events.First(r => r.Payload is MatchEventPayload { EventKind: "goal" });
        var selected = SnapshotSelection(end, goal); var view = ledger.Query(end.Context with { CursorMs = state == "neutral" ? selected.ReleaseCursorMs - 5000 : 10000 });
        var result = await new MatchWorkflowExecutor((query, _) => {
            var bound = ledger.Query(query); return Task.FromResult(state == "no-frame" ? bound with { Frame = null } : bound);
        }).ExecuteAsync(SnapshotRequest(view), _ => true);
        Assert.Equal(MatchWorkflowStatus.Success, result.Status);
        foreach (var language in MatchLanguages.Supported)
        {
            var answer = FormatSnapshot(result.Bundle!, language);
            Assert.Equal(SnapshotText.Get(language, state == "no-frame" ? "NoFrame" : "Neutral"), answer);
            Assert.DoesNotContain("\n", answer);
        }
    }
}
