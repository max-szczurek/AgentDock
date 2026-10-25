using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using AgentDock.Tests.Infrastructure;

namespace AgentDock.Tests.Integration;

[TestClass]
public class SkillPagesTests
{
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string token, params (string Name, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
            content.Add(part, "files", name);
        }
        return await c.PostAsync("/Skills?handler=Upload", content);
    }

    [TestMethod]
    public async Task V7_UploadEditPreviewDelete_AndIsolation()
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

            var page = await ca.GetStringAsync("/Skills");
            StringAssert.Contains(page, "skills-empty");
            var up = await UploadAsync(ca, WebHelpers.Token(page),
                ("refunds.md", Encoding.UTF8.GetBytes("---\nname: Refunds\n---\n# Policy\n<script>x</script>")),
                ("notes.txt", "x"u8.ToArray()), ("bin.md", [0xC3, 0x28]));
            Assert.AreEqual(HttpStatusCode.OK, up.StatusCode);
            var html = await up.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "refunds.md: saved");
            StringAssert.Contains(html, "notes.txt");
            StringAssert.Contains(html, "bin.md: File is not valid UTF-8.");
            var id = Regex.Match(html, "data-skill-id=\"([0-9a-f-]{36})\"").Groups[1].Value;

            var edit = await ca.GetStringAsync($"/Skills/Edit/{id}");
            StringAssert.Contains(edit, "Policy");
            Assert.IsFalse(edit.Contains("<script>x</script>"), "raw HTML is not rendered in preview");

            var prev = await WebHelpers.PostAsync(ca, $"/Skills/Edit/{id}?handler=Preview", WebHelpers.Token(edit), new() { ["Content"] = "## Live **preview** [x](javascript:alert(1))" });
            var prevHtml = await prev.Content.ReadAsStringAsync();
            StringAssert.Contains(prevHtml, "<h2");
            Assert.IsFalse(prevHtml.Contains("javascript:"));

            var save = await WebHelpers.PostAsync(ca, $"/Skills/Edit/{id}", WebHelpers.Token(edit), new() { ["Content"] = "# Updated" });
            Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode);
            StringAssert.Contains(await ca.GetStringAsync($"/Skills/Edit/{id}"), "v2");

            Assert.AreEqual(HttpStatusCode.NotFound, (await cb.GetAsync($"/Skills/Edit/{id}")).StatusCode);
            var tokenB = WebHelpers.Token(await cb.GetStringAsync("/Skills"));
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(cb, $"/Skills/Edit/{id}", tokenB, new() { ["Content"] = "hijack" })).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(cb, $"/Skills/Edit/{id}?handler=Delete", tokenB)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await WebHelpers.PostAsync(cb, $"/Skills/Edit/{id}?handler=Preview", tokenB, new() { ["Content"] = "x" })).StatusCode);
            StringAssert.Contains(await cb.GetStringAsync("/Skills"), "skills-empty");

            var del = await WebHelpers.PostAsync(ca, $"/Skills/Edit/{id}?handler=Delete", WebHelpers.Token(await ca.GetStringAsync($"/Skills/Edit/{id}")));
            Assert.AreEqual(HttpStatusCode.Redirect, del.StatusCode);
            StringAssert.Contains(await ca.GetStringAsync("/Skills"), "skills-empty");
        }
        finally { await TestDb.DeleteUserAsync(a); await TestDb.DeleteUserAsync(b); }
    }

    [TestMethod]
    public async Task V7_AgentEditPage_AssignsSkills_ShowsAssembledPrompt_AndBlocksDeleteWhileUsed()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        var secret = WebHelpers.NewSecret();
        try
        {
            await using var f = WebHelpers.Factory(secret, email);
            var c = f.NewClient();
            await WebHelpers.LoginAsync(c, email, secret);
            await UploadAsync(c, WebHelpers.Token(await c.GetStringAsync("/Skills")), ("one.md", "ONE-BODY"u8.ToArray()), ("two.md", "TWO-BODY"u8.ToArray()));
            var list = await c.GetStringAsync("/Skills");
            var ids = Regex.Matches(list, "data-skill-id=\"([0-9a-f-]{36})\"").Select(m => m.Groups[1].Value).ToList();

            var form = await c.GetStringAsync("/Agents/Edit");
            Assert.IsFalse(form.Contains("id=\"skills\""), "skills section only for saved agents");
            var create = await WebHelpers.PostAsync(c, "/Agents/Edit", WebHelpers.Token(form), new Dictionary<string, string>
            {
                ["Input.Name"] = "Skilled", ["Input.SystemPrompt"] = "Be brief.", ["Input.DefaultFamily"] = "Sonnet", ["Input.MaxOutputTokens"] = "4096",
                ["Input.AllowHaiku"] = "true", ["Input.AllowSonnet"] = "true", ["Input.AllowOpus"] = "true",
                ["Input.Title"] = "t", ["Input.PrimaryColor"] = "#4F46E5", ["Input.TextOnPrimary"] = "#FFFFFF", ["Input.PanelBackground"] = "#FFFFFF",
                ["Input.IconPreset"] = "chat", ["Input.Position"] = "bottom-right", ["Input.PanelWidth"] = "380", ["Input.PanelHeight"] = "600",
                ["Input.TurnsPerSessionPerHour"] = "30", ["Input.TurnsPerIpPerHour"] = "60", ["Input.TurnsPerDay"] = "500", ["Input.MaxMessageChars"] = "4000",
            });
            var editUrl = create.Headers.Location!.ToString();
            var edit = await c.GetStringAsync(editUrl);
            StringAssert.Contains(edit, "id=\"skills\"");

            var agentId = Regex.Match(editUrl, "[0-9a-f-]{36}").Value;
            var save = await WebHelpers.PostAsync(c, $"/Agents/Edit/{agentId}?handler=SaveSkills", WebHelpers.Token(edit), new()
            {
                ["SkillPicks[0].Id"] = ids[0], ["SkillPicks[0].Selected"] = "true", ["SkillPicks[0].Order"] = "2",
                ["SkillPicks[1].Id"] = ids[1], ["SkillPicks[1].Selected"] = "true", ["SkillPicks[1].Order"] = "1",
            });
            Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode);
            edit = await c.GetStringAsync(editUrl);
            StringAssert.Contains(edit, "Skills saved.");
            var assembled = Regex.Match(edit, "id=\"assembled-text\"[^>]*>(.*?)</pre>", RegexOptions.Singleline).Groups[1].Value;
            Assert.IsTrue(assembled.IndexOf("Be brief.") > 0 && assembled.IndexOf("Be brief.") < assembled.IndexOf("ONE-BODY") && assembled.IndexOf("TWO-BODY") < assembled.IndexOf("ONE-BODY"), assembled);

            var del = await WebHelpers.PostAsync(c, $"/Skills/Edit/{ids[0]}?handler=Delete", WebHelpers.Token(await c.GetStringAsync($"/Skills/Edit/{ids[0]}")));
            Assert.AreEqual(HttpStatusCode.OK, del.StatusCode, "blocked: re-renders with an error");
            StringAssert.Contains(await del.Content.ReadAsStringAsync(), "used by 1 agent");
        }
        finally { await TestDb.DeleteUserAsync(email); }
    }
}
