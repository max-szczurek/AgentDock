using AgentDock.Tests.Infrastructure;

namespace AgentDock.Tests.Integration;

[TestClass]
public class DevLoginTests
{
    private static readonly string Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    private static AppFactory Staging(string email, params (string, string)[] extra)
    {
        var s = new Dictionary<string, string?>
        {
            ["AGENTDOCK_DEV_LOGIN_SECRET"] = Secret,
            ["AGENTDOCK_DEV_LOGIN_EMAILS"] = email,
        };
        foreach (var (k, v) in extra) s[k] = v;
        return new AppFactory("Staging", s);
    }

    private static HttpRequestMessage Post(string email, string? headerSecret, string url = "/dev/login")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }),
        };
        if (headerSecret != null) req.Headers.Add("X-Dev-Login-Secret", headerSecret);
        return req;
    }

    [TestMethod]
    public async Task Health_IsAnonymousAndOk()
    {
        TestDb.RequireSql();
        await using var f = Staging("x@agentdock.test");
        var r = await f.NewClient().GetAsync("/health");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, r.StatusCode);
    }

    [TestMethod]
    public async Task Unauthenticated_RedirectsToLogin()
    {
        TestDb.RequireSql();
        await using var f = Staging("x@agentdock.test");
        var r = await f.NewClient().GetAsync("/");
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        StringAssert.Contains(r.Headers.Location!.ToString(), "/Account/Login");
    }

    [TestMethod]
    public async Task Production_DevLoginReturns404EvenWithSecret()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        await using var f = new AppFactory("Production", new() { ["AGENTDOCK_DEV_LOGIN_SECRET"] = Secret, ["AGENTDOCK_DEV_LOGIN_EMAILS"] = email });
        var c = f.NewClient();
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/dev/login")).StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await c.SendAsync(Post(email, Secret))).StatusCode);
        Assert.AreEqual(0, await TestDb.CountUsersAsync(email));
    }

    [TestMethod]
    public async Task WithoutSecret_DevLoginReturns404()
    {
        TestDb.RequireSql();
        await using var f = new AppFactory("Staging", new() { ["AGENTDOCK_DEV_LOGIN_SECRET"] = "", ["AGENTDOCK_DEV_LOGIN_EMAILS"] = "" });
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await f.NewClient().GetAsync("/dev/login")).StatusCode);
    }

    [TestMethod]
    public async Task KillSwitch_Returns404()
    {
        TestDb.RequireSql();
        await using var f = Staging("x@agentdock.test", ("AgentDock:DevLogin:Disabled", "true"));
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, (await f.NewClient().GetAsync("/dev/login")).StatusCode);
    }

    [TestMethod]
    public async Task CorrectSecret_SignsInProvisionsAccountAndShowsBanner()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        try
        {
            await using var f = Staging(email);
            var c = f.NewClient();
            var form = await (await c.GetAsync("/dev/login")).Content.ReadAsStringAsync();
            StringAssert.Contains(form, email);
            StringAssert.Contains(form, "DEV LOGIN ACTIVE");

            var r = await c.SendAsync(Post(email, Secret));
            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, r.StatusCode);

            var home = await c.GetAsync("/");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, home.StatusCode);
            var html = await home.Content.ReadAsStringAsync();
            StringAssert.Contains(html, email);
            StringAssert.Contains(html, "DEV LOGIN ACTIVE");
            Assert.AreEqual(1, await TestDb.CountAccountsAsync(email));

            await c.SendAsync(Post(email, Secret));
            Assert.AreEqual(1, await TestDb.CountAccountsAsync(email), "sign-in is idempotent");
        }
        finally { await TestDb.DeleteUserAsync(email); }
    }

    [TestMethod]
    public async Task WrongSecret_OrUnlistedEmail_IsRefusedAndCreatesNothing()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        var other = TestDb.NewTestEmail();
        await using var f = Staging(email);
        var c = f.NewClient();
        Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, (await c.SendAsync(Post(email, "wrong"))).StatusCode);
        Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, (await c.SendAsync(Post(other, Secret))).StatusCode);
        Assert.AreEqual(0, await TestDb.CountUsersAsync(email));
        Assert.AreEqual(0, await TestDb.CountUsersAsync(other));
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
    }

    [TestMethod]
    public async Task SecretInQueryString_IsNotAccepted()
    {
        TestDb.RequireSql();
        var email = TestDb.NewTestEmail();
        await using var f = Staging(email);
        var c = f.NewClient();
        var r = await c.SendAsync(Post(email, null, $"/dev/login?secret={Uri.EscapeDataString(Secret)}"));
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        Assert.AreEqual(0, await TestDb.CountUsersAsync(email));
    }

    [TestMethod]
    public async Task RepeatedFailures_AreRateLimited()
    {
        TestDb.RequireSql();
        await using var f = Staging(TestDb.NewTestEmail());
        var c = f.NewClient();
        var last = System.Net.HttpStatusCode.OK;
        for (var i = 0; i < 12; i++) last = (await c.SendAsync(Post("nobody@agentdock.test", "wrong"))).StatusCode;
        Assert.AreEqual(System.Net.HttpStatusCode.TooManyRequests, last);
    }
}
