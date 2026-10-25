using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public sealed record ToolChoice(
    string ToolName, string? Title, string Description, string? AnnotationsJson,
    bool Allowed, bool RequiresConfirmation, bool DefaultConfirm, bool Drifted);

public sealed record ConnectionAssignmentView(
    Connection Connection, Guid? AgentConnectionId, string Alias, bool Assigned, bool Enabled, IReadOnlyList<ToolChoice> Tools);

public sealed record ToolSelection(string ToolName, bool RequiresConfirmation);
public sealed record ConnectionAssignmentInput(Guid ConnectionId, string Alias, bool Enabled, IReadOnlyList<ToolSelection> Tools);

public interface IAgentToolService
{
    Task<IReadOnlyList<ConnectionAssignmentView>> GetAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SaveAsync(Guid accountId, Guid agentId, IReadOnlyList<ConnectionAssignmentInput> assignments, CancellationToken ct = default);
}

public static partial class McpAnnotations
{
    /// <summary>On unless the tool is explicitly read-only and not destructive.</summary>
    public static bool DefaultRequiresConfirmation(string? annotationsJson)
    {
        if (string.IsNullOrWhiteSpace(annotationsJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(annotationsJson);
            var r = doc.RootElement;
            var readOnly = r.TryGetProperty("readOnlyHint", out var ro) && ro.ValueKind == JsonValueKind.True;
            var destructive = r.TryGetProperty("destructiveHint", out var d) && d.ValueKind == JsonValueKind.True;
            return !readOnly || destructive;
        }
        catch (JsonException) { return true; }
    }

    public static string SuggestAlias(string name)
    {
        var a = AliasInvalid().Replace(name.ToLowerInvariant(), "_").Trim('_');
        if (a.Length == 0) a = "tools";
        return a.Length > 24 ? a[..24] : a;
    }

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex AliasInvalid();
}

public sealed partial class AgentToolService(AgentDockDbContext db) : IAgentToolService
{
    public const int MaxToolsPerAgent = 64;
    private static readonly string[] ReservedAliases = ["http", "web", "web_search"];

    [GeneratedRegex("^[a-z0-9_]{1,24}$")]
    private static partial Regex AliasPattern();

    public async Task<IReadOnlyList<ConnectionAssignmentView>> GetAsync(Guid accountId, Guid agentId, CancellationToken ct = default)
    {
        var connections = await db.Connections.AsNoTracking().Where(c => c.AccountId == accountId && c.DeletedUtc == null && c.Kind == "Mcp").OrderBy(c => c.Name).ToListAsync(ct);
        var links = await db.AgentConnections.AsNoTracking().Where(a => a.AccountId == accountId && a.AgentId == agentId).ToListAsync(ct);
        var linkIds = links.Select(l => l.Id).ToList();
        var allowed = await db.AgentMcpTools.AsNoTracking().Where(t => linkIds.Contains(t.AgentConnectionId)).ToListAsync(ct);
        var connIds = connections.Select(c => c.Id).ToList();
        var snaps = await db.McpToolSnapshots.AsNoTracking().Where(t => connIds.Contains(t.ConnectionId) && t.IsPresent).OrderBy(t => t.ToolName).ToListAsync(ct);

        return connections.Select(c =>
        {
            var link = links.FirstOrDefault(l => l.ConnectionId == c.Id);
            var tools = snaps.Where(s => s.ConnectionId == c.Id).Select(s =>
            {
                var row = link == null ? null : allowed.FirstOrDefault(a => a.AgentConnectionId == link.Id && a.ToolName == s.ToolName);
                var def = McpAnnotations.DefaultRequiresConfirmation(s.AnnotationsJson);
                return new ToolChoice(s.ToolName, s.Title, s.Description, s.AnnotationsJson, row != null, row?.RequiresConfirmation ?? def, def,
                    row != null && !row.ApprovedSchemaHash.AsSpan().SequenceEqual(s.SchemaHash));
            }).ToList();
            return new ConnectionAssignmentView(c, link?.Id, link?.Alias ?? McpAnnotations.SuggestAlias(c.Name), link != null, link?.Enabled ?? true, tools);
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> SaveAsync(Guid accountId, Guid agentId, IReadOnlyList<ConnectionAssignmentInput> assignments, CancellationToken ct = default)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId && a.AccountId == accountId && a.DeletedUtc == null, ct)) return ["Agent not found."];
        var errors = new List<string>();
        var aliases = new HashSet<string>();
        var connIds = assignments.Select(a => a.ConnectionId).Distinct().ToList();
        var conns = await db.Connections.Where(c => c.AccountId == accountId && c.DeletedUtc == null && c.Kind == "Mcp" && connIds.Contains(c.Id)).ToListAsync(ct);
        var snaps = await db.McpToolSnapshots.Where(t => t.AccountId == accountId && connIds.Contains(t.ConnectionId) && t.IsPresent).ToListAsync(ct);
        var total = 0;
        foreach (var a in assignments)
        {
            var conn = conns.FirstOrDefault(c => c.Id == a.ConnectionId);
            if (conn == null) { errors.Add("A selected connection was not found."); continue; }
            if (!AliasPattern().IsMatch(a.Alias ?? "")) errors.Add($"Alias for '{conn.Name}' must be 1-24 characters: a-z, 0-9 and '_'.");
            else if (ReservedAliases.Contains(a.Alias)) errors.Add($"Alias '{a.Alias}' is reserved.");
            else if (!aliases.Add(a.Alias)) errors.Add($"Alias '{a.Alias}' is used twice.");
            var names = a.Tools.Select(t => t.ToolName).ToList();
            if (names.Distinct().Count() != names.Count) errors.Add($"Duplicate tools for '{conn.Name}'.");
            foreach (var t in a.Tools)
                if (!snaps.Any(s => s.ConnectionId == conn.Id && s.ToolName == t.ToolName)) errors.Add($"Tool '{t.ToolName}' is not available on '{conn.Name}'. Discover tools again.");
            total += a.Tools.Count;
        }
        if (total > MaxToolsPerAgent) errors.Add($"At most {MaxToolsPerAgent} tools per agent.");
        if (errors.Count > 0) return errors;

        var existing = await db.AgentConnections.Where(a => a.AgentId == agentId).ToListAsync(ct);
        var existingIds = existing.Select(e => e.Id).ToList();
        var existingTools = await db.AgentMcpTools.Where(t => existingIds.Contains(t.AgentConnectionId)).ToListAsync(ct);

        foreach (var gone in existing.Where(e => !connIds.Contains(e.ConnectionId))) db.AgentConnections.Remove(gone);
        foreach (var a in assignments)
        {
            var link = existing.FirstOrDefault(e => e.ConnectionId == a.ConnectionId);
            if (link == null)
            {
                link = new AgentConnection { AccountId = accountId, AgentId = agentId, ConnectionId = a.ConnectionId };
                db.AgentConnections.Add(link);
            }
            link.Alias = a.Alias; link.Enabled = a.Enabled;

            var mine = existingTools.Where(t => t.AgentConnectionId == link.Id).ToList();
            db.AgentMcpTools.RemoveRange(mine.Where(t => a.Tools.All(x => x.ToolName != t.ToolName)));
            foreach (var sel in a.Tools)
            {
                var snap = snaps.First(s => s.ConnectionId == a.ConnectionId && s.ToolName == sel.ToolName);
                var row = mine.FirstOrDefault(t => t.ToolName == sel.ToolName);
                if (row == null) db.AgentMcpTools.Add(new AgentMcpTool { AccountId = accountId, AgentConnectionId = link.Id, ToolName = sel.ToolName, RequiresConfirmation = sel.RequiresConfirmation, ApprovedSchemaHash = snap.SchemaHash });
                else { row.RequiresConfirmation = sel.RequiresConfirmation; row.ApprovedSchemaHash = snap.SchemaHash; }
            }
        }
        await db.SaveChangesAsync(ct);
        return [];
    }
}
