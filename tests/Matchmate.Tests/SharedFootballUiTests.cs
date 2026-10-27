using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Microsoft.AspNetCore.Components;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual generated component tree, without a browser substitute host.
public sealed class SharedFootballUiTests
{
    [Fact]
    public void F_D087_001_UnknownHighlightsShowDashesAndPlaceholdersWhileReadyEmptyIsZero()
    {
        var component = new RenderableHighlights(); Parameter(component, "IsLoading", true); Parameter(component, "CountsKnown", false);
        using var loading = new RenderTreeBuilder(); component.Render(loading);
        Assert.Contains("Goals—", TreeText(loading)); Assert.DoesNotContain("Goals0", TreeText(loading));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(TreeText(loading), "class=\"highlight-placeholder\"").Count);
        AssertLoading(loading, "Loading match highlights…");
        Parameter(component, "IsLoading", false); Parameter(component, "CountsKnown", true); Parameter(component, "Status", "No reported events are present in this recording.");
        using var empty = new RenderTreeBuilder(); component.Render(empty);
        Assert.Contains("Goals0", TreeText(empty)); Assert.Contains("No reported events", TreeText(empty));
        Assert.DoesNotContain("highlight-placeholder", TreeText(empty)); AssertNoLoading(empty);
    }

    [Fact]
    public void F_D087_001_RefreshingHighlightsKeepsAcceptedCardsAndCounts()
    {
        var component = new RenderableHighlights();
        Parameter(component, "Highlights", new[] { new ReplayHighlight("goal", 60000, "goal", "Source goal", "1", "Scorer") });
        Parameter(component, "IsRefreshing", true); component.Apply();
        using var refreshing = new RenderTreeBuilder(); component.Render(refreshing);
        Assert.Contains("Scorer", TreeText(refreshing)); Assert.Contains("Goals1", TreeText(refreshing));
        AssertLoading(refreshing, "Refreshing highlights…"); Assert.DoesNotContain("highlight-placeholder", Attributes(refreshing, "class"));
    }

    [Fact]
    public async Task F_D087_001_CatalogBusyBelongsToLatestRequestAndClearsOnFailure()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new CallbackHandler((_, _) => ++calls == 1 ? first.Task : latest.Task);
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var component = new RenderableFootball(); Set(component, "Api", new ReplayApiClient(http));
        var old = Invoke(component, "RefreshAsync"); Assert.True((bool)Get(component, "_catalogBusy")!);
        var current = Invoke(component, "RefreshAsync");
        first.SetResult(new(HttpStatusCode.ServiceUnavailable)); await old;
        Assert.True((bool)Get(component, "_catalogBusy")!);
        using var pending = new RenderTreeBuilder(); component.Render(pending); AssertLoading(pending, "Refreshing…");
        latest.SetResult(new(HttpStatusCode.ServiceUnavailable)); await current;
        Assert.False((bool)Get(component, "_catalogBusy")!); Assert.Contains("unavailable", (string)Get(component, "_status")!);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void F_D087_001_OpenMissingPositionsAreLoadingWhileClosedGapsStaySourceGaps(bool closed)
    {
        var view = IndexView(2, closed) with { Frame = null, TrackingGap = true, CoverageMessage = "No committed sample." };
        var page = new RenderableFootball(); Set(page, "_view", view);
        var loading = (bool)typeof(FootballReplay).GetProperty("PositionsLoading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal(!closed, loading);
        using var pageTree = new RenderTreeBuilder(); page.Render(pageTree);
        if (closed) Assert.Contains("Playback paused at a source gap", TreeText(pageTree));
        else { AssertLoading(pageTree, "Preparing this replay moment… Player positions are still loading."); Assert.DoesNotContain("source gap", TreeText(pageTree)); }
        var roster = new RenderableRoster(); Parameter(roster, "View", view); Parameter(roster, "PositionsLoading", loading);
        using var rosterTree = new RenderTreeBuilder(); roster.Render(rosterTree);
        Assert.Contains(closed ? "0 / 22" : "— / 22", TreeText(rosterTree));
        var pitch = new RenderablePitch(); Parameter(pitch, "View", view); using var pitchTree = new RenderTreeBuilder(); pitch.Render(pitchTree);
        Assert.Contains(closed ? "Tracking gap" : "Preparing this replay moment…", TreeText(pitchTree));
        if (!closed) { Assert.DoesNotContain("Tracking gap", TreeText(pitchTree)); AssertLoading(pitchTree, "Player positions are still loading."); }
    }

    [Fact]
    public async Task F_D085_INDEX_001_HeadAdvanceRecoversClosedIndexWithoutTearingDownReadyReplay()
    {
        var open = IndexView(2, false); var closed = IndexView(3, true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var component = new RetryingFootball { Delay = token => gate.Task.WaitAsync(token) };
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { closed.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            Assert.NotNull(Get(component, "_view")); return Json(AtIndex(closed, query)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        PrepareIndex(component, http, open); Set(component, "_playing", true); Set(component, "_personId", "person-one");
        Set(component, "_selectedEvent", new Matchmate.Web.Chat.MatchSelectedEvent("goal-1", 60000, "1"));
        await Invoke(component, "LoadHighlightsAsync", open.Dataset, 0L);
        Assert.Same(open, Get(component, "_view")); Assert.Contains("Retrying", (string)Get(component, "_highlightStatus")!);
        Assert.True((bool)Get(component, "_playing")!); gate.TrySetResult(); await IndexTask(component).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, Assert.IsType<FootballView>(Get(component, "_view")).Dataset.HeadSequence);
        Assert.Equal("goal-1", Assert.Single((ReplayHighlight[])Get(component, "_highlights")!).RecordId); Assert.Equal(3L, Get(component, "_highlightHead"));
        Assert.Equal(0L, Get(component, "_cursorMs")); Assert.Equal("person-one", Get(component, "_personId")); Assert.True((bool)Get(component, "_playing")!);
        Assert.Null(Get(component, "_selectedEvent")); Assert.DoesNotContain("Loading", (string)Get(component, "_highlightStatus")!);
        await component.DisposeAsync();
    }

    [Fact]
    public async Task F_D085_INDEX_001_UnchangedHeadKeepsPlaybackGoalAndExistingHighlightsDuringImportPoll()
    {
        var open = IndexView(2, false); var closed = IndexView(2, true); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var component = new RetryingFootball { Delay = token => gate.Task.WaitAsync(token) };
        var initial = true; using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { closed.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; var view = initial ? open : closed; initial = false; return Json(AtIndex(view, query)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; PrepareIndex(component, http, open);
        var goal = new Matchmate.Web.Chat.MatchSelectedEvent("goal-1", 60000, "1"); Set(component, "_selectedEvent", goal); Set(component, "_playing", true);
        await Invoke(component, "LoadHighlightsAsync", open.Dataset, 0L);
        Assert.Single((ReplayHighlight[])Get(component, "_highlights")!); Assert.Contains("importing", (string)Get(component, "_highlightStatus")!);
        gate.TrySetResult(); await IndexTask(component).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(goal, Get(component, "_selectedEvent")); Assert.True((bool)Get(component, "_playing")!); Assert.Single((ReplayHighlight[])Get(component, "_highlights")!);
    }

    [Theory]
    [InlineData("head")] [InlineData("identity")] [InlineData("failure")]
    public async Task F_D085_INDEX_001_RetryLimitSettlesAndPreservesCoherentExistingContent(string fault)
    {
        var view = IndexView(2, true); var catalogs = 0; var posts = 0; await using var component = new RetryingFootball();
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) { catalogs++; return fault == "failure" ? new(HttpStatusCode.ServiceUnavailable) : Json(new[] { view.Dataset }); }
            posts++; var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            var wrong = fault == "identity" ? view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { DatasetVersion = "wrong-version" } } }
                : view with { Dataset = view.Dataset with { HeadSequence = 3 } }; return Json(AtIndex(wrong, query)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; PrepareIndex(component, http, view);
        var existing = ExperiencePresentation.Highlights(AtIndex(view, view.Context with { CursorMs = 100000 })); Set(component, "_highlights", existing); Set(component, "_highlightHead", 2L);
        await Invoke(component, "LoadHighlightsAsync", view.Dataset, 0L); await IndexTask(component).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(60, catalogs); Assert.InRange(posts, 1, 121); Assert.Same(view, Get(component, "_view")); Assert.Same(existing, Get(component, "_highlights"));
        Assert.Contains("retry limit", (string)Get(component, "_highlightStatus")!); Assert.Contains("Refresh", (string)Get(component, "_highlightStatus")!);
        Assert.DoesNotContain("Loading", (string)Get(component, "_highlightStatus")!);
        Assert.False((bool)Get(component, "_indexBusy")!);
    }

    [Fact]
    public async Task F_D085_INDEX_001_UserSeekWinsOverLateBackgroundSnapshot()
    {
        var open = IndexView(2, false); var closed = IndexView(3, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var waits = 0;
        await using var component = new RetryingFootball { Delay = token => ++waits == 1 ? Task.CompletedTask : waitAgain.Task.WaitAsync(token) };
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { closed.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (query.CursorMs == 0) { entered.TrySetResult(); return await response.Task; } return Json(AtIndex(closed, query)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; PrepareIndex(component, http, open);
        await Invoke(component, "LoadHighlightsAsync", open.Dataset, 0L); await entered.Task;
        await Invoke(component, "SeekAsync", 10000L); var accepted = Get(component, "_view");
        response.SetResult(Json(AtIndex(closed, open.Context))); await component.Rendered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(accepted, Get(component, "_view")); Assert.Equal(10000L, Get(component, "_cursorMs"));
        Assert.Contains("replay changed", ((string)Get(component, "_highlightStatus")!).ToLowerInvariant());
    }

    [Theory]
    [InlineData("switch")] [InlineData("route")] [InlineData("dispose")]
    public async Task F_D085_INDEX_001_ChangedSelectionOrDisposalCancelsIgnoredTransportWithoutLateCommit(string change)
    {
        var open = IndexView(2, false); var closed = IndexView(3, true); var other = Rebind(IndexView(2, true), "other-recording");
        other = other with { Context = other.Context with { StreamId = "other-stream" }, Dataset = other.Dataset with { Stream = other.Dataset.Stream with { StreamId = "other-stream" } } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routeChanged = false; CancellationToken oldToken = default; await using var component = new RetryingFootball();
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { routeChanged ? other.Dataset : closed.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (query.StreamId == open.Context.StreamId && query.CursorMs == 0) { oldToken = token; entered.TrySetResult(); return await late.Task; }
            return Json(AtIndex(query.StreamId == other.Context.StreamId ? other : closed, query)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; PrepareIndex(component, http, open);
        await Invoke(component, "LoadHighlightsAsync", open.Dataset, 0L); await entered.Task; var old = IndexTask(component);
        if (change == "dispose") await component.DisposeAsync();
        else if (change == "switch") await Switch(component, other.Dataset);
        else { routeChanged = true; Set(component, "_routeDataset", open.Context.DatasetId); Parameter(component, "DatasetId", other.Context.DatasetId); await Invoke(component, "OnParametersSetAsync"); }
        await old.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(oldToken.IsCancellationRequested); var accepted = Get(component, "_view");
        late.TrySetResult(Json(AtIndex(closed, open.Context))); Assert.Same(accepted, Get(component, "_view"));
        Assert.False((bool)Get(component, "_indexBusy")!);
        if (change != "dispose") Assert.Equal(other.Dataset.Manifest.DatasetId, Assert.IsType<FootballView>(accepted).Dataset.Manifest.DatasetId);
    }

    [Fact]
    public void F_D085_INDEX_001_ImportStatusRemainsVisibleBesideExistingHighlightCards()
    {
        var component = new RenderableHighlights(); Parameter(component, "Highlights", ExperiencePresentation.Highlights(AtIndex(IndexView(2, false), IndexView(2, false).Context with { CursorMs = 100000 })));
        Parameter(component, "Status", "Recording importing; retrying the committed index."); component.Apply(); using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Contains("Recording importing; retrying", TreeText(tree)); Assert.Contains("One", TreeText(tree));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RefreshPrefersNewExplicitManifestRevisionForSameRouteWithoutMixingOldView(bool newestFirst)
    {
        var old = View(SharedFootballTests.Fixture("old-version", "old-stream")); var current = View(SharedFootballTests.Fixture("new-version", "new-stream"));
        old = old with { Dataset = old.Dataset with { Manifest = old.Dataset.Manifest with { ManifestRevision = 3 } } };
        current = current with { Dataset = current.Dataset with { Manifest = current.Dataset.Manifest with { ManifestRevision = 4 } } };
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(newestFirst ? new[] { current.Dataset, old.Dataset } : [old.Dataset, current.Dataset]);
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; Assert.Equal(current.Dataset.Stream.StreamId, query.StreamId);
            Assert.Equal(current.Dataset.Manifest.DatasetVersion, query.DatasetVersion); return Json(current with { Context = query }); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new FootballReplay();
        Set(component, "Api", new ReplayApiClient(http)); Parameter(component, "DatasetId", old.Dataset.Manifest.DatasetId);
        Set(component, "_selectedStream", old.Dataset.Stream.StreamId); Set(component, "_view", old);
        Set(component, "_selectedEvent", new Matchmate.Web.Chat.MatchSelectedEvent("old-goal", 60000, "1"));
        await Invoke(component, "RefreshAsync"); var accepted = Assert.IsType<FootballView>(Get(component, "_view"));
        var choice = Assert.Single(Assert.IsType<FootballDataset[]>(Get(component, "_catalog")));
        Assert.Equal(current.Dataset.Stream.StreamId, choice.Stream.StreamId);
        Assert.Equal(current.Dataset.Manifest.DatasetVersion, accepted.Context.DatasetVersion); Assert.Equal(4, accepted.Dataset.Manifest.ManifestRevision);
        Assert.Equal(current.Dataset.Stream.StreamId, Get(component, "_selectedStream")); Assert.Null(Get(component, "_selectedEvent"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecordingChoicesBreakRevisionTiesByStreamAndKeepSeparateDatasetsWithTheSameFixtureLabel(bool otherFirst)
    {
        var laterStream = View(SharedFootballTests.Fixture("tie-z", "stream-z")); var chosen = View(SharedFootballTests.Fixture("tie-a", "stream-a"));
        laterStream = laterStream with { Dataset = laterStream.Dataset with { Closed = true, Manifest = laterStream.Dataset.Manifest with { ManifestRevision = 3 } } };
        chosen = chosen with { Dataset = chosen.Dataset with { Closed = true, Manifest = chosen.Dataset.Manifest with { ManifestRevision = 3 } } };
        var other = Rebind(View(SharedFootballTests.Fixture("other-version", "other-stream")), "other-recording");
        other = other with { Dataset = other.Dataset with { Closed = true } };
        Assert.Equal(chosen.Dataset.Manifest.FixtureId, other.Dataset.Manifest.FixtureId); Assert.Equal(chosen.Dataset.Fixture!.Label, other.Dataset.Fixture!.Label);
        var catalog = otherFirst ? new[] { other.Dataset, laterStream.Dataset, chosen.Dataset } : [laterStream.Dataset, other.Dataset, chosen.Dataset];
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(catalog);
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            Assert.Equal(chosen.Dataset.Manifest.DatasetId, query.DatasetId); Assert.Equal(chosen.Dataset.Manifest.DatasetVersion, query.DatasetVersion);
            Assert.Equal(chosen.Dataset.Stream.StreamId, query.StreamId); return Json(chosen with { Context = query }); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new FootballReplay();
        Set(component, "Api", new ReplayApiClient(http)); Parameter(component, "DatasetId", chosen.Dataset.Manifest.DatasetId);
        await Invoke(component, "RefreshAsync"); var options = Assert.IsType<FootballDataset[]>(Get(component, "_catalog"));
        Assert.Equal(2, options.Length); Assert.Equal(otherFirst ? new[] { "other-recording", chosen.Dataset.Manifest.DatasetId } : [chosen.Dataset.Manifest.DatasetId, "other-recording"], options.Select(d => d.Manifest.DatasetId));
        Assert.Equal("stream-a", options.Single(d => d.Manifest.DatasetId == chosen.Dataset.Manifest.DatasetId).Stream.StreamId);
        Assert.Equal(chosen.Dataset.Manifest.DatasetVersion, Assert.IsType<FootballView>(Get(component, "_view")).Context.DatasetVersion);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TechnicalSampleFirstCatalogDefaultsOnlyToMainRecordingsAndKeepsLatestUnknownDataset(bool keepCurrentMain)
    {
        var sample = SampleView(); var oldSample = SampleView("old-sample", "old-sample-stream");
        sample = sample with { Dataset = sample.Dataset with { Manifest = sample.Dataset.Manifest with { ManifestRevision = 3 } } };
        var old = Closed(View(SharedFootballTests.Fixture("old-version", "old-stream"))); var current = Closed(View(SharedFootballTests.Fixture("new-version", "new-stream")));
        current = current with { Dataset = current.Dataset with { Manifest = current.Dataset.Manifest with { ManifestRevision = 3 } } };
        var unknown = Rebind(Closed(View(SharedFootballTests.Fixture("unknown-version", "unknown-stream"))), "unclassified-recording");
        var expected = keepCurrentMain ? unknown : current;
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { oldSample.Dataset, old.Dataset, unknown.Dataset, sample.Dataset, current.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            Assert.Equal(expected.Context.DatasetId, query.DatasetId); Assert.Equal(expected.Context.DatasetVersion, query.DatasetVersion); Assert.Equal(expected.Context.StreamId, query.StreamId);
            return Json(expected with { Context = query }); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new RenderableFootball();
        Set(component, "Api", new ReplayApiClient(http)); Set(component, "_selectedStream", keepCurrentMain ? unknown.Context.StreamId : sample.Context.StreamId);
        Set(component, "_view", keepCurrentMain ? unknown : sample);
        await Invoke(component, "RefreshAsync");
        var catalog = Assert.IsType<FootballDataset[]>(Get(component, "_catalog")); Assert.Equal(3, catalog.Length);
        Assert.Equal(sample.Context.DatasetVersion, catalog.Single(d => d.Manifest.DatasetId == "australian-recorded").Manifest.DatasetVersion);
        Assert.Equal(expected.Context.StreamId, Assert.IsType<FootballView>(Get(component, "_view")).Context.StreamId);
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Equal(new[] { current.Context.StreamId, unknown.Context.StreamId }, RecordingOptions(tree));
        Assert.Contains("Technical samples", TreeText(tree)); Assert.Contains("Home vs Away", TreeText(tree)); Assert.False(TechnicalSamplesOpen(tree));
        Assert.Contains("/recordings/australian-recorded", Attributes(tree, "href"));
    }

    [Fact]
    public async Task DirectTechnicalSampleRouteShowsItsContextAndReturnLinkWithoutAMainSelectedOption()
    {
        var sample = SampleView(); var main = Closed(View(SharedFootballTests.Fixture()));
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { main.Dataset, sample.Dataset });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            Assert.Equal("australian-recorded", query.DatasetId); Assert.Equal(sample.Context.StreamId, query.StreamId); return Json(sample with { Context = query }); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new RenderableFootball();
        Set(component, "Api", new ReplayApiClient(http)); Parameter(component, "DatasetId", "australian-recorded"); Set(component, "_view", main); Set(component, "_selectedStream", main.Context.StreamId);
        await Invoke(component, "RefreshAsync"); using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Empty(RecordingOptions(tree)); Assert.Contains("Technical sample", TreeText(tree)); Assert.Contains("Back to match recordings", TreeText(tree));
        Assert.Contains("/recordings", Attributes(tree, "href")); Assert.Contains("/", Attributes(tree, "href")); Assert.True(TechnicalSamplesOpen(tree));
        Assert.Same(CurrentDataset(component), HeaderDataset(tree)); // The header's actual source text is covered by ReplayMatchHeaderTests.
        Assert.Equal("australian-recorded", Assert.IsType<FootballView>(Get(component, "_view")).Context.DatasetId);
    }

    [Fact]
    public async Task SampleOnlyCatalogShowsNoMatchStateAndKeepsTheDirectSampleDiscoverableWithoutLoadingItByDefault()
    {
        var sample = SampleView(); var viewReads = 0;
        using var handler = new CallbackHandler((request, _) => {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(new[] { sample.Dataset }));
            viewReads++; return Task.FromResult(Json(sample)); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new RenderableFootball();
        Set(component, "Api", new ReplayApiClient(http)); await Invoke(component, "RefreshAsync");
        Assert.Equal(0, viewReads); Assert.Null(Get(component, "_view")); Assert.Equal("", Get(component, "_selectedStream"));
        using var tree = new RenderTreeBuilder(); component.Render(tree); Assert.Empty(RecordingOptions(tree));
        Assert.Contains("No match recordings are available", TreeText(tree)); Assert.Contains("Technical samples", TreeText(tree));
        Assert.Contains("/recordings/australian-recorded", Attributes(tree, "href")); Assert.False(TechnicalSamplesOpen(tree));
    }

    private static FootballView Closed(FootballView view) => view with { Dataset = view.Dataset with { Closed = true } };
    private static FootballView SampleView(string version = "sample-version", string stream = "sample-stream") => Rebind(Closed(View(SharedFootballTests.Fixture(version, stream))), "australian-recorded");
    private static string[] RecordingOptions(RenderTreeBuilder tree)
    {
        var frames = Frames(tree);
        return Enumerable.Range(0, frames.Count - 1).Where(i => frames.Array[i].FrameType == RenderTreeFrameType.Element && frames.Array[i].ElementName == "option")
            .Select(i => frames.Array[i + 1].AttributeValue.ToString()!).ToArray();
    }
    private static bool TechnicalSamplesOpen(RenderTreeBuilder tree)
    {
        var frames = Frames(tree);
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != "details") continue;
            var attributes = frames.Array.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
            if (attributes.Any(f => f.AttributeName == "class" && Equals(f.AttributeValue, "technical-samples")))
                return attributes.Any(f => f.AttributeName == "open" && Equals(f.AttributeValue, true));
        }
        return false;
    }

    [Fact]
    public async Task InvalidFailedAndSupersededHighlightSeeksDoNotSignalConversation()
    {
        foreach (var fault in new[] { "invalid", "failed", "same-cursor-failed", "superseded" })
        {
            var first = View(SharedFootballTests.Fixture()); var highlight = new ReplayHighlight("booking-one", 60000, "yellow_card", "Booking", "1");
            first = first with { Dataset = first.Dataset with { Schedule = first.Dataset.Schedule! with { Releases = [new(highlight.RecordId, highlight.CursorMs)] } } };
            if (fault == "same-cursor-failed") first = first with { Context = first.Context with { CursorMs = highlight.CursorMs } };
            var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
            using var handler = new CallbackHandler(async (request, token) =>
            {
                var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
                if (fault == "same-cursor-failed") Assert.Equal(first.Context, query);
                if (++reads == 1 && fault == "superseded") return await late.Task;
                return fault is "failed" or "same-cursor-failed" ? new(HttpStatusCode.ServiceUnavailable) : Json(first with { Context = query });
            });
            using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new FootballReplay();
            Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { first.Dataset }); Set(component, "_view", first);
            Set(component, "_cursorMs", first.Context.CursorMs);
            Set(component, "_selectedStream", first.Context.StreamId); Set(component, "_highlights", new[] { highlight }); Set(component, "_highlightHead", first.Dataset.HeadSequence);
            var selecting = Invoke(component, "HighlightAsync", fault == "invalid" ? highlight with { RecordId = "forged-event" } : highlight);
            if (fault == "superseded")
            {
                await Invoke(component, "SeekAsync", 30000L);
                late.SetResult(Json(first with { Context = first.Context with { CursorMs = highlight.CursorMs } }));
            }
            await selecting;
            Assert.Equal(0L, Get(component, "_eventSelectionVersion"));
            if (fault == "invalid") Assert.Equal(0, reads);
            if (fault == "same-cursor-failed")
            {
                Assert.True((bool)Get(component, "_contextUnavailable")!);
                Assert.Equal(highlight.CursorMs, Assert.IsType<FootballView>(Get(component, "_view")).Context.CursorMs);
            }
        }
    }

    [Theory]
    [InlineData("goal")] [InlineData("yellow_card")] [InlineData("red_card")] [InlineData("substitution")]
    [InlineData("var_decision")] [InlineData("period_end")]
    public async Task AcceptedHighlightSelectionsSignalConversationOnceEachAndOrdinarySeeksDoNot(string kind)
    {
        var first = View(SharedFootballTests.Fixture()); var highlight = new ReplayHighlight("event-one", 60000, kind, "Event", "1");
        first = first with { Dataset = first.Dataset with { Schedule = first.Dataset.Schedule! with { Releases = [new(highlight.RecordId, highlight.CursorMs)] } } };
        using var handler = new CallbackHandler(async (request, token) =>
        {
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            return Json(first with { Context = query });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new FootballReplay();
        Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { first.Dataset }); Set(component, "_view", first);
        Set(component, "_selectedStream", first.Context.StreamId); Set(component, "_highlights", new[] { highlight }); Set(component, "_highlightHead", first.Dataset.HeadSequence);
        for (var i = 1; i <= 2; i++)
        {
            var selecting = Invoke(component, "HighlightAsync", highlight);
            typeof(FootballReplay).GetMethod("Pause", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null); await selecting;
            Assert.Equal((long)i, Get(component, "_eventSelectionVersion"));
            Assert.Equal(Matchmate.Web.Chat.MatchEventVisual.SupportedKind(kind), Get(component, "_selectedEvent") is not null);
            if (kind is "var_decision" or "period_end")
            {
                var context = (Matchmate.Web.Chat.MatchChatContext)typeof(FootballReplay).GetProperty("ConversationContext", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!;
                Assert.Null(context.SelectedEvent); // Opening the pane never grants unsupported diagram authority.
            }
        }
        await Invoke(component, "SeekAsync", 30000L);
        Assert.Equal(2L, Get(component, "_eventSelectionVersion"));
    }

    [Fact]
    public async Task OnlyAnExistingGoalHighlightSetsSelectionAndPausePreservesItWhileSeekAndClearRemoveIt()
    {
        var first = View(SharedFootballTests.Fixture()); var highlight = new ReplayHighlight("goal-one", 60000, "goal", "Goal", "1");
        first = first with { Dataset = first.Dataset with { Schedule = first.Dataset.Schedule! with { Releases = [new(highlight.RecordId, highlight.CursorMs)] } } };
        var reads = 0; using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { first.Dataset });
            reads++; var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; return Json(first with { Context = query }); });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") }; await using var component = new FootballReplay();
        Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { first.Dataset }); Set(component, "_view", first);
        Set(component, "_selectedStream", first.Context.StreamId); Set(component, "_highlights", new[] { highlight }); Set(component, "_highlightHead", first.Dataset.HeadSequence);
        await Invoke(component, "HighlightAsync", highlight with { RecordId = "forged-goal" }); Assert.Equal(0, reads); Assert.Null(Get(component, "_selectedEvent"));
        var pending = Invoke(component, "HighlightAsync", highlight);
        typeof(FootballReplay).GetMethod("Pause", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null); await pending;
        var selected = Assert.IsType<Matchmate.Web.Chat.MatchSelectedEvent>(Get(component, "_selectedEvent")); Assert.Equal(highlight.RecordId, selected.RecordId);
        Assert.Equal(45000L, Get(component, "_cursorMs")); Assert.False((bool)Get(component, "_playing")!);
        var context = (Matchmate.Web.Chat.MatchChatContext)typeof(FootballReplay).GetProperty("ConversationContext", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!;
        Assert.Equal(selected, context.SelectedEvent);
        typeof(FootballReplay).GetMethod("ClearSelectedEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null); Assert.Null(Get(component, "_selectedEvent"));
        Set(component, "_selectedEvent", selected); await Invoke(component, "SeekAsync", 30000L); Assert.Null(Get(component, "_selectedEvent"));
        Set(component, "_selectedEvent", selected); await Invoke(component, "RefreshAsync"); Assert.Null(Get(component, "_selectedEvent"));
    }

    [Theory]
    [InlineData("complete-existing-generated-100ms-model")]
    [InlineData("sparse-generated-windows-only")]
    public async Task GeneratedModelCapabilityAlwaysRendersGeneratedDisclosure(string capability)
    {
        await using var component = new RenderableFootball(); var view = View(SharedFootballTests.Fixture());
        view = view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = [capability] } } };
        Set(component, "_view", view); using var builder = new RenderTreeBuilder(); component.Render(builder);
        var tree = Frames(builder); var text = string.Concat(tree.Array.Take(tree.Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
        Assert.Same(view.Dataset, HeaderDataset(builder)); // Actual child rendering covers both generated disclosure variants.
        Assert.DoesNotContain("Recorded source observations and estimates", text);
    }
    [Theory]
    [InlineData("en-SE")] [InlineData("sv-SE")]
    public async Task RoutedSharedComponentUsesInvariantCoordinatesAndExplicitSliceLabels(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        await using var component = new RenderableFootball();
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var view = View(SharedFootballTests.Fixture());
            var pitch = new RenderablePitch(); Parameter(pitch, "View", view);
            using var builder = new RenderTreeBuilder(); pitch.Render(builder);
            var frames = Frames(builder); var nodes = frames.Array.Take(frames.Count).ToArray();
            var attributes = nodes.Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName is "cx" or "cy" or "x" or "y" or "min" or "max" or "step").ToArray();
            Assert.Contains(attributes, f => f.AttributeName == "cx" && Equals(f.AttributeValue, "7.25"));
            Assert.Contains(attributes, f => f.AttributeName == "cy" && Equals(f.AttributeValue, "8.75"));
            Assert.All(attributes, f => Assert.DoesNotContain(',', f.AttributeValue.ToString()!));
            var roster = new RenderableRoster(); Parameter(roster, "View", view); Parameter(roster, "SelectedPerson", "person-one");
            using var rosterBuilder = new RenderTreeBuilder(); roster.Render(rosterBuilder);
            Assert.Contains("roster people", TreeText(rosterBuilder));
            Parameter(roster, "Language", MatchLanguage.Swedish);
            using var localizedRoster = new RenderTreeBuilder(); roster.Render(localizedRoster);
            Assert.Contains("1,25", TreeText(localizedRoster));
            var controls = new RenderableControls(); Parameter(controls, "Schedule", view.Dataset.Schedule);
            using var controlsBuilder = new RenderTreeBuilder(); controls.Render(controlsBuilder);
            Assert.Contains(Frames(controlsBuilder).Array.Take(Frames(controlsBuilder).Count), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "step" && Equals(f.AttributeValue, "100"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ActualSharedComponentClearsSelectionPausesAndRejectsLateOtherRunResponse()
    {
        var first = View(SharedFootballTests.Fixture()); var second = View(SharedFootballTests.Fixture("version-two", "replay-b"));
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SwitchingHandler(pending, second); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await using var component = new FootballReplay(); Set(component, "Api", new ReplayApiClient(http));
        Set(component, "_view", first); Set(component, "_personId", "person-one"); Set(component, "_playing", true);
        var old = Switch(component, first.Dataset);
        Assert.Null(Get(component, "_view")); Assert.Null(Get(component, "_personId")); Assert.False((bool)Get(component, "_playing")!);
        await Switch(component, second.Dataset);
        pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(first) }); await old;
        Assert.Equal("replay-b", ((FootballView)Get(component, "_view")!).Context.StreamId);
        Assert.Equal("version-two", ((FootballView)Get(component, "_view")!).Context.DatasetVersion);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task F002_BackwardSeekKeepsAcceptedSceneLabelledAndBlockedUntilCoherentCommit(bool fail)
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(fixture);
        SharedFootballTests.Add(ledger, fixture, 2, SharedFootballTests.Goal(fixture), SharedFootballTests.Frame(fixture, 0), SharedFootballTests.Frame(fixture, 80000));
        var future = ledger.Query(SharedFootballTests.Query(fixture, 80000)); var past = ledger.Query(SharedFootballTests.Query(fixture, 0));
        Assert.Single(future.Events); Assert.Equal("80000", future.Frame!.SourceFrameId); Assert.Empty(past.Events);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SwitchingHandler(pending, past); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await using var component = new RenderableFootball(); Set(component, "Api", new ReplayApiClient(http));
        Set(component, "_catalog", new[] { future.Dataset }); Set(component, "_selectedStream", fixture.Stream.StreamId);
        Set(component, "_view", future); Set(component, "_cursorMs", 80000L); Set(component, "_playing", true);
        using var beforeTree = new RenderTreeBuilder(); component.Render(beforeTree);
        var seeking = (Task)typeof(FootballReplay).GetMethod("SeekAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [0L])!;
        Assert.False(seeking.IsCompleted); Assert.Same(future, Get(component, "_view")); Assert.Equal(0L, Get(component, "_cursorMs"));
        Assert.False((bool)Get(component, "_playing")!); Assert.Contains("Updating", (string)Get(component, "_status")!);
        Assert.Contains("Showing accepted replay 01:20", RenderedText(component));
        using var waiting = new RenderTreeBuilder(); component.Render(waiting);
        var waitingFrames = Frames(waiting).Array.Take(Frames(waiting).Count).ToArray();
        foreach (var type in new[] { typeof(Matchmate.Web.Components.Football.FootballPitch), typeof(PlayerRoster) })
        {
            var before = Frames(beforeTree).Array.Take(Frames(beforeTree).Count).Single(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == type);
            var pendingFrame = waitingFrames.Single(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == type);
            Assert.Equal(before.Sequence, pendingFrame.Sequence); // Stable parent render location; real DOM continuity remains a browser gate.
        }
        Assert.Contains(waitingFrames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "InteractionDisabled" && Equals(f.AttributeValue, true));
        Assert.Contains(waitingFrames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Disabled" && Equals(f.AttributeValue, true));
        Assert.Contains(waitingFrames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "Updating" && Equals(f.AttributeValue, true));
        await Invoke(component, "ToggleAsync"); Assert.False((bool)Get(component, "_playing")!);
        typeof(FootballReplay).GetMethod("SelectPerson", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, ["person-one"]); Assert.Null(Get(component, "_personId"));

        pending.SetResult(new(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = JsonContent.Create(past) }); await seeking;
        if (fail)
        {
            Assert.Same(future, Get(component, "_view")); Assert.Contains("cannot be loaded", (string)Get(component, "_status")!);
            Assert.True((bool)Get(component, "_contextUnavailable")!); Assert.Contains("Showing accepted replay 01:20", RenderedText(component));
        }
        else
        {
            var accepted = (FootballView)Get(component, "_view")!;
            Assert.Equal(0, accepted.Context.CursorMs); Assert.Empty(accepted.Events); Assert.Equal("0", accepted.Frame!.SourceFrameId);
            Assert.False((bool)Get(component, "_contextUnavailable")!);
        }
        Assert.False((bool)Get(component, "_playing")!);
    }
    [Fact]
    public async Task RetrospectiveHighlightsNeverBecomeCurrentFactsAndUseTypedReleaseNotLabels()
    {
        var f = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(f);
        SharedFootballTests.Add(ledger, f, 2, SharedFootballTests.Goal(f), SharedFootballTests.Frame(f, 0), SharedFootballTests.Frame(f, 80000));
        var past = ledger.Query(SharedFootballTests.Query(f, 0)); var future = ledger.Query(SharedFootballTests.Query(f, 80000));
        future = future with { Dataset = future.Dataset with { Schedule = future.Dataset.Schedule! with { Releases = [new(future.Events[0].RecordId, 61000)], Milestones = [new("Misleading goal text 01:01", 99999)] } } };
        var highlight = Assert.Single(ExperiencePresentation.Highlights(future));
        Assert.Equal(future.Dataset.Schedule!.Releases.Single(r => r.RecordId == future.Events[0].RecordId).CursorMs, highlight.CursorMs);
        Assert.Equal(0, ExperiencePresentation.ReportedGoals(past, past.Dataset.Fixture!.Home.Id));
        Assert.Equal(1, ExperiencePresentation.ReportedGoals(future, future.Dataset.Fixture!.Home.Id));
        await using var component = new RenderableFootball(); Set(component, "_view", past); Set(component, "_highlights", new[] { highlight });
        Set(component, "_selectedStream", past.Context.StreamId); Set(component, "_catalog", new[] { past.Dataset });
        Assert.Contains("Reported goals:", RenderedText(component)); Assert.DoesNotContain("Confirmed goal contributions represented here", RenderedText(component));
    }
    [Fact]
    public async Task RefreshReloadsTheCurrentViewAndKeepsIndependentBallDuringPlayerGap()
    {
        var view = View(SharedFootballTests.Fixture()); var calls = 0;
        using var handler = new RefreshHandler(view, () => calls++); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await using var component = new FootballReplay(); Set(component, "Api", new ReplayApiClient(http));
        Set(component, "_catalog", new[] { view.Dataset }); Set(component, "_selectedStream", view.Context.StreamId); Set(component, "_view", view);
        await (Task)typeof(FootballReplay).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [])!;
        Assert.Equal(2, calls); // current cursor plus separate retrospective end index
        var pitch = new RenderablePitch(); Parameter(pitch, "View", view with { TrackingGap = true, Frame = view.Frame! with { Players = [] } });
        using var builder = new RenderTreeBuilder(); pitch.Render(builder);
        Assert.Contains(Frames(builder).Array.Take(Frames(builder).Count), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && f.AttributeValue.ToString()!.StartsWith("ball ", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task F_UI_001_ActualRouteChangeHidesOldSelectionBeforeDelayedOrFailedCatalog(bool fail)
    {
        var first = View(SharedFootballTests.Fixture()); var second = Rebind(View(SharedFootballTests.Fixture("version-two", "replay-b")), "second-recording");
        var catalog = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldHighlight = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldHighlightToken = default;
        using var handler = new CallbackHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return await catalog.Task;
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (query.StreamId == first.Context.StreamId) { oldHighlightToken = token; return await oldHighlight.Task; }
            return Json(second with { Context = query });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var component = new RenderableFootball(); Set(component, "Api", new ReplayApiClient(http));
        Set(component, "_catalog", new[] { first.Dataset }); Set(component, "_view", first); Set(component, "_selectedStream", first.Context.StreamId);
        Set(component, "_routeDataset", first.Context.DatasetId); Set(component, "_personId", "person-one"); Set(component, "_playing", true); Set(component, "_stopAt", 65000L);
        var loadingOldHighlights = Invoke(component, "LoadHighlightsAsync", first.Dataset, 0L);
        Set(component, "_highlights", new[] { new ReplayHighlight("old-goal", 60000, "goal", "Old recording goal", "1") });
        var frameRequest = new CancellationTokenSource(); Set(component, "_request", frameRequest); // component owns and disposes this request
        Parameter(component, "DatasetId", second.Context.DatasetId);
        var routing = Invoke(component, "OnParametersSetAsync");
        Assert.False(routing.IsCompleted); Assert.Null(Get(component, "_view")); Assert.Empty((ReplayHighlight[])Get(component, "_highlights")!);
        Assert.Equal("", Get(component, "_selectedStream")); Assert.Null(CurrentDataset(component)); Assert.Null(Get(component, "_personId")); Assert.Null(Get(component, "_stopAt"));
        Assert.False((bool)Get(component, "_playing")!); Assert.True(frameRequest.IsCancellationRequested); Assert.True(oldHighlightToken.IsCancellationRequested);
        Assert.DoesNotContain("Reported goals:", RenderedText(component)); Assert.DoesNotContain("Old recording goal", RenderedText(component));
        catalog.SetResult(fail ? new(HttpStatusCode.ServiceUnavailable) : Json(new[] { second.Dataset })); await routing;
        oldHighlight.SetResult(Json(first)); await loadingOldHighlights;
        Assert.Empty((ReplayHighlight[])Get(component, "_highlights")!);
        if (fail) { Assert.Null(Get(component, "_view")); Assert.Null(CurrentDataset(component)); Assert.DoesNotContain("Reported goals:", RenderedText(component)); }
        else { Assert.Equal(second.Context.DatasetId, Assert.IsType<FootballView>(Get(component, "_view")).Context.DatasetId); }
    }
    [Fact]
    public async Task F_UI_002_DisallowedCorrectionIsDisclosedAndExcludedWhileUnknownGoalRemainsReported()
    {
        var f = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(f); var goal = SharedFootballTests.Goal(f);
        var unknown = goal with { RecordId = "goal-unknown", Payload = ((MatchEventPayload)goal.Payload) with { Decision = "unknown" } };
        SharedFootballTests.Add(ledger, f, 2, goal, unknown, SharedFootballTests.Frame(f, 80000));
        var before = ledger.Query(SharedFootballTests.Query(f, 80000)); Assert.Equal(2, ExperiencePresentation.ReportedGoals(before, "team-home"));
        SharedFootballTests.Add(ledger, f, 3, SharedFootballTests.Revise(goal, 2, 60000, SharedFootballTests.At("12:05")) with { Payload = ((MatchEventPayload)goal.Payload) with { Decision = "disallowed" } });
        var corrected = ledger.Query(SharedFootballTests.Query(f, 80000)); Assert.Equal(2, corrected.Events.Length);
        Assert.Equal(1, ExperiencePresentation.ReportedGoals(corrected, "team-home"));
        corrected = corrected with { Dataset = corrected.Dataset with { Schedule = corrected.Dataset.Schedule! with { Releases = [new(goal.RecordId, 60000), new(unknown.RecordId, 60000)] } } };
        var highlights = ExperiencePresentation.Highlights(corrected); Assert.Contains(highlights, h => h.Label.Contains("(disallowed)", StringComparison.Ordinal)); Assert.Contains(highlights, h => h.Label.Contains("(unknown)", StringComparison.Ordinal));
        await using var component = new RenderableFootball(); Set(component, "_view", corrected);
        var text = RenderedText(component); Assert.Contains("decision disallowed", text); Assert.Contains("decision unknown", text);
        Assert.Contains("Explicitly disallowed goals are excluded", text);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task F_UI_003_LateHighlightCannotStartCompletedNewRecordingOrOverrideManualSeek(bool switchRecording)
    {
        var first = View(SharedFootballTests.Fixture()); var second = Rebind(View(SharedFootballTests.Fixture("version-two", "replay-b")), "second-recording");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var requests = 0;
        using var handler = new CallbackHandler(async (request, token) =>
        {
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (++requests == 1) return await pending.Task;
            return Json((query.StreamId == second.Context.StreamId ? second : first) with { Context = query });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var component = new FootballReplay(); Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { first.Dataset, second.Dataset });
        first = first with { Dataset = first.Dataset with { Schedule = first.Dataset.Schedule! with { Releases = [new("goal-one", 60000)] } } };
        var highlight = new ReplayHighlight("goal-one", 60000, "goal", "Old goal", "1"); Set(component, "_highlights", new[] { highlight }); Set(component, "_highlightHead", first.Dataset.HeadSequence);
        Set(component, "_view", first); Set(component, "_selectedStream", first.Context.StreamId);
        var highlighting = Invoke(component, "HighlightAsync", highlight);
        Assert.False(highlighting.IsCompleted);
        if (switchRecording) await Switch(component, second.Dataset); else await Invoke(component, "SeekAsync", 10000L);
        var accepted = Assert.IsType<FootballView>(Get(component, "_view")); var cursor = (long)Get(component, "_cursorMs")!;
        pending.SetResult(Json(first with { Context = first.Context with { CursorMs = 45000 } })); await highlighting;
        Assert.Same(accepted, Get(component, "_view")); Assert.Equal(cursor, Get(component, "_cursorMs"));
        Assert.False((bool)Get(component, "_playing")!); Assert.Null(Get(component, "_stopAt")); Assert.Null(Get(component, "_selectedEvent"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task F_UI_003_OldPlayAwaitCannotStartNewSelection(bool waitingForEndSeek)
    {
        var first = View(SharedFootballTests.Fixture()); var second = Rebind(View(SharedFootballTests.Fixture("version-two", "replay-b")), "second-recording");
        var oldTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSeek = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var firstRequest = true;
        using var handler = new CallbackHandler(async (request, token) =>
        {
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (waitingForEndSeek && firstRequest) { firstRequest = false; return await oldSeek.Task; }
            return Json(second with { Context = query });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        await using var component = new FootballReplay(); Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { first.Dataset, second.Dataset }); Set(component, "_selectedStream", first.Context.StreamId);
        var cursor = waitingForEndSeek ? 100000L : 0; Set(component, "_cursorMs", cursor); Set(component, "_view", first with { Context = first.Context with { CursorMs = cursor } });
        if (!waitingForEndSeek) Set(component, "_playTask", oldTask.Task);
        var toggling = Invoke(component, "ToggleAsync"); Assert.False(toggling.IsCompleted);
        await Switch(component, second.Dataset); var accepted = Get(component, "_view");
        oldTask.SetResult(); oldSeek.SetResult(Json(first)); await toggling;
        Assert.Same(accepted, Get(component, "_view")); Assert.False((bool)Get(component, "_playing")!); Assert.Null(Get(component, "_stopAt")); Assert.Equal(0L, Get(component, "_cursorMs"));
    }
    [Fact]
    public void ReplayCardsProjectSourceAssistsAndScoreAllEligibleGoalsIncludingThoseWithoutHighlightReleases()
    {
        var f = SharedFootballTests.Fixture(); var view = View(f); var goal = SharedFootballTests.Goal(f);
        var away = goal with { RecordId = "away-goal", Payload = new MatchEventPayload("goal", "team-away", "person-two", Decision: "unknown") };
        var assisted = goal with { Payload = ((MatchEventPayload)goal.Payload) with { AssistPersonId = "person-two" } };
        var disallowed = goal with { RecordId = "disallowed", Payload = ((MatchEventPayload)goal.Payload) with { Decision = "disallowed" } };
        var laterWindow = away with { RecordId = "later-window", FootballTime = away.FootballTime! with { StartMs = 60000, EndMs = 80000, Precision = "reported_minute" } };
        var unscheduled = goal with { RecordId = "no-release" };
        view = view with { Events = [assisted, away, disallowed, laterWindow, unscheduled], Dataset = view.Dataset with { Schedule = view.Dataset.Schedule! with {
            Releases = [new(assisted.RecordId, 60000), new(away.RecordId, 60000), new(disallowed.RecordId, 60000), new(laterWindow.RecordId, 60000)] } } };
        var cards = ExperiencePresentation.Highlights(view);
        Assert.Equal(4, cards.Length); Assert.DoesNotContain(cards, c => c.RecordId == "no-release");
        var ledger = new Matchmate.Core.FootballLedger();
        var context = SharedFootballTests.Context(f).Select(r => r.Payload is SchedulePayload ? r with { Payload = view.Dataset.Schedule! } : r).ToArray();
        SharedFootballTests.Add(ledger, f, 1, context); SharedFootballTests.Add(ledger, f, 2, view.Events);
        var eligible = ledger.Query(SharedFootballTests.Query(f, 60000));
        Assert.Equal(2, ExperiencePresentation.ReportedGoals(eligible, "team-home")); Assert.Equal(1, ExperiencePresentation.ReportedGoals(eligible, "team-away"));
        var score = $"{ExperiencePresentation.ReportedGoals(eligible, "team-home")}–{ExperiencePresentation.ReportedGoals(eligible, "team-away")}";
        Assert.All(cards, c => Assert.Equal(score, c.ScoreAfter));
        Assert.Equal("Two", cards.Single(c => c.RecordId == goal.RecordId).Assist);
        Assert.Null(cards.Single(c => c.RecordId == away.RecordId).Assist);
        Assert.Equal("disallowed", cards.Single(c => c.RecordId == disallowed.RecordId).Decision);
        Assert.Equal(new[] { 1, 2, 3, 4 }, cards.Select(c => c.GoalNumber!.Value));
        var missing = ExperiencePresentation.Highlights(view with { Events = [goal with { Payload = new MatchEventPayload("goal", "team-home") }] });
        Assert.Equal("Unspecified player", Assert.Single(missing).Actor); Assert.Null(missing[0].Assist);
    }

    [Fact]
    public void ActualHighlightCardsDefaultToGoalsAfterLoadingAndExposeCountedAccessibleFilters()
    {
        var component = new RenderableHighlights(); Parameter(component, "ContextKey", "one"); component.Apply();
        var goal = new ReplayHighlight("goal", 60000, "goal", "Source goal", "1", "Scorer", "Source assistant", "Home", "1–0", 1);
        Parameter(component, "Highlights", new[] { goal, new ReplayHighlight("sub", 70000, "substitution", "Source substitution", "2", "Substitute") }); component.Apply();
        using var builder = new RenderTreeBuilder(); component.Render(builder); var text = TreeText(builder);
        Assert.Contains("Scorer", text); Assert.Contains("Assist · Source assistant", text); Assert.Contains("1–0", text);
        Assert.DoesNotContain("Substitute", text); Assert.Contains("retrospective spoilers", text);
        var attributes = Frames(builder).Array.Take(Frames(builder).Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-pressed").ToArray();
        Assert.Equal("true", attributes[0].AttributeValue); Assert.All(attributes.Skip(1), a => Assert.Equal("false", a.AttributeValue));
        Assert.Equal(7, attributes.Length); // six counted filters and the visible card's pin
        Assert.Contains("Goals1", text); Assert.Contains("All moments2", text); Assert.Contains("Cards0", text); Assert.Contains("Completed possessions0", text);
        Parameter(component, "ContextKey", "recording-with-no-events"); Parameter(component, "Highlights", Array.Empty<ReplayHighlight>()); component.Apply();
        using var empty = new RenderTreeBuilder(); component.Render(empty);
        Assert.DoesNotContain("Scorer", TreeText(empty)); Assert.DoesNotContain("Watch this goal", TreeText(empty));
    }

    [Fact]
    public async Task ActualTransportUsesHumanPeriodLabelsAndTypedReleasePinsAndKeyboardCallback()
    {
        var view = View(SharedFootballTests.Fixture());
        var dataset = view.Dataset with { Fixture = view.Dataset.Fixture! with { Periods = [new("period-one", 1, "First half", 0, 50000), new("period-two", 2, "Second half", 45000, 50000)] },
            Schedule = view.Dataset.Schedule! with { Segments = [new("period-one", 0, 50000, 0), new("period-one", 50000, 51000, 50000, "halftime_transition", 50000), new("period-two", 51000, 100000, 0)] } };
        var goal = new ReplayHighlight("source-goal", 62500, "goal", "Misleading 99:99 label", "47", GoalNumber: 1);
        ReplayHighlight? navigated = null; var component = new RenderableControls(); Parameter(component, "Schedule", dataset.Schedule); Parameter(component, "Dataset", dataset);
        Parameter(component, "Highlights", new[] { goal }); Parameter(component, "NavigateHighlight", Microsoft.AspNetCore.Components.EventCallback.Factory.Create<ReplayHighlight>(new object(), (ReplayHighlight item) => navigated = item));
        using var builder = new RenderTreeBuilder(); component.Render(builder);
        Assert.Contains("First half", TreeText(builder)); Assert.Contains("Second half →", TreeText(builder)); Assert.DoesNotContain("period-two", TreeText(builder));
        Assert.Contains("Halftime", TreeText(builder));
        Assert.Equal(51000, ((PresentationSegment)typeof(ReplayControls).GetProperty("NextPlaySegment", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!).CursorStartMs);
        var attributes = Frames(builder).Array.Take(Frames(builder).Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
        Assert.Contains(attributes, a => a.AttributeName == "x" && Equals(a.AttributeValue, "62.5%"));
        await (Task)typeof(ReplayControls).GetMethod("GoalKeyAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }, goal])!;
        Assert.Same(goal, navigated);
        Assert.Equal("Period unavailable", ExperiencePresentation.PeriodLabel(view with { PeriodId = "absent" }));
        Assert.Equal("Match period", ExperiencePresentation.SegmentLabel(dataset, new("absent", 0, 1, 0)));
    }

    [Theory] [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void RedCardAndVarHighlightsRenderTheirLocalizedKindsAndDistinctDecisions(MatchLanguage language)
    {
        var component = new RenderableHighlights(); Parameter(component, "Language", language);
        Parameter(component, "Highlights", new[] {
            new ReplayHighlight("dismissal", 60000, "red_card", "red_card", "1", "Reported player", Decision: "second_yellow"),
            new ReplayHighlight("review", 65000, "var_decision", "var_decision", "2", "Reported review", Decision: "card_upgraded") });
        component.Apply(); using var builder = new RenderTreeBuilder(); component.Render(builder); var text = TreeText(builder);
        Assert.Contains(ReplayText.Kind(language, "red_card"), text); Assert.Contains(ReplayText.Kind(language, "var_decision"), text);
        Assert.Contains(ReplayText.Value(language, "second_yellow"), text); Assert.Contains(ReplayText.Value(language, "card_upgraded"), text);
        Assert.Contains(ReplayText.Get(language, "FilterCards") + "1", text);
    }

    [Fact]
    public async Task LiverpoolFirstBatchPartialImportKeepsSequenceIndexUnavailable()
    {
        var ledger = new FootballLedger();
        await foreach (var batch in new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json")).ReadBatchesAsync(default))
        { ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow)); break; }
        var source = Assert.Single(ledger.Catalog()); var end = ledger.Query(FullFootballSourceTests.Query(source, 6540000));
        Assert.Equal(23, end.Events.Length);
        Assert.False(MatchPitchScene.IndexAvailable(end, source.HeadSequence, end.Events));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LiverpoolOptionalGoalSequencesKeepAllTwentyThreeReportsInInitialAndRetriedIndexes(bool retry)
    {
        var ledger = new FootballLedger();
        await foreach (var batch in new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json")).ReadBatchesAsync(default))
        { ledger.Accept(new(FootballCodec.Decode(FootballCodec.Encode(batch)), DateTimeOffset.UtcNow)); }
        var source = Assert.Single(ledger.Catalog()); var ready = source with { Closed = true, HeadSequence = source.HeadSequence + 1 }; var open = source with { Closed = false };
        var first = ledger.Query(FullFootballSourceTests.Query(source, 0)); var end = ledger.Query(FullFootballSourceTests.Query(source, 6540000));
        Assert.Equal(23, end.Events.Length); Assert.Equal(11, end.Events.Count(r => r.Payload is MatchEventPayload { Sequence: not null }));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var component = new RetryingFootball { Delay = _ => gate.Task };
        using var handler = new CallbackHandler(async (request, token) => {
            if (request.Method == HttpMethod.Get) return Json(new[] { ready });
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            return Json((query.CursorMs == 0 ? first : end) with { Context = query, Dataset = query.ApplicationSequence == source.HeadSequence ? open : ready });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://test.invalid/") };
        var initial = retry ? open : ready;
        // The first bound end-index read uses its requested stream head; retry then sees the closed higher head.
        using var initialHandler = new CallbackHandler(async (request, token) => {
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            return Json(end with { Context = query, Dataset = initial });
        });
        using var initialHttp = new HttpClient(initialHandler) { BaseAddress = new("https://test.invalid/") };
        PrepareIndex(component, initialHttp, first with { Dataset = initial });
        await Invoke(component, "LoadHighlightsAsync", initial, 0L);
        if (retry)
        {
            Set(component, "Api", new ReplayApiClient(http)); gate.TrySetResult();
            await IndexTask(component).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(23, Assert.IsType<FootballRecord[]>(Get(component, "_sequenceIndex")).Length);
        Assert.True((bool)typeof(FootballReplay).GetProperty("SequenceIndexAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!);
        Assert.Equal(11, Assert.IsType<FootballRecord[]>(Get(component, "_sequenceIndex")).Count(r => r.Payload is MatchEventPayload { Sequence: not null }));
        var current = Assert.IsType<FootballView>(Get(component, "_view"));
        var goal = end.Events.Single(r => r.RecordId == "goal-1");
        Set(component, "_view", current with { Context = current.Context with { CursorMs = 2625000 } });
        Set(component, "_selectedEvent", new Matchmate.Web.Chat.MatchSelectedEvent("goal-1", 2640000, "44", "goal"));
        var scenes = (MatchSequenceProjection[])typeof(FootballReplay).GetProperty("PitchSequences", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component)!;
        Assert.Equal(goal.RecordId, Assert.Single(scenes).RecordId); Assert.Equal("retrospective_selected_goal", scenes[0].Scope);
    }

    [Fact]
    public async Task RecordingWithoutEventCapabilityNeverInventsScoreAndReplayConversationPreservesLeagueDefault()
    {
        var view = View(SharedFootballTests.Fixture()); view = view with { Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = ["tracking_frame", "player_possession"] } } };
        await using var component = new RenderableFootball(); Set(component, "_view", view);
        var text = RenderedText(component); Assert.Contains("No match score supplied", text); Assert.DoesNotContain("Reported goals:", text);
        Assert.Contains("not a goal feed or match score", text);
        var conversation = new RenderableConversation(); Parameter(conversation, "Title", "this match"); Parameter(conversation, "Context", "First half · 00:00");
        using var league = new RenderTreeBuilder(); conversation.Render(league); Assert.Contains("What would you like to know?", TreeText(league)); Assert.DoesNotContain("At this moment", TreeText(league));
        Parameter(conversation, "ReplayLayout", true); using var replay = new RenderTreeBuilder(); conversation.Render(replay);
        Assert.Contains("At this moment · this match", TreeText(replay)); Assert.Contains("Source answers are computed in code from the available data. These questions do not call a model.", TreeText(replay));
    }

    [Fact]
    public void PitchAndRosterKeepUnknownAndGeneratedCuesExplicitWithoutInventedRoles()
    {
        var view = View(SharedFootballTests.Fixture()); var observed = view.Frame!.Players[0];
        view = view with { Frame = view.Frame with { Players = [observed with { Method = "generated", SlotId = "slot-one" }, observed with { SubjectId = "person-two", Method = "unknown", XM = 8.125, YM = 5.875 }], ActiveSubjectIds = ["person-one", "person-two"] },
            Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = ["complete-existing-generated-100ms-model"] } } };
        var pitch = new RenderablePitch(); Parameter(pitch, "View", view); Parameter(pitch, "SelectedPerson", "person-two"); using var builder = new RenderTreeBuilder(); pitch.Render(builder);
        var attrs = Frames(builder).Array.Take(Frames(builder).Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
        Assert.Contains(attrs, a => a.AttributeName == "class" && a.AttributeValue.ToString()!.Contains("unknown focused", StringComparison.Ordinal));
        Assert.Contains(attrs, a => a.AttributeName == "x" && Equals(a.AttributeValue, "12.425"));
        Assert.Contains(attrs, a => a.AttributeName == "class" && Equals(a.AttributeValue, "selection-ring"));
        Assert.All(attrs.Where(a => a.AttributeName == "aria-pressed"), a => Assert.IsType<string>(a.AttributeValue));
        var roster = new RenderableRoster(); Parameter(roster, "View", view); Parameter(roster, "SelectedPerson", "person-two"); using var rosterTree = new RenderTreeBuilder(); roster.Render(rosterTree);
        var text = TreeText(rosterTree); Assert.Contains("2 / 22 active slots", text); Assert.Contains("Source sample", text); Assert.Contains("No tactical role is inferred", text);
        Assert.Contains("generated / present", text); Assert.Contains("unknown / present", text); Assert.Contains("Role unverified", text);
        Assert.DoesNotContain(Frames(rosterTree).Array.Take(Frames(rosterTree).Count), f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && Equals(f.AttributeValue, "distance-sort"));
        Assert.Contains("Position assignments are generated", text); Assert.DoesNotContain("Striker", text);
    }
    private static FootballView IndexView(long head, bool closed)
    {
        var view = View(SharedFootballTests.Fixture()); return view with { Dataset = view.Dataset with { HeadSequence = head, Closed = closed,
            Schedule = view.Dataset.Schedule! with { Releases = [new("goal-1", 60000)] } } };
    }
    private static FootballView AtIndex(FootballView view, FootballQuery query) => view with { Context = query,
        Events = query.CursorMs < 60000 ? [] : [SharedFootballTests.Goal(SharedFootballTests.Fixture())] };
    private static void PrepareIndex(FootballReplay component, HttpClient http, FootballView view)
    { Set(component, "Api", new ReplayApiClient(http)); Set(component, "_catalog", new[] { view.Dataset }); Set(component, "_selectedStream", view.Context.StreamId); Set(component, "_view", view); }
    private static Task IndexTask(FootballReplay component) => Assert.IsAssignableFrom<Task>(Get(component, "_indexRetryTask"));
    private sealed class RetryingFootball : FootballReplay
    {
        public Func<CancellationToken, Task> Delay { get; set; } = _ => Task.CompletedTask;
        public TaskCompletionSource Rendered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task WaitForIndexRetryAsync(CancellationToken token) => Delay(token);
        protected override Task RenderIndexRetryAsync() { Rendered.TrySetResult(); return Task.CompletedTask; }
    }
    private static string RenderedText(RenderableFootball component)
    {
        using var builder = new RenderTreeBuilder(); component.Render(builder); var frames = Frames(builder);
        return string.Concat(frames.Array.Take(frames.Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
            .Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent));
    }
    private static FootballView View(SharedFootballTests.TestFixture fixture)
    {
        var ledger = SharedFootballTests.Ledger(fixture); SharedFootballTests.Add(ledger, fixture, 2, SharedFootballTests.Frame(fixture, 0));
        return ledger.Query(SharedFootballTests.Query(fixture, 0));
    }
    private static Task Switch(FootballReplay component, FootballDataset dataset) => (Task)typeof(FootballReplay).GetMethod("SwitchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, [dataset])!;
    private static Task Invoke(FootballReplay component, string method, params object[] args) => (Task)typeof(FootballReplay).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, args)!;
    private static FootballDataset? CurrentDataset(FootballReplay component) => (FootballDataset?)typeof(FootballReplay).GetProperty("CurrentDataset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
    private static FootballView Rebind(FootballView view, string datasetId) => view with { Context = view.Context with { DatasetId = datasetId }, Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { DatasetId = datasetId }, Stream = view.Dataset.Stream with { DatasetId = datasetId } } };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static object? Get(FootballReplay component, string name) => typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component);
    private static void Set(FootballReplay component, string name, object? value)
    {
        var property = typeof(FootballReplay).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null) property.SetValue(component, value); else typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component, value);
    }
    private sealed class RenderableFootball : FootballReplay { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderablePitch : Matchmate.Web.Components.Football.FootballPitch { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderableRoster : PlayerRoster { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderableControls : ReplayControls { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private sealed class RenderableHighlights : MatchHighlights { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); public void Apply() => OnParametersSet(); }
    private sealed class RenderableConversation : Matchmate.Web.Components.Experience.SourceConversation { public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder); }
    private static void Parameter(object component, string name, object? value) => component.GetType().GetProperty(name)!.SetValue(component, value);
    private static ArrayRange<RenderTreeFrame> Frames(RenderTreeBuilder builder)
    {
        var range = builder.GetFrames(); var direct = range.Array.Take(range.Count).ToArray();
        var nested = new List<RenderTreeFrame>(direct);
        foreach (var fragment in direct.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute).Select(frame => frame.AttributeValue).OfType<RenderFragment>())
        { using var child = new RenderTreeBuilder(); fragment(child); var children = Frames(child); nested.AddRange(children.Array.Take(children.Count)); }
        var frames = nested.ToArray(); return new(frames, frames.Length);
    }
    private static FootballDataset HeaderDataset(RenderTreeBuilder builder)
    {
        var frames = Frames(builder);
        var index = Assert.Single(Enumerable.Range(0, frames.Count), i => frames.Array[i].FrameType == RenderTreeFrameType.Component && frames.Array[i].ComponentType == typeof(ReplayMatchHeader));
        var parameter = Assert.Single(frames.Array.Skip(index + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute), f => f.AttributeName == nameof(ReplayMatchHeader.Dataset));
        return Assert.IsType<FootballDataset>(parameter.AttributeValue);
    }
    private static string TreeText(RenderTreeBuilder builder) { var frames = Frames(builder); return string.Concat(frames.Array.Take(frames.Count).Where(f => f.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup).Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.MarkupContent)); }
    private static string[] Attributes(RenderTreeBuilder tree, string name) => Frames(tree).Array.Take(Frames(tree).Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name).Select(f => f.AttributeValue?.ToString() ?? "").ToArray();
    private static void AssertLoading(RenderTreeBuilder tree, string text) => Assert.Contains(Frames(tree).Array.Take(Frames(tree).Count), frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "Text" && Equals(frame.AttributeValue, text));
    private static void AssertNoLoading(RenderTreeBuilder tree) => Assert.DoesNotContain(Frames(tree).Array.Take(Frames(tree).Count), frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(LoadingStatus));
    private sealed class RefreshHandler(FootballView view, Action queried) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { view.Dataset }) };
            var context = await request.Content!.ReadFromJsonAsync<FootballQuery>(cancellationToken); queried();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(view with { Context = context! }) };
        }
    }
    private sealed class SwitchingHandler(TaskCompletionSource<HttpResponseMessage> pending, FootballView next) : HttpMessageHandler
    {
        private int _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            ++_requests == 1 ? pending.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(next) });
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
}
#pragma warning restore BL0006
