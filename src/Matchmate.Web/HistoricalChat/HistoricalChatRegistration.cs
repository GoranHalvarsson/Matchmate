using Matchmate.Web.Chat;

namespace Matchmate.Web.HistoricalChat;

public static class HistoricalChatRegistration
{
    public static IServiceCollection AddHistoricalMatchChat(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var historical = services.AddOptions<HistoricalReplyModelOptions>();
        if (configuration is not null) historical.Bind(configuration.GetSection(HistoricalReplyModelOptions.Section));
        services.AddHttpClient<IHistoricalWorkflowRouter, DecisionHistoricalWorkflowRouter>()
            .AddHttpMessageHandler(s => new MatchChatTimingHandler(s.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.RouterHttp))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IHistoricalChatModel, AzureHistoricalChatModel>()
            .AddHttpMessageHandler(s => new MatchChatTimingHandler(s.GetRequiredService<MatchChatDiagnostics>(), MatchChatTimingStage.NarratorHttp))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<HistoricalChatSession>();
        return services;
    }
}
