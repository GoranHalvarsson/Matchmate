using System.Reflection;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Real component trees and lifecycle; no replacement browser host.
public sealed partial class MatchConversationTests
{
    [Theory]
    [InlineData(MatchLanguage.English, false)] [InlineData(MatchLanguage.English, true)]
    [InlineData(MatchLanguage.Swedish, false)] [InlineData(MatchLanguage.Swedish, true)]
    [InlineData(MatchLanguage.Spanish, false)] [InlineData(MatchLanguage.Spanish, true)]
    public void PitchWorkspaceHasOneComposerLocalizedEntryAndTwoOrThreeCompactActions(MatchLanguage language, bool player)
    {
        using var h = new Harness(); using var component = Workspace(h);
        WorkspaceSet(component, "Language", language); WorkspaceSet(component, "Title", player ? "Selected player" : ReplayText.Get(language, "ThisMatch"));
        WorkspaceSet(component, "FixtureLabel", "Home v Away"); WorkspaceSet(component, "ContextLabel", "96:00 · replay 98:00"); component.Apply();
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        var frames = Frames(tree); var text = string.Join(' ', frames.Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
        Assert.Contains(ReplayText.Get(language, "WorkspaceEyebrow"), text); Assert.Contains("Home v Away", text); Assert.Contains("96:00 · replay 98:00", text);
        Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "textarea");
        Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "form");
        Assert.Equal(player ? 3 : 2, WorkspaceChildren(frames, "chat-quick-questions", "button").Length);
        Assert.True((bool)WorkspaceElement(frames, "match-chat-paper")["hidden"]!);
        Assert.False(Equals(WorkspaceElement(frames, "replay-roster-pane").GetValueOrDefault("hidden"), true));
        var switches = WorkspaceChildren(frames, "workspace-switch", "button"); Assert.Equal(2, switches.Length);
        Assert.Equal("true", switches[0]["aria-pressed"]); Assert.Equal("false", switches[1]["aria-pressed"]);
        Assert.Equal("match-players-panel", switches[0]["aria-controls"]); Assert.Equal("match-conversation-panel", switches[1]["aria-controls"]);
        Assert.DoesNotContain(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "role" && Equals(f.AttributeValue, "tab"));
        Assert.Contains(ReplayText.Get(language, "WorkspaceEmpty"), text); Assert.DoesNotContain(ReplayText.Get(language, "ChatEmpty"), text);
        WorkspaceSet(component, "PitchSide", false);
        using var standalone = new RenderTreeBuilder(); component.Render(standalone);
        var standaloneText = string.Join(' ', Frames(standalone).Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
        Assert.Contains(ReplayText.Get(language, "ChatEmpty"), standaloneText); Assert.DoesNotContain(ReplayText.Get(language, "WorkspaceEmpty"), standaloneText);
        Assert.Empty(h.Router.Inputs); Assert.Empty(h.Api.Queries);
    }

    [Fact]
    public async Task PitchPaneSwitchesKeepDraftHistorySlotsAndPlaybackCallbackUntouched()
    {
        using var h = new Harness(); using var component = Workspace(h); var paused = 0;
        WorkspaceSet(component, "OnAsking", EventCallback.Factory.Create(new object(), () => paused++));
        WorkspaceSet(component, "PitchContent", (RenderFragment)(b => b.AddContent(0, "PITCH_RETAINED")));
        WorkspaceSet(component, "RosterContent", (RenderFragment)(b => b.AddContent(0, "ROSTER_RETAINED")));
        await WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Summary); var turn = Assert.Single(h.Session.Turns);
        Assert.Equal(1, paused); Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!);
        WorkspaceSet(component, "_question", "My unsent follow-up");
        for (var i = 0; i < 3; i++)
        {
            await WorkspaceInvoke(component, "ShowPlayers"); component.Apply();
            using var tree = new RenderTreeBuilder(); component.Render(tree); var frames = Frames(tree);
            Assert.True((bool)WorkspaceElement(frames, "match-chat-paper")["hidden"]!);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text && f.TextContent == "PITCH_RETAINED");
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text && f.TextContent == "ROSTER_RETAINED");
            await WorkspaceInvoke(component, "ShowConversation"); component.Apply();
            Assert.Equal("My unsent follow-up", WorkspaceGet(component, "_question")); Assert.Same(turn, Assert.Single(h.Session.Turns));
        }
        Assert.Equal(1, paused); Assert.Single(h.Api.Queries);
    }

    [Fact]
    public async Task PitchTypedSendOpensConversationAndCancelRemainsVisibleOnPlayersPane()
    {
        using var h = new Harness(); using var component = Workspace(h);
        var waiting = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Router.Respond = (_, _) => waiting.Task; WorkspaceSet(component, "_question", "Explain the current shape");
        var pending = WorkspaceInvoke(component, "SubmitAsync"); Assert.False(pending.IsCompleted); Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!);
        await WorkspaceInvoke(component, "ShowPlayers");
        using (var tree = new RenderTreeBuilder())
        {
            component.Render(tree); var frames = Frames(tree);
            Assert.Single(WorkspaceChildren(frames, "chat-entry-pending", "button"));
            Assert.True((bool)WorkspaceElement(frames, "match-chat-paper")["hidden"]!);
            Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "form");
        }
        await WorkspaceInvoke(component, "Cancel"); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        waiting.SetResult(new(MatchRoutingStatus.Selected, "current_match_summary"));
        Assert.Equal("Explain the current shape", WorkspaceGet(component, "_question")); Assert.Empty(h.Session.Turns); Assert.Empty(h.Model.Messages);
    }

    [Fact]
    public async Task PitchRewindCancelsPendingAndRetainsDraftWithoutPaneRemount()
    {
        using var h = new Harness(); using var component = Workspace(h);
        var waiting = new TaskCompletionSource<MatchRoutingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Router.Respond = (_, _) => waiting.Task; WorkspaceSet(component, "_question", "Keep this question");
        var pending = WorkspaceInvoke(component, "SubmitAsync");
        WorkspaceSet(component, "Updating", true); component.Apply(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        WorkspaceSet(component, "Updating", false); WorkspaceSet(component, "Context", h.Context with { Query = h.Context.Query with { CursorMs = 1000 } }); component.Apply();
        waiting.SetResult(new(MatchRoutingStatus.Selected, "current_match_summary"));
        Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!); Assert.Equal("Keep this question", WorkspaceGet(component, "_question"));
        Assert.Empty(h.Session.Turns); Assert.False((bool)WorkspaceGet(component, "_pending")!);
    }

    [Fact]
    public async Task PitchAnswerRevealIsOnceWithinTranscriptAndWaitsWhilePlayersAreVisible()
    {
        using var h = new Harness(); await using var component = Workspace(h); var js = new WorkspaceJs(); WorkspaceSet(component, "JS", js);
        WorkspaceSet(component, "_conversationPanel", new ElementReference("conversation", new WebElementReferenceContext(js)));
        await WorkspaceInvoke(component, "OnAfterRenderAsync", true); Assert.Null(js.Module.Calls[^1][3]);
        await WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Summary); var turn = Assert.Single(h.Session.Turns);
        await WorkspaceInvoke(component, "ShowPlayers"); await WorkspaceInvoke(component, "OnAfterRenderAsync", false);
        Assert.Null(js.Module.Calls[^1][3]); Assert.NotNull(WorkspaceGet(component, "_revealTurn"));
        await WorkspaceInvoke(component, "ShowConversation"); await WorkspaceInvoke(component, "OnAfterRenderAsync", false);
        Assert.Equal(turn.Id.ToString("N"), js.Module.Calls[^1][3]); Assert.Null(WorkspaceGet(component, "_revealTurn"));
        WorkspaceSet(component, "_question", "Typing without a jump"); await WorkspaceInvoke(component, "OnAfterRenderAsync", false); Assert.Null(js.Module.Calls[^1][3]);
        Assert.Equal(1, js.Imports); Assert.Equal(1, js.FocusCalls); Assert.True(js.LastPreventScroll);
        Assert.All(js.Module.Calls, call => Assert.IsType<ElementReference>(call[0]));
        var intent = WorkspaceGet(component, "_intent"); component.Dispose(); var disposedIntent = WorkspaceGet(component, "_intent");
        Assert.NotEqual(intent, disposedIntent); await component.DisposeAsync(); await component.DisposeAsync();
        Assert.Equal(disposedIntent, WorkspaceGet(component, "_intent")); Assert.Equal(1, js.Module.Disposals);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ExplicitHighlightSelectionOpensConversationWhileManualPlayersSurvivesClockUpdatesAndSameEventCanReopen(bool supportsSelectedEvent)
    {
        using var h = new Harness(); using var component = Workspace(h);
        var selected = h.Context with { SelectedEvent = supportsSelectedEvent ? new("selected-event", 60000, "1") : null };
        WorkspaceSet(component, "Context", selected); WorkspaceSet(component, "_question", "Keep my unsent question"); component.Apply();
        Assert.False((bool)WorkspaceGet(component, "_conversationOpen")!); // No initial default switch.
        WorkspaceSet(component, "EventSelectionVersion", 1L); component.Apply();
        Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!); Assert.False((bool)WorkspaceGet(component, "_focusPanel")!);
        await WorkspaceInvoke(component, "ShowPlayers");
        for (var i = 1; i <= 3; i++)
        {
            WorkspaceSet(component, "Context", selected with { Query = selected.Query with { CursorMs = selected.Query.CursorMs + 100 * i } }); component.Apply();
            Assert.False((bool)WorkspaceGet(component, "_conversationOpen")!);
        }
        WorkspaceSet(component, "EventSelectionVersion", 2L); component.Apply();
        Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!); Assert.False((bool)WorkspaceGet(component, "_focusPanel")!);
        Assert.Equal("Keep my unsent question", WorkspaceGet(component, "_question"));
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages); Assert.Empty(h.Session.Turns);
    }

    [Fact]
    public async Task ReplayCallSiteKeepsConversationSequenceAndSlotsAcrossMissingViewAndDataset()
    {
        using var h = new Harness(); await using var replay = new RenderableReplay(); int? sequence = null;
        WorkspaceSet(replay, "_eventSelectionVersion", 7L);
        foreach (var ready in new[] { false, true, false, true })
        {
            typeof(FootballReplay).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(replay, ready ? h.Api.View : null);
            using var tree = new RenderTreeBuilder(); replay.Render(tree); var frames = Frames(tree);
            var component = Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(MatchConversation));
            if (sequence is { } expected) Assert.Equal(expected, component.Sequence); else sequence = component.Sequence;
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "PitchSide" && Equals(f.AttributeValue, true));
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "PitchContent" && f.AttributeValue is RenderFragment);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "RosterContent" && f.AttributeValue is RenderFragment);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == nameof(MatchConversation.EventSelectionVersion) && Equals(f.AttributeValue, 7L));
        }
    }

    private static RenderableConversation Workspace(Harness h)
    {
        var component = new RenderableConversation(); WorkspaceSet(component, "Session", h.Session); WorkspaceSet(component, "Context", h.Context); WorkspaceSet(component, "PitchSide", true); component.Apply(); return component;
    }
    private static void WorkspaceSet(object target, string name, object? value)
    {
        var type = target is FootballReplay ? typeof(FootballReplay) : typeof(MatchConversation);
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (property is not null) property.SetValue(target, value); else type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }
    private static object? WorkspaceGet(object target, string name) => typeof(MatchConversation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static Task WorkspaceInvoke(object target, string name, params object?[] args) => typeof(MatchConversation).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args) as Task ?? Task.CompletedTask;
    private static Dictionary<string, object?> WorkspaceAttributes(RenderTreeFrame[] frames, int index)
    {
        var result = new Dictionary<string, object?>();
        for (var i = index + 1; i < frames.Length && frames[i].FrameType == RenderTreeFrameType.Attribute; i++) result[frames[i].AttributeName] = frames[i].AttributeValue;
        return result;
    }
    private static int WorkspaceIndex(RenderTreeFrame[] frames, string name) => Enumerable.Range(0, frames.Length).Single(i => frames[i].FrameType == RenderTreeFrameType.Element && WorkspaceAttributes(frames, i).GetValueOrDefault("class")?.ToString()?.Split(' ').Contains(name) == true);
    private static Dictionary<string, object?> WorkspaceElement(RenderTreeFrame[] frames, string name) => WorkspaceAttributes(frames, WorkspaceIndex(frames, name));
    private static Dictionary<string, object?>[] WorkspaceChildren(RenderTreeFrame[] frames, string name, string element)
    {
        var start = WorkspaceIndex(frames, name); return Enumerable.Range(start + 1, frames[start].ElementSubtreeLength - 1)
            .Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == element).Select(i => WorkspaceAttributes(frames, i)).ToArray();
    }
    private sealed class WorkspaceJs : IJSRuntime
    {
        public WorkspaceModule Module { get; } = new(); public int Imports { get; private set; } public int FocusCalls { get; private set; } public bool LastPreventScroll { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        { try { return InvokeAsync<TValue>(identifier, default, args); } catch (JSException) { throw; } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args)
        {
            if (identifier == "Blazor._internal.domWrapper.focus") { FocusCalls++; LastPreventScroll = (bool)args![1]!; return ValueTask.FromResult(default(TValue)!); }
            Assert.Equal("import", identifier); Assert.Equal("./Components/Experience/LeagueConversation.razor.js", Assert.Single(args!)); Imports++; return ValueTask.FromResult((TValue)(object)Module);
        }
    }
    private sealed class WorkspaceModule : IJSObjectReference
    {
        public List<object?[]> Calls { get; } = []; public int Disposals { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        { try { return InvokeAsync<TValue>(identifier, default, args); } catch (JSException) { throw; } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) { Assert.Equal("updateThread", identifier); Calls.Add(args!); return ValueTask.FromResult(default(TValue)!); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
#pragma warning restore BL0006
