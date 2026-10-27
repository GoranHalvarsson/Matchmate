using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Matchmate.Api;
using Matchmate.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Matchmate.Tests;

public sealed class ApiServiceAuthenticationTests
{
    internal const string Tenant = "800542d5-c10f-4db2-97fb-618f0ce3bcd8";
    internal const string ApiId = "26865c27-288a-45bb-af11-1a86ab67bc2c";
    internal const string WebId = "11111111-1111-4111-8111-111111111111";
    internal const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private static readonly SymmetricSecurityKey Key = new("test-only-signing-key-not-an-azure-secret-0123456789"u8.ToArray());

    [Fact]
    public async Task HostedApiSeparatesProcessServiceAccessAndDatasetReadiness()
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/hosting/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/matches/1874553/metadata")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Contains("awaiting-pipeline", await client.GetStringAsync("/api/hosting/status"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/matches/1874553/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/matches/1874553/frame?cursor=61.2")).StatusCode);
    }

    [Theory]
    [InlineData("issuer", 401)]
    [InlineData("audience", 401)]
    [InlineData("signature", 401)]
    [InlineData("expired", 401)]
    [InlineData("caller", 403)]
    [InlineData("role", 403)]
    public async Task HostedApiRejectsInvalidOrUnapprovedServiceTokens(string changed, int expected)
    {
        await using var factory = Factory(); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(changed));
        Assert.Equal(expected, (int)(await client.GetAsync("/api/hosting/status")).StatusCode);
    }

    internal static string Token(string changed = "")
    {
        var key = changed == "signature" ? new SymmetricSecurityKey("wrong-test-signature-key-abcdefghijklmnopqrstuvwxyz"u8.ToArray()) : Key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: changed == "issuer" ? "https://wrong.example/v2.0" : Issuer,
            audience: changed == "audience" ? "22222222-2222-4222-8222-222222222222" : ApiId,
            claims: [new Claim("azp", changed == "caller" ? ApiId : WebId), new Claim("roles", changed == "role" ? "Other.Read" : "Replay.Read")],
            notBefore: DateTime.UtcNow.AddMinutes(-30), expires: DateTime.UtcNow.AddMinutes(changed == "expired" ? -10 : 10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    internal static WebApplicationFactory<Matchmate.Api.Program> Factory(IReplayDatasetStore? store = null) =>
        new HostingFactory<Matchmate.Api.Program>(new Dictionary<string, string?>
        {
            ["Runtime:Hosted"] = "true", ["Pipeline:Enabled"] = "false", ["ServiceAuthentication:TenantId"] = Tenant,
            ["ServiceAuthentication:ApiClientId"] = ApiId, ["ServiceAuthentication:WebClientId"] = WebId
        }).WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing").UseContentRoot(Path.Combine(HostingTestPaths.Root(), "src", "Matchmate.Api"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:Hosted"] = "true", ["Pipeline:Enabled"] = "false", ["ServiceAuthentication:TenantId"] = Tenant,
                ["ServiceAuthentication:ApiClientId"] = ApiId, ["ServiceAuthentication:WebClientId"] = WebId
            }));
            builder.ConfigureServices(services =>
            {
                if (store is not null) services.AddSingleton(store);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer }; metadata.SigningKeys.Add(Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                });
            });
        });
}

internal sealed class HostingFactory<TProgram>(Dictionary<string, string?> settings) : WebApplicationFactory<TProgram> where TProgram : class
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal-host startup reads Runtime before ConfigureAppConfiguration callbacks are replayed.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        return base.CreateHost(builder);
    }
}

internal static class HostingTestPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Run hosting tests from the Matchmate source workspace.");
    }
}
