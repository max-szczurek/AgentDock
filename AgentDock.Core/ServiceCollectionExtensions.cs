using AgentDock.Core.Outbound;
using AgentDock.Core.Runtime;
using AgentDock.Core.Security;
using AgentDock.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers secret storage, credential and model services. The IKeyWrapper (Key Vault in the app,
    /// a local wrapper in unit tests) is registered by the host.
    /// </summary>
    public static IServiceCollection AddAgentDockCore(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<ISecretProtector>(sp => new EnvelopeSecretProtector(sp.GetRequiredService<IKeyWrapper>()));
        services.AddScoped<SecretStore>();
        services.AddScoped<ISecretStore>(sp => sp.GetRequiredService<SecretStore>());
        services.AddHttpClient<IAnthropicModelsClient, AnthropicModelsClient>(c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IAnthropicCredentialService, AnthropicCredentialService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<ISkillService, SkillService>();
        services.AddHttpClient<IClaudeGateway, ClaudeGateway>(c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com");
            c.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<SlidingWindowLimiter>();
        services.AddSingleton<ActiveTurnRegistry>();
        services.AddScoped<IVisitorSessionService, VisitorSessionService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IChatTurnService, ChatTurnService>();
        services.AddScoped<AgentDock.Core.Mcp.IMcpGateway, AgentDock.Core.Mcp.McpGateway>();
        services.AddScoped<AgentDock.Core.Mcp.IConnectionService, AgentDock.Core.Mcp.ConnectionService>();
        services.AddScoped<AgentDock.Core.Mcp.IAgentToolService, AgentDock.Core.Mcp.AgentToolService>();
        services.AddScoped<AgentDock.Core.Mcp.IToolRegistryFactory, AgentDock.Core.Mcp.ToolRegistryFactory>();
        services.AddScoped<AgentDock.Core.Mcp.IToolExecutor, AgentDock.Core.Mcp.ToolExecutor>();
        services.AddScoped<IWebSearchProbe, WebSearchProbe>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, new OutboundOptions());
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IIpPolicy, DefaultIpPolicy>();
        services.AddSingleton<IOutboundHttpClientFactory>(sp => new OutboundHttpClientFactory(
            sp.GetRequiredService<OutboundOptions>(), sp.GetRequiredService<IDnsResolver>(), sp.GetRequiredService<IIpPolicy>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboundClient>>()));
        return services;
    }
}
