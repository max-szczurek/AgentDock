using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Runtime;

namespace AgentDock.Tests.Unit;

[TestClass]
public class WebSearchUnitTests
{
    private static ResolvedModel Model(bool search) => new("m", ModelFamily.Sonnet, "M", 4096, search, false, false, null);

    [TestMethod]
    public void Tool_OnlyBuilt_WhenEnabledAndModelSupportsIt()
    {
        Assert.IsNull(WebSearchTool.Build(new Agent { WebSearchEnabled = false }, Model(true)));
        Assert.IsNull(WebSearchTool.Build(new Agent { WebSearchEnabled = true }, Model(false)));
        var t = WebSearchTool.Build(new Agent { WebSearchEnabled = true, WebSearchMaxUses = 3 }, Model(true))!;
        Assert.AreEqual(WebSearchTool.ToolType, t["type"]!.GetValue<string>());
        Assert.AreEqual("web_search", t["name"]!.GetValue<string>());
        Assert.AreEqual(3, t["max_uses"]!.GetValue<int>());
        Assert.AreEqual("direct", t["allowed_callers"]![0]!.GetValue<string>());
        Assert.IsNull(t["allowed_domains"]);
        Assert.IsNull(t["blocked_domains"]);
    }

    [TestMethod]
    public void Tool_DomainModes_AreMutuallyExclusive_AndMaxUsesClamped()
    {
        var allow = WebSearchTool.Build(new Agent { WebSearchEnabled = true, WebSearchMaxUses = 99, WebSearchDomainMode = "Allow", WebSearchDomainsJson = """["a.com","b.org"]""" }, Model(true))!;
        Assert.AreEqual(20, allow["max_uses"]!.GetValue<int>());
        Assert.AreEqual(2, allow["allowed_domains"]!.AsArray().Count);
        Assert.IsNull(allow["blocked_domains"]);
        var block = WebSearchTool.Build(new Agent { WebSearchEnabled = true, WebSearchDomainMode = "Block", WebSearchDomainsJson = """["a.com"]""" }, Model(true))!;
        Assert.IsNull(block["allowed_domains"]);
        Assert.AreEqual("a.com", block["blocked_domains"]![0]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("Example.COM", true, "example.com")]
    [DataRow("sub.example.co.uk", true, "sub.example.co.uk")]
    [DataRow("https://example.com", false, "")]
    [DataRow("example.com/path", false, "")]
    [DataRow("*.example.com", false, "")]
    [DataRow("example.com:8080", false, "")]
    [DataRow("localhost", false, "")]
    [DataRow("", false, "")]
    public void Domain_Normalization(string input, bool ok, string expected)
    {
        Assert.AreEqual(ok, WebSearchTool.TryNormalizeDomain(input, out var d, out var err));
        Assert.AreEqual(expected, d);
        Assert.AreEqual(!ok, err != null);
    }

    [TestMethod]
    public void Registry_NumbersSourcesPerUrl_AndAccumulatorEmitsMarkers()
    {
        var reg = new CitationRegistry();
        var acc = new StreamingContentAccumulator(reg);
        JsonObject Ev(string s) => (JsonObject)JsonNode.Parse(s)!;
        string Cite(string url) => """{"type":"content_block_delta","index":0,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"__U__","title":"T","cited_text":"c","encrypted_index":"x"}}}""".Replace("__U__", url);
        var sb = new System.Text.StringBuilder();
        foreach (var e in new[]
        {
            Ev("""{"type":"message_start","message":{"model":"m","usage":{}}}"""),
            Ev("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"A"}}"""),
            Ev(Cite("https://a.com")), Ev(Cite("https://a.com")),
            Ev("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"B"}}"""),
            Ev(Cite("https://b.com")),
            Ev("""{"type":"content_block_stop","index":0}"""),
        }) sb.Append(acc.Apply(e));
        Assert.AreEqual("A\u27E61\u27E7B\u27E62\u27E7", sb.ToString());
        Assert.AreEqual(sb.ToString(), acc.DisplayText());
        Assert.AreEqual(2, acc.UsedSources.Count);
        Assert.AreEqual("https://b.com", acc.UsedSources[1].Url);
        var block = acc.BlockAt(0)!;
        Assert.IsFalse(block.ToJsonString().Contains('\u27E6'), "stored content never contains markers");
    }
}
