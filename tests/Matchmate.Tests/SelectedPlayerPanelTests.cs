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

public sealed class SelectedPlayerPanelTests
{
    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task SelectedSummaryPrecedesRosterScrollAndKeepsEvidenceInNativeCollapsedDetails(MatchLanguage language)
    {
        var view = PreparedGeneratedDistanceStore.LoadEmbedded().Attach(await GeneratedPlayerDistanceTests.ExistingView(2820000));
        var before = JsonSerializer.Serialize(view);
        var html = await Render(view, language, "person-pl-reference-brentford-kayode");
        var sectionStart = html.IndexOf("<section class=\"selected-player\"", StringComparison.Ordinal);
        var sectionEnd = html.IndexOf("</section>", sectionStart, StringComparison.Ordinal);
        var rosterStart = html.IndexOf("<div class=\"roster-scroll\"", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && sectionEnd < rosterStart);
        var selected = html[sectionStart..sectionEnd]; var details = selected.IndexOf("<details", StringComparison.Ordinal);
        var summary = selected[..details]; var evidence = selected[details..];
        Assert.Contains("Michael Kayode", summary); Assert.Contains("Brentford", summary); Assert.Matches(">33</b>", summary);
        Assert.Contains(ReplayText.Get(language, "RoleStartingPosition"), summary);
        Assert.Single(Regex.Matches(selected, Regex.Escape(ReplayText.Get(language, "RoleStartingPosition"))));
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceLabel"), summary);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceShortExplanation"), summary);
        var cm = view.GeneratedDistance!.Players.Single(p => p.PersonId == "person-pl-reference-brentford-kayode").CumulativeCentimetres!.Value;
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceMetres", Math.Round(cm / 100m, 0, MidpointRounding.AwayFromZero).ToString("N0", MatchLanguages.Culture(language))), summary);
        Assert.Contains(ReplayText.Get(language, "SelectedPlayerDetails"), evidence);
        Assert.Contains(ReplayText.Get(language, "RoleSourceBrentford"), evidence);
        Assert.Contains(ReplayText.Get(language, "RoleEvidenceBoundary"), evidence);
        Assert.Contains("external-link-icon", evidence);
        Assert.Contains(view.Frame!.Players.Single(p => p.SubjectId == "person-pl-reference-brentford-kayode").SlotId!, evidence);
        Assert.DoesNotMatch("<details[^>]*\\bopen(?:[ =>])", evidence);
        Assert.Contains("data-roster=\"played\"", html[rosterStart..]); Assert.Contains("data-roster=\"all\"", html[rosterStart..]);
        Assert.Equal(before, JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task UnverifiedAndUnavailableRemainVisibleWithoutInventedRoleOrZeroWhileNoSelectionHasNoSummary()
    {
        var view = GeneratedPlayerDistanceTests.UnitStore().Attach(GeneratedPlayerDistanceTests.UnitView(GeneratedPlayerDistanceTests.UnitDataset(), 600));
        var html = await Render(view, MatchLanguage.English, GeneratedPlayerDistanceTests.Bench);
        var selected = html[..html.IndexOf("<div class=\"roster-scroll\"", StringComparison.Ordinal)];
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "RoleUnverified"), selected);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "GeneratedDistanceUnavailable"), selected);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "GeneratedDistanceMetres", "0"), selected);
        Assert.DoesNotContain("external-link-icon", selected);
        var noMetric = await Render(view with { GeneratedDistance = null }, MatchLanguage.English, GeneratedPlayerDistanceTests.Bench);
        Assert.DoesNotContain("selected-generated-distance", noMetric); Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "GeneratedDistanceShortExplanation"), noMetric);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "NotActiveSample"), noMetric);
        var noFixture = await Render(view with { Dataset = view.Dataset with { Fixture = null } }, MatchLanguage.English, GeneratedPlayerDistanceTests.Bench);
        Assert.Contains("Unused bench", noFixture); Assert.Contains(ReplayText.Get(MatchLanguage.English, "Unknown"), noFixture);
        var noSelection = await Render(view, MatchLanguage.English, null);
        Assert.DoesNotContain("<section class=\"selected-player\"", noSelection); Assert.Contains("<div class=\"roster-scroll\"", noSelection);
    }

    private static async Task<string> Render(FootballView view, MatchLanguage language, string? selected)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = b => { b.OpenComponent<PlayerRoster>(0); b.AddAttribute(1, nameof(PlayerRoster.View), view); b.AddAttribute(2, nameof(PlayerRoster.SelectedPerson), selected); b.CloseComponent(); };
            var output = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CascadingValue<MatchLanguage>.Value)] = language, [nameof(CascadingValue<MatchLanguage>.ChildContent)] = child }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
