using System.Net;
using Matchmate.Web.Components.Layout;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Matchmate.Tests;

internal sealed class LinkTestNavigationManager : NavigationManager
{
    public LinkTestNavigationManager() => Initialize("https://matchmate.test/", "https://matchmate.test/recordings/current");
    protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Rendering must not navigate.");
}

public sealed class AppLinkTests
{
    [Theory]
    [InlineData("/recordings/current", LinkDestinationKind.Internal)]
    [InlineData("recordings/current", LinkDestinationKind.Internal)]
    [InlineData("?date=3", LinkDestinationKind.Internal)]
    [InlineData("#source", LinkDestinationKind.Internal)]
    [InlineData("https://MATCHMATE.test:443/page", LinkDestinationKind.Internal)]
    [InlineData("//matchmate.test/page", LinkDestinationKind.Internal)]
    [InlineData("https://source.test/report", LinkDestinationKind.External)]
    [InlineData("//source.test/report", LinkDestinationKind.External)]
    [InlineData("http://matchmate.test/page", LinkDestinationKind.External)]
    [InlineData("https://matchmate.test:444/page", LinkDestinationKind.External)]
    [InlineData("mailto:reader@example.test", LinkDestinationKind.Email)]
    [InlineData("tel:+4612345678", LinkDestinationKind.Telephone)]
    public void DestinationUsesOriginAndProtocolRatherThanArrowOrText(string href, LinkDestinationKind expected)
    {
        var result = LinkDestination.Classify(href, new("https://matchmate.test/"));
        Assert.Equal(expected, result.Kind); Assert.Equal(href, result.Href);
        Assert.Equal(expected != LinkDestinationKind.Internal, result.LeavesApp);
    }

    [Theory]
    [InlineData("javascript:alert(1)")] [InlineData("data:text/html,example")] [InlineData("file:///C:/private")]
    [InlineData("https://source.test/\nreport")] [InlineData("\\\\source.test/report")]
    public async Task UnsupportedSchemesControlsAndBackslashesCannotBecomeClickableHref(string href)
    {
        var destination = LinkDestination.Classify(href, new("https://matchmate.test/"));
        Assert.Equal(LinkDestinationKind.Unsupported, destination.Kind); Assert.Null(destination.Href); Assert.False(destination.LeavesApp);
        var html = await Render(MatchLanguage.English, new() { [nameof(AppLink.Href)] = href });
        Assert.DoesNotContain("href=", html); Assert.Contains("aria-disabled=\"true\"", html); Assert.DoesNotContain("external-link-icon", html);
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task ExternalHintsAreLocalizedAndDescribeOnlyActualTargetBehavior(MatchLanguage language)
    {
        var external = await Render(language, new() { [nameof(AppLink.Href)] = "//source.test/report", [nameof(AppLink.Target)] = "_blank", [nameof(AppLink.Rel)] = "nofollow noopener" });
        Assert.Contains("external-link-icon", external); Assert.Contains("aria-hidden=\"true\"", external);
        Assert.Contains(ShellText.Get(language, "LinkExternalNewTab"), external); Assert.Contains("target=\"_blank\"", external);
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", external);
        var sameTab = await Render(language, new() { [nameof(AppLink.Href)] = "https://source.test/report" });
        Assert.Contains(ShellText.Get(language, "LinkExternal"), sameTab); Assert.DoesNotContain(ShellText.Get(language, "LinkExternalNewTab"), sameTab); Assert.DoesNotContain("target=", sameTab);
        foreach (var (href, key) in new[] { ("mailto:reader@example.test", "LinkOpensEmail"), ("tel:+4612345678", "LinkOpensTelephone") })
        {
            var contact = await Render(language, new() { [nameof(AppLink.Href)] = href });
            Assert.Contains("external-link-icon", contact); Assert.Contains(ShellText.Get(language, key), contact);
            Assert.DoesNotContain(ShellText.Get(language, "LinkExternalNewTab"), contact); Assert.DoesNotContain("target=", contact);
        }
        var internalLink = await Render(language, new() { [nameof(AppLink.Href)] = "https://matchmate.test/source", [nameof(AppLink.Target)] = "_blank" });
        Assert.DoesNotContain("external-link-icon", internalLink); Assert.DoesNotContain("link-accessible-hint", internalLink);
        Assert.Contains("target=\"_blank\"", internalLink); // Preserve requested behavior; same-origin remains unmarked.
    }

    [Fact]
    public async Task ExplicitDestinationAndTargetWinWhileNormalAttributesAndChildContentSurvive()
    {
        var html = await Render(MatchLanguage.English, new()
        {
            [nameof(AppLink.Href)] = "https://source.test/report", [nameof(AppLink.Target)] = "_blank", [nameof(AppLink.Rel)] = "author",
            [nameof(AppLink.AdditionalAttributes)] = new Dictionary<string, object>
            { ["href"] = "javascript:alert(1)", ["target"] = "_self", ["rel"] = "opener", ["class"] = "source-link", ["data-source"] = "report", ["aria-label"] = "Source report" }
        });
        Assert.Contains("href=\"https://source.test/report\"", html); Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"author noopener noreferrer\"", html); Assert.DoesNotContain("javascript:", html); Assert.DoesNotContain("_self", html);
        Assert.Contains("class=\"source-link\"", html); Assert.Contains("data-source=\"report\"", html);
        Assert.Contains("Source report (" + ShellText.Get(MatchLanguage.English, "LinkExternalNewTab") + ")", html);
        Assert.Contains("<strong>Read source</strong>", html);
    }

    private static async Task<string> Render(MatchLanguage language, Dictionary<string, object> parameters)
    {
        parameters[nameof(AppLink.ChildContent)] = (RenderFragment)(b => { b.OpenElement(0, "strong"); b.AddContent(1, "Read source"); b.CloseElement(); });
        using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment child = b => { b.OpenComponent<AppLink>(0); b.AddMultipleAttributes(1, parameters); b.CloseComponent(); };
            var output = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CascadingValue<MatchLanguage>.Value)] = language, [nameof(CascadingValue<MatchLanguage>.ChildContent)] = child }));
            return WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
