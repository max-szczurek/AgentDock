using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Runtime;

public interface IUsageService
{
    /// <summary>Atomically reserves one turn against the daily cap.</summary>
    Task<bool> TryReserveTurnAsync(Guid accountId, Guid agentId, int dailyCap, CancellationToken ct = default);
    Task RecordRejectedAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task AddTokensAsync(Guid agentId, long input, long output, int webSearches, int toolCalls, CancellationToken ct = default);
}

public sealed class UsageService(AgentDockDbContext db, TimeProvider time) : IUsageService
{
    private DateTime Today => time.GetUtcNow().UtcDateTime.Date;

    public async Task<bool> TryReserveTurnAsync(Guid accountId, Guid agentId, int dailyCap, CancellationToken ct = default)
    {
        var d = Today;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.UsageDaily SET Turns = Turns + 1 WHERE AgentId = {agentId} AND DateUtc = {d} AND Turns < {dailyCap}", ct);
            if (updated == 1) return true;
            if (await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM dbo.UsageDaily WHERE AgentId = {agentId} AND DateUtc = {d}").SingleAsync(ct) > 0)
                return false;
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT dbo.UsageDaily (AccountId, AgentId, DateUtc, Turns) VALUES ({accountId}, {agentId}, {d}, 1)", ct);
                return true;
            }
            catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { }
        }
        return false;
    }

    public Task RecordRejectedAsync(Guid accountId, Guid agentId, CancellationToken ct = default) =>
        UpsertRejectedAsync(accountId, agentId, ct);

    public async Task AddTokensAsync(Guid agentId, long input, long output, int webSearches, int toolCalls, CancellationToken ct = default)
    {
        var d = Today;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.UsageDaily SET InputTokens = InputTokens + {input}, OutputTokens = OutputTokens + {output}, WebSearches = WebSearches + {webSearches}, ToolCalls = ToolCalls + {toolCalls} WHERE AgentId = {agentId} AND DateUtc = {d}", ct);
    }

    private async Task UpsertRejectedAsync(Guid accountId, Guid agentId, CancellationToken ct)
    {
        var d = Today;
        var n = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.UsageDaily SET RejectedRequests = RejectedRequests + 1 WHERE AgentId = {agentId} AND DateUtc = {d}", ct);
        if (n == 0)
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT dbo.UsageDaily (AccountId, AgentId, DateUtc, RejectedRequests) VALUES ({accountId}, {agentId}, {d}, 1)", ct);
            }
            catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { }
    }
}
