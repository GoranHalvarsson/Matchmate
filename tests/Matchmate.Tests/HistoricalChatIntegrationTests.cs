using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.HistoricalChat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Matchmate.Web.Chat;
using Xunit;

namespace Matchmate.Tests;

public sealed class HistoricalChatIntegrationTests
{
    [Fact]
    public async Task ConversationRendersUserTextEscapedAndRetainsTheBoundThread()
    {
        var auth = new HistoricalChatSessionTests.Auth(); var context = HistoricalChatContext.From(HistoricalChatFixtures.Fixture());
        using var session = new HistoricalChatSession(auth, HistoricalChatFixtures.Evidence(), new HistoricalChatSessionTests.Router("overview"), new HistoricalChatSessionTests.Model(), Options.Create(new MatchChatOptions()));
        session.SetContext(context); Assert.True((await session.AskAsync(context, "<script>alert('unsafe')</script>")).Accepted);
        using var services = new ServiceCollection().AddLogging().AddSingleton(session).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<HistoricalConversation>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(HistoricalConversation.Context)] = context }))).ToHtmlString());
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>", html); Assert.Single(session.Turns);
    }
    [Fact]
    public void RegistrationDisablesRedirectsAndSeparatesCircuitHistories()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddOptions();
        services.AddSingleton<IHostEnvironment>(new Environment()); services.AddSingleton<MatchChatDiagnostics>();
        services.AddSingleton<AuthenticationStateProvider>(new HistoricalChatSessionTests.Auth());
        services.AddSingleton(new ReplayApiClient(new HttpClient())); services.AddHistoricalMatchChat();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        foreach (var name in new[] { nameof(IHistoricalWorkflowRouter), nameof(IHistoricalChatModel) })
        {
            var handler = factory.CreateHandler(name); while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
            Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        }
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<HistoricalChatSession>(), first.ServiceProvider.GetRequiredService<HistoricalChatSession>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<HistoricalChatSession>(), second.ServiceProvider.GetRequiredService<HistoricalChatSession>());
    }
    [Fact, Trait("RequiresLocalData", "true")]
    public async Task ActualPinnedHistoricalAndOngoingResultsPassTheSameEvidenceBinding()
    {
        var model = await LeagueReferenceStore.Local(LocalPaths.FindRoot()).ReadAsync(default);
        foreach (var fixture in new[] { model.Match("2016-17", "southampton:stoke"), model.Match("2026-27", model.Snapshot("2026-27", null).Results[0].MatchKey) })
        {
            var evidence = new HistoricalEvidence((s, m, _) => Task.FromResult(model.Match(s, m)), (s, i, c, _) => Task.FromResult(model.Snapshot(s, i, c)));
            var loaded = await evidence.LoadAsync(HistoricalChatContext.From(fixture), default);
            var prepared = HistoricalEvidence.Prepare(loaded.Match, loaded.Clubs, "overview", null);
            Assert.Equal(4, prepared.Evidence.Length); Assert.All(prepared.Evidence, e => Assert.NotEmpty(e.NumericTokens));
            Assert.True(prepared.Evidence.Sum(e => e.Text.Length) < 32000);
            if (fixture.Season.Status == "ongoing") Assert.Contains(fixture.Season.ResultsThrough!, prepared.Evidence[0].Text);
            else Assert.Contains("2016-12-14", prepared.Evidence.Single(e => e.Id == "reference").Text);
        }
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Synthetic tests";
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
