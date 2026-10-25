using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Services;

namespace AgentDock.Core.Runtime;

/// <summary>
/// Raw HttpClient + SSE parser for POST /v1/messages. Chosen over the SDK (plan 5.4 fallback) so unknown
/// content-block fields survive unchanged. Never sends temperature/top_p/top_k (removed in 2026-10-21).
/// </summary>
public sealed class ClaudeGateway(HttpClient http) : IClaudeGateway
{
    public async IAsyncEnumerable<JsonObject> StreamAsync(
        ClaudeRequest request, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["system"] = new JsonArray(request.System.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode?)m.DeepClone()).ToArray()),
        };
        if (request.Tools is { Count: > 0 })
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)t.DeepClone()).ToArray());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", AnthropicModelsClient.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new ClaudeApiException((int)resp.StatusCode, ErrorType(text), $"Anthropic returned {(int)resp.StatusCode}", ErrorMessage(text));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var ev = Parse(data.ToString());
                    data.Clear();
                    if (ev != null) yield return ev;
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
        if (data.Length > 0 && Parse(data.ToString()) is { } last) yield return last;
    }

    private static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string? ErrorType(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["type"]?.GetValue<string>(); }
        catch { return null; }
    }
}
