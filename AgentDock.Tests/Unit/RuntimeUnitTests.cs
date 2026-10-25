using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentDock.Core.Runtime;
using AgentDock.Web.Widget;

namespace AgentDock.Tests.Unit;

[TestClass]
public class RuntimeUnitTests
{
    private static async Task<List<JsonObject>> ParseFixtureAsync(string name)
    {
        var body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })) { BaseAddress = new Uri("https://example.test") };
        var events = new List<JsonObject>();
        await foreach (var e in new ClaudeGateway(http).StreamAsync(Req(), "k", default)) events.Add(e);
        return events;
    }

    private static ClaudeRequest Req() =>
        new("claude-test-1", 1024, InstructionAssembler.Assemble("Be nice.", DateTimeOffset.UnixEpoch), [new JsonObject { ["role"] = "user", ["content"] = "hi" }]);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public string? LastBody; public HttpRequestMessage? Last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    [TestMethod]
    public async Task Accumulator_RebuildsBlocksVerbatim_IncludingUnknownFields()
    {
        var acc = new StreamingContentAccumulator();
        var text = new StringBuilder();
        foreach (var e in await ParseFixtureAsync("stream_tool_thinking.sse")) text.Append(acc.Apply(e));

        Assert.AreEqual("Hello \u27E61\u27E7world", text.ToString());
        Assert.AreEqual("Hello \u27E61\u27E7world", acc.DisplayText());
        Assert.AreEqual("tool_use", acc.StopReason);
        Assert.AreEqual("claude-test-1", acc.ModelId);
        Assert.AreEqual(25, acc.InputTokens);
        Assert.AreEqual(42, acc.OutputTokens);
        Assert.AreEqual(3, acc.CacheReadTokens);
        Assert.AreEqual(4, acc.CacheWriteTokens);
        Assert.AreEqual(1, acc.WebSearchRequests);

        var blocks = acc.ContentArray();
        Assert.AreEqual(4, blocks.Count);
        Assert.AreEqual("Let me think.", blocks[0]!["thinking"]!.GetValue<string>());
        Assert.AreEqual("SIG==", blocks[0]!["signature"]!.GetValue<string>());
        Assert.AreEqual(1, blocks[0]!["future_field"]!["a"]!.GetValue<int>(), "unknown block fields survive");
        Assert.AreEqual("https://example.com", blocks[1]!["citations"]![0]!["url"]!.GetValue<string>());
        Assert.AreEqual("cats", blocks[2]!["input"]!["query"]!.GetValue<string>());
        Assert.AreEqual(0, ((JsonObject)blocks[3]!["input"]!).Count, "empty tool input becomes {}");
        Assert.AreEqual(1, acc.ClientToolUses().Count);
        Assert.AreEqual("toolu_1", acc.ClientToolUses()[0]["id"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Gateway_SendsExpectedRequest_AndNeverSamplingParams()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"message_stop\"}\n\n") });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        await foreach (var _ in new ClaudeGateway(http).StreamAsync(Req(), "sk-secret", default)) { }

        Assert.AreEqual("/v1/messages", handler.Last!.RequestUri!.AbsolutePath);
        Assert.AreEqual("sk-secret", handler.Last.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual(AgentDock.Core.Services.AnthropicModelsClient.ApiVersion, handler.Last.Headers.GetValues("anthropic-version").Single());

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.IsTrue(body["stream"]!.GetValue<bool>());
        Assert.AreEqual("claude-test-1", body["model"]!.GetValue<string>());
        Assert.AreEqual(1024, body["max_tokens"]!.GetValue<int>());
        foreach (var banned in new[] { "temperature", "top_p", "top_k" }) Assert.IsFalse(body.ContainsKey(banned), banned);
        Assert.IsFalse(body.ContainsKey("tools"), "no tools in M4");
        Assert.AreEqual(3, body["system"]!.AsArray().Count);
        Assert.AreEqual("ephemeral", body["system"]![1]!["cache_control"]!["type"]!.GetValue<string>());
        StringAssert.Contains(handler.LastBody!, "Be nice.");
    }

    [TestMethod]
    public async Task Gateway_NonSuccess_ThrowsClassifiedException()
    {
        foreach (var (code, auth, busy) in new[] { (401, true, false), (429, false, true), (529, false, true), (500, false, false) })
        {
            using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)code)
            { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"x\",\"message\":\"secret detail\"}}") })) { BaseAddress = new Uri("https://example.test") };
            var ex = await Assert.ThrowsAsync<ClaudeApiException>(async () =>
            {
                await foreach (var _ in new ClaudeGateway(http).StreamAsync(Req(), "k", default)) { }
            });
            Assert.AreEqual(auth, ex.IsAuth, code.ToString());
            Assert.AreEqual(busy, ex.IsBusy, code.ToString());
            Assert.IsFalse(ex.Message.Contains("secret detail"), "upstream body is not surfaced");
        }
    }

    [TestMethod]
    public void Assembler_OrdersPreambleOwnerPromptRuntime_WithSingleCacheBreakpoint()
    {
        var blocks = InstructionAssembler.Assemble("Owner prompt", new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(3, blocks.Count);
        Assert.AreEqual(InstructionAssembler.Preamble, blocks[0]["text"]!.GetValue<string>());
        Assert.AreEqual("Owner prompt", blocks[1]["text"]!.GetValue<string>());
        StringAssert.Contains(blocks[2]["text"]!.GetValue<string>(), "2026-10-24");
        Assert.AreEqual(1, blocks.Count(b => b.ContainsKey("cache_control")));
        Assert.AreEqual(2, InstructionAssembler.Assemble("  ", DateTimeOffset.UnixEpoch).Count);
    }

    [TestMethod]
    public void Limiter_AllowsMaxThenBlocks_AndRecoversAfterWindow()
    {
        var clock = new MutableClock();
        var l = new SlidingWindowLimiter(clock);
        for (var i = 0; i < 3; i++) Assert.IsTrue(l.TryAcquire("k", 3, TimeSpan.FromMinutes(1)));
        Assert.IsFalse(l.TryAcquire("k", 3, TimeSpan.FromMinutes(1)));
        Assert.IsTrue(l.TryAcquire("other", 3, TimeSpan.FromMinutes(1)));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.IsTrue(l.TryAcquire("k", 3, TimeSpan.FromMinutes(1)));
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public void Csp_FrameAncestorsListExactlyAllowedOrigins_AndPreviewIsSelfOnly()
    {
        var csp = WidgetEndpoints.BuildCsp(["https://b.example.com", "http://localhost:3000"], preview: false);
        StringAssert.Contains(csp, "frame-ancestors 'self' http://localhost:3000 https://b.example.com");
        StringAssert.Contains(csp, "default-src 'self'; script-src 'self'; connect-src 'self'");
        StringAssert.Contains(csp, "base-uri 'none'; form-action 'none'");
        Assert.IsTrue(WidgetEndpoints.BuildCsp([], false).EndsWith("frame-ancestors 'self'", StringComparison.Ordinal));
        Assert.IsTrue(WidgetEndpoints.BuildCsp(["https://b.example.com"], true).EndsWith("frame-ancestors 'self'", StringComparison.Ordinal));
    }
}
