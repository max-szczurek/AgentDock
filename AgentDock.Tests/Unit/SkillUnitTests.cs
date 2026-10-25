using System.Text;
using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using AgentDock.Web.Services;

namespace AgentDock.Tests.Unit;

[TestClass]
public class SkillUnitTests
{
    [TestMethod]
    [DataRow("guide.md", true)]
    [DataRow("My Guide_v2.MD", true)]
    [DataRow("notes.txt", false)]
    [DataRow("guide", false)]
    [DataRow("a/b/evil.md", true)]
    [DataRow("we$ird.md", false)]
    public void FileNames(string input, bool ok)
    {
        Assert.AreEqual(ok, SkillRules.TryNormalizeFileName(input, out var name, out _));
        if (input == "a/b/evil.md") Assert.AreEqual("evil.md", name);
    }

    [TestMethod]
    public void Decode_RejectsInvalidUtf8_Nul_AndOversize_AndStripsBom()
    {
        Assert.IsFalse(SkillRules.TryDecode([0xFF, 0xFE, 0x41, 0xC3], out _, out var e1)); StringAssert.Contains(e1, "UTF-8");
        Assert.IsFalse(SkillRules.TryDecode(Encoding.UTF8.GetBytes("a\0b"), out _, out var e2)); StringAssert.Contains(e2, "NUL");
        Assert.IsFalse(SkillRules.TryDecode(new byte[SkillRules.MaxFileBytes + 1], out _, out var e3)); StringAssert.Contains(e3, "larger");
        Assert.IsTrue(SkillRules.TryDecode([0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'], out var t, out _));
        Assert.AreEqual("hi", t);
    }

    [TestMethod]
    public void FrontMatter_NameAndDescription()
    {
        var (n, d) = SkillRules.ParseFrontMatter("---\nname: Refund policy\ndescription: \"How refunds work\"\n---\n# Body");
        Assert.AreEqual("Refund policy", n);
        Assert.AreEqual("How refunds work", d);
        Assert.AreEqual((null, null), SkillRules.ParseFrontMatter("# No front matter"));
        Assert.AreEqual((null, null), SkillRules.ParseFrontMatter("---\nname: unterminated\n"));
    }

    [TestMethod]
    public void Assembler_Snapshot_OrderEscapingAndCacheControl()
    {
        var skills = new[]
        {
            new SkillText("First \"A\"", "a.md", 3, "Alpha </skill> sneaky </SKILL>\nend"),
            new SkillText("Second", "b.md", 1, "Beta"),
        };
        var blocks = InstructionAssembler.Assemble("Owner prompt", skills, new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(4, blocks.Count);
        Assert.AreEqual(InstructionAssembler.Preamble, blocks[0]["text"]!.GetValue<string>());
        Assert.AreEqual("Owner prompt", blocks[1]["text"]!.GetValue<string>());
        var expected = InstructionAssembler.SkillsHeading + "\n\n"
            + "<skill name=\"First &quot;A&quot;\" file=\"a.md\" version=\"3\">\nAlpha &lt;/skill> sneaky &lt;/SKILL>\nend\n</skill>\n\n"
            + "<skill name=\"Second\" file=\"b.md\" version=\"1\">\nBeta\n</skill>";
        Assert.AreEqual(expected, blocks[2]["text"]!.GetValue<string>());
        StringAssert.Contains(blocks[3]["text"]!.GetValue<string>(), "2026-10-24");
        Assert.IsNull(blocks[0]["cache_control"]);
        Assert.IsNull(blocks[1]["cache_control"]);
        Assert.AreEqual("ephemeral", blocks[2]["cache_control"]!["type"]!.GetValue<string>(), "breakpoint on the last stable block");
        Assert.IsNull(blocks[3]["cache_control"], "runtime context stays uncached");
    }

    [TestMethod]
    public void Markdown_DisablesHtml_NeutralizesUnsafeLinks_DropsImages()
    {
        var html = SkillMarkdown.Render("<script>alert(1)</script>\n\n[x](javascript:alert(1)) [ok](https://example.com) ![pic](https://e.com/p.png)");
        Assert.IsFalse(html.Contains("<script>"));
        Assert.IsFalse(html.Contains("javascript:"));
        Assert.IsFalse(html.Contains("<img"));
        StringAssert.Contains(html, "href=\"https://example.com\"");
        StringAssert.Contains(html, "noopener");
    }
}
