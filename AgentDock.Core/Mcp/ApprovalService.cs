using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Runtime;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public enum ClaimKind { NotFound, AlreadyDecided, Approved, Denied, Expired }

public sealed record ClaimResult(ClaimKind Kind, ToolExecution? Row, string? CurrentStatus = null);

public sealed record ParkedResult(string ToolUseId, string ToolName, ToolOutcome Outcome);

public sealed record PendingApprovalView(Guid ToolExecutionId, string Name, string Summary, DateTime ExpiresUtc);

public interface IApprovalService
{
    /// <summary>Stores every result of a paused tool group so the single tool_result message can be assembled once all approvals are decided.</summary>
    Task ParkAsync(ToolCallContext ctx, IReadOnlyList<ParkedResult> results, CancellationToken ct = default);
    Task<ToolExecution?> FindAsync(Conversation c, Guid executionId, CancellationToken ct = default);
    Task<ClaimResult> ClaimAsync(Conversation c, Guid executionId, bool approve, CancellationToken ct = default);
    Task<IReadOnlyList<PendingApprovalView>> PendingAsync(Conversation c, CancellationToken ct = default);
    /// <summary>Marks overdue approvals expired (treated as denied). Returns true if unexpired approvals are still pending.</summary>
    Task<bool> SettleExpiredAsync(Conversation c, CancellationToken ct = default);
    /// <summary>If the conversation ends in an assistant tool_use message whose approvals are all decided, appends the combined tool_result message.</summary>
    Task<bool> TryAssembleAsync(Conversation c, CancellationToken ct = default);
}

public sealed class ApprovalService(AgentDockDbContext db, IConversationService conversations, TimeProvider time) : IApprovalService
{
    public async Task ParkAsync(ToolCallContext ctx, IReadOnlyList<ParkedResult> results, CancellationToken ct = default)
    {
        foreach (var r in results.Where(r => !r.Outcome.IsPending))
        {
            ToolExecution? row = r.Outcome.ExecutionId is { } id ? await db.ToolExecutions.FirstOrDefaultAsync(t => t.Id == id, ct) : null;
            if (row == null)
            {
                var now = time.GetUtcNow().UtcDateTime;
                row = new ToolExecution
                {
                    AccountId = ctx.AccountId, AgentId = ctx.AgentId, ConversationId = ctx.ConversationId, TurnId = ctx.TurnId,
                    ToolUseId = r.ToolUseId.Length > 100 ? r.ToolUseId[..100] : r.ToolUseId,
                    ExposedName = r.ToolName.Length > 128 ? r.ToolName[..128] : r.ToolName, Kind = "Unknown",
                    Status = "Blocked", ErrorCode = "too_many_calls", ErrorMessage = r.Outcome.Content, RequestedUtc = now, DecidedUtc = now, CompletedUtc = now,
                };
                db.ToolExecutions.Add(row);
            }
            row.ResultContent ??= r.Outcome.Content;
            row.ResultIsError ??= r.Outcome.IsError;
        }
        await db.SaveChangesAsync(ct);
    }

    public Task<ToolExecution?> FindAsync(Conversation c, Guid executionId, CancellationToken ct = default) =>
        db.ToolExecutions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == executionId && t.AccountId == c.AccountId && t.ConversationId == c.Id, ct);

    public async Task<ClaimResult> ClaimAsync(Conversation c, Guid executionId, bool approve, CancellationToken ct = default)
    {
        var row = await FindAsync(c, executionId, ct);
        if (row == null) return new(ClaimKind.NotFound, null);
        if (row.Status != "PendingApproval") return new(ClaimKind.AlreadyDecided, row, row.Status);

        var now = time.GetUtcNow().UtcDateTime;
        var expired = row.ExpiresUtc is { } e && e <= now;
        var kind = expired ? ClaimKind.Expired : approve ? ClaimKind.Approved : ClaimKind.Denied;
        var query = db.ToolExecutions.Where(t => t.Id == executionId && t.Status == "PendingApproval");
        var changed = kind switch
        {
            ClaimKind.Approved => await query.ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Running").SetProperty(t => t.DecidedUtc, now), ct),
            ClaimKind.Denied => await query.ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Denied").SetProperty(t => t.DecidedUtc, now)
                .SetProperty(t => t.ErrorCode, "denied").SetProperty(t => t.ResultContent, ApprovalText.Declined).SetProperty(t => t.ResultIsError, true), ct),
            _ => await query.ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Expired").SetProperty(t => t.DecidedUtc, now)
                .SetProperty(t => t.ErrorCode, "expired").SetProperty(t => t.ResultContent, ApprovalText.Expired).SetProperty(t => t.ResultIsError, true), ct),
        };
        if (changed == 0)
        {
            var again = await FindAsync(c, executionId, ct);
            return new(ClaimKind.AlreadyDecided, again, again?.Status);
        }
        return new(kind, await FindAsync(c, executionId, ct));
    }

    public async Task<IReadOnlyList<PendingApprovalView>> PendingAsync(Conversation c, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await (from t in db.ToolExecutions.AsNoTracking()
                          join s in db.McpToolSnapshots.AsNoTracking() on new { Id = t.ConnectionId, Name = t.UpstreamToolName } equals new { Id = (Guid?)s.ConnectionId, Name = (string?)s.ToolName } into snaps
                          from s in snaps.DefaultIfEmpty()
                          where t.AccountId == c.AccountId && t.ConversationId == c.Id && t.Status == "PendingApproval" && t.ExpiresUtc > now
                          orderby t.RequestedUtc
                          select new { t.Id, t.ExposedName, t.UpstreamToolName, Title = s == null ? null : s.Title, t.InputJson, t.ExpiresUtc }).ToListAsync(ct);
        return rows.Select(r => new PendingApprovalView(r.Id, r.Title ?? r.UpstreamToolName ?? r.ExposedName, ApprovalText.Summarize(r.InputJson), r.ExpiresUtc!.Value)).ToList();
    }

    public async Task<bool> SettleExpiredAsync(Conversation c, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expired = await db.ToolExecutions
            .Where(t => t.AccountId == c.AccountId && t.ConversationId == c.Id && t.Status == "PendingApproval" && t.ExpiresUtc <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "Expired").SetProperty(t => t.DecidedUtc, now)
                .SetProperty(t => t.ErrorCode, "expired").SetProperty(t => t.ResultContent, ApprovalText.Expired).SetProperty(t => t.ResultIsError, true), ct);
        if (expired > 0) await TryAssembleAsync(c, ct);
        return await db.ToolExecutions.AnyAsync(t => t.AccountId == c.AccountId && t.ConversationId == c.Id && t.Status == "PendingApproval", ct);
    }

    public async Task<bool> TryAssembleAsync(Conversation c, CancellationToken ct = default)
    {
        var tail = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == c.Id)
            .OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(ct);
        if (tail == null || tail.Role != "assistant") return false;
        var ids = (JsonNode.Parse(tail.ContentJson) as JsonArray ?? [])
            .Where(b => b?["type"]?.GetValue<string>() == "tool_use").Select(b => b!["id"]!.GetValue<string>()).ToList();
        if (ids.Count == 0) return false;

        var rows = await db.ToolExecutions.AsNoTracking().Where(t => t.AccountId == c.AccountId && t.ConversationId == c.Id && ids.Contains(t.ToolUseId)).ToListAsync(ct);
        if (rows.Any(r => r.Status == "PendingApproval")) return false;

        var results = new JsonArray();
        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(r => r.ToolUseId == id);
            var content = row?.ResultContent ?? row?.ErrorMessage ?? "Tool result unavailable.";
            results.Add(new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content,
                ["is_error"] = row?.ResultIsError ?? row?.Status != "Succeeded",
            });
        }
        await conversations.AppendAsync(c, new ConversationMessage { TurnId = tail.TurnId, Role = "user", ContentJson = results.ToJsonString() }, CancellationToken.None);
        return true;
    }
}
