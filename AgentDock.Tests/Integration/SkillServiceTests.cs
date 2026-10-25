using System.Text;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Tests.Integration;

[TestClass]
public class SkillServiceTests
{
    private static SkillService Svc(AgentDockDbContext db) => new(db, TimeProvider.System);
    private static AgentService Agents(AgentDockDbContext db) => new(db, TimeProvider.System);
    private static SkillUpload Up(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static async Task<Agent> NewAgentAsync(AccountHarness h, string name = "A", string prompt = "p")
    {
        var r = await Agents(h.Db).CreateAsync(h.AccountId, new AgentInput { Name = name, SystemPrompt = prompt, DefaultModelFamily = ModelFamily.Haiku });
        Assert.IsTrue(r.Success, string.Join(";", r.Errors));
        return r.Agent!;
    }

    [TestMethod]
    public async Task Upload_AcceptsMarkdown_RejectsBadFiles_AndReplacesBySameName()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var results = await svc.UploadAsync(h.AccountId,
        [
            Up("one.md", "---\nname: One\ndescription: first\n---\nHello"), Up("two.md", "Two"),
            Up("bad.txt", "x"), new("nul.md", Encoding.UTF8.GetBytes("a\0b")), new("utf.md", [0xC3, 0x28]),
            new("big.md", new byte[SkillRules.MaxFileBytes + 1]),
        ]);
        CollectionAssert.AreEqual(new[] { true, true, false, false, false, false }, results.Select(r => r.Success).ToArray());
        var list = await svc.ListAsync(h.AccountId);
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual("One", list.Single(s => s.FileName == "one.md").DisplayName);
        Assert.AreEqual("first", list.Single(s => s.FileName == "one.md").Description);

        var again = await svc.UploadAsync(h.AccountId, [Up("one.md", "Changed")]);
        Assert.IsTrue(again[0].Success);
        var one = (await svc.GetAsync(h.AccountId, again[0].Id!.Value))!;
        Assert.AreEqual(2, one.Version);
        Assert.AreEqual("Changed", one.Content);
        Assert.AreEqual(2, (await svc.ListAsync(h.AccountId)).Count);
    }

    [TestMethod]
    public async Task Assignment_RespectsOrder_AndEnforcesBudgets()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var agent = await NewAgentAsync(h);
        var ids = (await svc.UploadAsync(h.AccountId, [Up("a.md", "A"), Up("b.md", "B"), Up("c.md", "C")])).Select(r => r.Id!.Value).ToList();

        Assert.AreEqual(0, (await svc.SetForAgentAsync(h.AccountId, agent.Id, [ids[2], ids[0], ids[1]])).Count);
        CollectionAssert.AreEqual(new[] { "c.md", "a.md", "b.md" }, (await svc.ForAgentAsync(h.AccountId, agent.Id)).Select(s => s.FileName).ToArray());
        await svc.SetForAgentAsync(h.AccountId, agent.Id, [ids[1], ids[2]]);
        CollectionAssert.AreEqual(new[] { "b.md", "c.md" }, (await svc.ForAgentAsync(h.AccountId, agent.Id)).Select(s => s.FileName).ToArray());
        Assert.AreEqual(1, (await svc.ListAsync(h.AccountId)).Single(s => s.FileName == "b.md").UsedByAgents);

        var many = (await svc.UploadAsync(h.AccountId, Enumerable.Range(0, 21).Select(i => Up($"m{i}.md", "m")).ToList())).Select(r => r.Id!.Value).ToList();
        Assert.AreEqual(1, (await svc.SetForAgentAsync(h.AccountId, agent.Id, many)).Count, "more than 20 skills");

        var big = new string('x', 60_000);
        var bigs = (await svc.UploadAsync(h.AccountId, [Up("big1.md", big), Up("big2.md", big), Up("big3.md", big)])).Select(r => r.Id!.Value).ToList();
        Assert.AreEqual(0, (await svc.SetForAgentAsync(h.AccountId, agent.Id, bigs.Take(2).ToList())).Count);
        StringAssert.Contains((await svc.SetForAgentAsync(h.AccountId, agent.Id, bigs)).Single(), "limit");

        var other = (await svc.UploadAsync(h.AccountId, [Up("edit.md", new string('y', 50_000))])).Single().Id!.Value;
        await svc.SetForAgentAsync(h.AccountId, agent.Id, [bigs[0], other]);
        var grow = await svc.UpdateContentAsync(h.AccountId, other, new string('y', 64_000));
        Assert.IsFalse(grow.Success, "edit that busts an agent's budget is refused");
    }

    [TestMethod]
    public async Task Delete_IsBlockedWhileAssigned_UnlessRemoveFromAll()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var agent = await NewAgentAsync(h, "Uses skill");
        var id = (await svc.UploadAsync(h.AccountId, [Up("x.md", "X")])).Single().Id!.Value;
        await svc.SetForAgentAsync(h.AccountId, agent.Id, [id]);

        var blocked = await svc.DeleteAsync(h.AccountId, id, false);
        Assert.IsFalse(blocked.Deleted);
        Assert.AreEqual("Uses skill", blocked.BlockedBy.Single().AgentName);
        Assert.IsNotNull(await svc.GetAsync(h.AccountId, id));

        var done = await svc.DeleteAsync(h.AccountId, id, true);
        Assert.IsTrue(done.Deleted);
        Assert.IsNull(await svc.GetAsync(h.AccountId, id));
        Assert.AreEqual(0, (await svc.ForAgentAsync(h.AccountId, agent.Id)).Count);
        Assert.IsTrue((await svc.UploadAsync(h.AccountId, [Up("x.md", "again")])).Single().Success, "file name is reusable after soft delete");
    }

    [TestMethod]
    public async Task Duplicate_CopiesSkillLinks_InOrder()
    {
        TestDb.RequireSql();
        await using var h = await AccountHarness.StartAsync();
        var svc = Svc(h.Db);
        var agent = await NewAgentAsync(h);
        var ids = (await svc.UploadAsync(h.AccountId, [Up("a.md", "A"), Up("b.md", "B")])).Select(r => r.Id!.Value).ToList();
        await svc.SetForAgentAsync(h.AccountId, agent.Id, [ids[1], ids[0]]);
        var copy = (await Agents(h.Db).DuplicateAsync(h.AccountId, agent.Id))!;
        CollectionAssert.AreEqual(new[] { "b.md", "a.md" }, (await svc.ForAgentAsync(h.AccountId, copy.Id)).Select(s => s.FileName).ToArray());
        Assert.AreEqual(2, (await svc.ListAsync(h.AccountId)).First().UsedByAgents);
    }

    [TestMethod]
    public async Task OtherAccount_CannotSeeOrUseSkills_AndDbRejectsCrossAccountLinks()
    {
        TestDb.RequireSql();
        await using var a = await AccountHarness.StartAsync();
        await using var b = await AccountHarness.StartAsync();
        var skillA = (await Svc(a.Db).UploadAsync(a.AccountId, [Up("secret.md", "S")])).Single().Id!.Value;
        var agentB = await NewAgentAsync(b);

        Assert.IsNull(await Svc(b.Db).GetAsync(b.AccountId, skillA));
        Assert.AreEqual(0, (await Svc(b.Db).ListAsync(b.AccountId)).Count);
        Assert.IsFalse((await Svc(b.Db).UpdateContentAsync(b.AccountId, skillA, "hijack")).Success);
        Assert.IsFalse((await Svc(b.Db).DeleteAsync(b.AccountId, skillA, true)).Found);
        Assert.AreEqual(1, (await Svc(b.Db).SetForAgentAsync(b.AccountId, agentB.Id, [skillA])).Count);

        b.Db.AgentSkills.Add(new AgentSkill { AccountId = b.AccountId, AgentId = agentB.Id, SkillFileId = skillA, SortOrder = 0 });
        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => b.Db.SaveChangesAsync());
    }
}
