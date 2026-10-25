using AgentDock.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AgentDock.Tests.Unit;

[TestClass]
public class AccessGateTests
{
    private sealed class Env(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
    }

    private static AccessGate Gate(string env, params (string, string?)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Item1, s => s.Item2)).Build(), new Env(env));

    private static readonly string Secret = new('s', 40);

    [TestMethod]
    public void Allowlist_IsCaseInsensitiveAndTrimmed()
    {
        var g = Gate(Environments.Production, ("AGENTDOCK_ALLOWED_EMAILS", " Max@Example.com , b@x.com "));
        Assert.IsTrue(g.IsEmailAllowed("max@example.com"));
        Assert.IsTrue(g.IsEmailAllowed(" B@X.COM "));
        Assert.IsFalse(g.IsEmailAllowed("c@x.com"));
        Assert.IsFalse(g.IsEmailAllowed(null));
    }

    [TestMethod]
    public void EmptyAllowlist_FailsClosedOutsideDevelopment()
    {
        Assert.IsFalse(Gate(Environments.Production).IsEmailAllowed("a@b.com"));
        Assert.IsFalse(Gate(Environments.Staging, ("AGENTDOCK_ALLOWED_EMAILS", "")).IsEmailAllowed("a@b.com"));
        Assert.IsTrue(Gate(Environments.Development).IsEmailAllowed("a@b.com"));
    }

    [TestMethod]
    [DataRow("Production", true, false)]
    [DataRow("Staging", true, true)]
    [DataRow("Development", true, true)]
    [DataRow("Staging", false, false)]
    public void DevLogin_EnabledMatrix(string env, bool hasSecret, bool expected)
    {
        var g = Gate(env, ("AGENTDOCK_DEV_LOGIN_SECRET", hasSecret ? Secret : null));
        Assert.AreEqual(expected, g.DevLoginEnabled);
    }

    [TestMethod]
    public void DevLogin_RequiresLongSecretAndHonoursKillSwitch()
    {
        Assert.IsFalse(Gate("Staging", ("AGENTDOCK_DEV_LOGIN_SECRET", "short")).DevLoginEnabled);
        Assert.IsFalse(Gate("Staging", ("AGENTDOCK_DEV_LOGIN_SECRET", Secret), ("AgentDock:DevLogin:Disabled", "true")).DevLoginEnabled);
    }

    [TestMethod]
    public void SecretMatches_ComparesExactly()
    {
        var g = Gate("Staging", ("AGENTDOCK_DEV_LOGIN_SECRET", Secret));
        Assert.IsTrue(g.SecretMatches(Secret));
        Assert.IsFalse(g.SecretMatches(Secret + "x"));
        Assert.IsFalse(g.SecretMatches(null));
    }

    [TestMethod]
    public void AttemptLimiter_BlocksAfterMaxThenRecovers()
    {
        var time = new FakeTime();
        var l = new AttemptLimiter(time) { Max = 3, Window = TimeSpan.FromMinutes(15) };
        Assert.IsTrue(l.TryAcquire("ip") && l.TryAcquire("ip") && l.TryAcquire("ip"));
        Assert.IsFalse(l.TryAcquire("ip"));
        Assert.IsTrue(l.TryAcquire("other"));
        time.Now += TimeSpan.FromMinutes(16);
        Assert.IsTrue(l.TryAcquire("ip"));
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
