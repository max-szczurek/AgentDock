using AgentDock.Core.Data;
using AgentDock.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentDock.Tests.Integration;

[TestClass]
public class SchemaMigratorTests
{
    [TestMethod]
    public async Task ConcurrentAndRepeatedRuns_AreIdempotent()
    {
        TestDb.RequireSql();
        var m = new SchemaMigrator(TestDb.ConnectionString!, NullLogger<SchemaMigrator>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => m.MigrateAsync()));
        Assert.AreEqual(0, await m.MigrateAsync());
    }
}
