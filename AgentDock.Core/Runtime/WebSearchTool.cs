using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using AgentDock.Core.Models;

namespace AgentDock.Core.Runtime;

public static partial class WebSearchTool
{
    public const string ToolType = "web_search_20260318";
    public const int MaxDomains = 50;

    /// <summary>
    /// The server tool definition, or null when search is off or the resolved model can't search.
    /// allowed_callers is pinned to direct so it works on every model and never produces code-execution blocks.
    /// </summary>
    public static JsonObject? Build(Agent agent, ResolvedModel model)
    {
        if (!agent.WebSearchEnabled || !model.SupportsWebSearch) return null;
        var tool = new JsonObject
        {
            ["type"] = ToolType, ["name"] = "web_search",
            ["max_uses"] = Math.Clamp(agent.WebSearchMaxUses, 1, 20),
            ["allowed_callers"] = new JsonArray("direct"),
        };
        var domains = ParseDomains(agent.WebSearchDomainsJson);
        if (domains.Count > 0 && agent.WebSearchDomainMode == "Allow")
            tool["allowed_domains"] = new JsonArray(domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
        else if (domains.Count > 0 && agent.WebSearchDomainMode == "Block")
            tool["blocked_domains"] = new JsonArray(domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
        return tool;
    }

    public static IReadOnlyList<string> ParseDomains(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Bare lowercase domains (optionally with a path-free port-less host); no scheme, wildcard or whitespace.</summary>
    public static bool TryNormalizeDomain(string? input, out string domain, out string? error)
    {
        domain = "";
        error = null;
        var d = (input ?? "").Trim().ToLowerInvariant();
        if (d.Length == 0) { error = "Domain is empty."; return false; }
        if (d.Contains("://") || d.Contains('/') || d.Contains('*') || d.Contains(' ') || d.Contains('@') || d.Contains(':'))
        { error = $"'{input}' must be a bare domain like example.com (no https://, path, port or wildcard)."; return false; }
        if (!DomainPattern().IsMatch(d)) { error = $"'{input}' is not a valid domain."; return false; }
        domain = d;
        return true;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
}
