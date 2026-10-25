using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentDock.Tests.Integration;

[TestClass]
public class WidgetTests
{
    private const string ApiKey = "sk-ant-api03-WIDGETTEST-aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Host = "https://host.example.com";

    private sealed class Env : IAsyncDisposable
    {
        public string Email = TestDb.NewTestEmail();
        public string Secret = WebHelpers.NewSecret();
        public FakeClaudeGateway Claude = new();
        public AppFactory Factory = null!;
        public HttpClient Owner = null!;
        public Guid AccountId;
        public Agent Agent = null!;

        public static async Task<Env> StartAsync(Action<AgentInput>? configure = null, bool enabled = true)
        {
            TestDb.RequireSql();
            var e = new Env();
            e.Factory = new AppFactory("Staging", new()
            {
                ["AGENTDOCK_DEV_LOGIN_SECRET"] = e.Secret, ["AGENTDOCK_DEV_LOGIN_EMAILS"] = e.Email,
                ["AGENTDOCK_KEYVAULT_KEY_ID"] = "https://unused.invalid/keys/x",
            }, services =>
            {
                services.RemoveAll<AgentDock.Core.Security.IKeyWrapper>();
                services.AddSingleton<AgentDock.Core.Security.IKeyWrapper>(new AgentDock.Core.Security.LocalKeyWrapper());
                services.RemoveAll<IAnthropicModelsClient>();
                services.AddSingleton<IAnthropicModelsClient>(new FakeModelsClient());
                services.RemoveAll<IClaudeGateway>();
                services.AddSingleton<IClaudeGateway>(e.Claude);
            });

            e.Owner = e.Factory.NewClient();
            await WebHelpers.LoginAsync(e.Owner, e.Email, e.Secret);
            using var scope = e.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            e.AccountId = (await db.Accounts.AsNoTracking().SingleAsync(a => db.Users.Any(u => u.Id == a.OwnerUserId && u.Email == e.Email))).Id;
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IAnthropicCredentialService>().SaveKeyAsync(e.AccountId, ApiKey)).Success);

            var input = new AgentInput { Name = "Widget bot", SystemPrompt = "Be brief.", Enabled = enabled, Origins = [Host, "http://localhost:3000"] };
            configure?.Invoke(input);
            var created = await scope.ServiceProvider.GetRequiredService<IAgentService>().CreateAsync(e.AccountId, input);
            Assert.IsTrue(created.Success, string.Join(";", created.Errors));
            e.Agent = created.Agent!;
            return e;
        }

        public HttpClient Visitor() => Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

        public async Task<HttpClient> SessionAsync(string host = Host)
        {
            var c = Visitor();
            var r = await RawSessionAsync(c, host);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode, await r.Content.ReadAsStringAsync());
            var token = (await r.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
            c.DefaultRequestHeaders.Authorization = new("Bearer", token);
            return c;
        }

        public Task<HttpResponseMessage> RawSessionAsync(HttpClient c, string host, bool sameOrigin = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/agents/{Agent.Id}/sessions")
            { Content = JsonContent.Create(new { hostOrigin = host }) };
            if (sameOrigin) req.Headers.Add("Sec-Fetch-Site", "same-origin");
            return c.SendAsync(req);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await TestDb.DeleteUserAsync(Email);
        }
    }

    private static async Task<Guid> NewConversationAsync(HttpClient c)
    {
        var r = await c.PostAsync("/api/widget/v1/conversations", null);
        Assert.AreEqual(HttpStatusCode.Created, r.StatusCode);
        return Guid.Parse((await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
    }

    private static Task<HttpResponseMessage> TurnAsync(HttpClient c, Guid conv, string text, string? model = null) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/conversations/{conv}/turns")
        { Content = JsonContent.Create(new { text, model }) }, HttpCompletionOption.ResponseHeadersRead);

    private sealed record Sse(string Name, JsonObject Data);

    private static async Task<List<Sse>> ReadAllAsync(HttpResponseMessage r)
    {
        var list = new List<Sse>();
        await using var s = await r.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        await foreach (var e in ReadEventsAsync(reader)) list.Add(e);
        return list;
    }

    private static async IAsyncEnumerable<Sse> ReadEventsAsync(StreamReader reader)
    {
        string? name = null; string? data = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) { if (name != null && data != null) yield return new Sse(name, (JsonObject)JsonNode.Parse(data)!); name = data = null; }
            else if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
        }
    }

    private static async Task<List<string>> MessageTextsAsync(HttpClient c, Guid conv)
    {
        var r = await c.GetFromJsonAsync<JsonObject>($"/api/widget/v1/conversations/{conv}/messages");
        return r!["messages"]!.AsArray().Select(m => $"{m!["role"]}:{m["text"]}{(m["incomplete"]!.GetValue<bool>() ? "[incomplete]" : "")}").ToList();
    }

    [TestMethod]
    public async Task V5_Streaming_FirstDeltaArrivesBeforeCompletion_AndPersists()
    {
        await using var env = await Env.StartAsync();
        var gate = new TaskCompletionSource();
        env.Claude.Script = (_, ct) => FakeClaudeGateway.Gated(gate.Task, ct);
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);

        var r = await TurnAsync(c, conv, "hi");
        Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.AreEqual("text/event-stream", r.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("no-cache", r.Headers.CacheControl!.ToString());
        Assert.AreEqual("no", r.Headers.GetValues("X-Accel-Buffering").Single());

        await using var s = await r.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        var events = new List<Sse>();
        var en = ReadEventsAsync(reader).GetAsyncEnumerator();
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)))
        {
            events.Add(en.Current);
            if (en.Current.Name == "text.delta") break;
        }
        Assert.AreEqual("Hel", events.Last().Data["text"]!.GetValue<string>());
        Assert.IsFalse(events.Any(e => e.Name is "message.completed" or "done"), "delta arrives before the turn completes");
        Assert.AreEqual("turn.started", events[0].Name);

        gate.SetResult();
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20))) events.Add(en.Current);
        CollectionAssert.AreEqual(new[] { "turn.started", "text.delta", "text.delta", "message.completed", "done" }, events.Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "user:hi", "assistant:Hello" }, await MessageTextsAsync(c, conv));
    }

    [TestMethod]
    public async Task V5_Stop_CancelsUpstream_MarksIncomplete_AndExcludesFromHistory()
    {
        await using var env = await Env.StartAsync();
        var gate = new TaskCompletionSource();
        env.Claude.Script = (_, ct) => FakeClaudeGateway.Gated(gate.Task, ct);
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);

        var r = await TurnAsync(c, conv, "first");
        await using var s = await r.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        var en = ReadEventsAsync(reader).GetAsyncEnumerator();
        var events = new List<Sse>();
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)))
        {
            events.Add(en.Current);
            if (en.Current.Name == "text.delta") break;
        }

        var cancel = await c.PostAsync($"/api/widget/v1/conversations/{conv}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20))) events.Add(en.Current);

        var done = events.Single(e => e.Name == "message.completed");
        Assert.AreEqual("cancelled", done.Data["stopReason"]!.GetValue<string>());
        Assert.IsTrue(env.Claude.UpstreamCancelled, "upstream request was cancelled");
        CollectionAssert.AreEqual(new[] { "user:first", "assistant:Hel[incomplete]" }, await MessageTextsAsync(c, conv));

        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("Second reply");
        var r2 = await TurnAsync(c, conv, "second");
        await ReadAllAsync(r2);
        var sent = env.Claude.Requests.Last().Messages;
        Assert.AreEqual(2, sent.Count, "incomplete assistant message is not replayed");
        Assert.IsTrue(sent.All(m => m!["role"]!.GetValue<string>() == "user"));
    }

    [TestMethod]
    public async Task AssistantBlocks_RoundTripVerbatim_AndKeyAndSystemReachGateway()
    {
        await using var env = await Env.StartAsync();
        env.Claude.Script = (req, _) => req.Messages.Count == 1 ? Thinking() : FakeClaudeGateway.Text("ok");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        await ReadAllAsync(await TurnAsync(c, conv, "one"));
        await ReadAllAsync(await TurnAsync(c, conv, "two"));

        var second = env.Claude.Requests[1];
        var assistant = second.Messages[1]!;
        Assert.AreEqual("assistant", assistant["role"]!.GetValue<string>());
        Assert.AreEqual("SIG==", assistant["content"]![0]!["signature"]!.GetValue<string>());
        Assert.AreEqual(7, assistant["content"]![0]!["future_field"]!["x"]!.GetValue<int>());
        Assert.AreEqual(ApiKey, env.Claude.Keys[0]);
        StringAssert.Contains(second.System[1]["text"]!.GetValue<string>(), "Be brief.");
        Assert.AreEqual(4096, second.MaxTokens);

        static async IAsyncEnumerable<JsonObject> Thinking()
        {
            yield return FakeClaudeGateway.Ev("""{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":"","future_field":{"x":7}}}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG=="}}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_stop","index":0}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}""");
            yield return FakeClaudeGateway.Ev("""{"type":"content_block_stop","index":1}""");
            yield return FakeClaudeGateway.Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}""");
            await Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task V12_Conversations_AreIsolatedPerSession_AndRevokedTokensFail()
    {
        await using var env = await Env.StartAsync();
        var a = await env.SessionAsync();
        var b = await env.SessionAsync();
        var conv = await NewConversationAsync(a);
        await ReadAllAsync(await TurnAsync(a, conv, "secret question"));

        Assert.AreEqual(HttpStatusCode.NotFound, (await b.GetAsync($"/api/widget/v1/conversations/{conv}/messages")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await TurnAsync(b, conv, "hijack")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await b.PostAsync($"/api/widget/v1/conversations/{conv}/cancel", null)).StatusCode);
        var listB = await b.GetFromJsonAsync<JsonArray>("/api/widget/v1/conversations");
        Assert.AreEqual(0, listB!.Count);
        Assert.AreEqual(1, (await a.GetFromJsonAsync<JsonArray>("/api/widget/v1/conversations"))!.Count);

        var anon = env.Visitor();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/widget/v1/conversations")).StatusCode);
        var bogus = env.Visitor();
        bogus.DefaultRequestHeaders.Authorization = new("Bearer", "adv_notarealtoken");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await bogus.GetAsync("/api/widget/v1/conversations")).StatusCode);

        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            var token = a.DefaultRequestHeaders.Authorization!.Parameter!;
            var s = await db.VisitorSessions.SingleAsync(x => x.TokenHash == VisitorSessionService.Hash(token));
            Assert.IsFalse(db.VisitorSessions.Any(x => x.TokenHash == System.Text.Encoding.UTF8.GetBytes(token)), "token is not stored raw");
            await scope.ServiceProvider.GetRequiredService<IVisitorSessionService>().RevokeAsync(s.Id);
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/widget/v1/conversations")).StatusCode, "token reuse after revoke");
    }

    [TestMethod]
    public async Task V13_Embed_CspListsExactlyAllowedOrigins_SessionsRejectUnlistedHosts_SnippetHasNoSecrets()
    {
        await using var env = await Env.StartAsync();
        var anon = env.Visitor();
        var r = await anon.GetAsync($"/embed/{env.Agent.Id}");
        Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        StringAssert.Contains(csp, "frame-ancestors 'self' http://localhost:3000 https://host.example.com");
        Assert.IsTrue(csp.EndsWith("https://host.example.com", StringComparison.Ordinal));
        Assert.AreEqual("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        StringAssert.Contains(await r.Content.ReadAsStringAsync(), env.Agent.Id.ToString());

        var denied = await env.RawSessionAsync(anon, "https://evil.example.com");
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        var noFetchMeta = await env.RawSessionAsync(anon, Host, sameOrigin: false);
        Assert.AreEqual(HttpStatusCode.Forbidden, noFetchMeta.StatusCode);
        var badOrigin = new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/agents/{env.Agent.Id}/sessions") { Content = JsonContent.Create(new { hostOrigin = Host }) };
        badOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
        badOrigin.Headers.Add("Origin", "https://evil.example.com");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await anon.SendAsync(badOrigin)).StatusCode);

        var page = await env.Owner.GetStringAsync($"/Agents/Embed/{env.Agent.Id}");
        StringAssert.Contains(page, "agentdock.js");
        StringAssert.Contains(page, $"data-agent-id=&quot;{env.Agent.Id}&quot;");
        Assert.IsFalse(page.Contains(ApiKey), "page never contains the API key");
        Assert.IsFalse(page.Contains("sk-ant"), "page never contains key material");

        var js = await anon.GetStringAsync("/widget/v1/agentdock.js");
        Assert.IsFalse(js.Contains("sk-ant"));
        var cfg = await anon.GetAsync($"/api/widget/v1/agents/{env.Agent.Id}/config");
        Assert.AreEqual("*", cfg.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var cfgText = await cfg.Content.ReadAsStringAsync();
        Assert.IsFalse(cfgText.Contains("Be brief."), "system prompt is never public");
        Assert.IsFalse(cfgText.Contains(Host), "allowed origins are not listed publicly");
    }

    [TestMethod]
    public async Task V14_Limits_SessionHourly_DailyCap_AndConcurrentTurn()
    {
        await using var env = await Env.StartAsync(i => i.Limits = new AgentLimits { TurnsPerDay = 40, TurnsPerSessionPerHour = 30, TurnsPerIpPerHour = 100 });
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("x");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        for (var i = 1; i <= 30; i++)
        {
            var r = await TurnAsync(c, conv, "m" + i);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode, "turn " + i);
            await ReadAllAsync(r);
        }
        var over = await TurnAsync(c, conv, "m31");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, over.StatusCode);
        StringAssert.Contains(await over.Content.ReadAsStringAsync(), "rate_limited");

        var other = await env.SessionAsync();
        var oc = await NewConversationAsync(other);
        for (var i = 0; i < 10; i++) await ReadAllAsync(await TurnAsync(other, oc, "o" + i));
        var daily = await TurnAsync(other, oc, "one more");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, daily.StatusCode);
        StringAssert.Contains(await daily.Content.ReadAsStringAsync(), "daily_limit");
        Assert.AreEqual(40, env.Claude.Requests.Count);

        var third = await env.SessionAsync();
        var tooLong = await TurnAsync(third, await NewConversationAsync(third), new string('a', 4001));
        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode, await tooLong.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task V14_SecondTurnOnSameConversation_IsRejectedWith409_UntilFirstFinishes()
    {
        await using var env = await Env.StartAsync();
        var gate = new TaskCompletionSource();
        env.Claude.Script = (_, ct) => FakeClaudeGateway.Gated(gate.Task, ct);
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);

        var first = await TurnAsync(c, conv, "slow");
        await using var s = await first.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        var en = ReadEventsAsync(reader).GetAsyncEnumerator();
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)) && en.Current.Name != "text.delta") { }

        var second = await TurnAsync(c, conv, "impatient");
        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        StringAssert.Contains(await second.Content.ReadAsStringAsync(), "turn_in_progress");

        gate.SetResult();
        while (await en.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20))) { }
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("fine");
        Assert.AreEqual(HttpStatusCode.OK, (await TurnAsync(c, conv, "now ok")).StatusCode);
    }

    [TestMethod]
    public async Task V16_ToolLoop_IsBoundedAtEightIterations_WithErrorToolResults()
    {
        await using var env = await Env.StartAsync();
        var n = 0;
        env.Claude.Script = (_, _) => FakeClaudeGateway.ToolUse("toolu_" + Interlocked.Increment(ref n));
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);

        var events = await ReadAllAsync(await TurnAsync(c, conv, "loop"));
        Assert.AreEqual(ChatTurnService.MaxIterations, env.Claude.Requests.Count);
        var err = events.Single(e => e.Name == "error");
        Assert.AreEqual("iteration_limit", err.Data["code"]!.GetValue<string>());
        Assert.AreEqual("done", events.Last().Name);

        var second = env.Claude.Requests[1].Messages;
        var toolResult = second.Last()!["content"]![0]!;
        Assert.AreEqual("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.AreEqual("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.IsTrue(toolResult["is_error"]!.GetValue<bool>());

        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("recovered");
        var after = await ReadAllAsync(await TurnAsync(c, conv, "again"));
        Assert.AreEqual("message.completed", after[^2].Name, "lock released after iteration_limit");
    }

    [TestMethod]
    public async Task Upstream_Errors_AreGenericToVisitors_AndAuthFailureMarksKeyInvalid()
    {
        await using var env = await Env.StartAsync();
        env.Claude.Script = (_, _) => throw new ClaudeApiException(401, "authentication_error", "Anthropic returned 401");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        var events = await ReadAllAsync(await TurnAsync(c, conv, "hi"));
        var err = events.Single(e => e.Name == "error");
        Assert.AreEqual("agent_unavailable", err.Data["code"]!.GetValue<string>());
        Assert.IsFalse(err.ToString()!.Contains("401"));

        using var scope = env.Factory.Services.CreateScope();
        var cred = await scope.ServiceProvider.GetRequiredService<IAnthropicCredentialService>().GetActiveAsync(env.AccountId);
        Assert.AreEqual("Invalid", cred!.Status);

        var next = await TurnAsync(c, conv, "again");
        Assert.AreEqual(HttpStatusCode.OK, next.StatusCode, "lock was released");
    }

    [TestMethod]
    public async Task ModelSelector_HonoredOnlyWhenEnabledAndAllowed()
    {
        await using var env = await Env.StartAsync(i =>
        {
            i.ShowModelSelector = true; i.DefaultModelFamily = ModelFamily.Sonnet;
            i.AllowedModelFamilies = [ModelFamily.Haiku, ModelFamily.Sonnet];
        });
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("x");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        var haiku = Fixtures.Models().First(m => m.Line == "haiku" && m.Lifecycle == "active").Id;

        await ReadAllAsync(await TurnAsync(c, conv, "a", "haiku"));
        Assert.AreEqual(haiku, env.Claude.Requests[0].ModelId);
        await ReadAllAsync(await TurnAsync(c, conv, "b", "opus"));
        Assert.AreNotEqual(Fixtures.Models().First(m => m.Line == "opus" && m.Lifecycle == "active").Id, env.Claude.Requests[1].ModelId, "opus is not allowed");
        Assert.AreEqual(Fixtures.Models().Where(m => m.Line == "sonnet" && m.Lifecycle == "active").OrderByDescending(m => m.CreatedAt).First().Id, env.Claude.Requests[1].ModelId);
    }

    [TestMethod]
    public async Task Preview_WorksForDisabledAgents_OnlyWithOwnerMintedSession()
    {
        await using var env = await Env.StartAsync(enabled: false);
        var anon = env.Visitor();
        Assert.AreEqual(HttpStatusCode.NotFound, (await anon.GetAsync($"/embed/{env.Agent.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/widget/v1/agents/{env.Agent.Id}/config")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await env.RawSessionAsync(anon, Host)).StatusCode);

        var previewPage = await env.Owner.GetStringAsync($"/Agents/Preview/{env.Agent.Id}");
        var mint = await WebHelpers.PostAsync(env.Owner, $"/Agents/Preview/{env.Agent.Id}?handler=Session", WebHelpers.Token(previewPage));
        Assert.AreEqual(HttpStatusCode.OK, mint.StatusCode);
        var token = (await mint.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();

        var noToken = await env.Owner.PostAsync($"/Agents/Preview/{env.Agent.Id}?handler=Session", new FormUrlEncodedContent([]));
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode, "antiforgery required");

        var embed = await anon.GetAsync($"/embed/{env.Agent.Id}?preview=true");
        Assert.AreEqual(HttpStatusCode.OK, embed.StatusCode);
        var csp = embed.Headers.GetValues("Content-Security-Policy").Single();
        Assert.IsTrue(csp.EndsWith("frame-ancestors 'self'", StringComparison.Ordinal));

        var v = env.Visitor();
        v.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.AreEqual(HttpStatusCode.OK, (await v.GetAsync($"/api/widget/v1/agents/{env.Agent.Id}/config")).StatusCode);
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("preview works");
        var conv = await NewConversationAsync(v);
        var events = await ReadAllAsync(await TurnAsync(v, conv, "hello"));
        Assert.IsTrue(events.Any(e => e.Name == "text.delta"));
    }

    private static async IAsyncEnumerable<JsonObject> SearchStream(string stopReason = "end_turn", string url = "https://example.com/a", bool failed = false)
    {
        var E = FakeClaudeGateway.Ev;
        yield return E("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10}}}""");
        yield return E("""{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}}""");
        yield return E("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"query\":\"cats\"}"}}""");
        yield return E("""{"type":"content_block_stop","index":0}""");
        if (failed)
            yield return E("""{"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":{"type":"web_search_tool_result_error","error_code":"unavailable"}}}""");
        else
            yield return E("""{"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"__U__","title":"Cats","encrypted_content":"ENC==abc"}]}}""".Replace("__U__", url));
        yield return E("""{"type":"content_block_stop","index":1}""");
        if (stopReason == "end_turn")
        {
            yield return E("""{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""");
            yield return E("""{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Cats purr."}}""");
            yield return E("""{"type":"content_block_delta","index":2,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"__U__","title":"Cats","cited_text":"purr","encrypted_index":"IDX=="}}}""".Replace("__U__", url));
            yield return E("""{"type":"content_block_stop","index":2}""");
        }
        yield return E("""{"type":"message_delta","delta":{"stop_reason":"__S__"},"usage":{"output_tokens":5,"server_tool_use":{"web_search_requests":1}}}""".Replace("__S__", stopReason));
        yield return E("""{"type":"message_stop"}""");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task V6_Tool_OnlySent_WhenEnabled_AndDomainModeHonored()
    {
        await using var off = await Env.StartAsync();
        var c0 = await off.SessionAsync();
        await ReadAllAsync(await TurnAsync(c0, await NewConversationAsync(c0), "hi"));
        Assert.IsTrue(off.Claude.Requests.All(r => r.Tools == null || r.Tools.Count == 0));

        await using var on = await Env.StartAsync(i => { i.WebSearchEnabled = true; i.WebSearchMaxUses = 2; i.WebSearchDomainMode = "Allow"; i.WebSearchDomains = ["Example.com"]; });
        var c = await on.SessionAsync();
        await ReadAllAsync(await TurnAsync(c, await NewConversationAsync(c), "hi"));
        var tool = on.Claude.Requests[0].Tools!.Single();
        Assert.AreEqual("web_search_20260318", tool["type"]!.GetValue<string>());
        Assert.AreEqual(2, tool["max_uses"]!.GetValue<int>());
        Assert.AreEqual("example.com", tool["allowed_domains"]![0]!.GetValue<string>());
        Assert.IsNull(tool["blocked_domains"]);
    }

    [TestMethod]
    public async Task V6_SearchEvents_Citations_AndByteForByteReplay()
    {
        await using var env = await Env.StartAsync(i => i.WebSearchEnabled = true);
        env.Claude.Script = (req, _) => req.Messages.Count == 1 ? SearchStream() : FakeClaudeGateway.Text("ok");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);

        var events = await ReadAllAsync(await TurnAsync(c, conv, "tell me about cats"));
        var names = events.Select(e => e.Name).ToList();
        Assert.IsTrue(names.IndexOf("search.started") < names.IndexOf("search.completed"));
        Assert.AreEqual("cats", events.Single(e => e.Name == "search.started").Data["query"]!.GetValue<string>());
        Assert.AreEqual(1, events.Single(e => e.Name == "search.completed").Data["resultCount"]!.GetValue<int>());
        var cit = events.Single(e => e.Name == "citation");
        Assert.AreEqual(1, cit.Data["index"]!.GetValue<int>());
        Assert.AreEqual("https://example.com/a", cit.Data["url"]!.GetValue<string>());
        Assert.IsTrue(names.IndexOf("citation") < names.LastIndexOf("text.delta"), "citation precedes its marker");
        var streamed = string.Concat(events.Where(e => e.Name == "text.delta").Select(e => e.Data["text"]!.GetValue<string>()));
        Assert.AreEqual("Cats purr.\u27E61\u27E7", streamed);

        var msgs = (await c.GetFromJsonAsync<JsonObject>($"/api/widget/v1/conversations/{conv}/messages"))!["messages"]!.AsArray();
        var assistant = msgs.Last()!;
        Assert.AreEqual(streamed, assistant["text"]!.GetValue<string>());
        Assert.AreEqual("https://example.com/a", assistant["citations"]![0]!["url"]!.GetValue<string>());

        await ReadAllAsync(await TurnAsync(c, conv, "more"));
        var replayed = env.Claude.Requests.Last().Messages[1]!["content"]!;
        var json = replayed.ToJsonString();
        Assert.IsTrue(json.Contains("\"encrypted_content\":\"ENC==abc\""));
        Assert.IsTrue(json.Contains("\"encrypted_index\":\"IDX==\""));
        Assert.AreEqual("server_tool_use", replayed[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("web_search_tool_result", replayed[1]!["type"]!.GetValue<string>());
        Assert.IsFalse(json.Contains('\u27E6'), "markers never reach the model");
    }

    [TestMethod]
    public async Task V6_SearchFailure_EmitsSearchFailed()
    {
        await using var env = await Env.StartAsync(i => i.WebSearchEnabled = true);
        env.Claude.Script = (_, _) => SearchStream(failed: true, stopReason: "end_turn");
        var c = await env.SessionAsync();
        var events = await ReadAllAsync(await TurnAsync(c, await NewConversationAsync(c), "q"));
        Assert.AreEqual("unavailable", events.Single(e => e.Name == "search.failed").Data["code"]!.GetValue<string>());
        Assert.IsFalse(events.Any(e => e.Name == "search.completed"));
    }

    [TestMethod]
    public async Task V6_PauseTurn_IsContinued_AndCountsTowardIterationBound()
    {
        await using var env = await Env.StartAsync(i => i.WebSearchEnabled = true);
        env.Claude.Script = (req, _) => req.Messages.Count == 1 ? SearchStream("pause_turn") : FakeClaudeGateway.Text("done");
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        var events = await ReadAllAsync(await TurnAsync(c, conv, "q"));
        Assert.AreEqual(2, env.Claude.Requests.Count);
        var second = env.Claude.Requests[1].Messages;
        Assert.AreEqual("assistant", second.Last()!["role"]!.GetValue<string>(), "paused message is resent unchanged, no user message appended");
        Assert.AreEqual("message.completed", events[^2].Name);

        await using var loop = await Env.StartAsync(i => i.WebSearchEnabled = true);
        loop.Claude.Script = (_, _) => SearchStream("pause_turn");
        var c2 = await loop.SessionAsync();
        var ev2 = await ReadAllAsync(await TurnAsync(c2, await NewConversationAsync(c2), "q"));
        Assert.AreEqual(ChatTurnService.MaxIterations, loop.Claude.Requests.Count);
        Assert.AreEqual("iteration_limit", ev2.Single(e => e.Name == "error").Data["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task V6_OrgDisabled_IsGenericToVisitor()
    {
        await using var env = await Env.StartAsync(i => i.WebSearchEnabled = true);
        env.Claude.Script = (_, _) => throw new ClaudeApiException(400, "invalid_request_error", "Anthropic returned 400", "Web search is not enabled for your organization.");
        var c = await env.SessionAsync();
        var events = await ReadAllAsync(await TurnAsync(c, await NewConversationAsync(c), "q"));
        var err = events.Single(e => e.Name == "error");
        Assert.AreEqual("agent_unavailable", err.Data["code"]!.GetValue<string>());
        Assert.IsFalse(err.Data.ToJsonString().Contains("organization"));
    }

    [TestMethod]
    public async Task V7_Skills_AreInTheSystemPrompt_InOrder_AndEditsApplyToNextTurn()
    {
        await using var env = await Env.StartAsync();
        Guid id1, id2;
        using (var scope = env.Factory.Services.CreateScope())
        {
            var skills = scope.ServiceProvider.GetRequiredService<ISkillService>();
            var up = await skills.UploadAsync(env.AccountId, [new("first.md", System.Text.Encoding.UTF8.GetBytes("FIRST-BODY")), new("second.md", System.Text.Encoding.UTF8.GetBytes("SECOND-BODY </skill> x"))]);
            id1 = up[0].Id!.Value; id2 = up[1].Id!.Value;
            Assert.AreEqual(0, (await skills.SetForAgentAsync(env.AccountId, env.Agent.Id, [id2, id1])).Count);
        }
        var c = await env.SessionAsync();
        var conv = await NewConversationAsync(c);
        await ReadAllAsync(await TurnAsync(c, conv, "hi"));

        string SystemText(ClaudeRequest r) => string.Join("\n", r.System.Select(b => b["text"]!.GetValue<string>()));
        var sys = SystemText(env.Claude.Requests[0]);
        Assert.IsTrue(sys.IndexOf("SECOND-BODY") < sys.IndexOf("FIRST-BODY"), "SortOrder respected");
        Assert.IsTrue(sys.IndexOf("Be brief.") < sys.IndexOf("SECOND-BODY"), "owner prompt precedes skills");
        Assert.IsFalse(sys.Contains("</skill> x"), "literal closing tag escaped");
        StringAssert.Contains(sys, "version=\"1\"");
        var cached = env.Claude.Requests[0].System.Where(b => b["cache_control"] != null).ToList();
        Assert.AreEqual(1, cached.Count);
        StringAssert.Contains(cached[0]["text"]!.GetValue<string>(), "FIRST-BODY");

        using (var scope = env.Factory.Services.CreateScope())
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<ISkillService>().UpdateContentAsync(env.AccountId, id1, "EDITED-BODY")).Success);
        await ReadAllAsync(await TurnAsync(c, conv, "again"));
        var sys2 = SystemText(env.Claude.Requests.Last());
        StringAssert.Contains(sys2, "EDITED-BODY");
        StringAssert.Contains(sys2, "version=\"2\"");
        Assert.IsFalse(sys2.Contains("FIRST-BODY"));
    }
}
