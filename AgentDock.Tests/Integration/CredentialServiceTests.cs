using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Security;
using AgentDock.Core.Services;
using AgentDock.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentDock.Tests.Integration;

[TestClass]
public class CredentialServiceTests
{
    private const string Key1 = "sk-ant-api03-FIRSTKEY-aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Key2 = "sk-ant-api03-SECONDKEY-bbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class Harness : IAsyncDisposable
    {
        public AgentDockDbContext Db = null!;
        public FakeModelsClient Models = new();
        public IKeyWrapper Wrapper = new LocalKeyWrapper();
        public Guid AccountId;
        public string Email = TestDb.NewTestEmail();
        public TestClock Clock = new();

        public IAnthropicCredentialService Service() => Create(Db, Wrapper);

        public IAnthropicCredentialService Create(AgentDockDbContext db, IKeyWrapper wrapper)
        {
            var secrets = new SecretStore(db, new EnvelopeSecretProtector(wrapper));
            return new AnthropicCredentialService(db, secrets, Models, new MemoryCache(new MemoryCacheOptions()), Clock,
                NullLogger<AnthropicCredentialService>.Instance);
        }

        public static async Task<Harness> StartAsync()
        {
            var h = new Harness();
            var opts = new DbContextOptionsBuilder<AgentDockDbContext>().UseSqlServer(TestDb.ConnectionString).Options;
            await new SchemaMigrator(TestDb.ConnectionString!, NullLogger<SchemaMigrator>.Instance).MigrateAsync();
            h.Db = new AgentDockDbContext(opts);
            var user = new Microsoft.AspNetCore.Identity.IdentityUser { UserName = h.Email, Email = h.Email, NormalizedUserName = h.Email.ToUpperInvariant(), NormalizedEmail = h.Email.ToUpperInvariant() };
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

    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
        public TimeSpan Offset;
    }

    private static Task<int> SnapshotCount(Harness h) =>
        (from snap in h.Db.ModelCatalogSnapshots
         join c in h.Db.ProviderCredentials on snap.ProviderCredentialId equals c.Id
         where c.AccountId == h.AccountId
         select snap).CountAsync();

    [TestMethod]
    public async Task Save_StoresCiphertextOnly_AndShowsHintAndCatalog()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var r = await h.Service().SaveKeyAsync(h.AccountId, Key1);
        Assert.IsTrue(r.Success);
        Assert.AreEqual("Valid", r.Credential!.Status);
        Assert.IsFalse(r.Credential.NeedsReentry);
        Assert.IsFalse(r.Credential.Hint!.Contains("FIRSTKEY"));

        var secret = await h.Db.Secrets.AsNoTracking().SingleAsync(s => s.AccountId == h.AccountId);
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(secret.Ciphertext).Contains("FIRSTKEY"));
        Assert.AreEqual(LocalKeyWrapper.Id, secret.KekId);

        var cat = await h.Service().GetCatalogAsync(h.AccountId, false);
        Assert.IsTrue(cat.Available);
        Assert.AreEqual("claude-opus-5-5", cat.Families[ModelFamily.Opus].ModelId);
        Assert.AreEqual(1, await SnapshotCount(h));
    }

    [TestMethod]
    public async Task InvalidKey_IsRejectedAndNothingStored()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        h.Models.Result = new(ModelsFetchStatus.Unauthorized, [], "http_401");
        var r = await h.Service().SaveKeyAsync(h.AccountId, Key1);
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.Error!, "Invalid");
        Assert.AreEqual(0, await h.Db.Secrets.CountAsync(s => s.AccountId == h.AccountId));
        Assert.IsNull(await h.Service().GetActiveAsync(h.AccountId));
    }

    [TestMethod]
    public async Task Replace_ShredsOldSecret_AndKeepsOneActive()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var svc = h.Service();
        await svc.SaveKeyAsync(h.AccountId, Key1);
        await svc.SaveKeyAsync(h.AccountId, Key2);

        var secrets = await h.Db.Secrets.AsNoTracking().Where(s => s.AccountId == h.AccountId).ToListAsync();
        Assert.AreEqual(2, secrets.Count);
        var shredded = secrets.Single(s => s.ShreddedUtc != null);
        Assert.AreEqual(0, shredded.Ciphertext.Length);
        Assert.AreEqual(1, await h.Db.ProviderCredentials.CountAsync(c => c.AccountId == h.AccountId && c.ReplacedUtc == null));
        Assert.AreEqual(Key2, await svc.RevealActiveKeyAsync(h.AccountId));
    }

    [TestMethod]
    public async Task Remove_ShredsAndClearsCatalog()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var svc = h.Service();
        await svc.SaveKeyAsync(h.AccountId, Key1);
        await svc.RemoveAsync(h.AccountId);
        Assert.IsNull(await svc.GetActiveAsync(h.AccountId));
        Assert.IsNull(await svc.RevealActiveKeyAsync(h.AccountId));
        Assert.IsTrue((await h.Db.Secrets.AsNoTracking().SingleAsync(s => s.AccountId == h.AccountId)).ShreddedUtc != null);
        Assert.AreEqual(0, await SnapshotCount(h));
        Assert.IsFalse((await svc.GetCatalogAsync(h.AccountId, true)).Available);
    }

    [TestMethod]
    public async Task UnavailableKek_ShowsNeedsReentry_WithoutThrowing()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        await h.Service().SaveKeyAsync(h.AccountId, Key1);
        var other = h.Create(h.Db, new LocalKeyWrapper());
        var view = await other.GetActiveAsync(h.AccountId);
        Assert.IsTrue(view!.NeedsReentry);
        var cat = await other.GetCatalogAsync(h.AccountId, true);
        Assert.IsFalse(cat.Available);
        StringAssert.Contains(cat.Error!, "re-entered");
    }

    [TestMethod]
    public async Task SecretMovedToAnotherAccount_CannotBeRevealed()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var r = await h.Service().SaveKeyAsync(h.AccountId, Key1);
        var secretId = (await h.Db.ProviderCredentials.SingleAsync(c => c.AccountId == h.AccountId)).SecretId;
        var store = new SecretStore(h.Db, new EnvelopeSecretProtector(h.Wrapper));
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => store.RevealForUseAsync(Guid.NewGuid(), secretId, SecretPurpose.AnthropicApiKey));
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => store.RevealForUseAsync(h.AccountId, secretId, SecretPurpose.ConnectionBearer));
    }

    [TestMethod]
    public async Task DiscoveryOutage_UsesSnapshot_ThenGivesUpAfterSevenDays()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var svc = h.Service();
        await svc.SaveKeyAsync(h.AccountId, Key1);
        h.Models.Result = new(ModelsFetchStatus.Unavailable, [], "http_503");

        var cat = await svc.GetCatalogAsync(h.AccountId, true);
        Assert.IsTrue(cat.Available);
        Assert.IsTrue(cat.FromSnapshot);
        Assert.AreEqual("claude-sonnet-5-5", cat.Families[ModelFamily.Sonnet].ModelId);

        h.Clock.Offset = TimeSpan.FromDays(8);
        Assert.IsFalse((await svc.GetCatalogAsync(h.AccountId, true)).Available);
    }

    [TestMethod]
    public async Task Discovery401_MarksCredentialInvalid()
    {
        TestDb.RequireSql();
        await using var h = await Harness.StartAsync();
        var svc = h.Service();
        await svc.SaveKeyAsync(h.AccountId, Key1);
        h.Models.Result = new(ModelsFetchStatus.Unauthorized, [], "http_401");
        Assert.IsFalse((await svc.GetCatalogAsync(h.AccountId, true)).Available);
        Assert.AreEqual("Invalid", (await svc.GetActiveAsync(h.AccountId))!.Status);
    }
}
