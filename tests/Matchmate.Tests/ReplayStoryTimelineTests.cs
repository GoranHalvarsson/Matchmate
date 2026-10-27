using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;
using static Matchmate.Tests.GeneratedIllustrationStatisticsTests;

namespace Matchmate.Tests;

#pragma warning disable BL0006
public sealed class ReplayStoryTimelineTests
{
    internal static (TestFixture Fixture, FootballLedger Ledger) Recording(bool lateRelease = false, long delayedReleaseMs = 4000)
    {
        var fixture = GeneratedFixture();
        fixture = fixture with { Manifest = fixture.Manifest with { Sources = [.. fixture.Manifest.Sources, new("generated-model-actions", "actions.json", new string('a', 64))] } };
        var ledger = new FootballLedger(); var context = GeneratedContext(fixture);
        if (lateRelease) context[2] = context[2] with { Payload = ((SchedulePayload)context[2].Payload) with { Releases = [new("control-one", delayedReleaseMs)] } };
        Add(ledger, fixture, 1, context);
        Add(ledger, fixture, 2, Action(fixture, "control-one", "possession", 1000, 3000, outcome: "controlled"),
            Action(fixture, "pass-one", "pass", 1000, 2000), Action(fixture, "turnover-one", "pass", 2000, 3000, outcome: "turnover"),
            Action(fixture, "control-two", "possession", 5000, 8000, outcome: "controlled"),
            Action(fixture, "pass-two", "pass", 5000, 6000), Action(fixture, "shot-two", "shot", 7000, 8000, outcome: "goal"));
        ledger.Accept(new(Batch(fixture, 3, [], close: true), DateTimeOffset.UtcNow));
        return (fixture, ledger);
    }

    [Fact]
    public void OnlyCompletedAcceptedPassagesAreListedAndUnsupportedIsDifferentFromEmpty()
    {
        var (f, ledger) = Recording();
        Assert.Empty(ReplayStory.Project(ledger.Query(Query(f, 2999)))!);
        var first = Assert.Single(ReplayStory.Project(ledger.Query(Query(f, 3000)))!);
        Assert.Equal(1, first.Passage.PassCompleted); Assert.Equal(2, first.Passage.PassAttempts);
        Assert.Contains("1 completed pass → turnover", first.Summary);
        Assert.Equal(2, ReplayStory.Project(ledger.Query(Query(f, 8000)))!.Length);
        var view = ledger.Query(Query(f, 9000));
        Assert.Null(ReplayStory.Project(view with { Story = null }));
        Assert.Null(ReplayStory.Project(view with { Dataset = view.Dataset with { Closed = false }, Story = null }));
        Assert.Null(ReplayStory.Project(view with { Context = view.Context with { FixtureId = "other-fixture" } }));
    }

    [Theory]
    [InlineData("future")] [InlineData("fingerprint")] [InlineData("member")] [InlineData("revision")]
    [InlineData("duplicate")] [InlineData("negative")] [InlineData("source")] [InlineData("ending")]
    public void MalformedProjectionFailsClosed(string fault)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var passage = view.Story!.Passages[0];
        passage = fault switch
        {
            "future" => passage with { EndCursorMs = 3001 }, "fingerprint" => passage with { Fingerprint = "bad" },
            "member" => passage with { Evidence = [passage.Evidence[0] with { RecordId = "foreign" }] },
            "revision" => passage with { Evidence = [passage.Evidence[0] with { Revision = 0 }] },
            "negative" => passage with { PassCompleted = -1 }, "ending" => passage with { Ending = "tactical_breakthrough" }, _ => passage
        };
        view = view with { Story = new(fault == "duplicate" ? [passage, passage] : [passage]) };
        if (fault == "source") view = view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Sources = [] } } };
        Assert.Null(ReplayStory.Project(view));
    }

    [Theory] [InlineData(MatchLanguage.English, "Match story")] [InlineData(MatchLanguage.Swedish, "Matchens förlopp")] [InlineData(MatchLanguage.Spanish, "Historia del partido")]
    public void NativeChronologicalRowsHaveKeyboardButtonsScopeAndLocalizedCopy(MatchLanguage language, string heading)
    {
        var (f, ledger) = Recording(); var items = ReplayStory.Project(ledger.Query(Query(f, 9000)), language)!;
        var component = new Timeline(); Parameter(component, "Items", items); Parameter(component, "Language", language); Parameter(component, "Disabled", true);
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        var frames = Frames(tree); var text = string.Concat(frames.Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(frame => frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent));
        Assert.Contains(heading, text); Assert.Contains(ReplayText.Get(language, "StoryGenerated"), text);
        Assert.Contains(frames, frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "ol");
        Assert.Equal(4, frames.Count(frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "button"));
        Assert.Equal(4, frames.Count(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && Equals(frame.AttributeValue, true)));
        Assert.Contains(frames, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-pressed");
    }

    [Fact]
    public void CollapsedTimelineRendersOnlyThreeRowsAndResponsiveFocusRulesRemain()
    {
        var (f, ledger) = Recording(); var item = ReplayStory.Project(ledger.Query(Query(f, 9000)))![0];
        var component = new Timeline(); Parameter(component, "Items", Enumerable.Repeat(item, 150).ToArray());
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Equal(7, Frames(tree).Count(frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "button"));
        var css = File.ReadAllText(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Components", "Football", "ReplayStoryTimeline.razor.css"));
        Assert.Contains("min-height: 44px", css); Assert.Contains(":focus-visible", css); Assert.Contains("max-width: 600px", css);
        Assert.Contains("minmax(0, 1fr)", css);
    }

    [Fact]
    public async Task RowSelectionPausesSeeksAcceptedEndAndDoesNotPrepareAQuestion()
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 9000)); var item = ReplayStory.Project(view)![0]; var queries = new List<FootballQuery>();
        using var http = new HttpClient(new Handler(q => { queries.Add(q); return Task.FromResult(ledger.Query(q)); })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = Page(view, new(http)); Set(page, "_playing", true);
        await Invoke(page, "SelectStoryPassageAsync", item.Action);
        Assert.False((bool)Get(page, "_playing")!); Assert.Equal(3000, Assert.Single(queries).CursorMs);
        Assert.Equal(item.Action.Passage, Get(page, "_selectedPassage")); Assert.Null(Get(page, "_preparedPassage"));
    }

    [Fact]
    public async Task AskKeepsAcceptedClockAndClearsPriorPlayerGoalAndStopBoundaryWithoutRetrieval()
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 9000)); var action = ReplayStory.Project(view)![0].Action;
        using var h = new ComposerHarness(); await using var page = Page(view, h.Api);
        Set(page, "_playing", true); Set(page, "_personId", "person-one"); Set(page, "_selectedEvent", new MatchSelectedEvent("future-goal", 60000, "1")); Set(page, "_stopAt", 65000L);
        await Invoke(page, "PrepareStoryQuestionAsync", action); h.ApplyPage(page);
        Assert.False((bool)Get(page, "_playing")!); Assert.Same(view, Get(page, "_view")); Assert.Equal(9000L, Get(page, "_cursorMs"));
        Assert.Null(Get(page, "_personId")); Assert.Null(Get(page, "_selectedEvent")); Assert.Null(Get(page, "_stopAt"));
        var prepared = Assert.IsType<ReplayStoryPreparation>(Get(page, "_preparedPassage"));
        Assert.Equal(view.Context, prepared.Context.Query); Assert.Null(prepared.Context.Query.SubjectId); Assert.Null(prepared.Context.SelectedEvent);
        Assert.Equal(action.Passage, prepared.Context.SelectedPassage); Assert.Equal(prepared.Context, h.Component.Context);
        Assert.Equal(ReplayText.Get(MatchLanguage.English, "StoryPreparedQuestion"), Field(typeof(MatchConversation), "_question").GetValue(h.Component));
        Assert.Equal(0, h.Calls); Assert.Empty(h.Session.Turns);
    }

    [Theory] [InlineData("empty", 3000L)] [InlineData("draft", 3000L)] [InlineData("pending", 3000L)]
    [InlineData("empty", 9000L)] [InlineData("draft", 9000L)] [InlineData("pending", 9000L)]
    public async Task RepeatedAskAndSameEndSelectKeepCommittedHistoryAndIntermediateConversationContext(string state, long cursor)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, cursor)); var action = ReplayStory.Project(view)![0].Action;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        using var h = new ComposerHarness(async query =>
        {
            if (++reads > 1) { entered.TrySetResult(); await release.Task; }
            return ledger.Query(query);
        });
        await using var page = Page(view, h.Api); Set(page, "_selectedPassage", action.Passage);
        typeof(FootballReplay).GetProperty("ChatSession", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, h.Session);
        h.ApplyPage(page); var context = h.Component.Context!;
        Assert.True((await h.Session.AskAsync(context, "Passes?")).Accepted); var answered = Assert.Single(h.Session.Turns);
        Task<MatchChatResult>? pendingReply = null; Task? repeatedAsk = null;
        try
        {
            var draft = state == "draft" ? "Keep my drafted question" : state == "pending" ? "My question is being answered" : "";
            SetObject(h.Component, "_question", draft);
            if (state == "pending")
            {
                pendingReply = h.Session.AskAsync(context, draft); await entered.Task;
                SetObject(h.Component, "_pending", true);
            }
            var expectedReads = h.Calls; var generation = Field(typeof(MatchChatSession), "_generation").GetValue(h.Session);
            Set(page, "_playing", true); repeatedAsk = Invoke(page, "PrepareStoryQuestionAsync", action);
            // Apply the actual page's parameters while the old implementation would be awaiting its seek.
            h.ApplyPage(page);
            Assert.True(repeatedAsk.IsCompletedSuccessfully); Assert.False((bool)Get(page, "_updating")!);
            Assert.False((bool)Get(page, "_playing")!); Assert.Equal(action.Passage, Get(page, "_selectedPassage"));
            Assert.Equal(context, h.Component.Context); Assert.Same(answered, Assert.Single(h.Session.Turns));
            Assert.Equal(generation, Field(typeof(MatchChatSession), "_generation").GetValue(h.Session)); Assert.Equal(expectedReads, h.Calls);
            Assert.Equal(state == "empty" ? ReplayText.Get(MatchLanguage.English, "StoryPreparedQuestion") : draft, Field(typeof(MatchConversation), "_question").GetValue(h.Component));
            Assert.Equal(state == "pending", Field(typeof(MatchConversation), "_pending").GetValue(h.Component));
            if (pendingReply is not null) Assert.False(pendingReply.IsCompleted);
            var prepared = Assert.IsType<ReplayStoryPreparation>(Get(page, "_preparedPassage")); Assert.Equal(context, prepared.Context);
            await Invoke(page, cursor == action.Passage.EndCursorMs ? "SelectStoryPassageAsync" : "PrepareStoryQuestionAsync", action); h.ApplyPage(page);
            Assert.Equal(context, h.Component.Context); Assert.Same(answered, Assert.Single(h.Session.Turns));
            Assert.Equal(expectedReads, h.Calls);
            if (cursor == action.Passage.EndCursorMs) Assert.Same(prepared, Get(page, "_preparedPassage"));
            else Assert.NotEqual(prepared.Id, Assert.IsType<ReplayStoryPreparation>(Get(page, "_preparedPassage")).Id);
            Assert.Equal(generation, Field(typeof(MatchChatSession), "_generation").GetValue(h.Session));
        }
        finally
        {
            release.TrySetResult(); if (repeatedAsk is not null) await repeatedAsk;
            if (pendingReply is not null) Assert.True((await pendingReply).Accepted);
        }
    }

    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task SelectedPassageAtDifferentCursorOrDifferentPassageStillFetchesItsEnd(int requested)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 9000)); var items = ReplayStory.Project(view)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var queries = new List<FootballQuery>();
        using var http = new HttpClient(new Handler(async query => { queries.Add(query); entered.TrySetResult(); await release.Task; return ledger.Query(query); })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = Page(view, new(http)); Set(page, "_selectedPassage", items[0].Action.Passage);
        var selecting = Invoke(page, "SelectStoryPassageAsync", items[requested].Action);
        try
        {
            await entered.Task; Assert.True((bool)Get(page, "_updating")!); Assert.Same(view, Get(page, "_view"));
            using var tree = new RenderTreeBuilder(); ((PageSurface)page).Render(tree);
            Assert.False(HasPitchLoadingMoment(WithChildFragments(tree).ToArray()));
            Assert.Equal(items[requested].Passage.EndCursorMs, Assert.Single(queries).CursorMs);
        }
        finally { release.TrySetResult(); await selecting; }
        Assert.Equal(items[requested].Action.Passage, Get(page, "_selectedPassage"));
        Assert.Equal(items[requested].Passage.EndCursorMs, Assert.IsType<FootballView>(Get(page, "_view")).Context.CursorMs); Assert.Null(Get(page, "_preparedPassage"));
    }

    [Theory] [InlineData("head")] [InlineData("policy")] [InlineData("fingerprint")] [InlineData("catalog-head")] [InlineData("catalog-policy")]
    public async Task RowSelectionCannotReuseChangedAuthorityAtTheSelectedEnd(string change)
    {
        var (f, ledger) = Recording(); var original = ledger.Query(Query(f, 3000)); var action = ReplayStory.Project(original)![0].Action;
        var changed = change switch
        {
            "head" or "catalog-head" => original with { Dataset = original.Dataset with { HeadSequence = original.Dataset.HeadSequence + 1 } },
            "policy" or "catalog-policy" => original with { Context = original.Context with { PresentationPolicyRevision = 2 }, Dataset = original.Dataset with { Schedule = original.Dataset.Schedule! with { PolicyRevision = 2 } } },
            _ => original with { Story = new([original.Story!.Passages[0] with { Fingerprint = new string('b', 64) }]) }
        };
        var calls = 0; using var http = new HttpClient(new Handler(query => { calls++; return Task.FromResult(changed with { Context = query }); })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = Page(change.StartsWith("catalog-", StringComparison.Ordinal) ? original : changed, new(http));
        if (change.StartsWith("catalog-", StringComparison.Ordinal)) Set(page, "_catalog", new[] { changed.Dataset });
        Set(page, "_selectedPassage", action.Passage); await Invoke(page, "SelectStoryPassageAsync", action);
        Assert.Null(Get(page, "_preparedPassage")); Assert.Equal(change.StartsWith("catalog-", StringComparison.Ordinal) ? 1 : 0, calls);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task UpdatingPitchShowsInlineLoadingOnlyBeforeAnyAcceptedView(bool retainedView)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); await using var page = Page(view, null);
        if (!retainedView) Set(page, "_view", null!); Set(page, "_updating", true);
        using var tree = new RenderTreeBuilder(); ((PageSurface)page).Render(tree); var frames = WithChildFragments(tree).ToArray();
        Assert.Equal(!retainedView, HasPitchLoadingMoment(frames));
        Assert.Equal(retainedView, frames.Any(frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(Matchmate.Web.Components.Football.FootballPitch)));
    }

    [Theory] [InlineData("late")] [InlineData("failed")] [InlineData("head")] [InlineData("fingerprint")] [InlineData("policy")] [InlineData("late-release")]
    public async Task ChangedOrFailedAcceptedEndNeverSelectsOrPrepares(string fault)
    {
        var (f, ledger) = Recording(lateRelease: fault == "late-release"); var view = ledger.Query(Query(f, 9000)); var action = ReplayStory.Project(view)![0].Action;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async q =>
        {
            entered.SetResult(); if (fault == "late") await release.Task;
            if (fault == "failed") throw new HttpRequestException();
            var next = ledger.Query(q);
            return fault switch {
                "head" => next with { Dataset = next.Dataset with { HeadSequence = 4 } },
                "fingerprint" => next with { Story = new([next.Story!.Passages[0] with { Fingerprint = new string('b', 64) }]) },
                "policy" => next with { Context = q with { PresentationPolicyRevision = 2 } }, _ => next };
        })) { BaseAddress = new("https://unit.invalid/") };
        await using var page = Page(view, new(http)); var pending = Invoke(page, "SelectStoryPassageAsync", action); await entered.Task;
        if (fault == "late") { Set(page, "_intentGeneration", (long)Get(page, "_intentGeneration")! + 1); release.SetResult(); }
        await pending; Assert.Null(Get(page, "_selectedPassage")); Assert.Null(Get(page, "_preparedPassage"));
        Assert.Equal(fault == "late" ? "" : ReplayText.Get(MatchLanguage.English, "StoryUnavailable"), Get(page, "_storyNotice"));
    }

    [Theory] [InlineData("future")] [InlineData("unreleased")] [InlineData("retracted")] [InlineData("fingerprint")]
    [InlineData("head")] [InlineData("policy")] [InlineData("stream")] [InlineData("catalog-head")] [InlineData("catalog-policy")]
    [InlineData("blocked")] [InlineData("disposed")]
    public async Task AskRejectsAnythingOutsideTheCurrentAcceptedAuthorityWithoutFetching(string fault)
    {
        var (f, ledger) = Recording(lateRelease: fault == "unreleased"); var view = ledger.Query(Query(f, 3000));
        var released = ReplayStory.Project(ledger.Query(Query(f, 9000)))!; var action = released[fault == "future" ? 1 : 0].Action with { Query = view.Context };
        if (fault == "retracted") view = view with { Story = new([]) };
        if (fault == "fingerprint") action = action with { Passage = action.Passage with { Fingerprint = new string('b', 64) } };
        if (fault == "head") action = action with { HeadSequence = action.HeadSequence + 1 };
        if (fault == "policy") action = action with { Query = action.Query with { PresentationPolicyRevision = 2 } };
        using var h = new ComposerHarness(); await using var page = Page(view, h.Api);
        if (fault == "stream") Set(page, "_selectedStream", "another-stream");
        if (fault == "catalog-head") Set(page, "_catalog", new[] { view.Dataset with { HeadSequence = view.Dataset.HeadSequence + 1 } });
        if (fault == "catalog-policy") Set(page, "_catalog", new[] { view.Dataset with { Schedule = view.Dataset.Schedule! with { PolicyRevision = 2 } } });
        if (fault == "blocked") Set(page, "_contextUnavailable", true);
        if (fault == "disposed") Set(page, "_disposed", true);
        await Invoke(page, "PrepareStoryQuestionAsync", action);
        Assert.Null(Get(page, "_preparedPassage")); Assert.Null(Get(page, "_selectedPassage")); Assert.Equal(0, h.Calls); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task AskUsesCurrentReleasedEvidenceWhileRowSelectionStillRejectsAnEarlierUnreleasedEnd()
    {
        var (f, ledger) = Recording(lateRelease: true, delayedReleaseMs: 7000); var view = ledger.Query(Query(f, 9000)); var item = ReplayStory.Project(view)![0];
        var queries = new List<FootballQuery>(); using var h = new ComposerHarness(query => { queries.Add(query); return Task.FromResult(ledger.Query(query)); });
        await using var page = Page(view, h.Api); await Invoke(page, "PrepareStoryQuestionAsync", item.Action); h.ApplyPage(page);
        Assert.Equal(9000L, Get(page, "_cursorMs")); Assert.Equal(0, h.Calls); Assert.Empty(h.Session.Turns);
        Assert.Equal(9000, Assert.IsType<ReplayStoryPreparation>(Get(page, "_preparedPassage")).Context.Query.CursorMs);
        Assert.True((await h.Session.AskAsync(h.Component.Context!, "Passes?")).Accepted);
        Assert.Equal(9000, Assert.Single(queries).CursorMs); Assert.Contains("1 of 2 passes completed", Assert.Single(h.Session.Turns).Answer);
        await Invoke(page, "SelectStoryPassageAsync", item.Action);
        Assert.Equal(3000L, Get(page, "_cursorMs")); Assert.Equal(3000, queries[^1].CursorMs);
        Assert.Null(Get(page, "_selectedPassage")); Assert.Null(Get(page, "_preparedPassage"));
        Assert.Equal(ReplayText.Get(MatchLanguage.English, "StoryUnavailable"), Get(page, "_storyNotice"));
    }

    [Theory] [InlineData(2999L, false)] [InlineData(3000L, true)] [InlineData(9000L, true)]
    public void ComposerAcceptsCurrentQueryAfterPassageEndButRejectsBeforeIt(long cursor, bool accepted)
    {
        var (f, ledger) = Recording(); var released = ledger.Query(Query(f, 3000)); var action = ReplayStory.Project(released)![0].Action with { Query = Query(f, cursor) };
        var context = new MatchChatContext(action.Query, action.HeadSequence, SelectedPassage: action.Passage);
        using var h = new ComposerHarness(); h.Set(context); Parameter(h.Component, "PreparedPassage", new ReplayStoryPreparation(Guid.NewGuid(), action, context)); h.Component.Apply();
        Assert.Equal(accepted ? ReplayText.Get(MatchLanguage.English, "StoryPreparedQuestion") : "", Field(typeof(MatchConversation), "_question").GetValue(h.Component));
        Assert.Equal(0, h.Calls); Assert.Empty(h.Session.Turns);
    }

    [Fact, Trait("RequiresLocalData", "true")]
    public async Task LiverpoolAskAt57KeepsBothCardsAndClockThenRowClickSeeksAndRemovesUnreachedRecap()
    {
        var view = await LiverpoolView(3420000); var half = await LiverpoolView(3360000);
        var item = ReplayStory.Project(view)!.Single(row => row.Passage.StartCursorMs == 3208000 && row.Passage.EndCursorMs == 3258000);
        var queries = new List<FootballQuery>(); using var h = new ComposerHarness(async query => { queries.Add(query); return await GeneratedPlayerDistanceTests.ExistingStore.QueryAsync(query, default); });
        await using var page = Page(view, h.Api); Set(page, "_recapEvidence", half);
        var insight = Assert.IsType<ReplayInsight>(Property(page, "CurrentInsight")); var recap = Assert.IsType<ReplayRecap>(Property(page, "CurrentRecap"));
        await Invoke(page, "PrepareStoryQuestionAsync", item.Action); h.ApplyPage(page);
        Assert.Equal(3420000L, Get(page, "_cursorMs")); Assert.Same(view, Get(page, "_view")); Assert.Equal(0, h.Calls);
        Assert.Equal(insight.Action, Assert.IsType<ReplayInsight>(Property(page, "CurrentInsight")).Action);
        Assert.Equal(recap.Action, Assert.IsType<ReplayRecap>(Property(page, "CurrentRecap")).Action);
        var context = h.Component.Context!; Assert.Equal(view.Context, context.Query); Assert.Equal(item.Action.Passage, context.SelectedPassage);
        Assert.Equal(ReplayText.Get(MatchLanguage.English, "StoryPreparedQuestion"), Field(typeof(MatchConversation), "_question").GetValue(h.Component)); Assert.Empty(h.Session.Turns);
        Assert.True((await h.Session.AskAsync(context, "How many passes?")).Accepted);
        var answer = Assert.Single(h.Session.Turns); Assert.Contains("4 of 5 passes completed", answer.Answer); Assert.Single(answer.Evidence, evidence => evidence.Tool == "selected_passage");
        Assert.Equal(3420000, Assert.Single(queries).CursorMs);
        await Invoke(page, "SelectStoryPassageAsync", item.Action);
        Assert.Equal(3258000L, Get(page, "_cursorMs")); Assert.Equal(3258000, queries[^1].CursorMs); Assert.Null(Property(page, "CurrentRecap"));
        if (Property(page, "CurrentInsight") is ReplayInsight rewound) Assert.Equal(3258000, rewound.Action.Query.CursorMs);
    }

    [Theory, Trait("RequiresLocalData", "true")] [InlineData(false)] [InlineData(true)]
    public async Task VisibleGlobalCardActionsExitPassageScopeBeforePreparingTheirOwnQuestion(bool recapAction)
    {
        var view = await LiverpoolView(3420000); var half = await LiverpoolView(3360000); var item = ReplayStory.Project(view)!.First();
        using var h = new ComposerHarness(query => GeneratedPlayerDistanceTests.ExistingStore.QueryAsync(query, default));
        await using var page = Page(view, h.Api); Set(page, "_recapEvidence", half);
        await Invoke(page, "PrepareStoryQuestionAsync", item.Action); h.ApplyPage(page); SetObject(h.Component, "_question", "");
        if (recapAction) await Invoke(page, "PrepareRecapQuestionAsync", Assert.IsType<ReplayRecap>(Property(page, "CurrentRecap")).Action);
        else await Invoke(page, "PrepareInsightQuestionAsync", Assert.IsType<ReplayInsight>(Property(page, "CurrentInsight")).Action);
        h.ApplyPage(page); Assert.Null(Get(page, "_selectedPassage")); Assert.Null(Get(page, "_preparedPassage"));
        var context = h.Component.Context!; Assert.Null(context.SelectedPassage); Assert.Equal(recapAction ? 3360000 : 3420000, context.Query.CursorMs);
        var preparedContext = recapAction ? Assert.IsType<ReplayRecapPreparation>(Get(page, "_preparedRecap")).Context : Assert.IsType<ReplayInsightPreparation>(Get(page, "_preparedInsight")).Context;
        Assert.Equal(context, preparedContext); Assert.False(string.IsNullOrWhiteSpace((string?)Field(typeof(MatchConversation), "_question").GetValue(h.Component)));
        Assert.Empty(h.Session.Turns); Assert.True((await h.Session.AskSourceAsync(context, MatchSourceQuestionId.Summary)).Accepted);
        var answer = Assert.Single(h.Session.Turns); Assert.Null(answer.Context.SelectedPassage); Assert.DoesNotContain(answer.Evidence, evidence => evidence.Tool == "selected_passage");
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void ExistingDraftSurvivesPreparationAndQuestionIsLocalized(MatchLanguage language)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var action = ReplayStory.Project(view, language)![0].Action;
        var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence, SelectedPassage: action.Passage);
        using var h = new ComposerHarness(); Parameter(h.Component, "Language", language); h.Set(context);
        SetObject(h.Component, "_question", "My draft"); Parameter(h.Component, "PreparedPassage", new ReplayStoryPreparation(Guid.NewGuid(), action, context)); h.Component.Apply();
        Assert.Equal("My draft", Field(typeof(MatchConversation), "_question").GetValue(h.Component)); Assert.Empty(h.Session.Turns); Assert.Equal(0, h.Calls);
        SetObject(h.Component, "_question", ""); Parameter(h.Component, "PreparedPassage", new ReplayStoryPreparation(Guid.NewGuid(), action, context)); h.Component.Apply();
        Assert.Equal(ReplayText.Get(language, "StoryPreparedQuestion"), Field(typeof(MatchConversation), "_question").GetValue(h.Component));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void PendingOrChangedContextPreparationCannotReplaceOrSendAQuestion(bool pending)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var action = ReplayStory.Project(view)![0].Action;
        var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence, SelectedPassage: action.Passage);
        using var h = new ComposerHarness(); h.Set(context); if (pending) SetObject(h.Component, "_pending", true);
        Parameter(h.Component, "PreparedPassage", new ReplayStoryPreparation(Guid.NewGuid(), action, pending ? context : context with { HeadSequence = 99 }));
        h.Component.Apply(); Assert.Equal("", Field(typeof(MatchConversation), "_question").GetValue(h.Component)); Assert.Empty(h.Session.Turns); Assert.Equal(0, h.Calls);
    }

    [Theory] [InlineData("")] [InlineData("The selected passage changed.")]
    public async Task PagePassesCurrentNoticeValueToRenderedTimeline(string notice)
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000));
        await using var page = Page(view, null); Set(page, "_storyNotice", notice);
        using var tree = new RenderTreeBuilder(); ((PageSurface)page).Render(tree);
        var frames = WithChildFragments(tree).ToArray();
        var timeline = Array.FindIndex(frames, frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(ReplayStoryTimeline));
        Assert.True(timeline >= 0);
        var passedNotice = frames.Skip(timeline + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .Single(frame => frame.AttributeName == nameof(ReplayStoryTimeline.Notice)).AttributeValue;
        Assert.Equal(notice, passedNotice); Assert.NotEqual("_storyNotice", passedNotice);
    }

    [Fact]
    public void ChangedPassageNoticeRemainsVisibleWithoutAvailableRows()
    {
        var component = new Timeline(); Parameter(component, "Notice", ReplayText.Get(MatchLanguage.English, "StoryUnavailable"));
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Contains(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Text && frame.TextContent.Contains("unavailable or changed"));
        Assert.Contains(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "role" && Equals(frame.AttributeValue, "status"));
        Assert.DoesNotContain(Frames(tree), frame => frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "button");
    }

    [Fact]
    public async Task RewindBeforeEndAndChangedFingerprintClearScope()
    {
        var (f, ledger) = Recording(); var view = ledger.Query(Query(f, 3000)); var selection = ReplayStory.Project(view)![0].Action.Passage;
        await using var page = Page(view, null); Set(page, "_selectedPassage", selection);
        await Invoke(page, "ReconcileStorySelection", ledger.Query(Query(f, 2999))); Assert.Null(Get(page, "_selectedPassage"));
        Set(page, "_selectedPassage", selection);
        var changed = view with { Story = new([view.Story!.Passages[0] with { Fingerprint = new string('b', 64) }]) };
        await Invoke(page, "ReconcileStorySelection", changed); Assert.Null(Get(page, "_selectedPassage"));
    }

    private static async Task<FootballView> LiverpoolView(long cursor)
    {
        var store = GeneratedPlayerDistanceTests.ExistingStore;
        var dataset = (await store.CatalogAsync(default)).Where(data => data.Manifest.DatasetId == LiverpoolFootballSource.DatasetId)
            .OrderByDescending(data => data.Manifest.ManifestRevision).First();
        return await store.QueryAsync(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId, cursor,
            "retrospective_replay", dataset.Schedule!.PolicyId, dataset.Schedule.PolicyRevision), default);
    }
    private static object? Property(FootballReplay page, string name) => typeof(FootballReplay).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static FootballReplay Page(FootballView view, ReplayApiClient? api)
    {
        var page = new PageSurface(); Set(page, "_view", view); Set(page, "_catalog", new[] { view.Dataset }); Set(page, "_selectedStream", view.Context.StreamId); Set(page, "_cursorMs", view.Context.CursorMs);
        if (api is not null) typeof(FootballReplay).GetProperty("Api", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, api); return page;
    }
    private static Task Invoke(object obj, string method, object argument) => (Task?)(obj is FootballReplay ? typeof(FootballReplay) : obj.GetType()).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(obj, [argument]) ?? Task.CompletedTask;
    private static FieldInfo Field(Type t, string n) => t.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static object? Get(object obj, string n) => Field(typeof(FootballReplay), n).GetValue(obj);
    private static void Set(object obj, string n, object value) => Field(typeof(FootballReplay), n).SetValue(obj, value);
    private static void SetObject(object obj, string n, object value) => Field(typeof(MatchConversation), n).SetValue(obj, value);
    private static void Parameter(object obj, string n, object value) => obj.GetType().GetProperty(n)!.SetValue(obj, value);
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static IEnumerable<RenderTreeFrame> WithChildFragments(RenderTreeBuilder tree)
    {
        foreach (var frame in Frames(tree))
        {
            yield return frame;
            if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeValue is RenderFragment fragment)
            {
                using var child = new RenderTreeBuilder(); fragment(child);
                foreach (var nested in WithChildFragments(child)) yield return nested;
            }
        }
    }
    private static bool HasPitchLoadingMoment(RenderTreeFrame[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
            if (frames[index].FrameType == RenderTreeFrameType.Component && frames[index].ComponentType == typeof(LoadingStatus)
                && frames.Skip(index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .Any(frame => frame.AttributeName == nameof(LoadingStatus.Text) && Equals(frame.AttributeValue, ReplayText.Get(MatchLanguage.English, "LoadingReplayMoment")))) return true;
        return false;
    }
    private sealed class PageSurface : FootballReplay { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Timeline : ReplayStoryTimeline { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Composer : MatchConversation { public void Apply() => OnParametersSet(); }
    private sealed class ComposerHarness : IDisposable
    {
        public int Calls; public MatchChatSession Session { get; } public ReplayApiClient Api { get; } public Composer Component { get; } = new(); private readonly HttpClient _http;
        public ComposerHarness(Func<FootballQuery, Task<FootballView>>? read = null)
        {
            _http = new(new Handler(query => { Calls++; return read is null ? throw new InvalidOperationException("Preparation cannot retrieve.") : read(query); })) { BaseAddress = new("https://unit.invalid/") };
            Api = new(_http); var model = new NoModel(); Session = new(new Auth(), Api, model, model, Options.Create(new MatchChatOptions()));
            typeof(MatchConversation).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Component, Session);
        }
        public void Set(MatchChatContext context) { Parameter(Component, "Context", context); Component.Apply(); }
        public void ApplyPage(FootballReplay page)
        {
            using var tree = new RenderTreeBuilder(); ((PageSurface)page).Render(tree); var frames = WithChildFragments(tree).ToArray();
            var conversation = Array.FindIndex(frames, frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(MatchConversation));
            Assert.True(conversation >= 0);
            foreach (var frame in frames.Skip(conversation + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute))
                if (frame.AttributeName is nameof(MatchConversation.Context) or nameof(MatchConversation.Updating) or nameof(MatchConversation.PreparedPassage)
                    or nameof(MatchConversation.PreparedInsight) or nameof(MatchConversation.PreparedRecap)
                    or nameof(MatchConversation.Language) or nameof(MatchConversation.AudienceMode) or nameof(MatchConversation.PitchSide))
                    Parameter(Component, frame.AttributeName, frame.AttributeValue!);
            Component.Apply();
        }
        public void Dispose() { Component.Dispose(); Session.Dispose(); _http.Dispose(); }
    }
    internal sealed class Handler(Func<FootballQuery, Task<FootballView>> read) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; return new(HttpStatusCode.OK) { Content = JsonContent.Create(await read(query)) }; }
    }
    internal sealed class Auth : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new("sub", "unit-owner"), new("roles", "Tester")], "unit", "sub", "roles")))); }
    internal sealed class NoModel : IMatchWorkflowRouter, IMatchChatModel
    {
        public bool Configured => false;
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) => throw new InvalidOperationException("No Decision call allowed.");
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token) => throw new InvalidOperationException("No narrator call allowed.");
    }
}
#pragma warning restore BL0006
