using System.Net;
using AgentDock.Core.Outbound;

namespace AgentDock.Tests.Unit;

[TestClass]
public class OutboundUnitTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("127.255.255.254")]
    [DataRow("169.254.169.254")]
    [DataRow("168.63.129.16")]
    [DataRow("10.1.2.3")]
    [DataRow("100.64.0.1")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.1.1")]
    [DataRow("192.0.0.8")]
    [DataRow("198.19.0.1")]
    [DataRow("0.0.0.0")]
    [DataRow("224.0.0.1")]
    [DataRow("240.0.0.1")]
    [DataRow("255.255.255.255")]
    [DataRow("::")]
    [DataRow("::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456::1")]
    [DataRow("fe80::1")]
    [DataRow("ff02::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("::ffff:169.254.169.254")]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("::127.0.0.1")]
    [DataRow("64:ff9b::7f00:1")]
    [DataRow("64:ff9b::a9fe:a9fe")]
    [DataRow("2002:7f00:1::1")]
    [DataRow("2002:0a00:0001::")]
    [DataRow("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void Blocks(string ip) => Assert.IsTrue(IpClassifier.IsBlocked(IPAddress.Parse(ip)), ip);

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("93.184.216.34")]
    [DataRow("172.15.255.255")]
    [DataRow("172.32.0.1")]
    [DataRow("100.63.255.255")]
    [DataRow("168.63.129.17")]
    [DataRow("2606:4700:4700::1111")]
    [DataRow("::ffff:8.8.8.8")]
    [DataRow("64:ff9b::808:808")]
    public void Allows(string ip) => Assert.IsFalse(IpClassifier.IsBlocked(IPAddress.Parse(ip)), ip);

    private static readonly OutboundOptions Prod = new();

    [TestMethod]
    [DataRow("https://api.example.com/x", null)]
    [DataRow("http://api.example.com/x", OutboundErrors.BlockedScheme)]
    [DataRow("ftp://api.example.com/x", OutboundErrors.BlockedScheme)]
    [DataRow("https://api.example.com:8443/x", OutboundErrors.BlockedPort)]
    [DataRow("https://127.0.0.1/x", OutboundErrors.BlockedHost)]
    [DataRow("https://169.254.169.254/latest", OutboundErrors.BlockedHost)]
    [DataRow("https://168.63.129.16/", OutboundErrors.BlockedHost)]
    [DataRow("https://[::1]/x", OutboundErrors.BlockedHost)]
    [DataRow("https://[::ffff:127.0.0.1]/x", OutboundErrors.BlockedHost)]
    [DataRow("https://2130706433/x", OutboundErrors.BlockedHost)]
    [DataRow("https://0x7f.1/x", OutboundErrors.BlockedHost)]
    [DataRow("https://localhost/x", OutboundErrors.BlockedHost)]
    [DataRow("https://metadata.google.internal/x", OutboundErrors.BlockedHost)]
    [DataRow("https://printer.local/x", OutboundErrors.BlockedHost)]
    [DataRow("https://app.localhost/x", OutboundErrors.BlockedHost)]
    [DataRow("https://intranet/x", OutboundErrors.BlockedHost)]
    [DataRow("https://api.example.com./x", null)]
    [DataRow("https://user:pw@api.example.com/x", OutboundErrors.InvalidRequest)]
    public void UrlValidation(string url, string? expectedCode)
    {
        var err = OutboundUrlValidator.Validate(new Uri(url), Prod);
        Assert.AreEqual(expectedCode, err?.Code, url);
    }

    [TestMethod]
    public void SelfHost_PortsAndDevHttp_AreConfigurable()
    {
        var self = new OutboundOptions { SelfHosts = ["agentdock.azurewebsites.net"] };
        Assert.AreEqual(OutboundErrors.BlockedHost, OutboundUrlValidator.Validate(new Uri("https://AgentDock.azurewebsites.net/x"), self)?.Code);
        Assert.IsNull(OutboundUrlValidator.Validate(new Uri("https://api.example.com:8443/x"), new OutboundOptions { AllowedPorts = [443, 8443] }));

        var devOnly = new OutboundOptions { AllowHttpForDevelopment = true, IsDevelopment = false };
        Assert.AreEqual(OutboundErrors.BlockedScheme, OutboundUrlValidator.Validate(new Uri("http://api.example.com/x"), devOnly)?.Code, "flag alone is ignored outside Development");
        var dev = new OutboundOptions { AllowHttpForDevelopment = true, IsDevelopment = true, AllowedPorts = [80] };
        Assert.IsNull(OutboundUrlValidator.Validate(new Uri("http://api.example.com/x"), dev));
    }

    [TestMethod]
    public void SameOrigin_ComparesSchemeHostAndPort()
    {
        Assert.IsTrue(OutboundUrlValidator.SameOrigin(new Uri("https://a.com/x"), new Uri("https://A.com:443/y")));
        Assert.IsFalse(OutboundUrlValidator.SameOrigin(new Uri("https://a.com/"), new Uri("https://b.com/")));
        Assert.IsFalse(OutboundUrlValidator.SameOrigin(new Uri("https://a.com/"), new Uri("https://a.com:8443/")));
        Assert.IsFalse(OutboundUrlValidator.SameOrigin(new Uri("https://a.com/"), new Uri("http://a.com/")));
    }
}
