using AgentDock.Core.Models;
using AgentDock.Core.Services;

namespace AgentDock.Tests.Unit;

[TestClass]
public class AgentRulesTests
{
    [TestMethod]
    [DataRow("https://app.example.com", "https://app.example.com")]
    [DataRow("HTTPS://App.Example.COM/", "https://app.example.com")]
    [DataRow("https://app.example.com:443", "https://app.example.com")]
    [DataRow("https://app.example.com:8443", "https://app.example.com:8443")]
    [DataRow("http://localhost:3000", "http://localhost:3000")]
    [DataRow("http://127.0.0.1:5294/", "http://127.0.0.1:5294")]
    [DataRow("  https://example.com  ", "https://example.com")]
    public void Origins_AreNormalized(string input, string expected)
    {
        Assert.IsTrue(OriginNormalizer.TryNormalize(input, out var o, out var err), err);
        Assert.AreEqual(expected, o);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("example.com")]
    [DataRow("https://*.example.com")]
    [DataRow("https://example.com/app")]
    [DataRow("https://example.com?x=1")]
    [DataRow("https://example.com#frag")]
    [DataRow("https://user:pw@example.com")]
    [DataRow("http://example.com")]
    [DataRow("ftp://example.com")]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://intranet")]
    public void Origins_AreRejected(string input)
    {
        Assert.IsFalse(OriginNormalizer.TryNormalize(input, out _, out var err));
        Assert.IsFalse(string.IsNullOrEmpty(err));
    }

    [TestMethod]
    public void Appearance_Validation()
    {
        Assert.AreEqual(0, new AppearanceSettings().Validate().Count);
        Assert.IsTrue(new AppearanceSettings { PrimaryColor = "red" }.Validate().Count > 0);
        Assert.IsTrue(new AppearanceSettings { PanelWidth = 319 }.Validate().Count > 0);
        Assert.IsTrue(new AppearanceSettings { PanelHeight = 801 }.Validate().Count > 0);
        Assert.IsTrue(new AppearanceSettings { IconPreset = "custom" }.Validate().Count > 0);
        Assert.IsTrue(new AppearanceSettings { Position = "top" }.Validate().Count > 0);
        Assert.IsTrue(new AppearanceSettings { Title = "" }.Validate().Count > 0);
    }

    [TestMethod]
    public void Appearance_RoundTripsAndToleratesBadJson()
    {
        var a = new AppearanceSettings { Title = "Help", PanelWidth = 400 };
        Assert.AreEqual("Help", AppearanceSettings.FromJson(a.ToJson()).Title);
        Assert.AreEqual("Ask us", AppearanceSettings.FromJson("{not json").Title);
        Assert.AreEqual("Ask us", AppearanceSettings.FromJson(null).Title);
    }

    [TestMethod]
    public void Limits_AreBoundedByCaps()
    {
        Assert.AreEqual(0, new AgentLimits().Validate().Count);
        Assert.IsTrue(new AgentLimits { TurnsPerDay = 5001 }.Validate().Count > 0);
        Assert.IsTrue(new AgentLimits { TurnsPerSessionPerHour = 0 }.Validate().Count > 0);
        Assert.IsTrue(new AgentLimits { MaxMessageChars = 9000 }.Validate().Count > 0);
    }

    [TestMethod]
    public void AgentValidation_CoversNameFamiliesPromptAndOrigins()
    {
        var ok = new AgentInput { Name = "Bot", Origins = ["https://a.example.com", "https://A.example.com/"] };
        Assert.AreEqual(0, AgentService.Validate(ok, out var origins, out _).Count);
        Assert.AreEqual(1, origins.Count, "duplicates after normalization collapse");

        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = " " }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", SystemPrompt = new string('a', 20_001) }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", AllowedModelFamilies = [] }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", DefaultModelFamily = ModelFamily.Opus, AllowedModelFamilies = [ModelFamily.Haiku] }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", PinnedModelId = "bad id!" }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", MaxOutputTokens = 10 }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", Origins = ["https://*.x.com"] }, out _, out _).Count > 0);
        Assert.IsTrue(AgentService.Validate(new AgentInput { Name = "x", Origins = Enumerable.Range(0, 21).Select(i => $"https://h{i}.example.com").ToList() }, out _, out _).Count > 0);
    }
}
