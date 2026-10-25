using System.Text;
using System.Text.Json.Nodes;

namespace AgentDock.Core.Runtime;

/// <summary>
/// Rebuilds the assistant content-block array from stream events, keeping every block and unknown field
/// verbatim so the message can be sent back to the API exactly as received (plan 5.4).
/// </summary>
public sealed class StreamingContentAccumulator(CitationRegistry? citations = null)
{
    private readonly CitationRegistry _citations = citations ?? new CitationRegistry();
    private readonly StringBuilder _display = new();
    private readonly HashSet<int> _usedSources = [];
    private readonly Dictionary<int, HashSet<int>> _blockMarkers = new();
    private readonly SortedDictionary<int, JsonObject> _blocks = new();
    private readonly Dictionary<int, StringBuilder> _partialJson = new();

    public string? StopReason { get; private set; }
    public string? ErrorType { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? ModelId { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }
    public int WebSearchRequests { get; private set; }

    public JsonObject? BlockAt(int index) => _blocks.GetValueOrDefault(index);

    public IReadOnlyList<JsonObject> Blocks => _blocks.Values.ToList();

    /// <summary>Applies one event; returns the text appended by it, if any.</summary>
    public string? Apply(JsonObject ev)
    {
        switch (ev["type"]?.GetValue<string>())
        {
            case "message_start":
                if (ev["message"] is JsonObject msg)
                {
                    ModelId = msg["model"]?.GetValue<string>();
                    ReadUsage(msg["usage"] as JsonObject);
                }
                return null;

            case "content_block_start":
            {
                var index = ev["index"]!.GetValue<int>();
                var block = (JsonObject)ev["content_block"]!.DeepClone();
                if (block["type"]?.GetValue<string>() == "text" && block["text"] == null) block["text"] = "";
                _blocks[index] = block;
                if (block["type"]?.GetValue<string>() == "text" && block["text"]!.GetValue<string>() is { Length: > 0 } t)
                {
                    _display.Append(t);
                    return t;
                }
                return null;
            }

            case "content_block_delta":
            {
                var index = ev["index"]!.GetValue<int>();
                if (!_blocks.TryGetValue(index, out var block) || ev["delta"] is not JsonObject delta) return null;
                switch (delta["type"]?.GetValue<string>())
                {
                    case "text_delta":
                        var text = delta["text"]?.GetValue<string>() ?? "";
                        block["text"] = (block["text"]?.GetValue<string>() ?? "") + text;
                        _display.Append(text);
                        return text;
                    case "input_json_delta":
                        if (!_partialJson.TryGetValue(index, out var sb)) _partialJson[index] = sb = new StringBuilder();
                        sb.Append(delta["partial_json"]?.GetValue<string>());
                        break;
                    case "thinking_delta":
                        block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + delta["thinking"]?.GetValue<string>();
                        break;
                    case "signature_delta":
                        block["signature"] = delta["signature"]?.GetValue<string>();
                        break;
                    case "citations_delta":
                        if (block["citations"] is not JsonArray list) block["citations"] = list = new JsonArray();
                        if (delta["citation"] is not JsonObject citation) break;
                        list.Add(citation.DeepClone());
                        var source = _citations.Register(citation);
                        if (source == null) break;
                        _usedSources.Add(source.Index);
                        if (!_blockMarkers.TryGetValue(index, out var seen)) _blockMarkers[index] = seen = [];
                        if (!seen.Add(source.Index)) break;
                        var marker = CitationRegistry.Marker(source.Index);
                        _display.Append(marker);
                        return marker;
                }
                return null;
            }

            case "content_block_stop":
            {
                var index = ev["index"]!.GetValue<int>();
                if (_blocks.TryGetValue(index, out var block) && _partialJson.TryGetValue(index, out var sb))
                {
                    var json = sb.ToString();
                    try { block["input"] = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json); }
                    catch (System.Text.Json.JsonException) { block["input"] = new JsonObject(); }
                    _partialJson.Remove(index);
                }
                return null;
            }

            case "message_delta":
                StopReason = ev["delta"]?["stop_reason"]?.GetValue<string>() ?? StopReason;
                ReadUsage(ev["usage"] as JsonObject);
                return null;

            case "error":
                ErrorType = ev["error"]?["type"]?.GetValue<string>() ?? "error";
                ErrorMessage = ev["error"]?["message"]?.GetValue<string>();
                return null;

            default:
                return null;
        }
    }

    /// <summary>Visitor-facing text exactly as streamed: text deltas plus citation markers.</summary>
    public string DisplayText() => _display.ToString();

    /// <summary>Sources cited by this message, using the turn-wide numbering.</summary>
    public IReadOnlyList<CitationSource> UsedSources => _citations.Sources.Where(x => _usedSources.Contains(x.Index)).ToList();

    public JsonArray ContentArray() => new(_blocks.Values.Select(b => (JsonNode?)b.DeepClone()).ToArray());

    public IReadOnlyList<JsonObject> ClientToolUses() =>
        _blocks.Values.Where(b => b["type"]?.GetValue<string>() == "tool_use").ToList();

    private void ReadUsage(JsonObject? u)
    {
        if (u == null) return;
        if (u["input_tokens"] is { } i) InputTokens = i.GetValue<int>();
        if (u["output_tokens"] is { } o) OutputTokens = o.GetValue<int>();
        if (u["cache_read_input_tokens"] is { } cr) CacheReadTokens = cr.GetValue<int>();
        if (u["cache_creation_input_tokens"] is { } cw) CacheWriteTokens = cw.GetValue<int>();
        if (u["server_tool_use"]?["web_search_requests"] is { } w) WebSearchRequests = w.GetValue<int>();
    }
}
