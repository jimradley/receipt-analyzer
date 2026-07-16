using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReceiptAnalyzer.Agent.Claude;
using ReceiptAnalyzer.Agent.ClaudeCode;
using ReceiptAnalyzer.Agent.OpenAi;

namespace ReceiptAnalyzer.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <param name="outputDir">
    /// Reports:OutputDir, used only to resolve a default <see cref="ClaudeCodeOptions.TmpDir"/> when
    /// none is configured. Optional — callers that don't use the "claude-code" provider can omit it.
    /// </param>
    public static IServiceCollection AddAnalysisAgent(
        this IServiceCollection services, IConfiguration configuration, string? outputDir = null)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.PostConfigure<AgentOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ClaudeCode.TmpDir) && !string.IsNullOrWhiteSpace(outputDir))
                o.ClaudeCode.TmpDir = Path.Combine(outputDir, ".state", "bridge-tmp");
        });

        services.AddHttpClient<ClaudeAgent>(http => { http.Timeout = TimeSpan.FromMinutes(3); });
        services.AddHttpClient<OpenAiAgent>(http => { http.Timeout = TimeSpan.FromMinutes(3); });
        // The bridge shells out to the host `claude` CLI, incl. real web searches — generous timeout.
        services.AddHttpClient<ClaudeCodeAgent>(http => { http.Timeout = TimeSpan.FromMinutes(15); });

        services.AddSingleton<IAnalysisAgent>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return opts.Provider.ToLowerInvariant() switch
            {
                "claude" => sp.GetRequiredService<ClaudeAgent>(),
                "openai" => sp.GetRequiredService<OpenAiAgent>(),
                "claude-code" => sp.GetRequiredService<ClaudeCodeAgent>(),
                _ => throw new InvalidOperationException($"Unknown agent provider '{opts.Provider}'.")
            };
        });

        return services;
    }
}
