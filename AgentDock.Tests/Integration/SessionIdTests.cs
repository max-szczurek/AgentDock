using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock.Tests.Integration;

public partial class McpToolTests
{
    private static async Task<HttpResponseMessage> RawSessionAsync(Env env, Guid agentId, string? sessionId)
    {
        var c = env.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/agents/{agentId}/sessions") { Content = JsonContent.Create(new { hostOrigin = Host, sessionId }) };
        req.Headers.Add("Sec-Fetch-Site", "same-origin");
        return await c.SendAsync(req);
    }

    private static async Task<List<string>> ListIdsAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonArray>("/api/widget/v1/conversations"))!.Select(x => x!["id"]!.GetValue<string>()).ToList();

    [TestMethod]
    public async Task V17_SameSessionId_SharesHistoryAcrossBrowsers_ButOthersSeeNothing()
    {
        await using var env = await Env.StartAsync();
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("Hello there");
        var sid = Guid.NewGuid().ToString();
        var browserA = await env.SessionAsync(sid);
        var first = await TurnAsync(browserA, "remember me");
        var conv = first.Last().Data["id"]!.GetValue<Guid>();

        var browserB = await env.SessionAsync(sid.ToUpperInvariant());
        CollectionAssert.AreEqual(new[] { conv.ToString() }, await ListIdsAsync(browserB));
        var history = await browserB.GetFromJsonAsync<JsonObject>($"/api/widget/v1/conversations/{conv}/messages");
        Assert.AreEqual("remember me", history!["messages"]![0]!["text"]!.GetValue<string>());

        await TurnAsync(browserB, "and continue", conv);
        var again = await browserA.GetFromJsonAsync<JsonObject>($"/api/widget/v1/conversations/{conv}/messages");
        Assert.AreEqual(4, again!["messages"]!.AsArray().Count);

        var otherGuid = await env.SessionAsync(Guid.NewGuid().ToString());
        Assert.AreEqual(0, (await ListIdsAsync(otherGuid)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await otherGuid.GetAsync($"/api/widget/v1/conversations/{conv}/messages")).StatusCode);
        var plain = await env.SessionAsync();
        Assert.AreEqual(0, (await ListIdsAsync(plain)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await plain.GetAsync($"/api/widget/v1/conversations/{conv}/messages")).StatusCode);
    }

    [TestMethod]
    public async Task V17_WithoutSessionId_BrowserSessionsStayIsolated()
    {
        await using var env = await Env.StartAsync();
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("Hi");
        var a = await env.SessionAsync();
        var conv = (await TurnAsync(a, "mine")).Last().Data["id"]!.GetValue<Guid>();
        var b = await env.SessionAsync();
        Assert.AreEqual(0, (await ListIdsAsync(b)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await b.GetAsync($"/api/widget/v1/conversations/{conv}/messages")).StatusCode);
        Assert.AreEqual(1, (await ListIdsAsync(a)).Count);
    }

    [TestMethod]
    public async Task V17_SameSessionId_OnAnotherAgent_IsIsolated()
    {
        await using var env = await Env.StartAsync();
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("Hi");
        Agent second;
        using (var scope = env.Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IAgentService>().CreateAsync(env.AccountId,
                new AgentInput { Name = "Second", SystemPrompt = "x", Enabled = true, Origins = [Host] });
            second = created.Agent!;
        }
        var sid = Guid.NewGuid().ToString();
        var one = await env.SessionAsync(sid);
        var conv = (await TurnAsync(one, "hi")).Last().Data["id"]!.GetValue<Guid>();
        var two = await env.SessionAsync(sid, second.Id);
        Assert.AreEqual(0, (await ListIdsAsync(two)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await two.GetAsync($"/api/widget/v1/conversations/{conv}/messages")).StatusCode);
    }

    [TestMethod]
    public async Task V17_MalformedOrEmptySessionIds_AreRejected()
    {
        await using var env = await Env.StartAsync();
        foreach (var bad in new[] { "order-123", "not a guid", Guid.Empty.ToString(), "{3f2b8c1e-9a4d-4c1b-8f0e-2d7a6b5c4e11}", new string('a', 500) })
        {
            var r = await RawSessionAsync(env, env.Agent.Id, bad);
            Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, bad);
            StringAssert.Contains(await r.Content.ReadAsStringAsync(), "invalid_session_id");
        }
        Assert.AreEqual(HttpStatusCode.OK, (await RawSessionAsync(env, env.Agent.Id, null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await RawSessionAsync(env, env.Agent.Id, "")).StatusCode);
    }

    [TestMethod]
    public async Task V17_SettingOff_RejectsSessionIds_AndTheGuidIsNeverInUrlsOrEmbedPage()
    {
        await using var env = await Env.StartAsync();
        var sid = Guid.NewGuid().ToString();
        Assert.AreEqual(HttpStatusCode.OK, (await RawSessionAsync(env, env.Agent.Id, sid)).StatusCode);

        var visitor = env.Factory.CreateClient();
        var embed = await visitor.GetStringAsync($"/embed/{env.Agent.Id}");
        Assert.IsFalse(embed.Contains(sid));

        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            (await db.Agents.SingleAsync(a => a.Id == env.Agent.Id)).AllowExternalSessionId = false;
            await db.SaveChangesAsync();
        }
        var off = await RawSessionAsync(env, env.Agent.Id, sid);
        Assert.AreEqual(HttpStatusCode.BadRequest, off.StatusCode);
        StringAssert.Contains(await off.Content.ReadAsStringAsync(), "session_id_not_allowed");
        Assert.AreEqual(HttpStatusCode.OK, (await RawSessionAsync(env, env.Agent.Id, null)).StatusCode);
    }

    [TestMethod]
    public async Task V17_EmbedPage_ExplainsTheSessionIdAndWarnsItIsASecret()
    {
        await using var env = await Env.StartAsync();
        var page = await env.Owner.GetStringAsync($"/Agents/Embed/{env.Agent.Id}");
        StringAssert.Contains(page, "data-session-id");
        StringAssert.Contains(page, "like a password");
    }
}
