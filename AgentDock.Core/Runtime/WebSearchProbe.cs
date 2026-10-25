using System.Text.Json.Nodes;
using AgentDock.Core.Models;
using AgentDock.Core.Services;

namespace AgentDock.Core.Runtime;

public sealed record ProbeResult(bool Ok, string Message);

public interface IWebSearchProbe
{
    /// <summary>One tiny search request with the owner's key, to tell them whether web search works for their organization.</summary>
    Task<ProbeResult> RunAsync(Guid accountId, CancellationToken ct = default);
}

public sealed class WebSearchProbe(IAnthropicCredentialService credentials, IClaudeGateway gateway) : IWebSearchProbe
{
    public async Task<ProbeResult> RunAsync(Guid accountId, CancellationToken ct = default)
    {
        var key = await credentials.RevealActiveKeyAsync(accountId, ct);
        if (key == null) return new ProbeResult(false, "Save a valid Anthropic API key in Settings first.");
        var catalog = await credentials.GetCatalogAsync(accountId, false, ct);
        var model = catalog.Families.Values.FirstOrDefault(m => m.SupportsWebSearch && m.Family == ModelFamily.Haiku)
            ?? catalog.Families.Values.FirstOrDefault(m => m.SupportsWebSearch);
        if (model == null) return new ProbeResult(false, "None of the available models report web search support.");

        var tool = new JsonObject
        {
            ["type"] = WebSearchTool.ToolType, ["name"] = "web_search", ["max_uses"] = 1, ["allowed_callers"] = new JsonArray("direct"),
        };
        var request = new ClaudeRequest(model.ModelId, 256, InstructionAssembler.Assemble("", DateTimeOffset.UtcNow),
            [new JsonObject { ["role"] = "user", ["content"] = "Search the web once for 'current UTC time' and reply with one short sentence." }], [tool]);
        try
        {
            var acc = new StreamingContentAccumulator();
            await foreach (var ev in gateway.StreamAsync(request, key, ct)) acc.Apply(ev);
            if (acc.ErrorType != null) return new ProbeResult(false, "Anthropic reported an error while searching: " + acc.ErrorType);
            var ranSearch = acc.WebSearchRequests > 0 || acc.Blocks.Any(b => b["type"]?.GetValue<string>() == "web_search_tool_result");
            return ranSearch
                ? new ProbeResult(true, $"Web search works ({model.DisplayName}).")
                : new ProbeResult(true, $"The request succeeded ({model.DisplayName}) but the model chose not to search.");
        }
        catch (ClaudeApiException e) when (e.IsWebSearchDisabled)
        {
            return new ProbeResult(false, "Web search is disabled for your Anthropic organization. Enable it in the Anthropic Console, then test again.");
        }
        catch (ClaudeApiException e)
        {
            return new ProbeResult(false, $"Anthropic returned {e.StatusCode}{(e.ErrorType == null ? "" : " (" + e.ErrorType + ")")}.");
        }
    }
}
