using System.Net;
using System.Text.RegularExpressions;
using AgentDock.Tests.Infrastructure;

namespace AgentDock.Tests.Integration;

[TestClass]
public class ConnectionPagesTests
{
    [TestMethod]
    public async Task Connections_CreateEdit_NeverRenderToken_AndAreIsolated()
    {
        TestDb.RequireSql();
        var a = TestDb.NewTestEmail(); var b = TestDb.NewTestEmail();
        var secret = WebHelpers.NewSecret();
        try
        {
            await using var f = WebHelpers.Factory(secret, a, b);
            var ca = f.NewClient(); var cb = f.NewClient();
            await WebHelpers.LoginAsync(ca, a, secret);
            await WebHelpers.LoginAsync(cb, b, secret);

            var index = await ca.GetStringAsync("/Connections");
            StringAssert.Contains(index, "connections-empty");

            var form = await ca.GetStringAsync("/Connections/Edit");
            var tpl = await WebHelpers.PostAsync(ca, "/Connections/Edit?handler=Template", WebHelpers.Token(form));
            var tplHtml = await tpl.Content.ReadAsStringAsync();
            StringAssert.Contains(tplHtml, "https://api.githubcopilot.com/mcp/");
            StringAssert.Contains(tplHtml, "X-MCP-Readonly: true");

            var bad = await WebHelpers.PostAsync(ca, "/Connections/Edit", WebHelpers.Token(form), new()
            { ["Input.Name"] = "Bad", ["Input.BaseUrl"] = "http://169.254.169.254/mcp", ["Input.AuthType"] = "None", ["Input.TimeoutSeconds"] = "30" });
            Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode, "validation errors re-render the form");

            var save = await WebHelpers.PostAsync(ca, "/Connections/Edit", WebHelpers.Token(form), new()
            {
                ["Input.Name"] = "GitHub", ["Input.BaseUrl"] = "https://api.githubcopilot.com/mcp/", ["Input.AuthType"] = "Bearer",
                ["Input.BearerToken"] = "ghp_SUPERSECRETTOKEN123", ["HeadersText"] = "X-MCP-Readonly: true", ["Input.TimeoutSeconds"] = "30",
            });
            Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode, await save.Content.ReadAsStringAsync());
            var edit = await ca.GetStringAsync(save.Headers.Location!.ToString());
            Assert.IsFalse(edit.Contains("ghp_SUPERSECRETTOKEN123"), "token is write-only");
            StringAssert.Contains(edit, "X-MCP-Readonly: true");
            var list = await ca.GetStringAsync("/Connections");
            Assert.IsFalse(list.Contains("ghp_SUPERSECRETTOKEN123"));
            var id = Regex.Match(list, "data-connection-id=\"([0-9a-f-]{36})\"").Groups[1].Value;

            Assert.AreEqual(HttpStatusCode.NotFound, (await cb.GetAsync($"/Connections/Edit/{id}")).StatusCode);
            var tokenB = WebHelpers.Token(await cb.GetStringAsync("/Connections/Edit"));
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(cb, $"/Connections/Edit/{id}?handler=Delete", tokenB)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(cb, $"/Connections/Edit/{id}?handler=Test", tokenB)).StatusCode);

            var del = await WebHelpers.PostAsync(ca, $"/Connections/Edit/{id}?handler=Delete", WebHelpers.Token(edit));
            Assert.AreEqual(HttpStatusCode.Redirect, del.StatusCode);
            StringAssert.Contains(await ca.GetStringAsync("/Connections"), "connections-empty");
        }
        finally { await TestDb.DeleteUserAsync(a); await TestDb.DeleteUserAsync(b); }
    }
}
