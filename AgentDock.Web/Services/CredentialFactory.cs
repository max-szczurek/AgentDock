using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace AgentDock.Web.Services;

public static class CredentialFactory
{
    /// <summary>
    /// Managed identity on Azure; in Development the cloud-agent service principal if present, else DefaultAzureCredential.
    /// </summary>
    public static TokenCredential Create(IHostEnvironment env, IConfiguration config)
    {
        if (!env.IsDevelopment()) return new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);

        var json = config["AZURE_CREDENTIALS_AGENTDOCK"];
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new ClientSecretCredential(
                r.GetProperty("tenantId").GetString(), r.GetProperty("clientId").GetString(), r.GetProperty("clientSecret").GetString());
        }
        return new DefaultAzureCredential();
    }
}
