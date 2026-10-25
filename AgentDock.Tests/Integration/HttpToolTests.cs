using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using AgentDock.Core.HttpTools;
using AgentDock.Core.Mcp;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock.Tests.Integration;

public sealed record SeenRequest(string Method, string PathAndQuery, string? Auth, string? Tenant, string? Body);

public sealed class FakeApiState
{
    public readonly ConcurrentQueue<SeenRequest> Seen = new();
    public int Calls(string pathPrefix, string? method = null) =>
        Seen.Count(r => r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal) && (method == null || r.Method == method));
}

public partial class McpToolTests
{
    private const string HttpBearer = "http-secret-token";

    private static void MapFakeApi(WebApplication app, FakeApiState st, Func<int> port)
    {
        async Task Record(HttpContext ctx)
        {
            string? body = null;
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync();
            }
            st.Seen.Enqueue(new SeenRequest(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,
                ctx.Request.Headers["X-Tenant"].ToString() is { Length: > 0 } t ? t : null, body));
        }

        app.MapGet("/api/orders/{id}", async (HttpContext ctx, string id) =>
        {
            await Record(ctx);
            return Results.Json(new
            {
                id, status = "shipped", internalNote = "do-not-leak",
                items = new[] { new { sku = "A1", qty = 2, cost = 5 }, new { sku = "B2", qty = 1, cost = 9 } },
                customer = new { name = "Ada", email = "ada@example.com" },
            });
        });
        app.MapPost("/api/orders", async (HttpContext ctx) =>
        {
            await Record(ctx);
            var body = JsonNode.Parse(st.Seen.Last().Body ?? "{}");
            return Results.Json(new { created = true, received = body }, statusCode: 201);
        });
        app.MapGet("/api/search", async (HttpContext ctx) =>
        {
            await Record(ctx);
            return Results.Json(new { query = ctx.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToArray()) });
        });
        app.MapGet("/api/slow", async (HttpContext ctx) => { await Record(ctx); await Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted); return Results.Ok(); });
        app.MapGet("/api/boom", async (HttpContext ctx) => { await Record(ctx); return Results.Text("internal https://leak.example/secret", statusCode: 500); });
        app.MapGet("/api/huge", async (HttpContext ctx) => { await Record(ctx); return Results.Text(new string('z', 20_000)); });
        app.MapGet("/api/text", async (HttpContext ctx) => { await Record(ctx); return Results.Text("plain hello"); });
        app.MapGet("/api/redirect-private", async (HttpContext ctx) =>
        {
            await Record(ctx);
            return Results.Redirect($"http://169.254.169.254:{port()}/latest/meta-data");
        });
        app.MapGet("/api/redirect-other", async (HttpContext ctx) =>
        {
            await Record(ctx);
            return Results.Redirect($"http://other.test:{port()}/api/whoami");
        });
        app.MapGet("/api/whoami", async (HttpContext ctx) =>
        {
            await Record(ctx);
            return Results.Json(new { auth = st.Seen.Last().Auth });
        });
    }

    private static HttpToolInput OrderLookup(Guid connectionId, string name = "order_lookup") => new()
    {
        Name = name, DisplayName = "Order lookup", Description = "Look up an order by its id.", Method = "GET", ConnectionId = connectionId,
        UrlTemplate = "/orders/{orderId}", TimeoutSeconds = 5,
        Parameters =
        [
            new() { Name = "orderId", In = "path", Type = "string", Required = true, Description = "The order id" },
            new() { Name = "expand", In = "query", Type = "string", Description = "Extra detail" },
        ],
        StaticQuery = new() { ["format"] = "json" },
        ResponseSelect = ["id", "status", "items[*].sku", "items[*].qty"],
    };

    private static HttpToolInput Simple(Guid connectionId, string name, string path, int timeout = 5, int maxBytes = 262144) => new()
    {
        Name = name, DisplayName = name, Description = "Calls " + path, Method = "GET", ConnectionId = connectionId, UrlTemplate = path,
        TimeoutSeconds = timeout, MaxBytes = maxBytes,
    };

    private static async Task<Connection> AddHttpConnectionAsync(Env env, string name = "Orders API", string? baseUrl = null, string bearer = HttpBearer)
    {
        using var scope = env.Factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IConnectionService>().CreateAsync(env.AccountId, new ConnectionInput
        {
            Name = name, Kind = "Http", BaseUrl = baseUrl ?? $"http://fake.test:{env.McpPort}/api", AuthType = "Bearer", BearerToken = bearer, TimeoutSeconds = 10,
            StaticHeaders = new() { ["X-Tenant"] = "acme" },
        });
        Assert.IsTrue(saved.Success, string.Join(";", saved.Errors));
        return saved.Connection!;
    }

    private static async Task<HttpTool> AddHttpToolAsync(Env env, HttpToolInput input)
    {
        using var scope = env.Factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IHttpToolService>().SaveAsync(env.AccountId, null, HttpToolRules.FromForm(input));
        Assert.IsTrue(saved.Success, string.Join(";", saved.Errors));
        return saved.Tool!;
    }

    private static async Task AssignHttpAsync(Env env, Guid? agentId = null, params (HttpTool Tool, bool? Confirm, bool Enabled)[] tools)
    {
        using var scope = env.Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IHttpToolService>();
        var errors = await svc.SaveAssignmentsAsync(env.AccountId, agentId ?? env.Agent.Id, tools.Select(t =>
        {
            var method = HttpToolRules.TryParseExecution(t.Tool.ExecutionConfigJson, out var x) ? x.Method : "GET";
            return new HttpToolAssignmentInput(t.Tool.Id, t.Enabled, t.Confirm ?? HttpToolRules.DefaultRequiresConfirmation(method));
        }).ToList());
        Assert.AreEqual(0, errors.Count, string.Join(";", errors));
    }

    private static Task AssignHttpAsync(Env env, params HttpTool[] tools) => AssignHttpAsync(env, null, tools.Select(t => (t, (bool?)null, true)).ToArray());

    [TestMethod]
    public async Task Http_OrderLookup_RunsThroughTheLoop_WithProjection_AndServerSideCredentials()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var tool = await AddHttpToolAsync(env, OrderLookup(conn.Id));
        await AssignHttpAsync(env, tool);
        env.Claude.Script = CallThenAnswer("http__order_lookup", """{"orderId":"o-100","expand":"items"}""");
        var c = await env.SessionAsync();

        var events = await TurnAsync(c, "where is order 100?");

        var sent = env.Claude.Requests[0].Tools!;
        Assert.AreEqual("http__order_lookup", sent.Single()["name"]!.GetValue<string>());
        var schemaText = sent.Single()["input_schema"]!.ToJsonString();
        StringAssert.Contains(schemaText, "orderId");
        Assert.IsFalse(schemaText.Contains("fake.test") || schemaText.Contains("/orders/") || schemaText.Contains(HttpBearer), "the model sees only the input schema");

        CollectionAssert.AreEqual(new[] { "tool.started", "tool.completed" }, events.Select(e => e.Name).Where(n => n.StartsWith("tool.")).ToList());
        var result = JsonNode.Parse(LastToolResultText(env.Claude.Requests[1]))!;
        Assert.IsTrue(JsonNode.DeepEquals(result, JsonNode.Parse("""{"status":200,"data":{"id":"o-100","status":"shipped","items":[{"sku":"A1","qty":2},{"sku":"B2","qty":1}]}}""")), result.ToJsonString());
        Assert.IsFalse(result.ToJsonString().Contains("do-not-leak"));

        var seen = env.Api.Seen.Single(r => r.PathAndQuery.StartsWith("/api/orders/o-100"));
        StringAssert.Contains(seen.PathAndQuery, "format=json");
        StringAssert.Contains(seen.PathAndQuery, "expand=items");
        Assert.AreEqual("Bearer " + HttpBearer, seen.Auth);
        Assert.AreEqual("acme", seen.Tenant);

        var row = (await env.ExecutionsAsync()).Single();
        Assert.AreEqual("Succeeded", row.Status);
        Assert.AreEqual("Http", row.Kind);
        Assert.AreEqual(tool.Id, row.HttpToolId);
        Assert.AreEqual(conn.Id, row.ConnectionId);
        Assert.AreEqual(200, row.HttpStatus);
        Assert.IsFalse((row.InputJson ?? "").Contains(HttpBearer));
        Assert.IsFalse((row.ResultPreview ?? "").Contains(HttpBearer));
    }

    [TestMethod]
    public async Task Http_PostTool_DefaultsToConfirmation_ThenSendsJsonBodyOnApprove()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var create = await AddHttpToolAsync(env, new HttpToolInput
        {
            Name = "create_order", DisplayName = "Create order", Description = "Places an order.", Method = "POST", ConnectionId = conn.Id, UrlTemplate = "/orders", TimeoutSeconds = 5,
            Parameters =
            [
                new() { Name = "sku", In = "body", Type = "string", Required = true },
                new() { Name = "qty", In = "body", Type = "integer" },
                new() { Name = "customerName", In = "body", Type = "string", BodyPath = "customer.name" },
            ],
        });
        using (var scope = env.Factory.Services.CreateScope())
        {
            var view = (await scope.ServiceProvider.GetRequiredService<IHttpToolService>().GetAssignmentsAsync(env.AccountId, env.Agent.Id)).Single(v => v.Tool.Id == create.Id);
            Assert.IsTrue(view.DefaultConfirm, "non-GET defaults to requiring confirmation");
        }
        await AssignHttpAsync(env, create);
        var c = await env.SessionAsync();
        env.Claude.Script = CallThenAnswer("http__create_order", """{"sku":"A1","qty":3,"customerName":"Ada"}""");

        var events = await TurnAsync(c, "order it");
        var conv = events.Last().Data["id"]!.GetValue<Guid>();
        var required = events.Single(e => e.Name == "approval.required");
        Assert.AreEqual(0, env.Api.Calls("/api/orders", "POST"), "nothing is sent before approval");

        var (status, resumed, _) = await DecideAsync(c, conv, required.Data["toolExecutionId"]!.GetValue<Guid>(), approve: true);
        Assert.AreEqual(HttpStatusCode.OK, status);
        Assert.IsTrue(resumed.Any(e => e.Name == "tool.completed"));
        var post = env.Api.Seen.Single(r => r.Method == "POST");
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(post.Body!), JsonNode.Parse("""{"sku":"A1","qty":3,"customer":{"name":"Ada"}}""")), post.Body);
        Assert.AreEqual("Bearer " + HttpBearer, post.Auth);
        StringAssert.Contains(LastToolResultText(env.Claude.Requests.Last()), "\"status\":201");
        Assert.AreEqual("Succeeded", (await env.ExecutionsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task Http_PathValues_CannotEscapeTheTemplate_AndUpstreamIsNotCalled()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        await AssignHttpAsync(env, await AddHttpToolAsync(env, OrderLookup(conn.Id)));
        var c = await env.SessionAsync();

        foreach (var evil in new[] { "../admin", "a/b", "x?y=1", "x#frag", ".." })
        {
            env.Claude.Script = CallThenAnswer("http__order_lookup", new JsonObject { ["orderId"] = evil }.ToJsonString());
            await TurnAsync(c, "go");
            Assert.IsTrue(env.Claude.Requests.Last().Messages.Last()!["content"]![0]!["is_error"]!.GetValue<bool>(), evil);
        }
        Assert.AreEqual(0, env.Api.Seen.Count, "no request left the server");
        var rows = await env.ExecutionsAsync();
        Assert.IsTrue(rows.All(r => r.Status == "Blocked" && r.ErrorCode == "invalid_input"), string.Join(",", rows.Select(r => r.Status + ":" + r.ErrorCode)));
    }

    [TestMethod]
    public async Task V8_Http_OnlyAllowlistedTools_AreOffered_AndForgedOrRemovedOnesAreBlocked()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var allowed = await AddHttpToolAsync(env, OrderLookup(conn.Id));
        await AddHttpToolAsync(env, Simple(conn.Id, "other_tool", "/text"));
        await AssignHttpAsync(env, allowed);
        var c = await env.SessionAsync();
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("hi");
        await TurnAsync(c, "hello");
        CollectionAssert.AreEqual(new[] { "http__order_lookup" }, env.Claude.Requests[0].Tools!.Select(t => t["name"]!.GetValue<string>()).ToList());

        env.Claude.Script = CallThenAnswer("http__other_tool", "{}");
        await TurnAsync(c, "go");
        Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()));
        Assert.AreEqual(0, env.Api.Calls("/api/text"), "an unassigned tool never reaches the upstream");

        using (var scope = env.Factory.Services.CreateScope())
        {
            var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
            var other = (await agents.CreateAsync(env.AccountId, new AgentInput { Name = "Other bot", SystemPrompt = "x", Enabled = true, Origins = [Host] })).Agent!;
            var c2 = await env.SessionAsync(agentId: other.Id);
            env.Claude.Script = CallThenAnswer("http__order_lookup", """{"orderId":"o-1"}""");
            await TurnAsync(c2, "go");
            Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()), "another agent's tool is blocked");
        }
        Assert.AreEqual(0, env.Api.Calls("/api/orders/"));

        env.Claude.Script = CallThenAnswer("http__order_lookup", """{"orderId":"o-1"}""");
        await TurnAsync(c, "go");
        Assert.AreEqual(1, env.Api.Calls("/api/orders/o-1"));

        await AssignHttpAsync(env, null, (allowed, null, false));
        await TurnAsync(c, "go again");
        Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()), "disabling takes effect on the next call");
        Assert.AreEqual(1, env.Api.Calls("/api/orders/o-1"));

        await AssignHttpAsync(env, null, (allowed, null, true));
        using (var scope = env.Factory.Services.CreateScope())
            Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IHttpToolService>().DeleteAsync(env.AccountId, allowed.Id, removeFromAll: true)).Deleted);
        await TurnAsync(c, "after delete");
        Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()));
        Assert.AreEqual(1, env.Api.Calls("/api/orders/o-1"));
    }

    [TestMethod]
    public async Task V10_Http_FailureMatrix_IsSanitized_RecordsStatus_AndConversationContinues()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var closed = await AddHttpConnectionAsync(env, "Closed API", $"http://closed.test:{env.ClosedPort}/api");

        var cases = new (string Name, HttpToolInput Input, string Status, string Code, string Message)[]
        {
            ("slow", Simple(conn.Id, "slow", "/slow", timeout: 1), "TimedOut", "timeout", "did not respond in time"),
            ("boom", Simple(conn.Id, "boom", "/boom"), "Failed", "http_500", "HTTP 500"),
            ("huge", Simple(conn.Id, "huge", "/huge", maxBytes: 2048), "Failed", "response_too_large", "too large"),
            ("private", Simple(conn.Id, "private", "/redirect-private"), "Failed", "blocked_host", "not allowed"),
            ("refused", Simple(closed.Id, "refused", "/anything"), "Failed", "connection_failed", "Could not connect"),
        };
        var tools = new List<(HttpTool, bool?, bool)>();
        foreach (var k in cases) tools.Add((await AddHttpToolAsync(env, k.Input), false, true));
        await AssignHttpAsync(env, null, tools.ToArray());
        var c = await env.SessionAsync();

        foreach (var k in cases)
        {
            env.Claude.Script = CallThenAnswer("http__" + k.Name, "{}");
            var events = await TurnAsync(c, "go " + k.Name);
            var failed = events.Single(e => e.Name == "tool.failed");
            var shown = failed.Data.ToJsonString() + LastToolResultText(env.Claude.Requests.Last());
            StringAssert.Contains(shown, k.Message, k.Name);
            foreach (var secret in new[] { "leak.example", "fake.test", "closed.test", HttpBearer, "/api", "169.254", "internal" })
                Assert.IsFalse(shown.Contains(secret), $"{k.Name} must not expose '{secret}': {shown}");
            Assert.IsTrue(env.Claude.Requests.Last().Messages.Last()!["content"]![0]!["is_error"]!.GetValue<bool>(), k.Name);
            Assert.AreEqual("done", events.Last(e => e.Name != "_conversation").Name, k.Name);
        }

        var rows = (await env.ExecutionsAsync()).ToDictionary(r => r.ExposedName);
        foreach (var k in cases)
        {
            var row = rows["http__" + k.Name];
            Assert.AreEqual(k.Status, row.Status, k.Name);
            Assert.AreEqual(k.Code, row.ErrorCode, k.Name);
            Assert.AreEqual("Http", row.Kind);
            StringAssert.Contains(row.ErrorMessage, k.Message);
        }
        Assert.AreEqual(500, rows["http__boom"].HttpStatus);
    }

    [TestMethod]
    public async Task Http_Credentials_AreSentToTheConnectionOrigin_ButDroppedOnCrossOriginRedirect()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var same = await AddHttpToolAsync(env, Simple(conn.Id, "whoami", "/whoami"));
        var cross = await AddHttpToolAsync(env, Simple(conn.Id, "cross", "/redirect-other"));
        await AssignHttpAsync(env, same, cross);
        var c = await env.SessionAsync();

        env.Claude.Script = CallThenAnswer("http__whoami", "{}");
        await TurnAsync(c, "who");
        StringAssert.Contains(LastToolResultText(env.Claude.Requests.Last()), "Bearer " + HttpBearer);

        env.Claude.Script = CallThenAnswer("http__cross", "{}", id: "toolu_2");
        await TurnAsync(c, "cross");
        var hop = env.Api.Seen.Last(r => r.PathAndQuery.StartsWith("/api/whoami"));
        Assert.IsNull(hop.Auth, "the bearer token is not forwarded to another origin");
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(LastToolResultText(env.Claude.Requests.Last())), JsonNode.Parse("""{"status":200,"data":{"auth":null}}""")));
    }

    [TestMethod]
    public async Task Http_NonJsonResponses_AreReturnedAsText_AndStatusCanBeOmitted()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var input = Simple(conn.Id, "text", "/text");
        input.IncludeStatusCode = false;
        await AssignHttpAsync(env, await AddHttpToolAsync(env, input));
        env.Claude.Script = CallThenAnswer("http__text", "{}");
        await TurnAsync(await env.SessionAsync(), "go");
        Assert.AreEqual("\"plain hello\"", LastToolResultText(env.Claude.Requests.Last()));
    }

    [TestMethod]
    public async Task Http_TestPanel_RunsTheRealRunner_AndRedactsTheQueryString()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var tool = await AddHttpToolAsync(env, OrderLookup(conn.Id));

        var page = await env.Owner.GetStringAsync($"/HttpTools/Edit/{tool.Id}");
        StringAssert.Contains(page, "test-panel");
        StringAssert.Contains(page, "orderId");
        var post = await WebHelpers.PostAsync(env.Owner, $"/HttpTools/Edit/{tool.Id}?handler=Test", WebHelpers.Token(page),
            new() { ["SampleInput"] = """{"orderId":"o-55","expand":"secret-looking-value"}""" });
        var html = await post.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, post.StatusCode, html);
        StringAssert.Contains(html, "id=\"test-status\">200<");
        StringAssert.Contains(html, "shipped");
        Assert.IsFalse(html.Contains("do-not-leak"), "projection applies in the test panel too");
        var url = Regex.Match(html, "id=\"test-url\">([^<]+)<").Groups[1].Value;
        StringAssert.EndsWith(url, "/api/orders/o-55?(redacted)");
        Assert.IsFalse(html.Contains("expand=secret-looking-value"), "query values are never echoed in the request URL");
        Assert.IsFalse(html.Contains(HttpBearer));

        var bad = await WebHelpers.PostAsync(env.Owner, $"/HttpTools/Edit/{tool.Id}?handler=Test", WebHelpers.Token(page), new() { ["SampleInput"] = """{"expand":"x"}""" });
        StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "does not match the tool&#x27;s input schema");
        Assert.AreEqual(1, env.Api.Calls("/api/orders/"), "an invalid sample is rejected before any request");
    }

    [TestMethod]
    public async Task Http_Pages_BuilderAndAdvancedJson_ShareOneModel_AndValidateOnSave()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var form = await env.Owner.GetStringAsync("/HttpTools/Edit");
        var token = WebHelpers.Token(form);

        var bad = await WebHelpers.PostAsync(env.Owner, "/HttpTools/Edit", token, new()
        {
            ["Input.Name"] = "lookup", ["Input.DisplayName"] = "Lookup", ["Input.Description"] = "d", ["Input.ConnectionId"] = conn.Id.ToString(),
            ["Input.Method"] = "GET", ["Input.UrlTemplate"] = "/orders/{orderId}", ["Input.TimeoutSeconds"] = "5", ["Input.MaxBytes"] = "262144",
            ["Input.Parameters[0].Name"] = "other", ["Input.Parameters[0].In"] = "query", ["Input.Parameters[0].Type"] = "string",
        });
        Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode, "validation errors re-render");
        StringAssert.Contains(await bad.Content.ReadAsStringAsync(), "must be a required path parameter");

        var save = await WebHelpers.PostAsync(env.Owner, "/HttpTools/Edit", token, new()
        {
            ["Input.Name"] = "lookup", ["Input.DisplayName"] = "Lookup", ["Input.Description"] = "Find an order", ["Input.ConnectionId"] = conn.Id.ToString(),
            ["Input.Method"] = "GET", ["Input.UrlTemplate"] = "/orders/{orderId}", ["Input.TimeoutSeconds"] = "5", ["Input.MaxBytes"] = "262144",
            ["Input.IncludeStatusCode"] = "true", ["SelectText"] = "id\nitems[*].sku", ["StaticQueryText"] = "format=json",
            ["Input.Parameters[0].Name"] = "orderId", ["Input.Parameters[0].In"] = "path", ["Input.Parameters[0].Type"] = "string", ["Input.Parameters[0].Description"] = "Order id",
            ["Input.Parameters[1].Name"] = "status", ["Input.Parameters[1].In"] = "query", ["Input.Parameters[1].Type"] = "string", ["Input.Parameters[1].EnumValues"] = "open, closed",
            ["Input.Parameters[2].Name"] = "", ["Input.Parameters[2].In"] = "query", ["Input.Parameters[2].Type"] = "string",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode, await save.Content.ReadAsStringAsync());
        var editUrl = save.Headers.Location!.ToString();
        var edit = await env.Owner.GetStringAsync(editUrl);
        StringAssert.Contains(edit, "http-tool-message");
        var id = Guid.Parse(Regex.Match(editUrl, "[0-9a-f-]{36}").Value);

        using (var scope = env.Factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AgentDockDbContext>().HttpTools.AsNoTracking().SingleAsync(t => t.Id == id);
            var schema = JsonNode.Parse(row.InputSchemaJson)!;
            Assert.AreEqual("object", schema["type"]!.GetValue<string>());
            CollectionAssert.AreEqual(new[] { "orderId" }, schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
            CollectionAssert.AreEqual(new[] { "open", "closed" }, schema["properties"]!["status"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
            StringAssert.Contains(row.ExecutionConfigJson, "\"urlTemplate\":\"/orders/{orderId}\"");
        }

        StringAssert.Contains(edit, "&quot;urlTemplate&quot;");
        var advancedJson = Regex.Match(edit, "<textarea id=\"AdvancedText\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline).Groups[1].Value;
        advancedJson = WebUtility.HtmlDecode(advancedJson).Replace("\"timeoutSeconds\": 5", "\"timeoutSeconds\": 9");
        var adv = await WebHelpers.PostAsync(env.Owner, $"/HttpTools/Edit/{id}?handler=Advanced", WebHelpers.Token(edit), new()
        {
            ["Input.Name"] = "lookup", ["Input.DisplayName"] = "Lookup", ["Input.Description"] = "Find an order", ["Input.ConnectionId"] = conn.Id.ToString(), ["AdvancedText"] = advancedJson,
        });
        Assert.AreEqual(HttpStatusCode.Redirect, adv.StatusCode, await adv.Content.ReadAsStringAsync());
        using (var scope = env.Factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AgentDockDbContext>().HttpTools.AsNoTracking().SingleAsync(t => t.Id == id);
            StringAssert.Contains(row.ExecutionConfigJson, "\"timeoutSeconds\":9");
            Assert.AreEqual(2, row.Version);
        }

        var badAdv = await WebHelpers.PostAsync(env.Owner, $"/HttpTools/Edit/{id}?handler=Advanced", WebHelpers.Token(edit), new()
        {
            ["Input.Name"] = "lookup", ["Input.DisplayName"] = "Lookup", ["Input.Description"] = "d", ["Input.ConnectionId"] = conn.Id.ToString(),
            ["AdvancedText"] = """{"inputSchema":{"type":"object"},"execution":{"method":"GET","urlTemplate":"https://evil.example/x"}}""",
        });
        StringAssert.Contains(await badAdv.Content.ReadAsStringAsync(), "URL template must be a path");

        var typo = await WebHelpers.PostAsync(env.Owner, $"/HttpTools/Edit/{id}?handler=Advanced", WebHelpers.Token(edit), new()
        {
            ["Input.Name"] = "lookup", ["Input.DisplayName"] = "Lookup", ["Input.Description"] = "d", ["Input.ConnectionId"] = conn.Id.ToString(),
            ["AdvancedText"] = """{"inputSchema":{"type":"object"},"execution":{"method":"GET","urlTemplat":"/x"}}""",
        });
        StringAssert.Contains(await typo.Content.ReadAsStringAsync(), "advanced JSON is not valid");

        var list = await env.Owner.GetStringAsync("/HttpTools");
        StringAssert.Contains(list, id.ToString());
    }

    [TestMethod]
    public async Task Http_AgentToolsPage_AssignsHttpTools_WithDefaultConfirmation_AndDuplicateCopiesThem()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var get = await AddHttpToolAsync(env, OrderLookup(conn.Id));
        var post = await AddHttpToolAsync(env, new HttpToolInput
        {
            Name = "make", DisplayName = "Make", Description = "m", Method = "POST", ConnectionId = conn.Id, UrlTemplate = "/orders",
            Parameters = [new() { Name = "sku", In = "body", Type = "string", Required = true }],
        });

        var page = await env.Owner.GetStringAsync($"/Agents/Tools/{env.Agent.Id}");
        StringAssert.Contains(page, "http-tools-card");
        var getBox = Regex.Match(page, $"data-http-tool=\"{get.Id}\".*?</tr>", RegexOptions.Singleline).Value;
        var postBox = Regex.Match(page, $"data-http-tool=\"{post.Id}\".*?</tr>", RegexOptions.Singleline).Value;
        var confirm = new Regex("HttpRows\\[\\d+\\]\\.Confirm\"[^>]*>");
        Assert.IsFalse(confirm.Match(getBox).Value.Contains("checked"), "GET is not confirmed by default");
        Assert.IsTrue(confirm.Match(postBox).Value.Contains("checked"), "POST is confirmed by default");

        var save = await WebHelpers.PostAsync(env.Owner, $"/Agents/Tools/{env.Agent.Id}", WebHelpers.Token(page), new()
        {
            ["HttpRows[0].HttpToolId"] = get.Id.ToString(), ["HttpRows[0].Assigned"] = "true", ["HttpRows[0].Enabled"] = "true",
            ["HttpRows[1].HttpToolId"] = post.Id.ToString(), ["HttpRows[1].Assigned"] = "true", ["HttpRows[1].Enabled"] = "true", ["HttpRows[1].Confirm"] = "true",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, save.StatusCode, await save.Content.ReadAsStringAsync());

        using var scope = env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
        var links = await db.AgentHttpTools.AsNoTracking().Where(a => a.AgentId == env.Agent.Id).ToListAsync();
        Assert.AreEqual(2, links.Count);
        Assert.IsFalse(links.Single(l => l.HttpToolId == get.Id).RequiresConfirmation);
        Assert.IsTrue(links.Single(l => l.HttpToolId == post.Id).RequiresConfirmation);

        var copy = await scope.ServiceProvider.GetRequiredService<IAgentService>().DuplicateAsync(env.AccountId, env.Agent.Id);
        var copied = await db.AgentHttpTools.AsNoTracking().Where(a => a.AgentId == copy!.Id).ToListAsync();
        Assert.AreEqual(2, copied.Count);
        Assert.IsTrue(copied.Single(l => l.HttpToolId == post.Id).RequiresConfirmation);
    }

    [TestMethod]
    public async Task Http_Connection_KindIsFixed_TestPings_DiscoverIsRefused_AndDeleteIsBlockedByTools()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        using var scope = env.Factory.Services.CreateScope();
        var conns = scope.ServiceProvider.GetRequiredService<IConnectionService>();

        var test = await conns.TestAsync(env.AccountId, conn.Id);
        Assert.IsTrue(test!.Ok, test.Message);
        StringAssert.StartsWith(test.Message, "Reachable");
        var (disc, snaps) = await conns.DiscoverAsync(env.AccountId, conn.Id);
        Assert.IsFalse(disc!.Ok);
        Assert.AreEqual(0, snaps.Count);

        var input = (await conns.GetInputAsync(env.AccountId, conn.Id))!;
        input.Kind = "Mcp";
        Assert.IsTrue((await conns.UpdateAsync(env.AccountId, conn.Id, input)).Success);
        Assert.AreEqual("Http", (await conns.GetAsync(env.AccountId, conn.Id))!.Kind, "kind cannot be changed after creation");

        var tool = await scope.ServiceProvider.GetRequiredService<IHttpToolService>().SaveAsync(env.AccountId, null, HttpToolRules.FromForm(OrderLookup(conn.Id)));
        Assert.IsTrue(tool.Success);
        var blocked = await conns.DeleteAsync(env.AccountId, conn.Id, removeFromAll: true);
        Assert.IsFalse(blocked.Deleted);
        CollectionAssert.AreEqual(new[] { "order_lookup" }, blocked.HttpTools!.ToArray());

        var mcpAgentView = await scope.ServiceProvider.GetRequiredService<IAgentToolService>().GetAsync(env.AccountId, env.Agent.Id);
        Assert.IsTrue(mcpAgentView.All(v => v.Connection.Kind == "Mcp"), "HTTP connections never appear in the MCP assignment list");
    }

    [TestMethod]
    public async Task V3_Http_OtherAccountGets404_AndCrossAccountRowsFailAtTheDatabase()
    {
        await using var env = await Env.StartAsync();
        var conn = await AddHttpConnectionAsync(env);
        var tool = await AddHttpToolAsync(env, OrderLookup(conn.Id));

        var emailB = TestDb.NewTestEmail();
        try
        {
            await using var factoryB = WebHelpers.Factory(env.Secret, emailB);
            var b = factoryB.NewClient();
            await WebHelpers.LoginAsync(b, emailB, env.Secret);
            Assert.AreEqual(HttpStatusCode.NotFound, (await b.GetAsync($"/HttpTools/Edit/{tool.Id}")).StatusCode);
            var tokenB = WebHelpers.Token(await b.GetStringAsync("/HttpTools/Edit"));
            foreach (var handler in new[] { "Test", "Delete", "Advanced", "" })
            {
                var r = await WebHelpers.PostAsync(b, $"/HttpTools/Edit/{tool.Id}?handler={handler}", tokenB, new() { ["SampleInput"] = "{}", ["AdvancedText"] = "{}", ["Input.Name"] = "x" });
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode, handler);
            }
            Assert.IsFalse((await b.GetStringAsync("/HttpTools")).Contains(tool.Id.ToString()));
            Assert.AreEqual(HttpStatusCode.NotFound, (await b.GetAsync($"/Agents/Tools/{env.Agent.Id}")).StatusCode);

            using var scope = env.Factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IHttpToolService>();
            var stolen = await svc.SaveAsync(Guid.CreateVersion7(), null, HttpToolRules.FromForm(OrderLookup(conn.Id, "stolen")));
            Assert.IsFalse(stolen.Success, "another account cannot attach this connection to a tool");

            await using var harness = await AccountHarness.StartAsync();
            var bConn = new Connection { AccountId = harness.AccountId, Name = "B API", Kind = "Http", BaseUrl = "https://b.example.com/api" };
            harness.Db.Connections.Add(bConn);
            var bTool = new HttpTool { AccountId = harness.AccountId, Name = "b_tool", DisplayName = "B", Description = "b", ConnectionId = bConn.Id, InputSchemaJson = "{\"type\":\"object\"}", ExecutionConfigJson = "{}" };
            harness.Db.HttpTools.Add(bTool);
            await harness.Db.SaveChangesAsync();

            harness.Db.AgentHttpTools.Add(new AgentHttpTool { AccountId = env.AccountId, AgentId = env.Agent.Id, HttpToolId = bTool.Id });
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => harness.Db.SaveChangesAsync());
            harness.Db.ChangeTracker.Clear();
            harness.Db.AgentHttpTools.Add(new AgentHttpTool { AccountId = harness.AccountId, AgentId = env.Agent.Id, HttpToolId = bTool.Id });
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => harness.Db.SaveChangesAsync());
            harness.Db.ChangeTracker.Clear();
            harness.Db.HttpTools.Add(new HttpTool { AccountId = harness.AccountId, Name = "steals", DisplayName = "S", Description = "s", ConnectionId = conn.Id, InputSchemaJson = "{\"type\":\"object\"}", ExecutionConfigJson = "{}" });
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => harness.Db.SaveChangesAsync());
        }
        finally { await TestDb.DeleteUserAsync(emailB); }
    }
}
