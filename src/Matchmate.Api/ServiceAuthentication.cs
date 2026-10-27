using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Matchmate.Api;

public sealed class ServiceAuthenticationSettings
{
    public const string Section = "ServiceAuthentication";
    public string TenantId { get; set; } = "";
    public string ApiClientId { get; set; } = "";
    public string WebClientId { get; set; } = "";
}

public static class ServiceAuthentication
{
    public const string ReplayPolicy = "Replay.Read";

    public static IServiceCollection AddReplayServiceAuthentication(this IServiceCollection services)
    {
        services.AddOptions<ServiceAuthenticationSettings>().BindConfiguration(ServiceAuthenticationSettings.Section)
            .Validate(settings => Guid.TryParse(settings.TenantId, out _) && Guid.TryParse(settings.ApiClientId, out _)
                && Guid.TryParse(settings.WebClientId, out _), "ServiceAuthentication requires valid TenantId, ApiClientId and WebClientId.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ServiceAuthenticationSettings>>((options, configured) =>
            {
                var settings = configured.Value;
                options.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
                options.Audience = settings.ApiClientId;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters.ValidIssuer = options.Authority;
                options.TokenValidationParameters.ValidAudience = settings.ApiClientId;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.TokenValidationParameters.NameClaimType = "name";
            });
        services.AddAuthorizationBuilder().AddPolicy(ReplayPolicy, policy => policy
            .RequireAuthenticatedUser().RequireRole(ReplayPolicy)
            .RequireAssertion(context => context.User.FindFirst("azp")?.Value is { } caller
                && Guid.TryParse(caller, out var actual)
                && Guid.TryParse(context.Resource is HttpContext http
                    ? http.RequestServices.GetRequiredService<IOptions<ServiceAuthenticationSettings>>().Value.WebClientId
                    : null, out var expected) && actual == expected));
        return services;
    }
}
