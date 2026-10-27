using System.Reflection;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect the actual component render tree without a substitute browser host.
public sealed class MobileReplayLayoutTests
{
    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public void HighlightsStartWithANativeCollapsedButtonAndSpoilerFreePreview(MatchLanguage language)
    {
        var component = Highlights(); Set(component, nameof(MatchHighlights.Language), language);
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        var frames = Frames(tree);
        var button = Element(frames, "button", "class", "highlight-disclosure");
        Assert.Contains(button, f => Attribute(f, "type", "button"));
        Assert.Contains(button, f => Attribute(f, "aria-expanded", "false"));
        var target = Assert.Single(button, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "aria-controls").AttributeValue;
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "id" && Equals(f.AttributeValue, target));
        var preview = Text(button);
        Assert.Contains(ReplayText.Get(language, "MatchHighlights"), preview);
        Assert.Contains(ReplayText.Get(language, "FullMatchSpoilerWarning"), preview);
        Assert.DoesNotContain("Future scorer", preview);
        Assert.DoesNotContain("2–0", preview);
        Assert.DoesNotContain(ReplayText.Get(language, "GoalNumber", 2), preview);
    }

    [Fact]
    public async Task ReopeningKeepsFilterAndPinsAndSelectionClosesBeforeNavigatingToTheSameEvent()
    {
        var component = Highlights();
        var goal = ((IReadOnlyList<ReplayHighlight>)Get(component, nameof(MatchHighlights.Highlights))!)[0];
        Call(component, "ToggleHighlights"); Call(component, "Pin", goal.RecordId); Set(component, "_filter", "pinned");
        Call(component, "ToggleHighlights"); Call(component, "ToggleHighlights");
        Assert.Equal("pinned", Get(component, "_filter"));
        Assert.Contains(goal.RecordId, (HashSet<string>)Get(component, "_pins")!);
        ReplayHighlight? navigated = null;
        Set(component, nameof(MatchHighlights.Navigate), EventCallback.Factory.Create<ReplayHighlight>(new object(), (ReplayHighlight selected) =>
        {
            Assert.False((bool)Get(component, "_mobileExpanded")!);
            navigated = selected;
        }));
        await (Task)Call(component, "NavigateAsync", goal)!;
        Assert.Same(goal, navigated);
        Assert.Equal("pinned", Get(component, "_filter"));
        Assert.Contains(goal.RecordId, (HashSet<string>)Get(component, "_pins")!);
        using var tree = new RenderTreeBuilder(); component.Render(tree);
        Assert.Contains(Element(Frames(tree), "button", "class", "highlight-disclosure"), f => Attribute(f, "aria-expanded", "false"));
    }

    [Fact]
    public void ChangingRecordingClosesChooserAndRetainsExistingRecordingScopedPinReset()
    {
        var component = Highlights(); Call(component, "ToggleHighlights"); Call(component, "Pin", "future-goal");
        Set(component, "ContextKey", "other-recording"); component.Apply();
        Assert.False((bool)Get(component, "_mobileExpanded")!);
        Assert.Empty((HashSet<string>)Get(component, "_pins")!);
        Assert.Equal("goal", Get(component, "_filter"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F_MOB_001_ExpandedSelectionRestoresVisibleDisclosureOnceAfterRenderUnlessRecordingChanged(bool changeRecording)
    {
        var component = Highlights(); var js = new FocusJs();
        var disclosure = new ElementReference("highlights-disclosure", new WebElementReferenceContext(js));
        Set(component, "_disclosure", disclosure);
        Call(component, "ToggleHighlights");
        var goal = ((IReadOnlyList<ReplayHighlight>)Get(component, nameof(MatchHighlights.Highlights))!)[0];
        await (Task)Call(component, "NavigateAsync", goal)!;
        Assert.Empty(js.Calls); // The card must disappear before focus moves to the visible disclosure.
        if (changeRecording) { Set(component, "ContextKey", "other-recording"); component.Apply(); }
        await component.AfterRenderAsync(); await component.AfterRenderAsync();
        if (changeRecording) Assert.Empty(js.Calls);
        else
        {
            var focus = Assert.Single(js.Calls);
            Assert.Equal(disclosure, focus[0]);
            Assert.False((bool)focus[1]!); // Native focus may scroll the disclosure into view.
        }
    }

    [Fact]
    public async Task F_MOB_001_SelectionWithoutAnExpandedMobileChooserDoesNotMoveDesktopFocus()
    {
        var component = Highlights(); var js = new FocusJs();
        Set(component, "_disclosure", new ElementReference("hidden-desktop-disclosure", new WebElementReferenceContext(js)));
        var goal = ((IReadOnlyList<ReplayHighlight>)Get(component, nameof(MatchHighlights.Highlights))!)[0];
        await (Task)Call(component, "NavigateAsync", goal)!;
        await component.AfterRenderAsync();
        Assert.Empty(js.Calls);
    }

    private static RenderableHighlights Highlights()
    {
        var component = new RenderableHighlights();
        Set(component, "ContextKey", "recording");
        Set(component, "Highlights", new ReplayHighlight[] { new("future-goal", 60000, "goal", "Source goal", "1", "Future scorer", ScoreAfter: "2–0", GoalNumber: 2) });
        component.Apply(); return component;
    }

    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static RenderTreeFrame[] Element(RenderTreeFrame[] frames, string tag, string attribute, string value)
    {
        for (var i = 0; i < frames.Length; i++)
            if (frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == tag)
            {
                var element = frames.Skip(i).Take(frames[i].ElementSubtreeLength).ToArray();
                if (element.Skip(1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).Any(f => Attribute(f, attribute, value))) return element;
            }
        throw new InvalidOperationException($"Element {tag}[{attribute}={value}] was not rendered.");
    }
    private static bool Attribute(RenderTreeFrame frame, string name, string value) => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name && Equals(frame.AttributeValue, value);
    private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(f => f.FrameType == RenderTreeFrameType.Text ? f.TextContent : f.FrameType == RenderTreeFrameType.Markup ? f.MarkupContent : ""));
    private static object? Get(object component, string name) => typeof(MatchHighlights).GetProperty(name)?.GetValue(component) ?? typeof(MatchHighlights).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
    private static void Set(object component, string name, object value)
    {
        if (typeof(MatchHighlights).GetProperty(name) is { } property) property.SetValue(component, value);
        else typeof(MatchHighlights).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
    }
    private static object? Call(object component, string name, params object[] arguments) => typeof(MatchHighlights).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, arguments);
    private sealed class RenderableHighlights : MatchHighlights
    {
        public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree);
        public void Apply() => OnParametersSet();
        public Task AfterRenderAsync() => OnAfterRenderAsync(false);
    }
    private sealed class FocusJs : IJSRuntime
    {
        public List<object?[]> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        { try { return InvokeAsync<TValue>(identifier, default, args); } catch (JSException) { throw; } }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("Blazor._internal.domWrapper.focus", identifier);
            Calls.Add(args!);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
#pragma warning restore BL0006
