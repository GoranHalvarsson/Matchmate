using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Matchmate.Web.Authentication;

public sealed class HostedWebSettings
{
    public string PublicOrigin { get; set; } = "";
    public string ManagedIdentityClientId { get; set; } = "";
    public string DataProtectionBlobUri { get; set; } = "";
}

public static class HostedWeb
{
    public static void ConfigureHostedWeb(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Runtime").Get<HostedWebSettings>() ?? new();
        if (!IsHttpsOrigin(settings.PublicOrigin) || !Guid.TryParse(settings.ManagedIdentityClientId, out _)
            || !Uri.TryCreate(settings.DataProtectionBlobUri, UriKind.Absolute, out var blob)
            || blob.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(blob.Query) || !string.IsNullOrEmpty(blob.UserInfo))
            throw new InvalidOperationException("Hosted Web requires HTTPS Runtime:PublicOrigin, DataProtectionBlobUri and its explicit ManagedIdentityClientId.");
        var apiId = builder.Configuration["ServiceAuthentication:ApiClientId"];
        if (!Guid.TryParse(apiId, out _)) throw new InvalidOperationException("Hosted Web requires ServiceAuthentication:ApiClientId.");
        if (!Uri.TryCreate(builder.Configuration["ReplayApi:BaseUrl"], UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Hosted Web requires an HTTPS ReplayApi:BaseUrl.");

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<TokenCredential>(new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId)));
        builder.Services.AddTransient<ReplayServiceTokenHandler>();
        builder.Services.AddHealthChecks().AddCheck<ReplayServiceHealth>("internal-api-service-auth");
        builder.Services.AddDataProtection().SetApplicationName("Matchmate.Web")
            .PersistKeysToAzureBlobStorage(blob, new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId)));
        // ACA is the sole ingress and overwrites forwarded proto. Consume one hop, never forwarded host.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
            Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = settings.PublicOrigin.TrimEnd('/') + options.CallbackPath;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = settings.PublicOrigin.TrimEnd('/') + options.SignedOutCallbackPath;
                    return Task.CompletedTask;
                };
            });
    }

    public static bool IsHttpsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var origin)
        && origin.Scheme == Uri.UriSchemeHttps && origin.AbsolutePath == "/" && string.IsNullOrEmpty(origin.UserInfo)
        && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment);
}

public sealed class ReplayServiceHealth(ReplayApiClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await client.HostingStatusAsync(cancellationToken);
            if (status.State == "ready")
            {
                var catalog = await client.FootballCatalogAsync(cancellationToken);
                if (catalog.Length is < 1 or > 32) return HealthCheckResult.Unhealthy("Published dataset catalog is invalid.");
                foreach (var dataset in catalog)
                {
                    if (dataset.Schedule is null || dataset.Fixture is null || dataset.Schedule.Segments.Length == 0) return HealthCheckResult.Unhealthy("Common presentation context is incomplete.");
                    var query = new Matchmate.Contracts.FootballQuery(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId,
                        dataset.Schedule.Segments[0].CursorStartMs, "retrospective_replay", dataset.Schedule.PolicyId, dataset.Schedule.PolicyRevision);
                    var view = await client.FootballViewAsync(query, cancellationToken);
                    if (view.Context != query || view.Frame is null || view.Frame.ActiveSubjectIds.Length == 0 || view.Dataset.Manifest.DatasetVersion != dataset.Manifest.DatasetVersion)
                        return HealthCheckResult.Unhealthy("Common dataset query context is inconsistent.");
                }
                return HealthCheckResult.Healthy("Authenticated common catalog and context-bound views verified.");
            }
            return status.State == "awaiting-pipeline"
                ? HealthCheckResult.Healthy("Authenticated internal API reachable; dataset pipeline remains pending.")
                : HealthCheckResult.Unhealthy("Unexpected internal API hosting state.");
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Authenticated internal API is unavailable.");
        }
    }
}

public sealed class ReplayServiceTokenHandler(TokenCredential credential, IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clientId = configuration["ServiceAuthentication:ApiClientId"];
        if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("Replay API client ID is not configured.");
        var token = await credential.GetTokenAsync(new TokenRequestContext([$"api://{clientId}/.default"]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
