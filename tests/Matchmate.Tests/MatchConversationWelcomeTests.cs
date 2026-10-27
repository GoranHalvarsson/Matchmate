using System.Net;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual component rendering and existing production-session fixtures.
public sealed partial class MatchConversationTests
{
    [Theory]
    [InlineData(MatchLanguage.English, "match")] [InlineData(MatchLanguage.English, "player")] [InlineData(MatchLanguage.English, "event")]
    [InlineData(MatchLanguage.Swedish, "match")] [InlineData(MatchLanguage.Swedish, "player")] [InlineData(MatchLanguage.Swedish, "event")]
    [InlineData(MatchLanguage.Spanish, "match")] [InlineData(MatchLanguage.Spanish, "player")] [InlineData(MatchLanguage.Spanish, "event")]
    public void PitchWelcomeUsesEventBeforePlayerThenFixtureWithoutRequestingAnAnswer(MatchLanguage language, string selection)
    {
        using var h = new Harness(); using var component = Workspace(h);
        const string player = "Selected player";
        WorkspaceSet(component, "Language", language);
        WorkspaceSet(component, "FixtureLabel", "Home v Away");
        WorkspaceSet(component, "Title", selection == "match" ? ReplayText.Get(language, "ThisMatch") : player);
        if (selection == "event") WorkspaceSet(component, "Context", h.Context with { SelectedEvent = new("selected-event", 60000, "1") });
        component.Apply();
        using var tree = new RenderTreeBuilder(); component.Render(tree); var frames = Frames(tree);
        var text = WelcomeText(frames);
        Assert.Contains(ReplayText.Get(language, "WorkspaceIntro"), text);
        Assert.Contains(ReplayText.Get(language, "WorkspaceWelcomeHeading"), text);
        Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "h3");
        var expected = selection switch
        {
            "event" => ReplayText.Get(language, "WorkspaceWelcomeEvent"),
            "player" => ReplayText.Get(language, "WorkspaceWelcomePlayer", player),
            _ => ReplayText.Get(language, "WorkspaceWelcomeMatch", "Home v Away")
        };
        Assert.Contains(expected, text);
        if (selection != "event") Assert.DoesNotContain(ReplayText.Get(language, "WorkspaceWelcomeEvent"), text);
        if (selection != "player") Assert.DoesNotContain(ReplayText.Get(language, "WorkspaceWelcomePlayer", player), text);
        Assert.Contains(ReplayText.Get(language, "WorkspaceExplainMoment"), text);
        Assert.Contains(ChatText.Get(language, "Chart.CompareTeams"), text);
        if (selection == "event")
        {
            Assert.Contains(ReplayText.Get(language, "GoalSelectionNote"), text);
            Assert.Contains(ReplayText.Get(language, "VisualGoalQuestion"), text);
        }
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
        Assert.False((bool)WorkspaceGet(component, "_conversationOpen")!);
    }

    [Theory]
    [InlineData("missing")] [InlineData("updating")] [InlineData("pending")] [InlineData("standalone")] [InlineData("standalone-missing")]
    public void WelcomeStaysOutOfUnavailablePendingAndStandaloneStates(string state)
    {
        using var h = new Harness(); using var component = Workspace(h);
        if (state is "missing" or "standalone-missing") WorkspaceSet(component, "Context", null);
        if (state == "updating") WorkspaceSet(component, "Updating", true);
        if (state.StartsWith("standalone", StringComparison.Ordinal)) WorkspaceSet(component, "PitchSide", false);
        component.Apply();
        if (state == "pending") WorkspaceSet(component, "_pending", true);
        using var tree = new RenderTreeBuilder(); component.Render(tree); var frames = Frames(tree); var text = WelcomeText(frames);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "WorkspaceIntro"), text);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "WorkspaceWelcomeHeading"), text);
        if (state is "missing" or "updating")
        {
            Assert.Contains(ReplayText.Get(MatchLanguage.English, state == "missing" ? "ChatWaiting" : "ChatUpdating"), text);
            Assert.All(WorkspaceChildren(frames, "chat-quick-questions", "button"), action => Assert.Equal(true, action["disabled"]));
        }
        if (state == "standalone") Assert.Contains(ReplayText.Get(MatchLanguage.English, "ChatEmpty"), text);
        if (state == "standalone-missing") Assert.Contains(ReplayText.Get(MatchLanguage.English, "ChatWaiting"), text);
        Assert.Single(frames, f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "textarea");
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    [Fact]
    public async Task FriendlySummaryButtonStillSendsSourceSummaryAndRemovesTheEmptyWelcome()
    {
        using var h = new Harness(); using var component = Workspace(h);
        using (var tree = new RenderTreeBuilder())
        {
            component.Render(tree);
            var summary = WorkspaceChildren(Frames(tree), "chat-quick-questions", "button")[0];
            await Assert.IsType<Func<Task>>(summary["onclick"])();
        }
        var turn = Assert.Single(h.Session.Turns);
        Assert.True(turn.EvidenceOnly);
        Assert.Equal(MatchSourceQuestions.DisplayQuestion(MatchSourceQuestionId.Summary, MatchLanguage.English), turn.Question);
        Assert.Single(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
        using var rendered = new RenderTreeBuilder(); component.Render(rendered); var text = WelcomeText(Frames(rendered));
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "WorkspaceIntro"), text);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "WorkspaceWelcomeHeading"), text);
        Assert.True((bool)WorkspaceGet(component, "_conversationOpen")!);
    }

    [Fact]
    public async Task PlayerAndFixtureWelcomeNamesAreHtmlEncodedByTheRealComponent()
    {
        const string untrusted = "Player <img src=x onerror=alert(1)> & friends";
        using var h = new Harness();
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddOptions();
        collection.AddSingleton(h.Session); collection.AddSingleton<MatchChatDiagnostics>();
        collection.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
        collection.AddSingleton<IJSRuntime>(new WorkspaceJs());
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new ComponentTestHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var player in new[] { true, false })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<MatchConversation>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["PitchSide"] = true, ["Context"] = h.Context,
                    ["Title"] = player ? untrusted : "this match", ["FixtureLabel"] = untrusted
                }));
                return root.ToHtmlString();
            });
            Assert.DoesNotContain("<img", html);
            Assert.Contains("&lt;img", html);
            Assert.Contains(ReplayText.Get(MatchLanguage.English, player ? "WorkspaceWelcomePlayer" : "WorkspaceWelcomeMatch", untrusted), WebUtility.HtmlDecode(html));
        }
        Assert.Empty(h.Api.Queries); Assert.Empty(h.Router.Inputs); Assert.Empty(h.Model.Messages);
    }

    private static string WelcomeText(RenderTreeFrame[] frames) => string.Join(' ', frames.Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
}
#pragma warning restore BL0006
