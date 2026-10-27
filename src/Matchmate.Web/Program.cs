using Matchmate.Web.Components;
using Matchmate.ServiceDefaults;
using Matchmate.Web;
using Matchmate.Web.Authentication;
using Matchmate.Web.Chat;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using Azure.Core;
using Azure.Identity;
using System.Globalization;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var hosted = RuntimeMode.Validate(builder.Configuration, builder.Environment);
builder.Services.AddExternalIdAuthentication();
builder.Services.Configure<MatchChatOptions>(builder.Configuration.GetSection(MatchChatOptions.Section));
builder.Services.Configure<DecisionRoutingOptions>(builder.Configuration.GetSection(DecisionRoutingOptions.Section));
builder.Services.Configure<MatchChatDiagnosticsOptions>(builder.Configuration.GetSection(MatchChatDiagnosticsOptions.Section));
builder.Services.AddSingleton<MatchChatDiagnostics>();
builder.Services.AddOpenTelemetry().WithTracing(traces => traces.AddSource(MatchChatDiagnostics.ActivitySourceName));
builder.Services.AddHttpClient<IMatchWorkflowRouter, DecisionWorkflowRouter>()
    .AddHttpMessageHandler(services => new MatchChatTimingHandler(services.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.RouterHttp))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IMatchChatModel, AzureMatchChatModel>()
    .AddHttpMessageHandler(services => new MatchChatTimingHandler(services.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.NarratorHttp))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<MatchChatSession>();
builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en-GB");
    // UI preference must not change parsing, geometry or application-wide numeric formatting.
    options.SupportedCultures = [CultureInfo.GetCultureInfo("en-GB")];
    options.SupportedUICultures = MatchLanguages.Supported.Select(MatchLanguages.Culture).ToArray();
    options.RequestCultureProviders = [new CookieRequestCultureProvider()];
});
builder.Services.AddScoped(_ => new MatchLanguageState(MatchLanguages.FromCulture(CultureInfo.CurrentUICulture)));
// League conversations use their own routing/evidence/prompt and per-circuit history.
builder.Services.AddHttpClient<Matchmate.Web.LeagueChat.ILeagueWorkflowRouter, Matchmate.Web.LeagueChat.DecisionLeagueWorkflowRouter>()
    .AddHttpMessageHandler(services => new MatchChatTimingHandler(services.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.RouterHttp))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<Matchmate.Web.LeagueChat.ILeagueChatModel, Matchmate.Web.LeagueChat.AzureLeagueChatModel>()
    .AddHttpMessageHandler(services => new MatchChatTimingHandler(services.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.NarratorHttp))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<Matchmate.Web.LeagueChat.LeagueChatSession>();
Matchmate.Web.HistoricalChat.HistoricalChatRegistration.AddHistoricalMatchChat(builder.Services, builder.Configuration);
if (hosted) builder.ConfigureHostedWeb();
else builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new DevelopmentModelCredential(new DefaultAzureCredential()) : new DefaultAzureCredential());
builder.Services.AddHttpsRedirection(options =>
{
    if (builder.Environment.IsDevelopment()) options.HttpsPort = 7410;
});
var replayClient = builder.Services.AddHttpClient<ReplayApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ReplayApi:BaseUrl"] ?? "http://api");
    client.Timeout = TimeSpan.FromSeconds(10);
});
if (hosted) replayClient.AddHttpMessageHandler<ReplayServiceTokenHandler>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
if (hosted) app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
// Preserve rejected POST/SignalR statuses instead of re-executing them as Razor form posts.
app.UseStatusCodePages("text/plain", "This page is unavailable. Return to /account/sign-in.");
if (!hosted) app.UseHttpsRedirection();
app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") }).AllowAnonymous();
app.MapExternalIdEndpoints();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ConfigureConnection = connection => connection.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization(ExternalIdAuthentication.TesterPolicy);

app.Run();

namespace Matchmate.Web { public partial class Program; }
