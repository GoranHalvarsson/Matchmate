using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueFeaturedMatchTests
{
    [Theory]
    [InlineData(MatchLanguage.English, "17 October 2026", "18 September 2026", "Replay demo", "Explore replay: Brentford–Liverpool", "Explore replay: Brentford–Chelsea")]
    [InlineData(MatchLanguage.Swedish, "17 oktober 2026", "18 september 2026", "Reprisdemo", "Utforska repris: Brentford–Liverpool", "Utforska repris: Brentford–Chelsea")]
    [InlineData(MatchLanguage.Spanish, "17 octubre 2026", "18 septiembre 2026", "Demo de repetición", "Explorar repetición: Brentford–Liverpool", "Explorar repetición: Brentford–Chelsea")]
    public async Task RealLeaguePageOffersTwoDistinctLocalizedMatchesBeforeTheUnchangedExplorerAndSupportingSample(
        MatchLanguage language, string liverpoolDate, string chelseaDate, string replayLabel, string liverpoolAction, string chelseaAction)
    {
        await RenderAsync(language, false, html =>
        {
            var heading = Assert.Single(Regex.Matches(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.Singleline).Cast<Match>());
            Assert.Equal(LeagueText.Get(language, "FeaturedHeadline"), heading.Groups[1].Value);
            Assert.Contains("lang=\"" + MatchLanguages.Code(language) + "\"", html);

            var featured = html.IndexOf("class=\"featured-match\"", StringComparison.Ordinal);
            var season = html.IndexOf("class=\"league-intro\"", StringComparison.Ordinal);
            var toolbar = html.IndexOf("class=\"league-toolbar\"", StringComparison.Ordinal);
            var conversation = html.IndexOf("league-conversation", StringComparison.Ordinal);
            var playback = html.IndexOf("class=\"season-monitor\"", StringComparison.Ordinal);
            var tableAndMap = html.IndexOf("class=\"league-layout\"", StringComparison.Ordinal);
            var sample = html.IndexOf("class=\"replay-background\"", StringComparison.Ordinal);
            Assert.True(featured >= 0 && featured < season && season < toolbar && toolbar < conversation
                && conversation < playback && playback < tableAndMap && tableAndMap < sample,
                "Keep both featured matches first, the complete season workspace together, and the recorded sample last.");
            Assert.Contains("<h2", html[season..toolbar]);
            Assert.Contains(LeagueText.Get(language, "SeasonHeading"), html[season..toolbar]);
            Assert.Contains(LeagueText.Get(language, "DayByDay"), html[season..toolbar]);

            var featuredHtml = html[featured..season];
            var cards = Regex.Matches(featuredHtml, @"<article\b[^>]*aria-labelledby=""([^""]+)""[^>]*>(.*?)</article>", RegexOptions.Singleline).Cast<Match>().ToArray();
            Assert.Equal(2, cards.Length);
            Assert.Equal("liverpool-match-heading", cards[0].Groups[1].Value);
            Assert.Equal("chelsea-match-heading", cards[1].Groups[1].Value);
            AssertCard(cards[0], "Brentford – Liverpool", "2026-10-17", liverpoolDate, "liverpool", "/recordings/brentford-liverpool-illustration", liverpoolAction);
            AssertCard(cards[1], "Brentford – Chelsea", "2026-09-18", chelseaDate, "chelsea", "/recordings/brentford-existing-illustration", chelseaAction);
            Assert.Equal(2, Regex.Matches(cards[0].Value, @"<span\b[^>]*class=""featured-match-crest""[^>]*>").Count);
            foreach (var card in cards)
            {
                var badge = Assert.Single(Regex.Matches(card.Value, @"<span\b[^>]*class=""featured-match-badge""[^>]*>(.*?)</span>").Cast<Match>());
                Assert.Equal(replayLabel, badge.Groups[1].Value);
            }
            Assert.Equal(2, Regex.Matches(featuredHtml, @"class=""featured-match-badge""").Count);
            Assert.Equal(2, Regex.Matches(featuredHtml, @"href=""/recordings/").Count);
            Assert.Contains(LeagueText.Get(language, "FeaturedInvitation"), featuredHtml);
            Assert.Contains(LeagueText.Get(language, "FeaturedProvenance"), featuredHtml);
            foreach (var club in new[] { "brentford", "liverpool", "chelsea" })
            {
                var path = "/assets/crests/" + club + "-2026.png";
                Assert.Contains("src=\"" + path + "\"", featuredHtml);
                Assert.True(File.Exists(Path.Combine(LocalPaths.FindRoot(), "src", "Matchmate.Web", "wwwroot", path.TrimStart('/'))));
            }
            Assert.DoesNotContain("/recordings/australian-recorded", featuredHtml);
            Assert.DoesNotContain("<video", featuredHtml);
            Assert.DoesNotContain("<svg", featuredHtml);

            var sampleHtml = html[sample..];
            AssertLink(sampleHtml, "/recordings/australian-recorded", LeagueText.Get(language, "ExploreRecordedSample"));
            foreach (var key in new[] { "BehindReplay", "RecordedTrackingSample", "RecordedSampleInvitation", "RecordedSampleLimit" })
                Assert.Contains(LeagueText.Get(language, key), sampleHtml);
            Assert.Contains("Brisbane Roar–Adelaide United", sampleHtml);
        });
    }

    [Fact]
    public async Task AllThreeReplayEntrypointsRemainAvailableWhenTheSeasonReferenceCannotLoad()
    {
        await RenderAsync(MatchLanguage.English, true, html =>
        {
            Assert.Contains(LeagueText.Get(MatchLanguage.English, "SeasonUnavailable"), html);
            AssertLink(html, "/recordings/brentford-liverpool-illustration", "Explore replay: Brentford–Liverpool");
            AssertLink(html, "/recordings/brentford-existing-illustration", "Explore replay: Brentford–Chelsea");
            AssertLink(html, "/recordings/australian-recorded", "Explore the recorded sample");
            Assert.Contains("Prepared replays with reported match facts and generated movement. Generated replay statistics describe the illustration, not the actual match. Live coverage would require a licensed data feed.", html);
            Assert.Contains("The sample includes estimated positions and gaps.", html);
        });
    }

    [Theory]
    [InlineData(MatchLanguage.English, "Live-match experience · Preview", "A conversation with the match—as it unfolds.", "Explore two replay demos of how Matchmate could help you follow a live match. Follow the action, pause and ask what changed.", "Prepared replays with reported match facts and generated movement. Generated replay statistics describe the illustration, not the actual match. Live coverage would require a licensed data feed.")]
    [InlineData(MatchLanguage.Swedish, "Matchupplevelse i realtid · Förhandsvisning", "Ett samtal med matchen – medan den pågår.", "Utforska två reprisdemon som visar hur Matchmate skulle kunna hjälpa dig att följa en match i realtid. Följ spelet, pausa och fråga vad som förändrats.", "Förberedda repriser med rapporterade matchfakta och genererade rörelser. Reprisens genererade statistik beskriver illustrationen, inte den verkliga matchen. Liverapportering skulle kräva ett licensierat dataflöde.")]
    [InlineData(MatchLanguage.Spanish, "La experiencia del partido en directo · Vista previa", "Una conversación con el partido mientras se juega.", "Explora dos demos en repetición de cómo Matchmate podría ayudarte a seguir un partido en directo. Sigue el juego, pausa y pregunta qué ha cambiado.", "Repeticiones preparadas con datos documentados del partido y movimientos generados. Las estadísticas generadas de la repetición describen la ilustración, no el partido real. La cobertura en directo requeriría una fuente de datos con licencia.")]
    public async Task PreviewInvitationAndSharedProvenanceRenderInTheSelectedLanguage(MatchLanguage language, string preview, string headline, string invitation, string provenance)
    {
        await RenderAsync(language, false, html =>
        {
            Assert.Contains(preview, html);
            Assert.Contains(headline, html);
            Assert.Contains(invitation, html);
            Assert.Contains(provenance, html);
            Assert.Single(Regex.Matches(html, Regex.Escape(provenance)).Cast<Match>());
        });
    }

    private static void AssertCard(Match card, string fixture, string date, string localizedDate, string opponent, string href, string action)
    {
        var html = card.Groups[2].Value;
        var heading = Assert.Single(Regex.Matches(html, @"<h2\b[^>]*id=""" + card.Groups[1].Value + @"""[^>]*>(.*?)</h2>", RegexOptions.Singleline).Cast<Match>());
        Assert.Equal(fixture, Regex.Replace(heading.Groups[1].Value, "<[^>]+>", ""));
        var shownDate = Assert.Single(Regex.Matches(html, "<time\\b[^>]*datetime=\"" + date + "\"[^>]*>(.*?)</time>").Cast<Match>());
        Assert.Equal(localizedDate, shownDate.Groups[1].Value);
        Assert.Contains("src=\"/assets/crests/brentford-2026.png\"", html);
        Assert.Contains("src=\"/assets/crests/" + opponent + "-2026.png\"", html);
        AssertLink(html, href, action);
        Assert.Single(Regex.Matches(html, "<a\\b").Cast<Match>());
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("tabindex=", html);
    }

    private static void AssertLink(string html, string href, string label)
    {
        var link = Assert.Single(Regex.Matches(html, "<a\\b[^>]*href=\"" + Regex.Escape(href) + "\"[^>]*>(.*?)</a>", RegexOptions.Singleline).Cast<Match>());
        Assert.StartsWith(label, link.Groups[1].Value, StringComparison.Ordinal);
    }

    private static async Task RenderAsync(MatchLanguage language, bool unavailable, Action<string> verify)
    {
        var snapshot = LeagueChatFixtures.Snapshot(2, null);
        using var http = new HttpClient(new Handler(request => unavailable
            ? new(HttpStatusCode.ServiceUnavailable)
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(request.RequestUri!.AbsolutePath == "/api/league/seasons" ? (object)new[] { snapshot.Season } : snapshot) }))
            { BaseAddress = new("https://unit.invalid/") };
        using var session = LeagueChatFixtures.Session();
        var state = new MatchLanguageState(); state.SetLanguage(language);
        using var services = new ServiceCollection().AddLogging().AddSingleton(state).AddSingleton(new ReplayApiClient(http)).AddSingleton(session)
            .AddSingleton<NavigationManager>(new PageNavigation()).AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new ComponentTestHtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SitePageLocalizationTests.LeaguePage>();
            verify(WebUtility.HtmlDecode(output.ToHtmlString()));
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }

    private sealed class PageNavigation : NavigationManager
    {
        public PageNavigation() => Initialize("https://unit.invalid/", "https://unit.invalid/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
        protected override void SetNavigationLockState(bool value) { }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static page rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new InvalidOperationException("Static page rendering must not invoke JavaScript.");
    }
}
