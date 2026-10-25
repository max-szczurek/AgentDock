using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AgentDock.Core.Data;

/// <summary>
/// Applies embedded, ordered, additive SQL scripts (Migrations/NNNN_name.sql) exactly once.
/// The database is shared by dev, tests and staging, so a session-level applock serialises runners.
/// </summary>
public class SchemaMigrator(string connectionString, ILogger<SchemaMigrator> logger)
{
    private const string LockName = "AgentDock.SchemaMigrator";
    private static readonly Regex BatchSeparator = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource=@n, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=120000; SELECT @r;";
            lockCmd.Parameters.AddWithValue("@n", LockName);
            var rc = Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct));
            if (rc < 0) throw new InvalidOperationException($"Could not acquire schema migration lock (code {rc}).");
        }

        try
        {
            await Exec(conn, """
                IF OBJECT_ID(N'dbo.SchemaVersion') IS NULL
                CREATE TABLE dbo.SchemaVersion (
                    Version int NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
                    Name nvarchar(200) NOT NULL,
                    AppliedUtc datetime2(3) NOT NULL CONSTRAINT DF_SchemaVersion_Applied DEFAULT SYSUTCDATETIME());
                """, ct);

            var applied = new HashSet<int>();
            await using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT Version FROM dbo.SchemaVersion";
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
            }

            var asm = typeof(SchemaMigrator).Assembly;
            var scripts = asm.GetManifestResourceNames()
                .Select(n => (Resource: n, Match: Regex.Match(n, @"Migrations\.(\d{4})_(.+)\.sql$")))
                .Where(x => x.Match.Success)
                .Select(x => (x.Resource, Version: int.Parse(x.Match.Groups[1].Value), Name: x.Match.Groups[2].Value))
                .OrderBy(x => x.Version);

            var count = 0;
            foreach (var s in scripts.Where(s => !applied.Contains(s.Version)))
            {
                logger.LogInformation("Applying migration {Version} {Name}", s.Version, s.Name);
                using var stream = asm.GetManifestResourceStream(s.Resource)!;
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(ct);

                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                foreach (var batch in BatchSeparator.Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = batch;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT dbo.SchemaVersion (Version, Name) VALUES (@v, @n)";
                    ins.Parameters.AddWithValue("@v", s.Version);
                    ins.Parameters.AddWithValue("@n", s.Name);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                count++;
            }
            return count;
        }
        finally
        {
            await using var rel = conn.CreateCommand();
            rel.CommandText = "EXEC sp_releaseapplock @Resource=@n, @LockOwner='Session'";
            rel.Parameters.AddWithValue("@n", LockName);
            try { await rel.ExecuteNonQueryAsync(CancellationToken.None); } catch { /* connection closing releases it */ }
        }
    }

    private static async Task Exec(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
