using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Runtime;
using AgentDock.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentDock.Tests.Integration;

public partial class McpToolTests
{
    private static async Task<(HttpStatusCode Status, List<Sse> Events, JsonObject? Json)> DecideAsync(HttpClient c, Guid conv, Guid exec, bool approve)
    {
        var r = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/conversations/{conv}/approvals/{exec}")
        { Content = JsonContent.Create(new { approve }) }, HttpCompletionOption.ResponseHeadersRead);
        var events = new List<Sse>();
        if (r.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return (r.StatusCode, events, r.Content.Headers.ContentLength is 0 or null && r.StatusCode == HttpStatusCode.NotFound ? null : await r.Content.ReadFromJsonAsync<JsonObject>());
        await using var s = await r.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s);
        string? name = null, data = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) { if (name != null && data != null) events.Add(new Sse(name, (JsonObject)JsonNode.Parse(data)!)); name = data = null; }
            else if (line.StartsWith("event: ")) name = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
        }
        return (r.StatusCode, events, null);
    }

    private static async Task<(Guid Conv, Guid Exec)> PauseAsync(Env env, HttpClient c, string? input = null)
    {
        env.Claude.Script = CallThenAnswer("gh__create_thing", input ?? """{"name":"x"}""");
        var events = await TurnAsync(c, "create it");
        var conv = events.Last().Data["id"]!.GetValue<Guid>();
        var required = events.Single(e => e.Name == "approval.required");
        return (conv, required.Data["toolExecutionId"]!.GetValue<Guid>());
    }

    [TestMethod]
    public async Task V9_DestructiveTool_PausesWithApprovalRequired_AndNothingRuns()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var calls = FakeMcpTools.CreateCalls;
        env.Claude.Script = CallThenAnswer("gh__create_thing", """{"name":"widget"}""");
        var events = await TurnAsync(c, "create it");

        var required = events.Single(e => e.Name == "approval.required");
        Assert.AreEqual("create_thing", required.Data["name"]!.GetValue<string>());
        StringAssert.Contains(required.Data["summary"]!.GetValue<string>(), "widget");
        Assert.IsFalse(events.Any(e => e.Name == "tool.started"));
        Assert.AreEqual("awaiting_approval", events.Single(e => e.Name == "message.completed").Data["stopReason"]!.GetValue<string>());
        Assert.AreEqual("done", events.Last(e => e.Name != "_conversation").Name);
        Assert.AreEqual(1, env.Claude.Requests.Count, "the model is not called again until the approval is decided");
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
        Assert.AreEqual("PendingApproval", (await env.ExecutionsAsync()).Single().Status);

        var conv = events.Last().Data["id"]!.GetValue<Guid>();
        var messages = await c.GetFromJsonAsync<JsonObject>($"/api/widget/v1/conversations/{conv}/messages");
        Assert.AreEqual(1, messages!["pendingApprovals"]!.AsArray().Count, "a reloaded widget can show the card again");
    }

    [TestMethod]
    public async Task V9_Approve_RunsOnce_ContinuesTheTurn_AndDoubleApproveIsIdempotent()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c, """{"name":"gizmo"}""");
        var calls = FakeMcpTools.CreateCalls;

        var (status, events, _) = await DecideAsync(c, conv, exec, true);
        Assert.AreEqual(HttpStatusCode.OK, status);
        CollectionAssert.AreEqual(new[] { "tool.started", "tool.completed" }, events.Select(e => e.Name).Where(n => n.StartsWith("tool.")).ToList());
        Assert.IsTrue(events.Any(e => e.Name == "text.delta"));
        Assert.AreEqual(calls + 1, FakeMcpTools.CreateCalls);
        Assert.AreEqual("created gizmo", LastToolResultText(env.Claude.Requests.Last()));
        Assert.IsFalse(env.Claude.Requests.Last().Messages.Last()!["content"]![0]!["is_error"]!.GetValue<bool>());
        Assert.AreEqual("Succeeded", (await env.ExecutionsAsync()).Single().Status);

        var requests = env.Claude.Requests.Count;
        var (status2, events2, json2) = await DecideAsync(c, conv, exec, true);
        Assert.AreEqual(HttpStatusCode.OK, status2);
        Assert.AreEqual(0, events2.Count);
        Assert.AreEqual("already_decided", json2!["code"]!.GetValue<string>());
        Assert.AreEqual(calls + 1, FakeMcpTools.CreateCalls, "second approve must not run the tool again");
        Assert.AreEqual(requests, env.Claude.Requests.Count);
    }

    [TestMethod]
    public async Task V9_Deny_ProducesErrorResult_AndUpstreamNeverCalled()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c);
        var calls = FakeMcpTools.CreateCalls;

        var (_, events, _) = await DecideAsync(c, conv, exec, false);
        Assert.AreEqual("Denied", events.Single(e => e.Name == "approval.decided").Data["status"]!.GetValue<string>());
        Assert.AreEqual("The user declined this action.", LastToolResultText(env.Claude.Requests.Last()));
        Assert.IsTrue(env.Claude.Requests.Last().Messages.Last()!["content"]![0]!["is_error"]!.GetValue<bool>());
        Assert.IsTrue(events.Any(e => e.Name == "text.delta"), "the assistant answers after the denial");
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
        Assert.AreEqual("Denied", (await env.ExecutionsAsync()).Single().Status);
    }

    private static async Task ExpireAsync(Env env, Guid exec)
    {
        using var scope = env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
        await db.ToolExecutions.Where(t => t.Id == exec).ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));
    }

    [TestMethod]
    public async Task V9_ExpiredApproval_CountsAsDenied()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c);
        await ExpireAsync(env, exec);
        var calls = FakeMcpTools.CreateCalls;

        var (_, events, _) = await DecideAsync(c, conv, exec, true);
        Assert.AreEqual("Expired", events.Single(e => e.Name == "approval.decided").Data["status"]!.GetValue<string>());
        Assert.AreEqual("The approval request expired.", LastToolResultText(env.Claude.Requests.Last()));
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
        Assert.AreEqual("Expired", (await env.ExecutionsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task V9_NewMessageWhilePending_IsRejected_ThenSettlesOnceExpired()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c);

        var blocked = await c.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/widget/v1/conversations/{conv}/turns") { Content = JsonContent.Create(new { text = "hello?" }) });
        Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "approval_pending");

        await ExpireAsync(env, exec);
        env.Claude.Script = (_, _) => FakeClaudeGateway.Text("moving on");
        var events = await TurnAsync(c, "hello?", conv);
        Assert.IsTrue(events.Any(e => e.Name == "text.delta"));
        var sent = env.Claude.Requests.Last().Messages.Select(m => m!["role"]!.GetValue<string>() + ":" + m["content"]![0]!["type"]!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "user:text", "assistant:tool_use", "user:tool_result", "user:text" }, sent);
        Assert.AreEqual("Expired", (await env.ExecutionsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task V9_AnotherSessionCannotApprove()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c);
        var calls = FakeMcpTools.CreateCalls;

        var other = await env.SessionAsync();
        var (status, _, _) = await DecideAsync(other, conv, exec, true);
        Assert.AreEqual(HttpStatusCode.NotFound, status);
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
        Assert.AreEqual("PendingApproval", (await env.ExecutionsAsync()).Single().Status);
    }

    [TestMethod]
    public async Task V9_MixedTools_ParkResults_AndSendOneOrderedToolResultMessage()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("echo", false), ("create_thing", true));
        var c = await env.SessionAsync();
        env.Claude.Script = UsesThenAnswer(("toolu_a", "gh__create_thing", """{"name":"one"}"""), ("toolu_b", "gh__echo", """{"text":"two"}"""), ("toolu_c", "gh__create_thing", """{"name":"three"}"""));
        var events = await TurnAsync(c, "go");
        var conv = events.Last().Data["id"]!.GetValue<Guid>();
        var pending = events.Where(e => e.Name == "approval.required").Select(e => e.Data["toolExecutionId"]!.GetValue<Guid>()).ToList();
        Assert.AreEqual(2, pending.Count);
        Assert.IsTrue(events.Any(e => e.Name == "tool.completed"), "the read-only tool ran immediately");
        Assert.AreEqual(1, env.Claude.Requests.Count);
        var calls = FakeMcpTools.CreateCalls;

        var (_, first, _) = await DecideAsync(c, conv, pending[0], true);
        Assert.IsFalse(first.Any(e => e.Name == "text.delta"), "the model waits for the remaining approval");
        Assert.AreEqual(1, env.Claude.Requests.Count);

        var (_, last, _) = await DecideAsync(c, conv, pending[1], false);
        Assert.IsTrue(last.Any(e => e.Name == "text.delta"));
        var results = env.Claude.Requests.Last().Messages.Last()!["content"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "toolu_a", "toolu_b", "toolu_c" }, results.Select(r => r!["tool_use_id"]!.GetValue<string>()).ToList());
        Assert.AreEqual("created one", results[0]!["content"]!.GetValue<string>());
        Assert.AreEqual("echo:two", results[1]!["content"]!.GetValue<string>());
        Assert.AreEqual("The user declined this action.", results[2]!["content"]!.GetValue<string>());
        Assert.AreEqual(calls + 1, FakeMcpTools.CreateCalls);
    }

    [TestMethod]
    public async Task ApprovalIsRechecked_WhenConnectionIsDisabledBeforeApproving()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("create_thing", true));
        var c = await env.SessionAsync();
        var (conv, exec) = await PauseAsync(env, c);
        using (var scope = env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentDockDbContext>();
            (await db.AgentConnections.SingleAsync(a => a.AgentId == env.Agent.Id)).Enabled = false;
            await db.SaveChangesAsync();
        }
        var calls = FakeMcpTools.CreateCalls;
        var (_, events, _) = await DecideAsync(c, conv, exec, true);
        Assert.AreEqual(calls, FakeMcpTools.CreateCalls);
        Assert.AreEqual("Tool not available.", LastToolResultText(env.Claude.Requests.Last()));
        Assert.AreEqual("Blocked", (await env.ExecutionsAsync()).Single().Status);
        Assert.IsTrue(events.Any(e => e.Name == "tool.failed"));
    }

    [TestMethod]
    public async Task ReadOnlyTools_RunInParallel_AndResultsKeepToolUseOrder()
    {
        await using var env = await Env.StartAsync();
        await env.AssignAsync("gh", ("nap", false), ("echo", false));
        var c = await env.SessionAsync();
        env.Claude.Script = UsesThenAnswer(("t1", "gh__nap", "{}"), ("t2", "gh__nap", "{}"), ("t3", "gh__nap", "{}"), ("t4", "gh__echo", """{"text":"last"}"""));
        Volatile.Write(ref FakeMcpTools.MaxConcurrentNaps, 0);
        var events = await TurnAsync(c, "go");
        Assert.IsTrue(events.Any(e => e.Name == "text.delta"));
        Assert.IsTrue(FakeMcpTools.MaxConcurrentNaps >= 2, $"read-only tools should overlap, max concurrency was {FakeMcpTools.MaxConcurrentNaps}");
        Assert.IsTrue(FakeMcpTools.MaxConcurrentNaps <= 4, "the parallel cap is 4");
        var results = env.Claude.Requests.Last().Messages.Last()!["content"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "t1", "t2", "t3", "t4" }, results.Select(r => r!["tool_use_id"]!.GetValue<string>()).ToList());
        Assert.AreEqual("echo:last", results[3]!["content"]!.GetValue<string>());
        Assert.AreEqual(4, (await env.ExecutionsAsync()).Count(e => e.Status == "Succeeded"));
    }
}
