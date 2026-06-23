using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReceiptAnalyzer.Agent.Claude;
using ReceiptAnalyzer.Agent.OpenAi;

namespace ReceiptAnalyzer.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));

        services.AddHttpClient<ClaudeAgent>(http => { http.Timeout = TimeSpan.FromMinutes(3); });
        services.AddHttpClient<OpenAiAgent>(http => { http.Timeout = TimeSpan.FromMinutes(3); });

        services.AddSingleton<IAnalysisAgent>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return opts.Provider.ToLowerInvariant() switch
            {
                "claude" => sp.GetRequiredService<ClaudeAgent>(),
                "openai" => sp.GetRequiredService<OpenAiAgent>(),
                _ => throw new InvalidOperationException($"Unknown agent provider '{opts.Provider}'.")
            };
        });

        return services;
    }
}
