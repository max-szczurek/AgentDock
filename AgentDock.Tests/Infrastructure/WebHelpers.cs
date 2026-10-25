using System.Text.RegularExpressions;

namespace AgentDock.Tests.Infrastructure;

public static class WebHelpers
{
    public static string NewSecret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    public static AppFactory Factory(string secret, params string[] emails) => new("Staging", new()
    {
        ["AGENTDOCK_DEV_LOGIN_SECRET"] = secret,
        ["AGENTDOCK_DEV_LOGIN_EMAILS"] = string.Join(",", emails),
        ["AGENTDOCK_KEYVAULT_KEY_ID"] = "https://unused.invalid/keys/x",
    }, services =>
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<AgentDock.Core.Security.IKeyWrapper>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<AgentDock.Core.Security.IKeyWrapper>(services, new AgentDock.Core.Security.LocalKeyWrapper());
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<AgentDock.Core.Services.IAnthropicModelsClient>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<AgentDock.Core.Services.IAnthropicModelsClient>(services, new FakeModelsClient());
    });

    public static async Task LoginAsync(HttpClient c, string email, string secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/dev/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }),
        };
        req.Headers.Add("X-Dev-Login-Secret", secret);
        var r = await c.SendAsync(req);
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, r.StatusCode, "dev login");
    }

    public static string Token(string html)
    {
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.IsTrue(m.Success, "antiforgery token present");
        return m.Groups[1].Value;
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient c, string url, string token, Dictionary<string, string>? fields = null)
    {
        var f = new Dictionary<string, string>(fields ?? []) { ["__RequestVerificationToken"] = token };
        return c.PostAsync(url, new FormUrlEncodedContent(f));
    }
}
