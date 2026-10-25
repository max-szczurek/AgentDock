using System.Net;
using AgentDock.Tests.Infrastructure;

namespace AgentDock.Tests.Integration;

[TestClass]
public class AgentPagesTests
{
    private static Dictionary<string, string> Form(string name, string origins = "") => new()
    {
        ["Input.Name"] = name, ["Input.Description"] = "desc", ["Input.Enabled"] = "true",
        ["Input.SystemPrompt"] = "Be brief.", ["Input.DefaultFamily"] = "Sonnet", ["Input.MaxOutputTokens"] = "4096",
        ["Input.AllowHaiku"] = "true", ["Input.AllowSonnet"] = "true", ["Input.AllowOpus"] = "true",
        ["Input.Title"] = "Ask us", ["Input.PrimaryColor"] = "#4F46E5", ["Input.TextOnPrimary"] = "#FFFFFF", ["Input.PanelBackground"] = "#FFFFFF",
        ["Input.IconPreset"] = "chat", ["Input.Position"] = "bottom-right", ["Input.PanelWidth"] = "380", ["Input.PanelHeight"] = "600",
        ["Input.WelcomeMessage"] = "Hi", ["Input.Placeholder"] = "Type", ["Input.OriginsText"] = origins,
        ["Input.TurnsPerSessionPerHour"] = "30", ["Input.TurnsPerIpPerHour"] = "60", ["Input.TurnsPerDay"] = "500", ["Input.MaxMessageChars"] = "4000",
    };

    [TestMethod]
    public async Task CreateEditDuplicateToggleDelete_Workflow()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        var secret = WebHelpers.NewSecret();
        try
        {
            await using var f = WebHelpers.Factory(secret, email);
            var c = f.NewClient();
            await WebHelpers.LoginAsync(c, email, secret);

            var home = await c.GetStringAsync("/");
            StringAssert.Contains(home, "empty-state");

            var form = await c.GetStringAsync("/Agents/Edit");
            var create = await WebHelpers.PostAsync(c, "/Agents/Edit", WebHelpers.Token(form), Form("Page bot", "https://a.example.com\nhttp://localhost:3000"));
            Assert.AreEqual(HttpStatusCode.Redirect, create.StatusCode);
            var editUrl = create.Headers.Location!.ToString();
            StringAssert.Contains(editUrl, "/Agents/Edit/");

            var edit = await c.GetStringAsync(editUrl);
            StringAssert.Contains(edit, "Page bot");
            StringAssert.Contains(edit, "https://a.example.com");
            StringAssert.Contains(edit, "Saved.");

            var changed = Form("Renamed bot", "https://b.example.com");
            changed["Input.ConcurrencyStamp"] = System.Text.RegularExpressions.Regex.Match(edit, "name=\"Input.ConcurrencyStamp\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;
            var save = await WebHelpers.PostAsync(c, editUrl, WebHelpers.Token(edit), changed);
            Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode);

            home = await c.GetStringAsync("/");
            StringAssert.Contains(home, "Renamed bot");
            var id = System.Text.RegularExpressions.Regex.Match(home, "data-agent-id=\"([0-9a-f-]{36})\"").Groups[1].Value;

            var dup = await WebHelpers.PostAsync(c, $"/?handler=Duplicate&id={id}", WebHelpers.Token(home));
            Assert.AreEqual(HttpStatusCode.Redirect, dup.StatusCode);
            home = await c.GetStringAsync("/");
            StringAssert.Contains(home, "Copy of Renamed bot");

            var toggle = await WebHelpers.PostAsync(c, $"/?handler=Toggle&id={id}&enable=false", WebHelpers.Token(home));
            Assert.AreEqual(HttpStatusCode.Redirect, toggle.StatusCode);

            var del = await WebHelpers.PostAsync(c, $"/?handler=Delete&id={id}", WebHelpers.Token(await c.GetStringAsync("/")));
            Assert.AreEqual(HttpStatusCode.Redirect, del.StatusCode);
            home = await c.GetStringAsync("/");
            Assert.IsFalse(home.Contains(">Renamed bot<"));
            StringAssert.Contains(home, "Copy of Renamed bot");
            Assert.AreEqual(HttpStatusCode.NotFound, (await c.GetAsync($"/Agents/Edit/{id}")).StatusCode);
        }
        finally { await TestDb.DeleteUserAsync(email); }
    }

    [TestMethod]
    public async Task InvalidForm_ShowsErrorsAndKeepsInput()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        var secret = WebHelpers.NewSecret();
        try
        {
            await using var f = WebHelpers.Factory(secret, email);
            var c = f.NewClient();
            await WebHelpers.LoginAsync(c, email, secret);
            var form = await c.GetStringAsync("/Agents/Edit");
            var bad = Form("Bad bot", "https://example.com/path");
            bad["Input.PrimaryColor"] = "red";
            var r = await WebHelpers.PostAsync(c, "/Agents/Edit", WebHelpers.Token(form), bad);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "id=\"errors\"");
            StringAssert.Contains(html, "cannot have a path");
            StringAssert.Contains(html, "#RRGGBB");
            StringAssert.Contains(html, "Bad bot");
            Assert.IsFalse((await c.GetStringAsync("/")).Contains("Bad bot"));
        }
        finally { await TestDb.DeleteUserAsync(email); }
    }

    [TestMethod]
    public async Task OtherUser_Gets404OnEveryAgentRoute()
    {
        TestDb.RequireSql();
        var owner = TestDb.NewTestEmail();
        var intruder = TestDb.NewTestEmail();
        var secret = WebHelpers.NewSecret();
        try
        {
            await using var f = WebHelpers.Factory(secret, owner, intruder);
            var a = f.NewClient();
            await WebHelpers.LoginAsync(a, owner, secret);
            var create = await WebHelpers.PostAsync(a, "/Agents/Edit", WebHelpers.Token(await a.GetStringAsync("/Agents/Edit")), Form("Owner bot", "https://a.example.com"));
            var id = create.Headers.Location!.ToString().Split('/').Last().Split('?')[0];

            var b = f.NewClient();
            await WebHelpers.LoginAsync(b, intruder, secret);
            var token = WebHelpers.Token(await b.GetStringAsync("/Settings"));

            Assert.AreEqual(HttpStatusCode.NotFound, (await b.GetAsync($"/Agents/Edit/{id}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(b, $"/Agents/Edit/{id}", token, Form("hijacked"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(b, $"/?handler=Duplicate&id={id}", token)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(b, $"/?handler=Toggle&id={id}&enable=false", token)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(b, $"/?handler=Delete&id={id}", token)).StatusCode);
            Assert.IsFalse((await b.GetStringAsync("/")).Contains("Owner bot"));

            StringAssert.Contains(await a.GetStringAsync($"/Agents/Edit/{id}"), "Owner bot");
        }
        finally { await TestDb.DeleteUserAsync(owner); await TestDb.DeleteUserAsync(intruder); }
    }

    [TestMethod]
    public async Task AgentRoutes_RequireSignIn()
    {
        TestDb.RequireSql();
        await using var f = WebHelpers.Factory(WebHelpers.NewSecret(), "x@agentdock.test");
        var c = f.NewClient();
        foreach (var url in new[] { "/", "/Agents/Edit", $"/Agents/Edit/{Guid.NewGuid()}" })
            Assert.AreEqual(HttpStatusCode.Redirect, (await c.GetAsync(url)).StatusCode, url);
    }
}
