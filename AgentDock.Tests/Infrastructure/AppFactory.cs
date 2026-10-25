using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentDock.Tests.Infrastructure;

public class AppFactory(string environment, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("AGENTDOCK_SQL_CONNECTION_STRING", TestDb.ConnectionString);
        // Real Key Vault is not needed for these tests; the key ring stays unprotected-at-rest in the shared table
        // only when the setting is absent, so force it empty.
        builder.UseSetting("AGENTDOCK_DATAPROTECTION_KEY_ID", "");
        builder.UseSetting("AGENTDOCK_GOOGLE_CLIENT_ID", "");
        builder.UseSetting("AGENTDOCK_GOOGLE_CLIENT_SECRET", "");
        foreach (var (k, v) in settings ?? [])
            builder.UseSetting(k, v);
        if (configureServices != null) builder.ConfigureServices(configureServices);
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
}
