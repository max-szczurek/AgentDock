using AgentDock.Core.Mcp;

namespace AgentDock.Tests.Unit;

[TestClass]
public class McpUnitTests
{
    [TestMethod]
    public void ExposedName_JoinsAliasAndTool_AndTruncatesLongNamesWithStableHash()
    {
        Assert.AreEqual("gh__issue_read", ToolRegistryFactory.ExposedName("gh", "issue_read"));
        var tool = new string('t', 200);
        var a = ToolRegistryFactory.ExposedName("gh", tool);
        Assert.AreEqual(128, a.Length);
        Assert.AreEqual(a, ToolRegistryFactory.ExposedName("gh", tool));
        Assert.AreNotEqual(a, ToolRegistryFactory.ExposedName("gh", tool + "x"));
    }

    [TestMethod]
    public void SchemaHash_IgnoresKeyOrder_ButChangesWithDescriptionOrSchema()
    {
        var h1 = ConnectionRules.SchemaHash("t", "d", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}}}""");
        var h2 = ConnectionRules.SchemaHash("t", "d", """{"properties":{"b":{"type":"number"},"a":{"type":"string"}},"type":"object"}""");
        CollectionAssert.AreEqual(h1, h2);
        CollectionAssert.AreNotEqual(h1, ConnectionRules.SchemaHash("t", "d2", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}}}"""));
        CollectionAssert.AreNotEqual(h1, ConnectionRules.SchemaHash("t", "d", """{"type":"object","properties":{"a":{"type":"string"}}}"""));
    }

    [TestMethod]
    [DataRow("X-MCP-Toolsets", "repos,issues", true)]
    [DataRow("Authorization", "Bearer x", false)]
    [DataRow("X-Api-Key", "k", false)]
    [DataRow("X-Auth-Thing", "k", false)]
    [DataRow("Host", "evil", false)]
    [DataRow("X-Ok", "a\r\nInjected: 1", false)]
    public void HeaderValidation(string name, string value, bool ok) =>
        Assert.AreEqual(ok, ConnectionRules.TryValidateHeader(name, value, out _));

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("""{"readOnlyHint":true}""", false)]
    [DataRow("""{"readOnlyHint":true,"destructiveHint":true}""", true)]
    [DataRow("""{"readOnlyHint":false}""", true)]
    [DataRow("not json", true)]
    public void DefaultConfirmation_OnUnlessReadOnly(string? annotations, bool expected) =>
        Assert.AreEqual(expected, McpAnnotations.DefaultRequiresConfirmation(annotations));
}
