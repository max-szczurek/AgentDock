using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public sealed record ToolCallContext(Guid AccountId, Guid AgentId, Guid ConversationId, Guid TurnId);

public sealed record ToolOutcome(
    string DisplayName, bool IsError, string Content, string Status, string? PublicError,
    Guid? ExecutionId = null, DateTime? ApprovalExpiresUtc = null, string? ApprovalSummary = null)
{
    public bool IsPending => Status == "PendingApproval";
}

public interface IToolExecutor
{
    Task<ToolOutcome> ExecuteAsync(ToolCallContext ctx, TurnToolRegistry registry, string toolUseId, string name, JsonNode? input, CancellationToken ct);

    /// <summary>Runs a tool whose approval was already claimed (row is <c>Running</c>), re-checking everything at call time.</summary>
    Task<ToolOutcome> RunApprovedAsync(ToolCallContext ctx, TurnToolRegistry registry, Guid executionId, CancellationToken ct);
}

public static class ApprovalText
{
    public const string Declined = "The user declined this action.";
    public const string Expired = "The approval request expired.";
    public const int SummaryMaxChars = 1024;

    public static string Summarize(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson)) return "";
        string text;
        try { text = JsonNode.Parse(inputJson)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? ""; }
        catch (JsonException) { text = inputJson; }
        return text.Length > SummaryMaxChars ? text[..SummaryMaxChars] + "\n..." : text;
    }
}

public sealed class ToolExecutor(AgentDockDbContext db, IConnectionService connections, IMcpGateway gateway, TimeProvider time) : IToolExecutor
{
    public const int MaxResultChars = 16_000;
    public const int MaxInputJsonChars = 32 * 1024;
    public const int MaxPreviewChars = 4000;
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);

    public async Task<ToolOutcome> ExecuteAsync(ToolCallContext ctx, TurnToolRegistry registry, string toolUseId, string name, JsonNode? input, CancellationToken ct)
    {
        var inputJson = input?.ToJsonString() ?? "{}";
        var row = new ToolExecution
        {
            AccountId = ctx.AccountId, AgentId = ctx.AgentId, ConversationId = ctx.ConversationId, TurnId = ctx.TurnId,
            ToolUseId = toolUseId.Length > 100 ? toolUseId[..100] : toolUseId,
            ExposedName = name.Length > 128 ? name[..128] : name,
            InputJson = inputJson.Length > MaxInputJsonChars ? inputJson[..MaxInputJsonChars] : inputJson,
            RequestedUtc = time.GetUtcNow().UtcDateTime,
        };

        if (!registry.TryGet(name, out var tool))
        {
            row.Kind = "Unknown";
            return await BlockAsync(row, name, "not_available", "Tool not available.", keepContent: false);
        }
        row.Kind = tool.Kind; row.ConnectionId = tool.ConnectionId; row.UpstreamToolName = tool.UpstreamName;

        if (input != null && input is not JsonObject)
            return await BlockAsync(row, tool.DisplayName, "invalid_input", "Tool input must be a JSON object.", keepContent: false);
        var args = input as JsonObject ?? new JsonObject();
        if (Validate(tool, args) is { } invalid)
            return await BlockAsync(row, tool.DisplayName, invalid.Code, invalid.Message, keepContent: false);

        if (tool.RequiresConfirmation)
        {
            if (inputJson.Length > MaxInputJsonChars)
                return await BlockAsync(row, tool.DisplayName, "input_too_large", "The tool input is too large to approve.", keepContent: false);
            row.Status = "PendingApproval";
            row.ExpiresUtc = row.RequestedUtc + ApprovalLifetime;
            db.ToolExecutions.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
            return new ToolOutcome(tool.DisplayName, false, "", "PendingApproval", null, row.Id, row.ExpiresUtc, ApprovalText.Summarize(row.InputJson));
        }

        return await RunAsync(ctx, tool, row, args, isNew: true, keepContent: false, ct);
    }

    public async Task<ToolOutcome> RunApprovedAsync(ToolCallContext ctx, TurnToolRegistry registry, Guid executionId, CancellationToken ct)
    {
        var row = await db.ToolExecutions.FirstOrDefaultAsync(t => t.Id == executionId && t.AccountId == ctx.AccountId
            && t.ConversationId == ctx.ConversationId && t.Status == "Running", ct);
        if (row == null) return new ToolOutcome("tool", true, "Tool not available.", "Blocked", "Tool not available.");

        if (!registry.TryGet(row.ExposedName, out var tool))
            return await BlockAsync(row, row.ExposedName, "not_available", "Tool not available.", keepContent: true);
        JsonObject args;
        try { args = JsonNode.Parse(row.InputJson ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return await BlockAsync(row, tool.DisplayName, "invalid_input", "Tool input must be a JSON object.", keepContent: true); }
        if (Validate(tool, args) is { } invalid)
            return await BlockAsync(row, tool.DisplayName, invalid.Code, invalid.Message, keepContent: true);

        return await RunAsync(ctx, tool, row, args, isNew: false, keepContent: true, ct);
    }

    private static (string Code, string Message)? Validate(RegisteredTool tool, JsonObject args)
    {
        try
        {
            var schema = JsonSchema.FromText(tool.InputSchemaJson);
            var result = schema.Evaluate(JsonSerializer.SerializeToElement(args), new EvaluationOptions { OutputFormat = OutputFormat.List });
            return result.IsValid ? null : ("invalid_input", "The tool input did not match the tool's schema.");
        }
        catch (Exception) { return ("invalid_schema", "The tool's schema could not be evaluated."); }
    }

    private async Task<ToolOutcome> RunAsync(ToolCallContext ctx, RegisteredTool tool, ToolExecution row, JsonObject args, bool isNew, bool keepContent, CancellationToken ct)
    {
        var stillEnabled = await db.AgentConnections.AsNoTracking().AnyAsync(ac => ac.Id == tool.AgentConnectionId && ac.AccountId == ctx.AccountId && ac.AgentId == ctx.AgentId && ac.Enabled, ct)
            && await db.Connections.AsNoTracking().AnyAsync(c => c.Id == tool.ConnectionId && c.AccountId == ctx.AccountId && c.DeletedUtc == null, ct);
        var access = stillEnabled ? await connections.ResolveAccessAsync(ctx.AccountId, tool.ConnectionId, ct) : null;
        if (access == null)
            return await BlockAsync(row, tool.DisplayName, "connection_disabled", "Tool not available.", keepContent);

        row.Status = "Running"; row.StartedUtc = time.GetUtcNow().UtcDateTime;
        if (isNew) db.ToolExecutions.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(access.TimeoutSeconds));
        try
        {
            var res = await gateway.CallToolAsync(access, tool.UpstreamName, args, timeout.Token);
            var text = res.Text.Length > MaxResultChars ? res.Text[..MaxResultChars] + $"\n[truncated {res.Text.Length - MaxResultChars} characters]" : res.Text;
            row.ResultBytes = res.Bytes;
            row.ResultPreview = text.Length > MaxPreviewChars ? text[..MaxPreviewChars] : text;
            if (res.IsError)
            {
                row.Status = "Failed"; row.ErrorCode = "tool_error"; row.ErrorMessage = "The tool reported an error.";
                return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, text, "Failed", "The tool reported an error.", row.Id), keepContent);
            }
            row.Status = "Succeeded";
            return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, false, text, "Succeeded", null, row.Id), keepContent);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var err = McpErrors.Sanitize(e);
            var timedOut = err.TimedOut || e is OperationCanceledException;
            if (timedOut) err = new SanitizedError("timeout", "The server did not respond in time.", true);
            row.Status = timedOut ? "TimedOut" : "Failed";
            row.ErrorCode = err.Code; row.ErrorMessage = err.Message; row.HttpStatus = err.HttpStatus;
            return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, "The tool call failed: " + err.Message, row.Status, err.Message, row.Id), keepContent);
        }
        catch (OperationCanceledException)
        {
            row.Status = "Failed"; row.ErrorCode = "cancelled"; row.ErrorMessage = "Cancelled.";
            await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, "Cancelled.", "Failed", "Cancelled.", row.Id), keepContent);
            throw;
        }
    }

    private async Task<ToolOutcome> FinishAsync(ToolExecution row, Stopwatch sw, ToolOutcome outcome, bool keepContent)
    {
        row.DurationMs = (int)sw.ElapsedMilliseconds;
        row.CompletedUtc = time.GetUtcNow().UtcDateTime;
        if (keepContent) { row.ResultContent = outcome.Content; row.ResultIsError = outcome.IsError; }
        await db.SaveChangesAsync(CancellationToken.None);
        return outcome;
    }

    private async Task<ToolOutcome> BlockAsync(ToolExecution row, string display, string code, string message, bool keepContent)
    {
        row.Status = "Blocked"; row.ErrorCode = code; row.ErrorMessage = message;
        row.DecidedUtc = row.CompletedUtc = time.GetUtcNow().UtcDateTime;
        if (keepContent) { row.ResultContent = message; row.ResultIsError = true; }
        if (db.Entry(row).State == EntityState.Detached) db.ToolExecutions.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);
        return new ToolOutcome(display, true, message, "Blocked", message, row.Id);
    }
}
