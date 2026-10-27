using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class ReplayMatchHeaderTests
{
    [Theory]
    [InlineData(MatchLanguage.English, "17 October 2026")]
    [InlineData(MatchLanguage.Swedish, "17 oktober 2026")]
    [InlineData(MatchLanguage.Spanish, "17 octubre 2026")]
    public async Task HeaderShowsTheExplicitFixtureDateAndLocalDecorativeCrestsInEachLanguage(MatchLanguage language, string expectedDate)
    {
        var dataset = Dataset();
        var html = await RenderAsync(dataset, language);
        var heading = Assert.Single(Regex.Matches(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.Singleline).Cast<Match>());
        Assert.Contains("aria-label=\"" + ReplayText.Get(language, "Versus", "Brentford", "Liverpool") + "\"", heading.Value);
        Assert.Contains("Brentford", heading.Value);
        Assert.Contains("Liverpool", heading.Value);
        foreach (var club in new[] { "brentford", "liverpool" })
        {
            var path = "/assets/crests/" + club + "-2026.png";
            Assert.Contains("src=\"" + path + "\"", heading.Value);
            Assert.True(File.Exists(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "wwwroot", path.TrimStart('/'))));
        }
        Assert.Equal(2, Regex.Matches(heading.Value, @"<span\b[^>]*class=""replay-match-crest""[^>]*aria-hidden=""true""").Count);
        Assert.Equal(2, Regex.Matches(heading.Value, @"<img\b[^>]*alt=""""").Count);
        Assert.Contains("datetime=\"2026-10-17\"", html);
        Assert.Contains(expectedDate, html);
        Assert.Contains("Premier League", html);
        Assert.Contains(ReplayText.Get(language, "RetrospectiveReplay"), html);
        Assert.Contains(ReplayText.Get(language, "GeneratedMovementSource"), html);
        Assert.Contains(ReplayText.Get(language, "AudienceFan"), html);
        Assert.Contains(ReplayText.Get(language, "AudienceAnalyst"), html);
        Assert.Equal(2, Regex.Matches(html, @"<button\b").Count);
        Assert.DoesNotContain("00:00", html);
        Assert.DoesNotContain("UTC", html);
        Assert.DoesNotContain("BST", html);
        Assert.DoesNotContain("<a ", html);
    }

    [Theory]
    [InlineData("Brentford vs Chelsea")]
    [InlineData("Brentford – Liverpool")]
    [InlineData("Brentford – Liverpool · 17/10/2026")]
    [InlineData("Brentford – Liverpool · 10/17/2026")]
    [InlineData("Brentford – Liverpool · 2026-10-17T00:00:00Z")]
    [InlineData("Brentford – Liverpool · 31 February 2026")]
    [InlineData("Brentford – Liverpool · 17 October 2026 00:00 UTC")]
    [InlineData("Brentford – Liverpool · 17 October 2026 ")]
    [InlineData("Chelsea – Liverpool · 17 October 2026")]
    public async Task MissingMalformedAmbiguousOrTimestampLabelsDoNotInventACalendarDateOrKickoff(string label)
    {
        var dataset = Dataset();
        var html = await RenderAsync(dataset with { Fixture = dataset.Fixture! with { Label = label } });
        Assert.DoesNotContain("<time", html);
        Assert.DoesNotContain("replay-match-date", html);
        Assert.DoesNotContain("00:00", html);
        Assert.Contains("Brentford", html);
        Assert.Contains("Liverpool", html);
    }

    [Fact]
    public async Task ChelseaUsesItsExistingLocalCrestWithoutBorrowingLiverpoolsDate()
    {
        var dataset = Dataset();
        dataset = dataset with { Manifest = dataset.Manifest with { FixtureId = "fixture-pl-2645236" },
            Fixture = dataset.Fixture! with { Label = "Brentford vs Chelsea", Away = new("team-pl-reference-chelsea", "Chelsea") } };
        var html = await RenderAsync(dataset);
        Assert.Contains("src=\"/assets/crests/chelsea-2026.png\"", html);
        Assert.DoesNotContain("liverpool-2026.png", html);
        Assert.DoesNotContain("<time", html);
    }

    [Theory]
    [InlineData("complete-existing-generated-100ms-model", "GeneratedSource")]
    [InlineData("sparse-generated-windows-only", "GeneratedSource")]
    [InlineData("tracking_frame", "RecordedSource")]
    public async Task ActualHeaderRenderingPreservesEachExistingSourceDisclosure(string capability, string sourceKey)
    {
        var dataset = Dataset();
        dataset = dataset with { Manifest = dataset.Manifest with { Capabilities = [capability] } };
        var html = await RenderAsync(dataset);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, sourceKey), html);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, sourceKey == "RecordedSource" ? "GeneratedSource" : "RecordedSource"), html);
    }

    [Fact]
    public async Task UnmappedTechnicalSampleUsesQuietDecorativeInitialsAndNeverFuzzyMatchesClubNames()
    {
        var dataset = Dataset();
        dataset = dataset with { Manifest = dataset.Manifest with { FixtureId = "fixture-technical-sample" },
            Fixture = dataset.Fixture! with { Label = "Brisbane Roar vs Adelaide United", Competition = "A-League",
                Home = new("team-skillcorner-1", "Brisbane Roar"), Away = new("team-skillcorner-2", "Adelaide United") } };
        var html = await RenderAsync(dataset);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<time", html);
        Assert.Contains("BR</span>", html);
        Assert.Contains("AU</span>", html);
        Assert.Contains("Brisbane Roar", html);
        Assert.Contains("Adelaide United", html);
        var knownNameWithUnknownId = Dataset();
        knownNameWithUnknownId = knownNameWithUnknownId with { Fixture = knownNameWithUnknownId.Fixture! with { Home = new("unknown-team", "Brentford") } };
        Assert.DoesNotContain("brentford-2026.png", await RenderAsync(knownNameWithUnknownId));
    }

    [Fact]
    public async Task ExplicitDisplayCultureDoesNotDependOnOrChangeAmbientCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var html = await RenderAsync(Dataset(), MatchLanguage.Spanish);
            Assert.Contains("17 octubre 2026", html);
            Assert.Equal("sv-SE", CultureInfo.CurrentCulture.Name);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static FootballDataset Dataset()
    {
        var fixture = Fixture();
        var dataset = Ledger(fixture).Query(Query(fixture, 0)).Dataset;
        return dataset with { Manifest = dataset.Manifest with { FixtureId = "fixture-pl-2645256", Capabilities = [FootballPresentationPolicy.GeneratedMovement] },
            Fixture = dataset.Fixture! with { Label = "Brentford – Liverpool · 17 October 2026", Competition = "Premier League",
                Home = new("team-pl-reference-brentford", "Brentford"), Away = new("team-pl-reference-liverpool", "Liverpool") } };
    }

    private static async Task<string> RenderAsync(FootballDataset dataset, MatchLanguage language = MatchLanguage.English)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new ComponentTestHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = builder =>
            {
                builder.OpenComponent<CascadingValue<MatchLanguage>>(0);
                builder.AddComponentParameter(1, "Value", language);
                builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(picker => { picker.OpenComponent<AudienceModePicker>(0); picker.CloseComponent(); }));
                builder.CloseComponent();
            };
            var output = await renderer.RenderComponentAsync<ReplayMatchHeader>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ReplayMatchHeader.Dataset)] = dataset, [nameof(ReplayMatchHeader.Language)] = language,
                [nameof(ReplayMatchHeader.ChildContent)] = child
            }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
