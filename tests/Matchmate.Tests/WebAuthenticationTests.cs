using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Matchmate.Infrastructure;
using Matchmate.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;
using Azure.Core;
using Azure.Identity;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Tests;

public sealed class WebAuthenticationTests
{
    [Fact]
    public async Task LocalModelCredentialIsSharedAcrossScopesAndDisabledAdaptersStayDisconnected()
    {
        await using var factory = Factory();
        using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        var credential = first.ServiceProvider.GetRequiredService<TokenCredential>();
        Assert.IsType<DefaultAzureCredential>(credential); Assert.Single(factory.Services.GetServices<TokenCredential>());
        Assert.Same(credential, second.ServiceProvider.GetRequiredService<TokenCredential>());
        Assert.False(first.ServiceProvider.GetRequiredService<IMatchWorkflowRouter>().Configured);
        Assert.False(second.ServiceProvider.GetRequiredService<IMatchChatModel>().Configured);
    }

    [Theory]
    [InlineData(MatchLanguage.English)]
    [InlineData(MatchLanguage.Swedish)]
    [InlineData(MatchLanguage.Spanish)]
    public async Task PublicAccountShellUsesStandaloneLanguageWithCookieSelectionAndPreservedReturnUrl(MatchLanguage language)
    {
        await using var factory = Factory(); using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Culture=c=en-GB|uic=" + MatchLanguages.Culture(language).Name);
        const string returnUrl = "/recordings?stream=one&cursor=61.2#pitch";
        foreach (var path in new[] { "/account/sign-in?returnUrl=" + Uri.EscapeDataString(returnUrl), "/account/denied", "/account/error", "/Error" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("data-standalone-language=\"\"", html);
            Assert.DoesNotContain("\"type\":\"server\"", html);
            Assert.Contains("<html lang=\"" + MatchLanguages.Code(language) + "\"", html);
            Assert.Contains(ShellText.Get(language, "LanguageLabel"), html);
            Assert.Contains(ShellText.Get(language, "Footer"), html);
            var selected = Assert.Single(Regex.Matches(html, "<option\\b[^>]*>").Cast<Match>(), option => Regex.IsMatch(option.Value, "\\bselected(?:[ =>])"));
            Assert.Contains("value=\"" + language + "\"", selected.Value);
            Assert.Contains("data-culture=\"" + MatchLanguages.Culture(language).Name + "\"", selected.Value);
            if (path.StartsWith("/account/sign-in", StringComparison.Ordinal))
                Assert.Contains("href=\"/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl) + "\"", html);
        }
    }

    [Fact]
    public async Task AnonymousCannotOpenReplayOrNegotiateAndAccountPagesStayPublic()
    {
        await using var factory = Factory(); using var client = Client(factory);
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/sign-in", response.Headers.Location!.AbsolutePath);
        Assert.DoesNotContain("Recorded player and ball positions", await response.Content.ReadAsStringAsync());
        var negotiate = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        foreach (var path in new[] { "/account/sign-in", "/account/denied", "/account/error", "/Error", "/alive" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Contains("Sign in with email", await client.GetStringAsync("/account/sign-in"));
    }

    [Fact]
    public async Task AuthenticatedNonTesterIsDeniedButCanSignOut()
    {
        await using var factory = Factory(); using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", Cookie(factory, tester: false));
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/denied", response.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);
        var denied = await client.GetStringAsync("/account/denied");
        Assert.Contains("data-standalone-language=\"\"", denied);
        Assert.DoesNotContain("\"type\":\"server\"", denied);
        Assert.Contains("doesn't have tester access", WebUtility.HtmlDecode(denied));
        Assert.Contains("action=\"/auth/logout\"", denied);
        // SignedIn, rather than Tester, authorizes this POST; missing CSRF protection is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task RealCookieHandlerAcceptsTesterAndRejectsExpiredTicket()
    {
        await using var factory = Factory(); using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", Cookie(factory));
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Matchmate tester preview", html);
        Assert.Contains("\"type\":\"server\"", html);
        Assert.DoesNotContain("data-standalone-language", html);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", Cookie(factory, expired: true));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("https://outside.example/path", "/")]
    [InlineData("//outside.example", "/")]
    [InlineData("/\\outside.example", "/")]
    [InlineData("\\outside.example", "/")]
    [InlineData("/%5coutside.example", "/")]
    [InlineData("/%2foutside.example", "/")]
    [InlineData("/path%0d%0aLocation:elsewhere", "/")]
    [InlineData("/match?cursor=61.2", "/match?cursor=61.2")]
    public async Task ChallengeUsesCodePkceAndOnlyLocalReturnUrl(string requested, string expected)
    {
        await using var factory = Factory(); using var client = Client(factory);
        var response = await client.GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString(requested));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("identity.example.test", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal("https://localhost:7410/signin-oidc", query["redirect_uri"]);
        Assert.Equal(new[] { "email", "openid", "profile" }, query["scope"].ToString().Split(' ').Order().ToArray());
        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = oidc.StateDataFormat.Unprotect(query["state"]!)!;
        Assert.Equal(expected, properties.RedirectUri);
        Assert.False(properties.IsPersistent);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains(".AspNetCore.Correlation.", StringComparison.Ordinal) && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LogoutRequiresPostAndAntiforgeryThenClearsCookieAndUsesFixedTarget()
    {
        await using var factory = Factory(); using var client = Client(factory);
        var cookie = Cookie(factory);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        // Blazor's GET catch-all renders unknown routes as 404; it must never execute logout.
        var wrongMethod = await client.GetAsync("/auth/logout");
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);
        Assert.False(wrongMethod.Headers.TryGetValues("Set-Cookie", out var getCookies)
            && getCookies.Any(value => value.StartsWith("__Host-Matchmate.Auth=;", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]))).StatusCode);
        var page = await client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);
        var antiCookie = page.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + antiCookie);
        var signedOut = await client.PostAsync("/auth/logout?returnUrl=https://outside.example", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) }));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("identity.example.test", signedOut.Headers.Location!.Host);
        var query = QueryHelpers.ParseQuery(signedOut.Headers.Location.Query);
        Assert.Equal("https://localhost:7410/signout-callback-oidc", query["post_logout_redirect_uri"]);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.Equal("/account/sign-in", options.StateDataFormat.Unprotect(query["state"]!)!.RedirectUri);
        Assert.Contains(signedOut.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-Matchmate.Auth=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingCredentialFailsClosedAndOidcKeepsValidationAndNoTokens()
    {
        await using var missing = Factory(secret: "");
        var failure = Assert.Throws<OptionsValidationException>(() => missing.CreateClient());
        Assert.Contains("Authentication:ExternalId:ClientSecret is required", failure.Message);
        await using var valid = Factory(); using var client = Client(valid);
        var oidc = valid.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(oidc.SaveTokens); Assert.False(oidc.MapInboundClaims); Assert.True(oidc.UsePkce);
        Assert.True(oidc.RequireHttpsMetadata); Assert.True(oidc.TokenValidationParameters.ValidateIssuer);
        Assert.True(oidc.TokenValidationParameters.ValidateAudience); Assert.True(oidc.TokenValidationParameters.RequireSignedTokens);
        Assert.Equal("roles", oidc.TokenValidationParameters.RoleClaimType);
        var cookie = valid.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.True(cookie.Cookie.HttpOnly); Assert.False(cookie.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(1), cookie.ExpireTimeSpan);
        var response = await client.GetAsync("/signin-oidc?error=access_denied");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/error", response.Headers.Location!.OriginalString);
    }

    private static HttpClient Client(WebApplicationFactory<Matchmate.Web.Program> factory) => factory.CreateClient(new()
    { BaseAddress = new Uri("https://localhost:7410"), AllowAutoRedirect = false, HandleCookies = false });

    // Exercise the production cookie handler/policy using a locally protected test ticket.
    // No replacement authentication handler or provider/browser service is started.
    private static string Cookie(WebApplicationFactory<Matchmate.Web.Program> factory, bool tester = true, bool expired = false)
    {
        var claims = new List<Claim> { new("sub", "local-auth-test"), new("name", "Local test") };
        if (tester) claims.Add(new("roles", "Tester"));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", "roles");
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-2), ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 30), IsPersistent = false };
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return options.Cookie.Name + "=" + options.TicketDataFormat.Protect(new(new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static WebApplicationFactory<Matchmate.Web.Program> Factory(string secret = "local-test-not-a-real-provider-secret") =>
        new WebApplicationFactory<Matchmate.Web.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing").UseContentRoot(Path.Combine(HostingTestPaths.Root(), "src", "Matchmate.Web"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ExternalId:ClientSecret"] = secret
            }));
            builder.ConfigureServices(services => services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                // Static protocol metadata prevents all provider discovery/network calls.
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new()
                {
                    Issuer = "https://identity.example.test", AuthorizationEndpoint = "https://identity.example.test/authorize",
                    TokenEndpoint = "https://identity.example.test/token", EndSessionEndpoint = "https://identity.example.test/logout"
                });
            }));
        });
}
