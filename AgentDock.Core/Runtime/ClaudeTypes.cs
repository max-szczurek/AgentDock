using System.Text.Json.Nodes;

namespace AgentDock.Core.Runtime;

/// <summary>A Messages API request. History and system blocks are raw JSON so content round-trips exactly.</summary>
public sealed record ClaudeRequest(
    string ModelId, int MaxTokens, IReadOnlyList<JsonObject> System, IReadOnlyList<JsonNode> Messages,
    IReadOnlyList<JsonObject>? Tools = null);

/// <summary>Abstracts Anthropic Messages streaming; yields each SSE event's JSON payload unchanged.</summary>
public interface IClaudeGateway
{
    IAsyncEnumerable<JsonObject> StreamAsync(ClaudeRequest request, string apiKey, CancellationToken ct);
}

public sealed class ClaudeApiException(int statusCode, string? errorType, string message, string? upstreamMessage = null) : Exception(message)
{
    /// <summary>Anthropic's own error text. For owner-facing diagnostics only; never shown to visitors.</summary>
    public string? UpstreamMessage { get; } = upstreamMessage;
    public bool IsWebSearchDisabled => StatusCode == 400 && UpstreamMessage?.Contains("web search", StringComparison.OrdinalIgnoreCase) == true
        && (UpstreamMessage.Contains("not enabled", StringComparison.OrdinalIgnoreCase) || UpstreamMessage.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    public int StatusCode { get; } = statusCode;
    public string? ErrorType { get; } = errorType;
    public bool IsAuth => StatusCode is 401 or 403;
    public bool IsModelNotFound => StatusCode == 404 || ErrorType == "not_found_error";
    public bool IsBusy => StatusCode is 429 or 529 || ErrorType is "rate_limit_error" or "overloaded_error";
}
