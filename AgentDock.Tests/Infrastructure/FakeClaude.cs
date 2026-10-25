using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentDock.Core.Runtime;

namespace AgentDock.Tests.Infrastructure;

/// <summary>Scriptable IClaudeGateway: records every request and replays canned event streams.</summary>
public sealed class FakeClaudeGateway : IClaudeGateway
{
    public List<ClaudeRequest> Requests { get; } = [];
    public List<string> Keys { get; } = [];
    public volatile bool UpstreamCancelled;
    public Func<ClaudeRequest, CancellationToken, IAsyncEnumerable<JsonObject>> Script { get; set; } = (_, _) => Text("Hello");

    public IAsyncEnumerable<JsonObject> StreamAsync(ClaudeRequest request, string apiKey, CancellationToken ct)
    {
        lock (Requests) { Requests.Add(request); Keys.Add(apiKey); }
        return Wrap(Script(request, ct), ct);
    }

    private async IAsyncEnumerable<JsonObject> Wrap(IAsyncEnumerable<JsonObject> inner, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var e = inner.GetAsyncEnumerator(ct);
        while (true)
        {
            bool has;
            try { has = await e.MoveNextAsync(); }
            catch (OperationCanceledException) { UpstreamCancelled = true; throw; }
            if (!has) yield break;
            yield return e.Current;
        }
    }

    public static JsonObject Ev(string json) => (JsonObject)JsonNode.Parse(json)!;

    public static async IAsyncEnumerable<JsonObject> Text(params string[] parts)
    {
        yield return Ev("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10,"output_tokens":1}}}""");
        yield return Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        foreach (var p in parts)
            yield return new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = p } };
        yield return Ev("""{"type":"content_block_stop","index":0}""");
        yield return Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""");
        yield return Ev("""{"type":"message_stop"}""");
        await Task.CompletedTask;
    }

    public static async IAsyncEnumerable<JsonObject> ToolUse(string id)
    {
        yield return Ev("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10}}}""");
        yield return Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"__ID__","name":"nothing","input":{}}}""".Replace("__ID__", id));
        yield return Ev("""{"type":"content_block_stop","index":0}""");
        yield return Ev("""{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""");
        await Task.CompletedTask;
    }

    /// <summary>Emits "Hel", then waits for the gate (or cancellation), then "lo".</summary>
    public static async IAsyncEnumerable<JsonObject> Gated(Task gate, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return Ev("""{"type":"message_start","message":{"model":"claude-fake","usage":{"input_tokens":10}}}""");
        yield return Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        yield return Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""");
        await gate.WaitAsync(ct);
        yield return Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""");
        yield return Ev("""{"type":"content_block_stop","index":0}""");
        yield return Ev("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}""");
    }
}
