using System.Net;
using AgentDock.Core.Outbound;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentDock.Tests.Integration;

[TestClass]
public class OutboundClientTests
{
    private sealed class MapResolver(Dictionary<string, IPAddress[]> map) : IDnsResolver
    {
        public int Lookups;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref Lookups);
            return map.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001);
        }
    }

    /// <summary>The default classifier, except loopback is allowed so tests can reach an in-process server.</summary>
    private sealed class LoopbackAllowedPolicy : IIpPolicy
    {
        public bool IsBlocked(IPAddress a) => !IPAddress.IsLoopback(a) && IpClassifier.IsBlocked(a);
    }

    private sealed class CaptureLogger : ILoggerProvider, ILogger<OutboundClient>
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
        public void Dispose() { }
    }

    private sealed class Env : IAsyncDisposable
    {
        public WebApplication Server = null!;
        public int Port;
        public CaptureLogger Log = new();
        public MapResolver Resolver = null!;
        public IOutboundClient Client = null!;
        public Dictionary<string, string?> LastHeaders = new();
        public List<string> Hits = [];

        public static async Task<Env> StartAsync(bool loopbackAllowed = true, Dictionary<string, IPAddress[]>? extraDns = null)
        {
            var e = new Env();
            var b = WebApplication.CreateBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            e.Server = b.Build();
            var s = e.Server;
            s.MapGet("/ok", (HttpContext c) => { e.Hits.Add(c.Request.Host.Host + c.Request.Path); Capture(e, c); return "hello"; });
            s.MapGet("/secret/path", (HttpContext c) => { Capture(e, c); return "ok"; });
            s.MapGet("/redirect-private", () => Results.Redirect($"http://169.254.169.254:{e.Port}/latest/meta-data"));
            s.MapGet("/redirect-internal-host", () => Results.Redirect($"http://internal.test:{e.Port}/ok"));
            s.MapGet("/redirect-same", () => Results.Redirect("/ok"));
            s.MapGet("/redirect-cross", () => Results.Redirect($"http://other.test:{e.Port}/ok"));
            s.MapGet("/loop", () => Results.Redirect("/loop"));
            s.MapGet("/big", () => new string('x', 5000));
            s.MapGet("/slow", async (HttpContext c) => { await Task.Delay(5000, c.RequestAborted); return "late"; });
            s.MapPost("/post-307", () => Results.Redirect($"http://other.test:{e.Port}/ok", false, true));
            await s.StartAsync();
            e.Port = new Uri(s.Urls.First()).Port;

            var dns = new Dictionary<string, IPAddress[]>
            {
                ["ok.test"] = [IPAddress.Loopback], ["other.test"] = [IPAddress.Loopback],
                ["internal.test"] = [IPAddress.Parse("10.0.0.5")],
                ["evil.test"] = [IPAddress.Loopback], ["meta.test"] = [IPAddress.Parse("169.254.169.254")],
                ["mixed.test"] = [IPAddress.Parse("93.184.216.34"), IPAddress.Loopback],
                ["v6.test"] = [IPAddress.Parse("::ffff:127.0.0.1")], ["wire.test"] = [IPAddress.Parse("168.63.129.16")],
            };
            foreach (var kv in extraDns ?? []) dns[kv.Key] = kv.Value;
            e.Resolver = new MapResolver(dns);
            var options = new OutboundOptions { IsDevelopment = true, AllowHttpForDevelopment = true, AllowedPorts = [e.Port], DefaultTimeout = TimeSpan.FromSeconds(10) };
            IIpPolicy policy = loopbackAllowed ? new LoopbackAllowedPolicy() : new DefaultIpPolicy();
            e.Client = new OutboundHttpClientFactory(options, e.Resolver, policy, e.Log).Create();
            return e;
        }

        private static void Capture(Env e, HttpContext c) =>
            e.LastHeaders = c.Request.Headers.ToDictionary(h => h.Key, h => (string?)h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        public Uri Url(string host, string path) => new($"http://{host}:{Port}{path}");
        public async ValueTask DisposeAsync() => await Server.DisposeAsync();
    }

    private static async Task<OutboundException> FailsAsync(Task t)
    {
        try { await t; }
        catch (OutboundException e) { return e; }
        Assert.Fail("expected OutboundException");
        return null!;
    }

    private static Task<OutboundResponse> Get(Env e, string host, string path, OutboundCallOptions? o = null, OutboundRequest? template = null) =>
        e.Client.SendAsync(new OutboundRequest { Url = e.Url(host, path), Credential = template?.Credential, CredentialBaseUrl = template?.CredentialBaseUrl }, o);

    [TestMethod]
    public async Task V11_AllowedHost_Works()
    {
        await using var e = await Env.StartAsync();
        var r = await Get(e, "ok.test", "/ok");
        Assert.AreEqual(200, r.StatusCode);
        Assert.AreEqual("hello", System.Text.Encoding.UTF8.GetString(r.Body));
    }

    [TestMethod]
    [DataRow("evil.test")]
    [DataRow("meta.test")]
    [DataRow("internal.test")]
    [DataRow("mixed.test")]
    [DataRow("v6.test")]
    [DataRow("wire.test")]
    public async Task V11_DnsThatResolvesToBlockedAddresses_IsRefused_WithDefaultPolicy(string host)
    {
        await using var e = await Env.StartAsync(loopbackAllowed: false);
        var err = await FailsAsync(Get(e, host, "/ok"));
        Assert.AreEqual(OutboundErrors.BlockedAddress, err.Code);
        Assert.AreEqual(0, e.Hits.Count, "the server was never contacted");
    }

    [TestMethod]
    public async Task V11_MixedAnswer_IsRefusedEvenWhenOneAddressIsPublic()
    {
        await using var e = await Env.StartAsync(loopbackAllowed: false);
        Assert.AreEqual(OutboundErrors.BlockedAddress, (await FailsAsync(Get(e, "mixed.test", "/ok"))).Code);
    }

    [TestMethod]
    public async Task V11_DnsIsResolvedOnce_PerConnection_NoRebinding()
    {
        await using var e = await Env.StartAsync();
        await Get(e, "ok.test", "/ok");
        Assert.AreEqual(1, e.Resolver.Lookups);
    }

    [TestMethod]
    public async Task V11_UnresolvableHost_IsDnsFailed()
    {
        await using var e = await Env.StartAsync();
        Assert.AreEqual(OutboundErrors.DnsFailed, (await FailsAsync(Get(e, "nope.test", "/ok"))).Code);
    }

    [TestMethod]
    public async Task V11_LiteralIpsAndInternalNames_AreRefusedBeforeConnecting()
    {
        await using var e = await Env.StartAsync(loopbackAllowed: false);
        foreach (var url in new[] { "http://127.0.0.1:{0}/ok", "http://169.254.169.254:{0}/", "http://[::1]:{0}/ok", "http://[::ffff:127.0.0.1]:{0}/ok", "http://metadata.google.internal:{0}/" })
            Assert.AreEqual(OutboundErrors.BlockedHost, (await FailsAsync(e.Client.SendAsync(new OutboundRequest { Url = new Uri(string.Format(url, e.Port)) }))).Code, url);
        Assert.AreEqual(0, e.Resolver.Lookups);
    }

    [TestMethod]
    public async Task V11_Production_RejectsHttpAndOtherPorts()
    {
        var factory = new OutboundHttpClientFactory(new OutboundOptions(), new MapResolver(new()), new DefaultIpPolicy(), new CaptureLogger());
        var c = factory.Create();
        Assert.AreEqual(OutboundErrors.BlockedScheme, (await FailsAsync(c.SendAsync(new OutboundRequest { Url = new Uri("http://api.example.com/x") }))).Code);
        Assert.AreEqual(OutboundErrors.BlockedPort, (await FailsAsync(c.SendAsync(new OutboundRequest { Url = new Uri("https://api.example.com:8443/x") }))).Code);
    }

    [TestMethod]
    public async Task V11_RedirectToPrivateTargets_IsRefused()
    {
        await using var e = await Env.StartAsync(loopbackAllowed: true);
        Assert.AreEqual(OutboundErrors.BlockedHost, (await FailsAsync(Get(e, "ok.test", "/redirect-private"))).Code, "literal IMDS ip");
        Assert.AreEqual(OutboundErrors.BlockedAddress, (await FailsAsync(Get(e, "ok.test", "/redirect-internal-host"))).Code, "name resolving to 10.x");
    }

    [TestMethod]
    public async Task Redirects_AreFollowedAtMostThree_AndMcpStyleCallsRefuseThem()
    {
        await using var e = await Env.StartAsync();
        var r = await Get(e, "ok.test", "/redirect-same");
        Assert.AreEqual(200, r.StatusCode);
        Assert.AreEqual(1, r.Redirects);
        Assert.AreEqual(OutboundErrors.TooManyRedirects, (await FailsAsync(Get(e, "ok.test", "/loop"))).Code);
        Assert.AreEqual(OutboundErrors.RedirectNotAllowed, (await FailsAsync(Get(e, "ok.test", "/redirect-same", new OutboundCallOptions { MaxRedirects = 0 }))).Code);
    }

    [TestMethod]
    public async Task V11_Credential_IsSentOnlyToTheBaseOrigin_AndDroppedOnCrossOriginRedirect()
    {
        await using var e = await Env.StartAsync();
        var template = new OutboundRequest
        {
            Url = e.Url("ok.test", "/ok"), Credential = new OutboundCredential("Authorization", "Bearer SECRET-TOKEN"),
            CredentialBaseUrl = e.Url("ok.test", "/"),
        };

        await Get(e, "ok.test", "/ok", null, template);
        Assert.AreEqual("Bearer SECRET-TOKEN", e.LastHeaders["Authorization"], "same origin gets the credential");

        await Get(e, "ok.test", "/redirect-same", null, template);
        Assert.AreEqual("Bearer SECRET-TOKEN", e.LastHeaders["Authorization"], "same-origin redirect keeps it");

        e.LastHeaders = new();
        var r = await Get(e, "ok.test", "/redirect-cross", null, template);
        Assert.AreEqual("other.test", new Uri(r.FinalUrl.ToString()).Host);
        Assert.IsFalse(e.LastHeaders.ContainsKey("Authorization"), "cross-origin hop must not carry the credential");

        e.LastHeaders = new();
        await Get(e, "other.test", "/ok", null, template);
        Assert.IsTrue(e.LastHeaders.Count > 0 && !e.LastHeaders.ContainsKey("Authorization"), "a different host is not the base origin");
    }

    [TestMethod]
    public async Task PostBody_IsNotForwardedAcrossOrigins_On307()
    {
        await using var e = await Env.StartAsync();
        var err = await FailsAsync(e.Client.SendAsync(new OutboundRequest { Method = HttpMethod.Post, Url = e.Url("ok.test", "/post-307"), Body = [1, 2, 3] }));
        Assert.AreEqual(OutboundErrors.RedirectNotAllowed, err.Code);
    }

    [TestMethod]
    public async Task ByteCap_AndTimeout_AreEnforced()
    {
        await using var e = await Env.StartAsync();
        Assert.AreEqual(OutboundErrors.ResponseTooLarge, (await FailsAsync(Get(e, "ok.test", "/big", new OutboundCallOptions { MaxBytes = 1000 }))).Code);
        Assert.AreEqual(5000, (await Get(e, "ok.test", "/big", new OutboundCallOptions { MaxBytes = 5000 })).Body.Length);
        Assert.AreEqual(OutboundErrors.Timeout, (await FailsAsync(Get(e, "ok.test", "/slow", new OutboundCallOptions { Timeout = TimeSpan.FromMilliseconds(300) }))).Code);
    }

    [TestMethod]
    public async Task ForbiddenHeaders_AreRejected_AndNoCookiesOrProxy()
    {
        await using var e = await Env.StartAsync();
        var err = await FailsAsync(e.Client.SendAsync(new OutboundRequest { Url = e.Url("ok.test", "/ok"), Headers = new Dictionary<string, string> { ["Host"] = "evil.example" } }));
        Assert.AreEqual(OutboundErrors.InvalidRequest, err.Code);
        var handler = SafeOutboundHandler.Create(new OutboundOptions(), new MapResolver(new()), new DefaultIpPolicy());
        Assert.IsFalse(handler.UseCookies);
        Assert.IsFalse(handler.UseProxy);
        Assert.IsFalse(handler.AllowAutoRedirect);
    }

    [TestMethod]
    public async Task Logs_ContainOnlyMethodOriginStatusAndDuration()
    {
        await using var e = await Env.StartAsync();
        var req = new OutboundRequest
        {
            Url = new Uri($"http://ok.test:{e.Port}/secret/path?token=QUERY-SECRET&x=1"),
            Credential = new OutboundCredential("Authorization", "Bearer HEADER-SECRET"), CredentialBaseUrl = e.Url("ok.test", "/"),
        };
        await e.Client.SendAsync(req);
        await FailsAsync(e.Client.SendAsync(new OutboundRequest { Url = new Uri($"http://evil.example.com:{e.Port}/private/thing?k=v") }));
        var all = string.Join("\n", e.Log.Lines);
        StringAssert.Contains(all, $"Outbound GET http://ok.test:{e.Port} -> 200");
        foreach (var leak in new[] { "secret/path", "token", "QUERY-SECRET", "HEADER-SECRET", "private/thing", "k=v" })
            Assert.IsFalse(all.Contains(leak), $"log leaked '{leak}': {all}");
    }
}
