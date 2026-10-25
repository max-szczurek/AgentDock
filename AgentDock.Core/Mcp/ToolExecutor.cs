using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public sealed record ToolCallContext(Guid AccountId, Guid AgentId, Guid ConversationId, Guid TurnId);

public sealed record ToolOutcome(string DisplayName, bool IsError, string Content, string Status, string? PublicError);

public interface IToolExecutor
{
    Task<ToolOutcome> ExecuteAsync(ToolCallContext ctx, TurnToolRegistry registry, string toolUseId, string name, JsonNode? input, CancellationToken ct);
}

public sealed class ToolExecutor(AgentDockDbContext db, IConnectionService connections, IMcpGateway gateway, TimeProvider time) : IToolExecutor
{
    public const int MaxResultChars = 16_000;
    public const int MaxInputJsonChars = 32 * 1024;
    public const int MaxPreviewChars = 4000;

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
            return await BlockAsync(row, name, "not_available", "Tool not available.");
        }

        row.Kind = tool.Kind; row.ConnectionId = tool.ConnectionId; row.UpstreamToolName = tool.UpstreamName;

        var args = input as JsonObject ?? new JsonObject();
        if (input != null && input is not JsonObject)
            return await BlockAsync(row, tool.DisplayName, "invalid_input", "Tool input must be a JSON object.");
        try
        {
            var schema = JsonSchema.FromText(tool.InputSchemaJson);
            var result = schema.Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(args), new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid)
                return await BlockAsync(row, tool.DisplayName, "invalid_input", "The tool input did not match the tool's schema.");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return await BlockAsync(row, tool.DisplayName, "invalid_schema", "The tool's schema could not be evaluated.");
        }

        if (tool.RequiresConfirmation)
            return await BlockAsync(row, tool.DisplayName, "confirmation_required", "This tool requires confirmation, which is not available yet.");

        var stillEnabled = await db.AgentConnections.AsNoTracking().AnyAsync(ac => ac.Id == tool.AgentConnectionId && ac.AccountId == ctx.AccountId && ac.AgentId == ctx.AgentId && ac.Enabled, ct)
            && await db.Connections.AsNoTracking().AnyAsync(c => c.Id == tool.ConnectionId && c.AccountId == ctx.AccountId && c.DeletedUtc == null, ct);
        if (!stillEnabled)
            return await BlockAsync(row, tool.DisplayName, "connection_disabled", "Tool not available.");

        var access = await connections.ResolveAccessAsync(ctx.AccountId, tool.ConnectionId, ct);
        if (access == null)
            return await BlockAsync(row, tool.DisplayName, "connection_disabled", "Tool not available.");

        row.Status = "Running"; row.StartedUtc = time.GetUtcNow().UtcDateTime;
        db.ToolExecutions.Add(row);
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
                return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, text, "Failed", "The tool reported an error."));
            }
            row.Status = "Succeeded";
            return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, false, text, "Succeeded", null));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var err = McpErrors.Sanitize(e);
            var timedOut = err.TimedOut || e is OperationCanceledException;
            if (timedOut) err = new SanitizedError("timeout", "The server did not respond in time.", true);
            row.Status = timedOut ? "TimedOut" : "Failed";
            row.ErrorCode = err.Code; row.ErrorMessage = err.Message; row.HttpStatus = err.HttpStatus;
            return await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, "The tool call failed: " + err.Message, row.Status, err.Message));
        }
        catch (OperationCanceledException)
        {
            row.Status = "Failed"; row.ErrorCode = "cancelled"; row.ErrorMessage = "Cancelled.";
            await FinishAsync(row, sw, new ToolOutcome(tool.DisplayName, true, "Cancelled.", "Failed", "Cancelled."));
            throw;
        }
    }

    private async Task<ToolOutcome> FinishAsync(ToolExecution row, Stopwatch sw, ToolOutcome outcome)
    {
        row.DurationMs = (int)sw.ElapsedMilliseconds;
        row.CompletedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(CancellationToken.None);
        return outcome;
    }

    private async Task<ToolOutcome> BlockAsync(ToolExecution row, string display, string code, string message)
    {
        row.Status = "Blocked"; row.ErrorCode = code; row.ErrorMessage = message;
        row.DecidedUtc = row.CompletedUtc = time.GetUtcNow().UtcDateTime;
        db.ToolExecutions.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);
        return new ToolOutcome(display, true, message, "Blocked", message);
    }
}
