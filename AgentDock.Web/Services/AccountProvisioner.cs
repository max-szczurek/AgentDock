using AgentDock.Core.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Web.Services;

public class AccountProvisioner(AgentDockDbContext db, UserManager<IdentityUser> users)
{
    public async Task<IdentityUser> EnsureUserAsync(string email, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email);
        if (user == null)
        {
            user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user);
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
        await EnsureAccountAsync(user, ct);
        return user;
    }

    public async Task<Account> EnsureAccountAsync(IdentityUser user, CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.OwnerUserId == user.Id, ct);
        if (account != null) return account;
        account = new Account { OwnerUserId = user.Id, DisplayName = user.Email ?? user.UserName ?? "Account" };
        db.Accounts.Add(account);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(account).State = EntityState.Detached;
            return await db.Accounts.FirstAsync(a => a.OwnerUserId == user.Id, ct);
        }
        return account;
    }
}
