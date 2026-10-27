using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Matchmate.Tests;

public sealed class GeneratedPlayerDistanceUiTests
{
    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task ActiveAllAndSelectedShowLocalizedQualifiedDistanceWithoutMutatingContext(MatchLanguage language)
    {
        var raw = GeneratedPlayerDistanceTests.UnitView(GeneratedPlayerDistanceTests.UnitDataset(), 600);
        var view = GeneratedPlayerDistanceTests.UnitStore().Attach(raw) with { Frame = new("unit", null,
            [GeneratedPlayerDistanceTests.Observation(GeneratedPlayerDistanceTests.Incoming)], GeneratedPlayerDistanceTests.Observation("ball"), [GeneratedPlayerDistanceTests.Incoming], "generated-complete-model") };
        var before = JsonSerializer.Serialize(view); var html = await Render(view, language, GeneratedPlayerDistanceTests.Player);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceLabel"), html);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceExplanation"), html);
        Assert.Contains(ReplayText.Get(language, "AtAcceptedReplay", "00:00"), html);
        Assert.Equal(5, Regex.Matches(html, "class=\"generated-distance-value\"").Count); // One active + one departed with movement, plus all three people.
        Assert.Contains(ReplayText.Get(language, "RosterOffPitch"), html);
        Assert.Contains(ReplayText.Get(language, "CurrentOnPitch", "1"), html);
        Assert.Contains("selected-generated-distance", html); Assert.Contains(ReplayText.Get(language, "GeneratedDistanceMetres", "1"), html);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceUnavailable"), html);
        Assert.Contains("Unused bench", html); Assert.Contains("Starter", html); Assert.Contains("Substitute", html);
        Assert.Equal(before, JsonSerializer.Serialize(view));
        var unavailable = await Render(view, language, GeneratedPlayerDistanceTests.Bench);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceLabel") + ": " + ReplayText.Get(language, "GeneratedDistanceUnavailable"), unavailable);
        var noPack = await Render(view with { GeneratedDistance = null }, language, GeneratedPlayerDistanceTests.Player);
        Assert.DoesNotContain("generated-distance-value", noPack); Assert.DoesNotContain("generated-distance-note", noPack);
    }
    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task FullRosterUsesPersonTotalsForDepartedPlayersAndLocalWholeMetreFormatting(MatchLanguage language)
    {
        var view = PreparedGeneratedDistanceStore.LoadEmbedded().Attach(await GeneratedPlayerDistanceTests.ExistingView(5880000));
        var html = await Render(view, language, "person-pl-reference-brentford-damsgaard");
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceMetres", 4627m.ToString("N0", MatchLanguages.Culture(language))), html);
        Assert.Contains(ReplayText.Get(language, "AtAcceptedReplay", "98:00"), html);
        Assert.Equal(71, Regex.Matches(html, "class=\"generated-distance-value\"").Count); // All31 already appeared plus all40; departed players remain in the ranking.
        Assert.Contains(ReplayText.Get(language, "CurrentOnPitch", "22"), html);
        Assert.Contains(ReplayText.Get(language, "AllRoster", 40), html);
    }
    private static async Task<string> Render(FootballView view, MatchLanguage language, string selected)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment fragment = builder =>
            {
                builder.OpenComponent<PlayerRoster>(0); builder.AddAttribute(1, nameof(PlayerRoster.View), view);
                builder.AddAttribute(2, nameof(PlayerRoster.SelectedPerson), selected); builder.CloseComponent();
            };
            var component = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CascadingValue<MatchLanguage>.Value)] = language, [nameof(CascadingValue<MatchLanguage>.ChildContent)] = fragment }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
