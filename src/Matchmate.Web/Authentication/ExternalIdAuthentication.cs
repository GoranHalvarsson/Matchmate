using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Matchmate.Web.Authentication;

public sealed class ExternalIdSettings
{
    public const string Section = "Authentication:ExternalId";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public static class ExternalIdAuthentication
{
    public const string TesterPolicy = "Tester";
    public const string SignedInPolicy = "SignedIn";

    public static IServiceCollection AddExternalIdAuthentication(this IServiceCollection services)
    {
        services.AddOptions<ExternalIdSettings>().BindConfiguration(ExternalIdSettings.Section)
            .Validate(settings => Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority)
                && authority.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(authority.Query)
                && string.IsNullOrEmpty(authority.Fragment), "Authentication:ExternalId:Authority must be an HTTPS authority.")
            .Validate(settings => Guid.TryParse(settings.ClientId, out _), "Authentication:ExternalId:ClientId must be configured.")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.ClientSecret),
                "Authentication:ExternalId:ClientSecret is required. Configure Matchmate.Web user secrets; anonymous access is not enabled.")
            .ValidateOnStart();

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "__Host-Matchmate.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.LoginPath = "/account/sign-in";
            options.AccessDeniedPath = "/account/denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(1);
            options.SlidingExpiration = false;
        })
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, _ => { });

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<ExternalIdSettings>>((options, configured) =>
            {
                var settings = configured.Value;
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email" }) options.Scope.Add(scope);
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.SignedOutRedirectUri = "/account/sign-in";
                options.AccessDeniedPath = "/account/denied";
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                // Discovery supplies CIAM's tenant-specific issuer. Keep normal token/state/nonce validation.
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/account/error");
                    return Task.CompletedTask;
                };
                options.Events.OnAuthenticationFailed = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/account/error");
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(options =>
        {
            var tester = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole("Tester").Build();
            options.AddPolicy(TesterPolicy, tester);
            options.DefaultPolicy = tester;
            options.FallbackPolicy = tester;
            options.AddPolicy(SignedInPolicy, policy => policy.RequireAuthenticatedUser());
        });
        services.AddCascadingAuthenticationState();
        return services;
    }

    public static void MapExternalIdEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/login", (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl), IsPersistent = false },
            [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/account/sign-in" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization(SignedInPolicy);
    }

    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl.Length > 2048) return "/";
        var decoded = Uri.UnescapeDataString(returnUrl);
        return IsLocalPath(returnUrl) && IsLocalPath(decoded) ? returnUrl : "/";
    }

    private static bool IsLocalPath(string value) => value.StartsWith('/')
        && (value.Length == 1 || value[1] != '/')
        && !value.Contains('\\') && !value.Any(char.IsControl);
}
