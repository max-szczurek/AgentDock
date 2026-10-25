using System.Security.Claims;
using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Web.Services;

public interface ICurrentAccountAccessor
{
    Task<Account?> GetAsync(CancellationToken ct = default);
}

public sealed class CurrentAccountAccessor(IHttpContextAccessor http, AgentDockDbContext db) : ICurrentAccountAccessor
{
    private Account? _account;

    public async Task<Account?> GetAsync(CancellationToken ct = default)
    {
        if (_account != null) return _account;
        var userId = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return null;
        return _account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.OwnerUserId == userId && a.Status == "Active", ct);
    }
}
