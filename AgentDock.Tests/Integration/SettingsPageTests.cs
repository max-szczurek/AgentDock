using AgentDock.Core.Security;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentDock.Tests.Integration;

[TestClass]
public class SettingsPageTests
{
    private static readonly string Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    private const string Key = "sk-ant-api03-PAGETESTKEY-cccccccccccccccccccccc";

    [TestMethod]
    public async Task SaveKey_NeverEchoesKey_ShowsStatusAndModels_AndRemoveClearsIt()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        var fake = new FakeModelsClient();
        try
        {
            await using var f = new AppFactory("Staging", new()
            {
                ["AGENTDOCK_DEV_LOGIN_SECRET"] = Secret,
                ["AGENTDOCK_DEV_LOGIN_EMAILS"] = email,
                ["AGENTDOCK_KEYVAULT_KEY_ID"] = "https://unused.invalid/keys/x",
            }, services =>
            {
                services.RemoveAll<IKeyWrapper>();
                services.AddSingleton<IKeyWrapper>(new LocalKeyWrapper());
                services.RemoveAll<IAnthropicModelsClient>();
                services.AddSingleton<IAnthropicModelsClient>(fake);
            });
            var c = f.NewClient();

            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, (await c.GetAsync("/Settings")).StatusCode, "requires sign-in");

            var login = new HttpRequestMessage(HttpMethod.Post, "/dev/login") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }) };
            login.Headers.Add("X-Dev-Login-Secret", Secret);
            await c.SendAsync(login);

            var page = await c.GetStringAsync("/Settings");
            StringAssert.Contains(page, "Not set");
            var token = Antiforgery(page);

            var post = await c.PostAsync("/Settings?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["apiKey"] = Key, ["__RequestVerificationToken"] = token }));
            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, post.StatusCode);
            Assert.IsFalse((await post.Content.ReadAsStringAsync()).Contains("PAGETESTKEY"));

            page = await c.GetStringAsync("/Settings");
            StringAssert.Contains(page, "Valid");
            StringAssert.Contains(page, "sk-ant-");
            StringAssert.Contains(page, "ccc</code>");
            StringAssert.Contains(page, "claude-opus-5-5");
            StringAssert.Contains(page, "claude-haiku-5-5");
            Assert.IsFalse(page.Contains("PAGETESTKEY"), "key must never be rendered back");
            CollectionAssert.Contains(fake.KeysSeen, Key);

            await c.PostAsync("/Settings?handler=Remove", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Antiforgery(page) }));
            StringAssert.Contains(await c.GetStringAsync("/Settings"), "Not set");
        }
        finally { await TestDb.DeleteUserAsync(email); }
    }

    private static string Antiforgery(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.IsTrue(m.Success, "antiforgery token present");
        return m.Groups[1].Value;
    }
}
