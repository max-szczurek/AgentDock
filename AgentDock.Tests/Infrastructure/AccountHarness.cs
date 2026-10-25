using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentDock.Tests.Infrastructure;

/// <summary>A throwaway user + account in the shared database, removed on dispose.</summary>
public sealed class AccountHarness : IAsyncDisposable
{
    public AgentDockDbContext Db { get; private set; } = null!;
    public Guid AccountId { get; private set; }
    public string Email { get; } = TestDb.NewTestEmail();

    public static DbContextOptions<AgentDockDbContext> Options() =>
        new DbContextOptionsBuilder<AgentDockDbContext>().UseSqlServer(TestDb.ConnectionString).Options;

    public static async Task<AccountHarness> StartAsync()
    {
        var h = new AccountHarness();
        await new SchemaMigrator(TestDb.ConnectionString!, NullLogger<SchemaMigrator>.Instance).MigrateAsync();
        h.Db = new AgentDockDbContext(Options());
        var user = new Microsoft.AspNetCore.Identity.IdentityUser
        {
            UserName = h.Email, Email = h.Email, NormalizedUserName = h.Email.ToUpperInvariant(), NormalizedEmail = h.Email.ToUpperInvariant(),
        };
        h.Db.Users.Add(user);
        var account = new Account { OwnerUserId = user.Id, DisplayName = h.Email };
        h.Db.Accounts.Add(account);
        await h.Db.SaveChangesAsync();
        h.AccountId = account.Id;
        return h;
    }

    public async ValueTask DisposeAsync()
    {
        await TestDb.DeleteUserAsync(Email);
        await Db.DisposeAsync();
    }
}
