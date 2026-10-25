using AgentDock.Core.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Tests.Infrastructure;

/// <summary>
/// Tests share the one AgentDockDB (plan section 0.2): each test owns a throwaway
/// "test-{guid}@agentdock.test" user and removes it afterwards; a janitor removes stale leftovers.
/// </summary>
public static class TestDb
{
    public const string EmailDomain = "@agentdock.test";

    public static string? ConnectionString
    {
        get
        {
            var cs = Environment.GetEnvironmentVariable("AGENTDOCK_SQL_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(cs)) return null;
            var b = new SqlConnectionStringBuilder(cs);
            if (!OperatingSystem.IsWindows()) b.IntegratedSecurity = false;
            return b.ConnectionString;
        }
    }

    public static void RequireSql()
    {
        if (ConnectionString == null) Assert.Inconclusive("AGENTDOCK_SQL_CONNECTION_STRING is not set; skipping SQL-backed test.");
    }

    public static string NewTestEmail() => $"test-{Guid.NewGuid():N}{EmailDomain}";

    public static async Task DeleteUserAsync(string email)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.AspNetUsers WHERE Email = @e";
        cmd.Parameters.AddWithValue("@e", email);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<int> CountUsersAsync(string email)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.AspNetUsers WHERE Email = @e";
        cmd.Parameters.AddWithValue("@e", email);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public static async Task<int> CountAccountsAsync(string email)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.Account a JOIN dbo.AspNetUsers u ON u.Id = a.OwnerUserId WHERE u.Email = @e";
        cmd.Parameters.AddWithValue("@e", email);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public static async Task RunJanitorAsync()
    {
        if (ConnectionString == null) return;
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT OBJECT_ID(N'dbo.Account')";
        if (await exists.ExecuteScalarAsync() is DBNull or null) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE u FROM dbo.AspNetUsers u
            JOIN dbo.Account a ON a.OwnerUserId = u.Id
            WHERE u.Email LIKE N'%@agentdock.test' AND a.CreatedUtc < DATEADD(hour, -24, SYSUTCDATETIME())
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static async Task Init(TestContext _) => await TestDb.RunJanitorAsync();
}
