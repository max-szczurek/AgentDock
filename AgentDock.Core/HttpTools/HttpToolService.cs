using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Mcp;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.HttpTools;

public sealed record HttpToolSummary(HttpTool Tool, string ConnectionName, string Method, int UsedByAgents);
public sealed record HttpToolSaveResult(bool Success, IReadOnlyList<string> Errors, HttpTool? Tool = null);
public sealed record HttpToolDeleteResult(bool Found, bool Deleted, IReadOnlyList<string> BlockedBy);
public sealed record HttpToolTestResult(bool Ran, bool IsError, string Status, int? HttpStatus, long DurationMs, string RequestUrl, string Output, string? Error);

public sealed record HttpToolAssignmentView(HttpTool Tool, string ConnectionName, string Method, bool Assigned, bool Enabled, bool RequiresConfirmation, bool DefaultConfirm);
public sealed record HttpToolAssignmentInput(Guid HttpToolId, bool Enabled, bool RequiresConfirmation);

public interface IHttpToolService
{
    Task<IReadOnlyList<HttpToolSummary>> ListAsync(Guid accountId, CancellationToken ct = default);
    Task<HttpTool?> GetAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<HttpToolSaveResult> SaveAsync(Guid accountId, Guid? id, HttpToolDraft draft, CancellationToken ct = default);
    Task<HttpToolDeleteResult> DeleteAsync(Guid accountId, Guid id, bool removeFromAll, CancellationToken ct = default);
    Task<HttpToolTestResult?> TestAsync(Guid accountId, Guid id, string? sampleInputJson, CancellationToken ct = default);
    Task<IReadOnlyList<HttpToolAssignmentView>> GetAssignmentsAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SaveAssignmentsAsync(Guid accountId, Guid agentId, IReadOnlyList<HttpToolAssignmentInput> assigned, CancellationToken ct = default);
}

public sealed class HttpToolService(AgentDockDbContext db, IConnectionService connections, IHttpToolRunner runner, TimeProvider time) : IHttpToolService
{
    public const int MaxToolsPerAccount = 100;
    public const int MaxHttpToolsPerAgent = 32;

    public async Task<IReadOnlyList<HttpToolSummary>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        var rows = await (from t in db.HttpTools.AsNoTracking()
                          join c in db.Connections.AsNoTracking() on t.ConnectionId equals c.Id
                          where t.AccountId == accountId && t.DeletedUtc == null
                          orderby t.Name
                          select new
                          {
                              Tool = t, ConnName = c.Name,
                              Used = db.AgentHttpTools.Count(a => a.HttpToolId == t.Id && db.Agents.Any(g => g.Id == a.AgentId && g.DeletedUtc == null)),
                          }).ToListAsync(ct);
        return rows.Select(r => new HttpToolSummary(r.Tool, r.ConnName,
            HttpToolRules.TryParseExecution(r.Tool.ExecutionConfigJson, out var x) ? x.Method : "?", r.Used)).ToList();
    }

    public Task<HttpTool?> GetAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
        db.HttpTools.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId && t.DeletedUtc == null, ct);

    public async Task<HttpToolSaveResult> SaveAsync(Guid accountId, Guid? id, HttpToolDraft draft, CancellationToken ct = default)
    {
        var errors = HttpToolRules.Validate(draft);
        var conn = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == draft.ConnectionId && c.AccountId == accountId && c.DeletedUtc == null && c.Kind == "Http", ct);
        if (conn == null) errors.Add("Choose one of your HTTP API connections.");
        var existing = id == null ? null : await db.HttpTools.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId && t.DeletedUtc == null, ct);
        if (id != null && existing == null) return new(false, ["HTTP tool not found."]);
        if (await db.HttpTools.AnyAsync(t => t.AccountId == accountId && t.DeletedUtc == null && t.Name == draft.Name && t.Id != id, ct)) errors.Add("An HTTP tool with this name already exists.");
        if (existing == null && await db.HttpTools.CountAsync(t => t.AccountId == accountId && t.DeletedUtc == null, ct) >= MaxToolsPerAccount) errors.Add($"Limit of {MaxToolsPerAccount} HTTP tools reached.");
        if (errors.Count > 0) return new(false, errors, existing);

        var now = time.GetUtcNow().UtcDateTime;
        var tool = existing ?? new HttpTool { AccountId = accountId };
        tool.Name = draft.Name; tool.DisplayName = draft.DisplayName; tool.Description = draft.Description; tool.ConnectionId = draft.ConnectionId;
        tool.InputSchemaJson = draft.InputSchema.ToJsonString();
        tool.ExecutionConfigJson = HttpToolRules.SerializeExecution(draft.Execution);
        tool.UpdatedUtc = now;
        if (existing == null) db.HttpTools.Add(tool); else tool.Version++;
        await db.SaveChangesAsync(ct);
        return new(true, [], tool);
    }

    public async Task<HttpToolDeleteResult> DeleteAsync(Guid accountId, Guid id, bool removeFromAll, CancellationToken ct = default)
    {
        var tool = await db.HttpTools.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == accountId && t.DeletedUtc == null, ct);
        if (tool == null) return new(false, false, []);
        var links = await db.AgentHttpTools.Where(a => a.AccountId == accountId && a.HttpToolId == id).ToListAsync(ct);
        if (links.Count > 0)
        {
            var agentIds = links.Select(l => l.AgentId).ToList();
            var names = await db.Agents.Where(a => agentIds.Contains(a.Id) && a.DeletedUtc == null).OrderBy(a => a.Name).Select(a => a.Name).ToListAsync(ct);
            if (names.Count > 0 && !removeFromAll) return new(true, false, names);
            db.AgentHttpTools.RemoveRange(links);
        }
        tool.DeletedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new(true, true, []);
    }

    public async Task<HttpToolTestResult?> TestAsync(Guid accountId, Guid id, string? sampleInputJson, CancellationToken ct = default)
    {
        var tool = await GetAsync(accountId, id, ct);
        if (tool == null) return null;
        if (!HttpToolRules.TryParseExecution(tool.ExecutionConfigJson, out var exec)) return Rejected("The tool's configuration is invalid.");

        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(sampleInputJson) ? "{}" : sampleInputJson) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return Rejected("The sample input must be a JSON object."); }
        try
        {
            var valid = JsonSchema.FromText(tool.InputSchemaJson).Evaluate(JsonSerializer.SerializeToElement(args), new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;
            if (!valid) return Rejected("The sample input does not match the tool's input schema.");
        }
        catch (Exception) { return Rejected("The tool's schema could not be evaluated."); }

        var access = await connections.ResolveAccessAsync(accountId, tool.ConnectionId, ct);
        if (access == null) return Rejected("The connection is no longer available.");
        var r = await runner.RunAsync(access, exec, args, ct);
        return new HttpToolTestResult(true, r.IsError, r.Status, r.HttpStatus, r.DurationMs, r.RequestUrl,
            r.IsError ? "" : Pretty(r.Content), r.IsError ? r.ErrorMessage : null);
    }

    private static HttpToolTestResult Rejected(string message) => new(false, true, "Blocked", null, 0, "", "", message);

    private static string Pretty(string content)
    {
        try { return JsonNode.Parse(content)?.ToJsonString(HttpToolRules.JsonPretty) ?? content; }
        catch (JsonException) { return content; }
    }

    public async Task<IReadOnlyList<HttpToolAssignmentView>> GetAssignmentsAsync(Guid accountId, Guid agentId, CancellationToken ct = default)
    {
        var tools = await ListAsync(accountId, ct);
        var links = await db.AgentHttpTools.AsNoTracking().Where(a => a.AccountId == accountId && a.AgentId == agentId).ToListAsync(ct);
        return tools.Select(t =>
        {
            var link = links.FirstOrDefault(l => l.HttpToolId == t.Tool.Id);
            var def = HttpToolRules.DefaultRequiresConfirmation(t.Method);
            return new HttpToolAssignmentView(t.Tool, t.ConnectionName, t.Method, link != null, link?.Enabled ?? true, link?.RequiresConfirmation ?? def, def);
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> SaveAssignmentsAsync(Guid accountId, Guid agentId, IReadOnlyList<HttpToolAssignmentInput> assigned, CancellationToken ct = default)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId && a.AccountId == accountId && a.DeletedUtc == null, ct)) return ["Agent not found."];
        var ids = assigned.Select(a => a.HttpToolId).Distinct().ToList();
        var valid = await db.HttpTools.Where(t => t.AccountId == accountId && t.DeletedUtc == null && ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
        if (valid.Count != ids.Count) return ["A selected HTTP tool was not found."];
        if (ids.Count > MaxHttpToolsPerAgent) return [$"At most {MaxHttpToolsPerAgent} HTTP tools per agent."];

        var existing = await db.AgentHttpTools.Where(a => a.AgentId == agentId).ToListAsync(ct);
        db.AgentHttpTools.RemoveRange(existing.Where(e => !ids.Contains(e.HttpToolId)));
        foreach (var a in assigned.DistinctBy(a => a.HttpToolId))
        {
            var row = existing.FirstOrDefault(e => e.HttpToolId == a.HttpToolId);
            if (row == null) db.AgentHttpTools.Add(new AgentHttpTool { AccountId = accountId, AgentId = agentId, HttpToolId = a.HttpToolId, Enabled = a.Enabled, RequiresConfirmation = a.RequiresConfirmation });
            else { row.Enabled = a.Enabled; row.RequiresConfirmation = a.RequiresConfirmation; }
        }
        await db.SaveChangesAsync(ct);
        return [];
    }
}
