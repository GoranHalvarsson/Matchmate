using System.Net;
using System.Reflection;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.HistoricalChat;
using Matchmate.Web.LeagueChat;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Inspect real components, without a replacement browser host.
public sealed partial class MatchConversationTests
{
    [Theory]
    [InlineData(MatchLanguage.English, false)] [InlineData(MatchLanguage.English, true)]
    [InlineData(MatchLanguage.Swedish, false)] [InlineData(MatchLanguage.Swedish, true)]
    [InlineData(MatchLanguage.Spanish, false)] [InlineData(MatchLanguage.Spanish, true)]
    public async Task PendingReplyHasAnswerCueComposerFeedbackAndOneIndependentAnnouncer(MatchLanguage language, bool pitchSide)
    {
        using var h = new Harness(); using var component = Workspace(h);
        WorkspaceSet(component, "Language", language); WorkspaceSet(component, "PitchSide", pitchSide); component.Apply();
        WorkspaceSet(component, "_question", "Keep this draft");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkspaceSet(component, "OnAsking", EventCallback.Factory.Create(new object(), () => gate.Task));
        var asking = WorkspaceInvoke(component, "SubmitAsync"); Assert.False(asking.IsCompleted);
        if (pitchSide) await WorkspaceInvoke(component, "ShowPlayers");
        using (var tree = new RenderTreeBuilder())
        {
            component.Render(tree);
            ChatResponsePendingTests.AssertPending(tree, language, "chat-pending-turn", pitchSide ? "chat-entry-pending" : "chat-composer-pending");
        }
        await WorkspaceInvoke(component, "Cancel"); gate.SetResult(); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new RenderTreeBuilder(); component.Render(cancelled);
        ChatResponsePendingTests.AssertCleared(cancelled, language);
        Assert.Equal("Keep this draft", WorkspaceGet(component, "_question")); Assert.Empty(h.Api.Queries); Assert.Empty(h.Session.Turns);
    }

    [Theory]
    [InlineData("source")] [InlineData("chart")] [InlineData("visual")]
    public async Task ReplayActionEntryPointsShowSamePendingCueBeforeEvidenceReturns(string action)
    {
        using var h = new Harness(); using var component = Workspace(h);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkspaceSet(component, "OnAsking", EventCallback.Factory.Create(new object(), () => gate.Task));
        var asking = action switch
        {
            "source" => WorkspaceInvoke(component, "SendAsync", MatchSourceQuestionId.Summary),
            "chart" => WorkspaceInvoke(component, "SendChartAsync"),
            _ => WorkspaceInvoke(component, "ShowEventAsync")
        };
        Assert.False(asking.IsCompleted);
        using (var tree = new RenderTreeBuilder())
        {
            component.Render(tree); ChatResponsePendingTests.AssertPending(tree, MatchLanguage.English, "chat-pending-turn", "chat-entry-pending");
        }
        await WorkspaceInvoke(component, "Cancel"); gate.SetResult(); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }
}

public sealed class ChatResponsePendingTests
{
    [Theory]
    [InlineData(MatchLanguage.English, "Matchmate is responding…", "Responding…", false)]
    [InlineData(MatchLanguage.English, "Matchmate is responding…", "Responding…", true)]
    [InlineData(MatchLanguage.Swedish, "Matchmate svarar…", "Svarar…", false)]
    [InlineData(MatchLanguage.Swedish, "Matchmate svarar…", "Svarar…", true)]
    [InlineData(MatchLanguage.Spanish, "Matchmate está respondiendo…", "Respondiendo…", false)]
    [InlineData(MatchLanguage.Spanish, "Matchmate está respondiendo…", "Respondiendo…", true)]
    public async Task SharedCueIsLocalizedAndDecorativeSoItDoesNotDuplicateAnnouncements(MatchLanguage language, string label, string button, bool compact)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ChatResponsePending>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(ChatResponsePending.Language)] = language, [nameof(ChatResponsePending.Compact)] = compact }))).ToHtmlString()));
        Assert.Contains(label, html); Assert.Equal(button, ConversationText.Get(language, "Responding"));
        Assert.Contains("aria-hidden=\"true\"", html); Assert.Contains($"lang=\"{MatchLanguages.Code(language)}\"", html);
        Assert.Contains("chat-response-dots", html); Assert.DoesNotContain("role=\"status\"", html); Assert.DoesNotContain("aria-live", html);
        Assert.Equal(compact, html.Contains("is-compact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task LeagueCueAndCancelStayAvailableWhileConversationIsCollapsed(MatchLanguage language)
    {
        using var session = LeagueChatFixtures.Session(); using var component = new LeagueConversation();
        Set(component, "Session", session); Set(component, "Context", LeagueChatContext.From(LeagueChatFixtures.Snapshot())); Set(component, "Language", language); Apply(component);
        Set(component, "_question", "Keep this league draft");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(component, "OnAsking", EventCallback.Factory.Create(new object(), () => gate.Task));
        var asking = Invoke(component, "SubmitAsync"); Assert.False(asking.IsCompleted);
        await Invoke(component, "ToggleConversation");
        using (var tree = Render(component)) AssertPending(tree, language, "league-pending-turn", "league-pending");
        await Invoke(component, "Cancel"); gate.SetResult(); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = Render(component); AssertCleared(cancelled, language);
        Assert.Equal("Keep this league draft", Field(component, "_question")); Assert.Empty(session.Turns);
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task HistoricalCueAndComposerFeedbackUseTheExistingPendingLifecycle(MatchLanguage language)
    {
        var model = new HistoricalChatSessionTests.Model { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new HistoricalChatSession(new HistoricalChatSessionTests.Auth(), HistoricalChatFixtures.Evidence(), new HistoricalChatSessionTests.Router("overview"), model, Options.Create(new MatchChatOptions()));
        await using var component = new HistoricalConversation();
        Set(component, "Session", session); Set(component, "Context", HistoricalChatContext.From(HistoricalChatFixtures.Fixture())); Set(component, "Language", language); Apply(component);
        Set(component, "_question", "Keep this match draft");
        var asking = Invoke(component, "SubmitAsync"); await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.False(asking.IsCompleted);
        using (var tree = Render(component)) AssertPending(tree, language, "historical-chat-pending", "historical-composer-pending");
        await Invoke(component, "Cancel"); await asking.WaitAsync(TimeSpan.FromSeconds(2));
        model.Delayed.SetResult(new(HistoricalChatFixtures.Reply("The result is recorded.", "fixture"), []));
        using var cancelled = Render(component); AssertCleared(cancelled, language);
        Assert.Equal("Keep this match draft", Field(component, "_question")); Assert.Empty(session.Turns);
    }

    [Fact]
    public void SharedMotionHasStaticReducedMotionFallbackAndFlexibleMobileSizing()
    {
        var css = File.ReadAllText(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "Components", "Experience", "ChatResponsePending.razor.css"));
        Assert.Contains("prefers-reduced-motion:reduce", css); Assert.Contains("animation:none", css);
        Assert.Contains("max-width:100%", css); Assert.Contains("overflow-wrap:anywhere", css);
    }

    internal static void AssertPending(RenderTreeBuilder tree, MatchLanguage language, string transcriptRow, string composerRow)
    {
        var frames = Frames(tree);
        var status = Assert.Single(Elements(frames), i => Equals(Attributes(frames, i).GetValueOrDefault("role"), "status"));
        var log = Assert.Single(Elements(frames), i => Equals(Attributes(frames, i).GetValueOrDefault("role"), "log"));
        Assert.True(status < log || status >= log + frames[log].ElementSubtreeLength);
        Assert.Equal("true", Attributes(frames, status)["aria-atomic"]);
        Assert.Contains(ConversationText.Get(language, "ResponsePending"), Text(frames, status));
        Assert.Equal(2, frames.Count(frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(ChatResponsePending)));
        foreach (var row in new[] { transcriptRow, composerRow })
        {
            var index = Assert.Single(Elements(frames), i => HasClass(frames, i, row));
            var cue = Assert.Single(Enumerable.Range(index + 1, frames[index].ElementSubtreeLength - 1), i => frames[i].FrameType == RenderTreeFrameType.Component && frames[i].ComponentType == typeof(ChatResponsePending));
            Assert.Equal(row == composerRow, Equals(Attributes(frames, cue).GetValueOrDefault(nameof(ChatResponsePending.Compact)), true));
        }
        var composer = Assert.Single(Elements(frames), i => HasClass(frames, i, composerRow));
        Assert.Single(Elements(frames), i => i > composer && i < composer + frames[composer].ElementSubtreeLength && frames[i].ElementName == "button");
        Assert.DoesNotContain(Elements(frames), i => i < composer && i + frames[i].ElementSubtreeLength > composer && Equals(Attributes(frames, i).GetValueOrDefault("hidden"), true));
        Assert.DoesNotContain(Elements(frames), i => i < status && i + frames[i].ElementSubtreeLength > status && Equals(Attributes(frames, i).GetValueOrDefault("hidden"), true));
        var submit = Assert.Single(Elements(frames), i => frames[i].ElementName == "button" && Equals(Attributes(frames, i).GetValueOrDefault("type"), "submit"));
        Assert.Equal(true, Attributes(frames, submit)["disabled"]); Assert.Contains(ConversationText.Get(language, "Responding"), Text(frames, submit));
    }

    internal static void AssertCleared(RenderTreeBuilder tree, MatchLanguage language)
    {
        var frames = Frames(tree);
        Assert.DoesNotContain(frames, frame => frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(ChatResponsePending));
        Assert.DoesNotContain(frames, frame => frame.FrameType == RenderTreeFrameType.Text && frame.TextContent == ConversationText.Get(language, "ResponsePending"));
    }

    private static IEnumerable<int> Elements(RenderTreeFrame[] frames) => Enumerable.Range(0, frames.Length).Where(i => frames[i].FrameType == RenderTreeFrameType.Element);
    private static Dictionary<string, object?> Attributes(RenderTreeFrame[] frames, int index)
    {
        var values = new Dictionary<string, object?>();
        for (var i = index + 1; i < frames.Length && frames[i].FrameType == RenderTreeFrameType.Attribute; i++) values[frames[i].AttributeName] = frames[i].AttributeValue;
        return values;
    }
    private static bool HasClass(RenderTreeFrame[] frames, int index, string name) => Attributes(frames, index).GetValueOrDefault("class")?.ToString()?.Split(' ').Contains(name) == true;
    private static string Text(RenderTreeFrame[] frames, int index) => string.Concat(frames.Skip(index + 1).Take(frames[index].ElementSubtreeLength - 1).Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent));
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static RenderTreeBuilder Render(object target) { var tree = new RenderTreeBuilder(); target.GetType().GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [tree]); return tree; }
    private static void Set(object target, string name, object? value) { var type = target.GetType(); if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } property) property.SetValue(target, value); else type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value); }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static Task Invoke(object target, string name) => (Task?)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null) ?? Task.CompletedTask;
    private static void Apply(object target) => target.GetType().GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
#pragma warning restore BL0006
