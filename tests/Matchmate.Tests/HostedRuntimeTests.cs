using System.Net;
using Azure.Core;
using Azure.Identity;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.ServiceDefaults;
using Matchmate.Web;
using Matchmate.Web.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;
using System.Xml.Linq;

namespace Matchmate.Tests;

public sealed class HostedRuntimeTests
{
    [Theory]
    [InlineData(false, "Production", false)]
    [InlineData(false, "Development", true)]
    [InlineData(false, "Testing", true)]
    [InlineData(true, "Production", true)]
    public void LocalModeNeverBecomesImplicitProductionFallback(bool hosted, string environment, bool valid)
    {
        var config = Config(new() { ["Runtime:Hosted"] = hosted.ToString() });
        var host = new HostApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        if (valid) Assert.Equal(hosted, RuntimeMode.Validate(config, host.Environment));
        else Assert.Throws<InvalidOperationException>(() => RuntimeMode.Validate(config, host.Environment));
    }

    [Fact]
    public void HostedWorkerRequiresExplicitDisabledImportAndLocalImportKeepsItsDefault()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeMode.RecordedImportEnabled(Config(new()), true));
        Assert.Throws<InvalidOperationException>(() => RuntimeMode.RecordedImportEnabled(Config(new() { ["RecordedImport:Enabled"] = "true" }), true));
        Assert.False(RuntimeMode.RecordedImportEnabled(Config(new() { ["RecordedImport:Enabled"] = "false" }), true));
        Assert.True(RuntimeMode.RecordedImportEnabled(Config(new()), false));
    }

    [Theory]
    [InlineData("http://web.example", false)]
    [InlineData("https://web.example/path", false)]
    [InlineData("https://user@web.example", false)]
    [InlineData("https://web.example?host=other", false)]
    [InlineData("https://web.example", true)]
    public void HostedPublicOriginIsAnExplicitHttpsOrigin(string origin, bool expected) => Assert.Equal(expected, HostedWeb.IsHttpsOrigin(origin));

    [Fact]
    public async Task HostedCallbackUsesConfiguredOriginAndIgnoresForwardedHost()
    {
        await using var api = ApiServiceAuthenticationTests.Factory();
        var credential = new TestCredential();
        await using var web = WebFactory(api, credential);
        Assert.DoesNotContain(web.Services.GetServices<TokenCredential>(), registered => registered is DefaultAzureCredential);
        using var client = web.CreateClient(new() { BaseAddress = new Uri("http://untrusted-host.example"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");
        var response = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://mm-web-dev.example.test/signin-oidc", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"]);
        var forwarded = web.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
        Assert.Equal(1, forwarded.ForwardLimit);
    }

    [Fact]
    public async Task HostedWebHealthCallsActualProtectedApiWithScopedServiceToken()
    {
        await using var api = ApiServiceAuthenticationTests.Factory(); var credential = new TestCredential();
        await using var web = WebFactory(api, credential);
        using var client = web.CreateClient(new() { BaseAddress = new Uri("https://mm-web-dev.example.test") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var probe = web.CreateClient(new() { BaseAddress = new Uri("http://replica:8080"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await probe.GetAsync("/alive")).StatusCode);
        Assert.Equal($"api://{ApiServiceAuthenticationTests.ApiId}/.default", credential.Scope);
        using var apiClient = api.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await apiClient.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task ReadyWebHealthReadsRealCatalogAndVersionGuardedFrameThroughProtectedApi()
    {
        var blobs = new MemoryReplayBlobs(); var indexes = new MemoryFootballIndexes();
        var journal = new AzureFootballStore(blobs, indexes); var fixture = SharedFootballTests.Fixture();
        await journal.AppendAsync(SharedFootballTests.Batch(fixture, 1, SharedFootballTests.Context(fixture).Append(SharedFootballTests.Frame(fixture, 0)).ToArray()), default);
        var store = new CountingStore(journal);
        await using var api = ApiServiceAuthenticationTests.Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IFootballStore>(store))); var credential = new TestCredential();
        await using var web = WebFactory(api, credential);
        using var client = web.CreateClient(new() { BaseAddress = new Uri("https://mm-web-dev.example.test") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.True(store.CatalogReads >= 2); Assert.Equal(1, store.SnapshotReads);
        Assert.Equal($"api://{ApiServiceAuthenticationTests.ApiId}/.default", credential.Scope);
        // A ready catalog is insufficient when the published object cannot actually be read.
        store.FailSnapshot = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
    }

    private sealed class CountingStore(IFootballStore inner) : IFootballStore
    {
        public int CatalogReads, SnapshotReads;
        public bool FailSnapshot;
        public Task<bool> AppendAsync(FootballBatch batch, CancellationToken token) => inner.AppendAsync(batch, token);
        public Task<FootballLedger> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task<FootballDataset[]> CatalogAsync(CancellationToken token) { CatalogReads++; return inner.CatalogAsync(token); }
        public Task<FootballView> QueryAsync(FootballQuery query, CancellationToken token)
        {
            SnapshotReads++; return FailSnapshot ? throw new IOException("test publication unavailable") : inner.QueryAsync(query, token);
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static WebApplicationFactory<Matchmate.Web.Program> WebFactory(WebApplicationFactory<Matchmate.Api.Program> api, TestCredential credential) =>
        new HostingFactory<Matchmate.Web.Program>(new Dictionary<string, string?>
        {
            ["Runtime:Hosted"] = "true", ["Runtime:PublicOrigin"] = "https://mm-web-dev.example.test",
            ["Runtime:ManagedIdentityClientId"] = ApiServiceAuthenticationTests.WebId,
            ["Runtime:DataProtectionBlobUri"] = "https://storage.example.test/data-protection-keys/web.xml",
            ["ServiceAuthentication:ApiClientId"] = ApiServiceAuthenticationTests.ApiId,
            ["ReplayApi:BaseUrl"] = "https://mm-api-dev.internal.example.test/",
            ["Authentication:ExternalId:ClientSecret"] = "test-only-not-a-provider-secret"
        }).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing").UseContentRoot(Path.Combine(HostingTestPaths.Root(), "src", "Matchmate.Web"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:Hosted"] = "true", ["Runtime:PublicOrigin"] = "https://mm-web-dev.example.test",
                ["Runtime:ManagedIdentityClientId"] = ApiServiceAuthenticationTests.WebId,
                ["Runtime:DataProtectionBlobUri"] = "https://storage.example.test/data-protection-keys/web.xml",
                ["ServiceAuthentication:ApiClientId"] = ApiServiceAuthenticationTests.ApiId,
                ["ReplayApi:BaseUrl"] = "https://mm-api-dev.internal.example.test/",
                ["Authentication:ExternalId:ClientSecret"] = "test-only-not-a-provider-secret"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TokenCredential>(credential);
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                // The real DataProtection hosted warmup also runs; keep its key repository fully offline.
                services.Configure<KeyManagementOptions>(options => options.XmlRepository = new TestKeyRepository());
                services.AddHttpClient<ReplayApiClient>().ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new()
                    { Issuer = "https://identity.example.test", AuthorizationEndpoint = "https://identity.example.test/authorize" }));
            });
        });

    private sealed class TestCredential : TokenCredential
    {
        public string? Scope;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scope = Assert.Single(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken(ApiServiceAuthenticationTests.Token(), DateTimeOffset.UtcNow.AddMinutes(10)));
        }
    }

    private sealed class TestKeyRepository : IXmlRepository
    {
        private readonly List<XElement> _keys = [];
        public IReadOnlyCollection<XElement> GetAllElements() { lock (_keys) return _keys.Select(key => new XElement(key)).ToArray(); }
        public void StoreElement(XElement element, string friendlyName) { lock (_keys) _keys.Add(new XElement(element)); }
    }
}
