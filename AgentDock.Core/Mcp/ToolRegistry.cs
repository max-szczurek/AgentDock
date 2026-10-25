using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.HttpTools;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public sealed record RegisteredTool(
    string ExposedName, string DisplayName, string Description, JsonObject InputSchema, string InputSchemaJson,
    Guid ConnectionId, string ConnectionName, Guid AgentConnectionId, string UpstreamName, bool RequiresConfirmation, string Kind = "Mcp",
    Guid? HttpToolId = null, HttpToolExecution? Http = null);

/// <summary>Per-turn tool set built only from DB allowlists. Anything not in here is unknown to the model and blocked at call time.</summary>
public sealed class TurnToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _byName;

    public TurnToolRegistry(IEnumerable<RegisteredTool> tools, IReadOnlyList<string> warnings)
    {
        _byName = tools.ToDictionary(t => t.ExposedName, StringComparer.Ordinal);
        Warnings = warnings;
    }

    public static TurnToolRegistry Empty { get; } = new([], []);
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyCollection<RegisteredTool> Tools => _byName.Values;
    public bool TryGet(string name, out RegisteredTool tool) => _byName.TryGetValue(name, out tool!);

    public IReadOnlyList<JsonObject> ClaudeToolDefinitions() => _byName.Values.OrderBy(t => t.ExposedName, StringComparer.Ordinal).Select(t => new JsonObject
    {
        ["name"] = t.ExposedName,
        ["description"] = t.Description,
        ["input_schema"] = t.InputSchema.DeepClone(),
    }).ToList();
}

public interface IToolRegistryFactory
{
    Task<TurnToolRegistry> BuildAsync(Guid accountId, Guid agentId, CancellationToken ct);
}

public sealed class ToolRegistryFactory(AgentDockDbContext db) : IToolRegistryFactory
{
    public const int MaxNameLength = 128;
    public const int MaxDescriptionChars = 4000;

    public static string ExposedName(string alias, string tool)
    {
        var name = $"{alias}__{tool}";
        if (name.Length <= MaxNameLength) return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return name[..119] + "_" + hash;
    }

    public async Task<TurnToolRegistry> BuildAsync(Guid accountId, Guid agentId, CancellationToken ct)
    {
        var rows = await (from ac in db.AgentConnections.AsNoTracking()
                          join c in db.Connections.AsNoTracking() on ac.ConnectionId equals c.Id
                          join t in db.AgentMcpTools.AsNoTracking() on ac.Id equals t.AgentConnectionId
                          join s in db.McpToolSnapshots.AsNoTracking() on new { c.Id, t.ToolName } equals new { Id = s.ConnectionId, s.ToolName }
                          where ac.AccountId == accountId && ac.AgentId == agentId && ac.Enabled
                                && c.AccountId == accountId && c.DeletedUtc == null && c.Kind == "Mcp" && s.IsPresent
                          select new { ac.Id, ac.Alias, ConnId = c.Id, ConnName = c.Name, t.ToolName, t.RequiresConfirmation, t.ApprovedSchemaHash, s.Title, s.Description, s.InputSchemaJson, s.SchemaHash })
                         .ToListAsync(ct);

        var warnings = new List<string>();
        var tools = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        foreach (var r in rows.OrderBy(r => r.Alias, StringComparer.Ordinal).ThenBy(r => r.ToolName, StringComparer.Ordinal))
        {
            if (!r.ApprovedSchemaHash.AsSpan().SequenceEqual(r.SchemaHash))
            {
                warnings.Add($"'{r.ToolName}' on '{r.ConnName}' changed since it was approved and is disabled until you review it.");
                continue;
            }
            var exposed = ExposedName(r.Alias, r.ToolName);
            if (exposed == "web_search" || tools.ContainsKey(exposed)) { warnings.Add($"Duplicate tool name '{exposed}' was dropped."); continue; }

            var schema = JsonNode.Parse(r.InputSchemaJson) as JsonObject ?? new JsonObject();
            if (schema["type"] == null) schema["type"] = "object";
            var description = $"[{r.ConnName}] {r.Description}";
            if (description.Length > MaxDescriptionChars) description = description[..MaxDescriptionChars];
            tools[exposed] = new RegisteredTool(exposed, string.IsNullOrWhiteSpace(r.Title) ? r.ToolName : r.Title!, description, schema, schema.ToJsonString(),
                r.ConnId, r.ConnName, r.Id, r.ToolName, r.RequiresConfirmation);
        }

        var httpRows = await (from a in db.AgentHttpTools.AsNoTracking()
                              join t in db.HttpTools.AsNoTracking() on a.HttpToolId equals t.Id
                              join c in db.Connections.AsNoTracking() on t.ConnectionId equals c.Id
                              where a.AccountId == accountId && a.AgentId == agentId && a.Enabled
                                    && t.AccountId == accountId && t.DeletedUtc == null
                                    && c.AccountId == accountId && c.DeletedUtc == null && c.Kind == "Http"
                              select new { a.RequiresConfirmation, Tool = t, ConnName = c.Name })
                             .ToListAsync(ct);
        foreach (var r in httpRows.OrderBy(r => r.Tool.Name, StringComparer.Ordinal))
        {
            var t = r.Tool;
            var exposed = ExposedName("http", t.Name);
            if (tools.ContainsKey(exposed)) { warnings.Add($"Duplicate tool name '{exposed}' was dropped."); continue; }
            if (!HttpToolRules.TryParseExecution(t.ExecutionConfigJson, out var exec) || JsonNode.Parse(t.InputSchemaJson) is not JsonObject schema)
            { warnings.Add($"HTTP tool '{t.Name}' has an invalid configuration and is disabled."); continue; }
            if (schema["type"] == null) schema["type"] = "object";
            var description = t.Description.Length > MaxDescriptionChars ? t.Description[..MaxDescriptionChars] : t.Description;
            tools[exposed] = new RegisteredTool(exposed, t.DisplayName, description, schema, schema.ToJsonString(),
                t.ConnectionId, r.ConnName, Guid.Empty, t.Name, r.RequiresConfirmation, "Http", t.Id, exec);
        }
        return new TurnToolRegistry(tools.Values, warnings);
    }
}
