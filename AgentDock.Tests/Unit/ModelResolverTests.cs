using AgentDock.Core.Models;
using AgentDock.Tests.Infrastructure;

namespace AgentDock.Tests.Unit;

[TestClass]
public class ModelResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T00:00:00Z");

    [TestMethod]
    public void PicksNewestActivePerLine_ByCreatedAt_NotById()
    {
        var f = ModelResolver.ResolveFamilies(Fixtures.Models(), Now);
        Assert.AreEqual("claude-haiku-5-5", f[ModelFamily.Haiku].ModelId);
        Assert.AreEqual("claude-sonnet-5-5", f[ModelFamily.Sonnet].ModelId);
        Assert.AreEqual("claude-opus-5-5", f[ModelFamily.Opus].ModelId, "id order would pick claude-opus-5");
        Assert.AreEqual(128000, f[ModelFamily.Opus].MaxOutputTokens);
        Assert.IsTrue(f[ModelFamily.Opus].SupportsWebSearch);
    }

    [TestMethod]
    public void IgnoresOtherLinesAndNullLine()
    {
        var f = ModelResolver.ResolveFamilies(Fixtures.Models(), Now);
        Assert.AreEqual(3, f.Count);
        Assert.IsFalse(f.Values.Any(m => m.ModelId is "claude-fable-1" or "claude-mystery"));
    }

    [TestMethod]
    public void FamilyWithNoModels_IsHidden()
    {
        var f = ModelResolver.ResolveFamilies(Fixtures.Models().Where(m => m.Line != "opus"), Now);
        Assert.IsFalse(f.ContainsKey(ModelFamily.Opus));
    }

    [TestMethod]
    public void DeprecatedFallback_OnlyWhenNoActive_AndNotRetired()
    {
        var models = Fixtures.Models().Where(m => m.Line != "opus" || m.Lifecycle == "deprecated").ToList();
        var f = ModelResolver.ResolveFamilies(models, Now);
        Assert.AreEqual("claude-opus-4-8", f[ModelFamily.Opus].ModelId);
        Assert.IsTrue(f[ModelFamily.Opus].IsFallback);

        var retiring = models.Select(m => m.Id == "claude-opus-4-8" ? m with { RetiresAt = Now.AddDays(-1) } : m);
        Assert.IsFalse(ModelResolver.ResolveFamilies(retiring, Now).ContainsKey(ModelFamily.Opus));
    }

    [TestMethod]
    public void Pinned_UsedUnlessRetiredOrMissing()
    {
        var models = Fixtures.Models();
        Assert.AreEqual("claude-opus-5", ModelResolver.ResolvePinned(models, "claude-opus-5")!.ModelId);
        Assert.IsNull(ModelResolver.ResolvePinned(models, "nope"));
        var retired = models.Select(m => m.Id == "claude-opus-5" ? m with { Lifecycle = "retired" } : m);
        Assert.IsNull(ModelResolver.ResolvePinned(retired, "claude-opus-5"));
    }

    [TestMethod]
    public void FallbackOrder_DefaultThenSonnetHaikuOpus()
    {
        var f = ModelResolver.ResolveFamilies(Fixtures.Models().Where(m => m.Line != "opus"), Now);
        Assert.AreEqual(ModelFamily.Sonnet, ModelResolver.FallbackOrder(ModelFamily.Opus, f));
        Assert.AreEqual(ModelFamily.Haiku, ModelResolver.FallbackOrder(ModelFamily.Haiku, f));
        Assert.IsNull(ModelResolver.FallbackOrder(ModelFamily.Opus, new Dictionary<ModelFamily, ResolvedModel>()));
    }

    [TestMethod]
    public void WebSearchCapability_IsParsed()
    {
        var m = Fixtures.Models().First(x => x.Id == "claude-haiku-4-5-20251001");
        Assert.IsTrue(m.SupportsWebSearch);
        Assert.IsFalse(m.SupportsProgrammaticToolCalling);
    }
}
