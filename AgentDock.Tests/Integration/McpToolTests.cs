
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Mcp;
using AgentDock.Core.Models;
using AgentDock.Core.Outbound;
using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace AgentDock.Tests.Integration;

[McpServerToolType]
public sealed class FakeMcpTools
{
    public static int EchoCalls, CreateCalls;

    [McpServerTool(Name = "echo", ReadOnly = true), System.ComponentModel.Description("Echo the text back")]
    public static string Echo([System.ComponentModel.Description("text to echo")] string text) { Interlocked.Increment(ref EchoCalls); return "echo:" + text; }

    [McpServerTool(Name = "create_thing", Destructive = true, ReadOnly = false), System.ComponentModel.Description("Creates a thing")]
    public static string CreateThing(string name) { Interlocked.Increment(ref CreateCalls); return "created " + name; }

    [McpServerTool(Name = "boom", ReadOnly = true), System.ComponentModel.Description("Always fails")]
    public static string Boom() => throw new InvalidOperationException("secret-internal-detail");

    [McpServerTool(Name = "nap", ReadOnly = true), System.ComponentModel.Description("Sleeps briefly")]
    public static async Task<string> Nap() { await Task.Delay(700); return "rested"; }

    [McpServerTool(Name = "long_text", ReadOnly = true), System.ComponentModel.Description("Returns a lot of text")]
    public static string LongText() => new('y', 40_000);
}

[TestClass]
public partial class McpToolTests
{
    private const string ApiKey = "sk-ant-api03-MCPTEST-aaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Host = "https://host.example.com";

    private sealed class LoopbackPolicy : IIpPolicy
    {
        public bool IsBlocked(IPAddress a) => !IPAddress.IsLoopback(a) && IpClassifier.IsBlocked(a);
    }

    private sealed class LoopbackResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Loopback });
    }

    public enum Fault { None, Hang, Http500, Huge, RedirectPrivate }

    private sealed class Env : IAsyncDisposable
    {
        public string Email = TestDb.NewTestEmail();
        public string Secret = WebHelpers.NewSecret();
        public FakeClaudeGateway Claude = new();
        public AppFactory Factory = null!;
        public WebApplication Mcp = null!;
        public int McpPort;
        public volatile Fault Fault;
        public int McpRequestsDuringFault;
        public HttpClient Owner = null!;
        public Guid AccountId;
        public Agent Agent = null!;
        public Connection Connection = null!;

        public static async Task<Env> StartAsync()
        {
            TestDb.RequireSql();
            var e = new Env();

            var b = WebApplication.CreateBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<FakeMcpTools>();
            e.Mcp = b.Build();
            e.Mcp.Use(async (ctx, next) =>
            {
                if (e.Fault != Fault.None && ctx.Request.Method == "POST")
                {
                    ctx.Request.EnableBuffering();
                    using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                    var body = await reader.ReadToEndAsync();
                    ctx.Request.Body.Position = 0;
                    if (body.Contains("\"tools/call\""))
                    {
                        Interlocked.Increment(ref e.McpRequestsDuringFault);
                        switch (e.Fault)
                        {
                            case Fault.Hang: await Task.Delay(TimeSpan.FromMinutes(1), ctx.RequestAborted); return;
                            case Fault.Http500: ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync("internal https://leak.example/secret"); return;
                            case Fault.Huge:
                                ctx.Response.ContentType = "text/event-stream";
                                await ctx.Response.WriteAsync("data: " + new string('z', 3 * 1024 * 1024) + "\n\n");
                                return;
                            case Fault.RedirectPrivate:
                                ctx.Response.Redirect($"http://169.254.169.254:{e.McpPort}/latest/meta-data"); return;
                        }
                    }
                }
                await next();
            });
            e.Mcp.MapMcp("/mcp");
            await e.Mcp.StartAsync();
            e.McpPort = new Uri(e.Mcp.Urls.First()).Port;

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
                services.RemoveAll<OutboundOptions>();
                services.AddSingleton(new OutboundOptions { IsDevelopment = true, AllowHttpForDevelopment = true, AllowedPorts = [e.McpPort] });
                services.RemoveAll<IDnsResolver>();
                services.AddSingleton<IDnsResolver>(new LoopbackResolver());
                services.RemoveAll<IIpPolicy>();
                services.AddSingleton<IIpPolicy>(new LoopbackPolicy());
            });

            var owner = e.Owner = e.Factory.NewClient();
            await WebHelpers.LoginAsync(owner, e.Email, e.Secret);
            using var scope = e.Factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AgentDockDbContext>();
            e.AccountId = (await db.Accounts.AsNoTracking().SingleAsync(a => db.Users.Any(u => u.Id == a.OwnerUserId && u.Email == e.Email))).Id;
            Assert.IsTrue((await sp.GetRequiredService<IAnthropicCredentialService>().SaveKeyAsync(e.AccountId, ApiKey)).Success);

            var created = await sp.GetRequiredService<IAgentService>().CreateAsync(e.AccountId,
                new AgentInput { Name = "Tool bot", SystemPrompt = "Use tools.", Enabled = true, Origins = [Host] });
            Assert.IsTrue(created.Success, string.Join(";", created.Errors));
            e.Agent = created.Agent!;

            var conns = sp.GetRequiredService<IConnectionService>();
            var saved = await conns.CreateAsync(e.AccountId, new ConnectionInput
            {
                Name = "Fake", BaseUrl = $"http://fake.test:{e.McpPort}/mcp", AuthType = "Bearer", BearerToken = "super-secret-token", TimeoutSeconds = 5,
            });
            Assert.IsTrue(saved.Success, string.Join(";", saved.Errors));
            e.Connection = saved.Connection!;
            var (res, tools) = await conns.DiscoverAsync(e.AccountId, e.Connection.Id);
            Assert.IsTrue(res!.Ok, res.Message);
            Assert.AreEqual(5, tools.Count);
            return e;
        }

        public async Task AssignAsync(string alias, params (string Tool, bool Confirm)[] tools)
        {
            using var scope = Factory.Services.CreateScope();
            var errors = await scope.ServiceProvider.GetRequiredService<IAgentToolService>().SaveAsync(AccountId, Agent.Id,
                [new ConnectionAssignmentInput(Connection.Id, alias, true, tools.Select(t => new ToolSelection(t.Tool, t.Confirm)).ToList())]);
            Assert.AreEqual(0, errors.Count, string.Join(";", errors));
        }

        public async Task<HttpClient> SessionAsync(string? sessionId = null, Guid? agentId = null)
        {
            var c = Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/agents/{agentId ?? Agent.Id}/sessions") { Content = JsonContent.Create(new { hostOrigin = Host, sessionId }) };
            req.Headers.Add("Sec-Fetch-Site", "same-origin");
            var r = await c.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode, await r.Content.ReadAsStringAsync());
            c.DefaultRequestHeaders.Authorization = new("Bearer", (await r.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>());
            return c;
        }

        public async Task<List<ToolExecution>> ExecutionsAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AgentDockDbContext>().ToolExecutions.AsNoTracking()
                .Where(t => t.AgentId == Agent.Id).OrderBy(t => t.RequestedUtc).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Mcp.DisposeAsync();
            await TestDb.DeleteUserAsync(Email);
        }
    }

    private sealed record Sse(string Name, JsonObject Data);

    private static async Task<List<Sse>> TurnAsync(HttpClient c, string text, Guid? existing = null)
    {
        var conv = existing ?? Guid.Parse((await (await c.PostAsync("/api/widget/v1/conversations", null)).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>());
        var r = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/conversations/{conv}/turns")
        { Content = JsonContent.Create(new { text }) }, HttpCompletionOption.ResponseHeadersRead);
        var list = new List<Sse>();
        await using var s = await r.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        string? name = null, data = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) { if (name != null && data != null) list.Add(new Sse(name, (JsonObject)JsonNode.Parse(data)!)); name = data = null; }
            else if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
        }
        list.Add(new Sse("_conversation", new JsonObject { ["id"] = conv }));
        return list;
    }

    private static Func<ClaudeRequest, CancellationToken, IAsyncEnumerable<JsonObject>> CallThenAnswer(string toolName, string inputJson, string id = "toolu_1") =>
        (req, _) =>
        {
            var last = req.Messages.Last();
            var isResult = last?["role"]?.GetValue<string>() == "user" && last["content"] is JsonArray a && a.Count > 0 && a[0]?["type"]?.GetValue<string>() == "tool_result";
            return isResult ? FakeClaudeGateway.Text("done") : ToolUseOf(id, toolName, inputJson);
        };

    private static async IAsyncEnumerable<JsonObject> ToolUseOf(string id, string name, string inputJson)
    {
        yield return FakeClaudeGateway.Ev("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10}}}""");
        yield return new JsonObject
        {
            ["type"] = "content_block_start", ["index"] = 0,
            ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
        };
        yield return new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = 0,
            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = inputJson },
        };
        yield return FakeClaudeGateway.Ev("""{"type":"content_block_stop","index":0}""");
        yield return FakeClaudeGateway.Ev("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<JsonObject> ToolUsesOf(params (string Id, string Name, string Input)[] uses)
    {
        yield return FakeClaudeGateway.Ev("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10}}}""");
        for (var i = 0; i < uses.Length; i++)
        {
            yield return new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = i,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = uses[i].Id, ["name"] = uses[i].Name, ["input"] = new JsonObject() },
            };
            yield return new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = i,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = uses[i].Input },
            };
            yield return new JsonObject { ["type"] = "content_block_stop", ["index"] = i };
        }
        yield return FakeClaudeGateway.Ev("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""");
        await Task.CompletedTask;
    }

    private static Func<ClaudeRequest, CancellationToken, IAsyncEnumerable<JsonObject>> UsesThenAnswer(params (string Id, string Name, string Input)[] uses) =>
        (req, _) =>
        {
            var last = req.Messages.Last();
            var isResult = last?["role"]?.GetValue<string>() == "user" && last["content"] is JsonArray a && a.Count > 0 && a[0]?["type"]?.GetValue<string>() == "tool_result";
            return isResult ? FakeClaudeGateway.Text("done") : ToolUsesOf(uses);
        };

    private static string LastToolResultText(ClaudeRequest req) =>
        req.Messages.Last()!["content"]![0]!["content"]!.GetValue<string>();

    [TestMethod]
    public async Task V8_OnlyAllowlistedTools_AreSentToClaude_WithExposedNames()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("hi");
        var c = await env.SessionAsync();
        await TurnAsync(c, "hello");

        var tools = env.Claude.Requests[0].Tools!;
        var names = tools.Select(t => t["name"]!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "gh__echo" }, names);
        StringAssert.StartsWith(tools[0]["description"]!.GetValue<string>(), "[Fake]");
    }

    [TestMethod]
    public async Task ToolLoop_ExecutesAllowedReadOnlyTool_AndRecordsExecution()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"ping"}""");
        var c = await env.SessionAsync();
        var before = FakeMcpTools.EchoCalls;
        var events = await TurnAsync(c, "go");

        Assert.AreEqual(before + 1, FakeMcpTools.EchoCalls);
        CollectionAssert.AreEqual(new[] { "tool.started", "tool.completed" }, events.Select(e => e.Name).Where(n => n.StartsWith("tool.")).ToList());
        Assert.AreEqual("echo", events.First(e => e.Name == "tool.started").Data["name"]!.GetValue<string>());
        Assert.AreEqual("echo:ping", LastToolResultText(env.Claude.Requests[1]));
        var rows = await env.ExecutionsAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("Succeeded", rows[0].Status);
        Assert.AreEqual("echo", rows[0].UpstreamToolName);
        Assert.AreEqual(env.Connection.Id, rows[0].ConnectionId);
        Assert.IsFalse((rows[0].InputJson ?? "").Contains("super-secret-token"));
        Assert.IsTrue(rows[0].DurationMs >= 0);
    }

    [TestMethod]
    public async Task V8_ForgedAndUnallowlistedToolUse_IsBlocked_AndUpstreamNeverCalled()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        var c = await env.SessionAsync();
        var calls = FakeMcpTools.CreateCalls;

        foreach (var forged in new[] { "gh__create_thing", "other__echo", "create_thing", "web_search" })
        {
            env.Claude.Script = CallThenAnswer(forged, """{"name":"x"}""");
            var events = await TurnAsync(c, "go");
            Assert.IsFalse(events.Any(e => e.Name is "tool.started" or "tool.failed" or "tool.completed"), forged);
            Assert.IsTrue(env.Claude.Requests.Last().Messages.Last()!["content"]![0]!["is_error"]!.GetValue<bool>(), forged);
            Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()), forged);
            Assert.AreEqual("message.completed", events.Last(e => !e.Name.StartsWith('_') && e.Name != "done").Name);
        }
        var rows = await env.ExecutionsAsync();
        Assert.AreEqual(4, rows.Count);
        Assert.IsTrue(rows.All(r => r.Status == "Blocked" && r.Kind == "Unknown"));
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
    }

    [TestMethod]
    public async Task V8_InvalidInput_FailsSchemaValidation_WithoutCallingUpstream()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":123}""");
        var c = await env.SessionAsync();
        var before = FakeMcpTools.EchoCalls;
        await TurnAsync(c, "go");
        Assert.AreEqual(before, FakeMcpTools.EchoCalls);
        Assert.AreEqual("invalid_input", (await env.ExecutionsAsync()).Single().ErrorCode);
    }

    [TestMethod]
    public async Task V8_SchemaDrift_ExcludesTool_UntilReapproved()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false), ("boom", false));
        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            var snap = await db.McpToolSnapshots.SingleAsync(s => s.ConnectionId == env.Connection.Id && s.ToolName == "echo");
            snap.Description = "Now also exfiltrates data";
            snap.SchemaHash = ConnectionRules.SchemaHash(snap.ToolName, snap.Description, snap.InputSchemaJson);
            await db.SaveChangesAsync();
            var reg = await scope.ServiceProvider.GetRequiredService<IToolRegistryFactory>().BuildAsync(env.AccountId, env.Agent.Id, default);
            CollectionAssert.AreEqual(new[] { "gh__boom" }, reg.Tools.Select(t => t.ExposedName).ToList());
            Assert.AreEqual(1, reg.Warnings.Count);
        }
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"x"}""");
        var c = await env.SessionAsync();
        var before = FakeMcpTools.EchoCalls;
        await TurnAsync(c, "go");
        Assert.AreEqual(before, FakeMcpTools.EchoCalls);
        Assert.AreEqual("Blocked", (await env.ExecutionsAsync()).Single().Status);

        await env.AssignAsync("gh", ("echo", false), ("boom", false));
        using var scope2 = env.Factory.Services.CreateScope();
        var reg2 = await scope2.ServiceProvider.GetRequiredService<IToolRegistryFactory>().BuildAsync(env.AccountId, env.Agent.Id, default);
        Assert.AreEqual(2, reg2.Tools.Count);
    }

    [TestMethod]
    public async Task V8_DisablingConnectionMidConversation_TakesEffectOnNextCall()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        var c = await env.SessionAsync();
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"one"}""");
        var first = await TurnAsync(c, "go");
        var conv = first.Last().Data["id"]!.GetValue<Guid>();
        var before = FakeMcpTools.EchoCalls;

        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            (await db.AgentConnections.SingleAsync(a => a.AgentId == env.Agent.Id)).Enabled = false;
            await db.SaveChangesAsync();
        }
        // The model still tries the tool (e.g. it remembered it); the registry no longer has it.
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"two"}""", "toolu_2");
        await TurnAsync(c, "again", conv);
        Assert.AreEqual(before, FakeMcpTools.EchoCalls);
        Assert.AreEqual(0, env.Claude.Requests.Last().Tools?.Count ?? 0);
        Assert.AreEqual("Blocked", (await env.ExecutionsAsync()).Last().Status);
    }

    [TestMethod]
    public async Task ToolError_And_LongResult_AreHandled()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("boom", false), ("long_text", false));
        var c = await env.SessionAsync();

        env.Claude.Script = CallThenAnswer("gh__boom", "{}");
        var events = await TurnAsync(c, "go");
        Assert.IsTrue(events.Any(e => e.Name == "tool.failed"));
        Assert.IsFalse(LastToolResultText(env.Claude.Requests.Last()).Contains("secret-internal-detail"));
        Assert.AreEqual("Failed", (await env.ExecutionsAsync()).Last().Status);

        env.Claude.Script = CallThenAnswer("gh__long_text", "{}", "toolu_2");
        await TurnAsync(c, "long");
        var text = LastToolResultText(env.Claude.Requests.Last());
        Assert.IsTrue(text.Length < 16_100);
        StringAssert.Contains(text, "[truncated 24000 characters]");
    }

    [TestMethod]
    [DataRow(Fault.Hang, "TimedOut")]
    [DataRow(Fault.Http500, "Failed")]
    [DataRow(Fault.Huge, "Failed")]
    [DataRow(Fault.RedirectPrivate, "Failed")]
    public async Task V10_McpFailures_AreSanitized_AndConversationContinues(Fault fault, string expectedStatus)
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        env.Fault = fault;
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"x"}""");
        var c = await env.SessionAsync();
        var events = await TurnAsync(c, "go");

        var failed = events.Single(e => e.Name == "tool.failed");
        var message = failed.Data["message"]!.GetValue<string>();
        AssertSanitized(message);
        AssertSanitized(LastToolResultText(env.Claude.Requests.Last()));
        var row = (await env.ExecutionsAsync()).Single();
        Assert.AreEqual(expectedStatus, row.Status, row.ErrorCode);
        AssertSanitized(row.ErrorMessage ?? "");
        Assert.AreEqual("message.completed", events.Last(e => !e.Name.StartsWith('_') && e.Name != "done").Name);
        Assert.AreEqual(1, env.McpRequestsDuringFault);

        env.Fault = Fault.None;
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("still alive");
        var again = await TurnAsync(c, "next", events.Last().Data["id"]!.GetValue<Guid>());
        Assert.IsTrue(again.Any(e => e.Name == "text.delta"));
    }

    [TestMethod]
    public async Task V10_ConnectionRefused_IsSanitized()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            (await db.Connections.SingleAsync(x => x.Id == env.Connection.Id)).BaseUrl = $"http://fake.test:{port}/mcp";
            await db.SaveChangesAsync();
        }
        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"x"}""");
        var events = await TurnAsync(await env.SessionAsync(), "go");
        AssertSanitized(events.Single(e => e.Name == "tool.failed").Data["message"]!.GetValue<string>());
        Assert.AreEqual("Failed", (await env.ExecutionsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task Connections_NeverExposeSecret_AndDeleteIsBlockedWhileInUse()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false));
        using var scope = env.Factory.Services.CreateScope();
        var conns = scope.ServiceProvider.GetRequiredService<IConnectionService>();
        var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();

        var input = await conns.GetInputAsync(env.AccountId, env.Connection.Id);
        Assert.IsTrue(string.IsNullOrEmpty(input!.BearerToken));
        var list = await conns.ListAsync(env.AccountId);
        Assert.IsFalse(list.Single().SecretHint!.Contains("super-secret-token"));
        Assert.AreEqual(1, list.Single().UsedByAgents);

        var blocked = await conns.DeleteAsync(env.AccountId, env.Connection.Id, removeFromAll: false);
        Assert.IsFalse(blocked.Deleted);
        Assert.AreEqual(1, blocked.BlockedBy.Count);

        var secretId = env.Connection.SecretId!.Value;
        var done = await conns.DeleteAsync(env.AccountId, env.Connection.Id, removeFromAll: true);
        Assert.IsTrue(done.Deleted);
        Assert.AreEqual(0, await db.AgentConnections.CountAsync(a => a.AgentId == env.Agent.Id));
        Assert.IsTrue(await db.Secrets.AsNoTracking().Where(s => s.Id == secretId).AllAsync(s => s.ShreddedUtc != null));
    }

    [TestMethod]
    public async Task Connections_AreIsolatedBetweenAccounts()
    {
        await using var env = await Env.StartAsync();
        await using var other = await AccountHarness.StartAsync();
        using var scope = env.Factory.Services.CreateScope();
        var conns = scope.ServiceProvider.GetRequiredService<IConnectionService>();
        Assert.IsNull(await conns.GetAsync(other.AccountId, env.Connection.Id));
        Assert.AreEqual(0, (await conns.ListAsync(other.AccountId)).Count);
        Assert.IsNull(await conns.ResolveAccessAsync(other.AccountId, env.Connection.Id));
        Assert.IsFalse((await conns.DeleteAsync(other.AccountId, env.Connection.Id, true)).Found);
    }

    [TestMethod]
    public async Task DuplicateAgent_CopiesConnectionAssignments()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false), ("create_thing", true));
        using var scope = env.Factory.Services.CreateScope();
        var copy = await scope.ServiceProvider.GetRequiredService<IAgentService>().DuplicateAsync(env.AccountId, env.Agent.Id);
        var reg = await scope.ServiceProvider.GetRequiredService<IToolRegistryFactory>().BuildAsync(env.AccountId, copy!.Id, default);
        CollectionAssert.AreEqual(new[] { "gh__create_thing", "gh__echo" }, reg.Tools.Select(t => t.ExposedName).OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.IsTrue(reg.TryGet("gh__create_thing", out var ct) && ct.RequiresConfirmation);
    }

    [TestMethod]
    public async Task OwnerPages_ShowToolsAndTranscriptWithExecutions()
    {
        await using var env = await Env.StartAsync();
        var page = await env.Owner.GetStringAsync($"/Agents/Tools/{env.Agent.Id}");
        StringAssert.Contains(page, "<code>echo</code>");
        var save = await WebHelpers.PostAsync(env.Owner, $"/Agents/Tools/{env.Agent.Id}", WebHelpers.Token(page), new()
        {
            ["Rows[0].ConnectionId"] = env.Connection.Id.ToString(), ["Rows[0].Assigned"] = "true", ["Rows[0].Enabled"] = "true", ["Rows[0].Alias"] = "gh",
            ["Rows[0].Tools[0].Name"] = "echo", ["Rows[0].Tools[0].Allowed"] = "true",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode, await save.Content.ReadAsStringAsync());

        env.Claude.Script = CallThenAnswer("gh__echo", """{"text":"hi"}""");
        var events = await TurnAsync(await env.SessionAsync(), "go");
        var conv = events.Last().Data["id"]!.GetValue<Guid>();

        var list = await env.Owner.GetStringAsync($"/Agents/Transcripts/{env.Agent.Id}");
        StringAssert.Contains(list, conv.ToString());
        var detail = await env.Owner.GetStringAsync($"/Agents/Transcripts/{env.Agent.Id}/{conv}");
        StringAssert.Contains(detail, "gh__echo");
        StringAssert.Contains(detail, "Succeeded");
        StringAssert.Contains(detail, "echo:hi");
    }

    private static void AssertSanitized(string text)
    {
        foreach (var bad in new[] { "http://", "https://", "169.254", "127.0.0.1", "fake.test", "leak.example", "super-secret-token", "Bearer", "Authorization" })
            Assert.IsFalse(text.Contains(bad, StringComparison.OrdinalIgnoreCase), $"'{bad}' leaked in: {text}");
    }
}
