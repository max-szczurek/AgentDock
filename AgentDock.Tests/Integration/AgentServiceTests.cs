using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Tests.Integration;

[TestClass]
public class AgentServiceTests
{
    private static AgentService Svc(AgentDockDbContext db) => new(db, TimeProvider.System);

    private static AgentInput Input(string name = "Support bot", params string[] origins) => new()
    {
        Name = name, SystemPrompt = "Be helpful.", Origins = origins.ToList(), Enabled = true,
        DefaultModelFamily = ModelFamily.Haiku,
    };

    [TestMethod]
    public async Task CreateListGetUpdate_RoundTrips()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var created = await svc.CreateAsync(h.AccountId, Input("A", "https://a.example.com", "http://localhost:3000"));
        Assert.IsTrue(created.Success, string.Join(";", created.Errors));
        var id = created.Agent!.Id;

        var got = (await Svc(new AgentDockDbContext(AccountHarness.Options())).GetAsync(h.AccountId, id))!;
        Assert.AreEqual("A", got.Name);
        Assert.AreEqual("haiku", got.DefaultModelFamily);
        Assert.AreEqual("Enabled", got.Status);
        Assert.AreEqual(2, got.Origins.Count);
        Assert.AreEqual(1, (await svc.ListAsync(h.AccountId)).Count);

        var input = AgentService.ToInput(got);
        input.Name = "A2";
        input.Origins = ["https://b.example.com", "http://localhost:3000"];
        input.Appearance = new AppearanceSettings { Title = "Hello", PanelWidth = 420 };
        input.Enabled = false;
        var upd = await svc.UpdateAsync(h.AccountId, id, input);
        Assert.IsTrue(upd.Success, string.Join(";", upd.Errors));

        var again = (await Svc(new AgentDockDbContext(AccountHarness.Options())).GetAsync(h.AccountId, id))!;
        Assert.AreEqual("A2", again.Name);
        Assert.AreEqual("Disabled", again.Status);
        CollectionAssert.AreEquivalent(new[] { "https://b.example.com", "http://localhost:3000" }, again.Origins.Select(o => o.Origin).ToArray());
        Assert.AreEqual("Hello", AppearanceSettings.FromJson(again.AppearanceJson).Title);
    }

    [TestMethod]
    public async Task InvalidInput_IsRejectedAndNothingSaved()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var r = await Svc(h.Db).CreateAsync(h.AccountId, Input("", "https://*.x.com"));
        Assert.IsFalse(r.Success);
        Assert.AreEqual(0, (await Svc(h.Db).ListAsync(h.AccountId)).Count);
    }

    [TestMethod]
    public async Task OtherAccount_CannotSeeOrChangeAgents()
    {
        TestDb.RequireSql();
        await using var a = await AccountHarness.StartAsync();
        await using var b = await AccountHarness.StartAsync();
        var id = (await Svc(a.Db).CreateAsync(a.AccountId, Input("Secret agent"))).Agent!.Id;
        var svcB = Svc(b.Db);

        Assert.IsNull(await svcB.GetAsync(b.AccountId, id));
        Assert.AreEqual(0, (await svcB.ListAsync(b.AccountId)).Count);
        Assert.IsFalse((await svcB.UpdateAsync(b.AccountId, id, Input("hijack"))).Success);
        Assert.IsNull(await svcB.DuplicateAsync(b.AccountId, id));
        Assert.IsFalse(await svcB.SetEnabledAsync(b.AccountId, id, false));
        Assert.IsFalse(await svcB.DeleteAsync(b.AccountId, id));
        Assert.AreEqual("Secret agent", (await Svc(a.Db).GetAsync(a.AccountId, id))!.Name);
    }

    [TestMethod]
    public async Task Duplicate_CopiesConfigAndOrigins_ButIsDisabledWithNewId()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var src = (await svc.CreateAsync(h.AccountId, Input("Original", "https://a.example.com"))).Agent!;

        var copy = (await svc.DuplicateAsync(h.AccountId, src.Id))!;
        Assert.AreNotEqual(src.Id, copy.Id);
        Assert.AreEqual("Copy of Original", copy.Name);
        Assert.AreEqual("Disabled", copy.Status);
        Assert.AreEqual("Be helpful.", copy.SystemPrompt);
        Assert.AreEqual("haiku", copy.DefaultModelFamily);
        var reloaded = (await Svc(new AgentDockDbContext(AccountHarness.Options())).GetAsync(h.AccountId, copy.Id))!;
        CollectionAssert.AreEqual(new[] { "https://a.example.com" }, reloaded.Origins.Select(o => o.Origin).ToArray());
        Assert.AreEqual("Enabled", (await svc.GetAsync(h.AccountId, src.Id))!.Status, "source untouched");
    }

    [TestMethod]
    public async Task Delete_IsSoft_AndHidesFromEverything()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var id = (await svc.CreateAsync(h.AccountId, Input())).Agent!.Id;
        Assert.IsTrue(await svc.DeleteAsync(h.AccountId, id));
        Assert.IsNull(await svc.GetAsync(h.AccountId, id));
        Assert.AreEqual(0, (await svc.ListAsync(h.AccountId)).Count);
        Assert.IsNull(await svc.DuplicateAsync(h.AccountId, id));
        Assert.IsFalse(await svc.DeleteAsync(h.AccountId, id));
        var row = await new AgentDockDbContext(AccountHarness.Options()).Agents.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.IsNotNull(row.DeletedUtc);
        Assert.AreEqual("Disabled", row.Status);
    }

    [TestMethod]
    public async Task ConcurrentEdit_IsDetected()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var id = (await Svc(h.Db).CreateAsync(h.AccountId, Input("v1"))).Agent!.Id;

        await using var db1 = new AgentDockDbContext(AccountHarness.Options());
        await using var db2 = new AgentDockDbContext(AccountHarness.Options());
        var in1 = AgentService.ToInput((await Svc(db1).GetAsync(h.AccountId, id))!);
        var in2 = AgentService.ToInput((await Svc(db2).GetAsync(h.AccountId, id))!);
        in1.Name = "first"; in2.Name = "second";
        Assert.IsTrue((await Svc(db1).UpdateAsync(h.AccountId, id, in1)).Success);
        var r = await Svc(db2).UpdateAsync(h.AccountId, id, in2);
        Assert.IsFalse(r.Success);
        Assert.IsTrue(r.Conflict);
    }

    [TestMethod]
    public async Task Database_RejectsOriginForAgentInAnotherAccount()
    {
        TestDb.RequireSql();
        await using var a = await AccountHarness.StartAsync();
        await using var b = await AccountHarness.StartAsync();
        var id = (await Svc(a.Db).CreateAsync(a.AccountId, Input())).Agent!.Id;

        await using var conn = new SqlConnection(TestDb.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT dbo.AgentAllowedOrigin (Id, AccountId, AgentId, Origin) VALUES (NEWID(), @acct, @agent, 'https://evil.example.com')";
        cmd.Parameters.AddWithValue("@acct", b.AccountId);
        cmd.Parameters.AddWithValue("@agent", id);
        await Assert.ThrowsExactlyAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
    }

    [TestMethod]
    public async Task DeletingAccount_CascadesToAgentsAndOrigins()
    {
        TestDb.RequireSql();
        var h = await AccountHarness.StartAsync();
        var id = (await Svc(h.Db).CreateAsync(h.AccountId, Input("x", "https://a.example.com"))).Agent!.Id;
        await h.DisposeAsync();
        await using var db = new AgentDockDbContext(AccountHarness.Options());
        Assert.IsFalse(await db.Agents.AnyAsync(a => a.Id == id));
        Assert.IsFalse(await db.AgentAllowedOrigins.AnyAsync(o => o.AgentId == id));
    }

    [TestMethod]
    public async Task WebSearchSettings_AreValidatedNormalizedAndPersisted()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);

        var i = Input("S"); i.WebSearchEnabled = true; i.WebSearchDomainMode = "Allow"; i.WebSearchDomains = ["https://a.com"];
        Assert.IsFalse((await svc.CreateAsync(h.AccountId, i)).Success, "scheme rejected");
        i.WebSearchDomains = [];
        Assert.IsFalse((await svc.CreateAsync(h.AccountId, i)).Success, "allow mode needs a domain");
        i.WebSearchDomains = ["A.com"]; i.WebSearchMaxUses = 0;
        Assert.IsFalse((await svc.CreateAsync(h.AccountId, i)).Success, "max uses 1-20");
        i.WebSearchMaxUses = 4; i.WebSearchDomainMode = "Block";
        var ok = await svc.CreateAsync(h.AccountId, i);
        Assert.IsTrue(ok.Success, string.Join(";", ok.Errors));
        Assert.AreEqual("Block", ok.Agent!.WebSearchDomainMode);
        Assert.AreEqual("""["a.com"]""", ok.Agent.WebSearchDomainsJson);

        i.WebSearchDomainMode = "None";
        var upd = await svc.UpdateAsync(h.AccountId, ok.Agent.Id, i);
        Assert.IsTrue(upd.Success);
        Assert.IsNull((await svc.GetAsync(h.AccountId, ok.Agent.Id))!.WebSearchDomainsJson, "domains cleared when mode is None");
    }
}
