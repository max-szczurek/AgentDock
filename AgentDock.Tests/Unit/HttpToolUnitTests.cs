using System.Text.Json.Nodes;
using AgentDock.Core.HttpTools;
using AgentDock.Core.Mcp;
using AgentDock.Core.Outbound;

namespace AgentDock.Tests.Unit;

[TestClass]
public class HttpToolUnitTests
{
    private static HttpToolInput Lookup() => new()
    {
        Name = "order_lookup", DisplayName = "Order lookup", Description = "Look up an order.", Method = "GET", ConnectionId = Guid.NewGuid(), UrlTemplate = "/v1/orders/{orderId}",
        Parameters =
        [
            new() { Name = "orderId", In = "path", Type = "string", Required = true },
            new() { Name = "expand", In = "query", Type = "string" },
        ],
    };

    private static ConnectionAccess Access(string baseUrl = "https://api.example.com/v2") =>
        new(Guid.NewGuid(), new Uri(baseUrl), null, new Dictionary<string, string>(), 10);

    private static List<string> Validate(HttpToolInput i) => HttpToolRules.Validate(HttpToolRules.FromForm(i));

    [TestMethod]
    public void Form_GeneratesSchema_AndPassesValidation()
    {
        var draft = HttpToolRules.FromForm(Lookup());
        Assert.AreEqual(0, HttpToolRules.Validate(draft).Count, string.Join(";", HttpToolRules.Validate(draft)));
        Assert.AreEqual("object", draft.InputSchema["type"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { "orderId" }, draft.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.IsFalse(draft.InputSchema["additionalProperties"]!.GetValue<bool>());
        Assert.AreEqual("none", draft.Execution.BodyMode);
    }

    [TestMethod]
    public void Validation_RejectsBadPlaceholdersAndTemplates()
    {
        var i = Lookup(); i.UrlTemplate = "/v1/orders/{missing}";
        Assert.IsTrue(Validate(i).Any(e => e.Contains("{missing}")), "unknown placeholder");
        Assert.IsTrue(Validate(i).Any(e => e.Contains("does not appear")), "unused path parameter");

        i = Lookup(); i.Parameters[0].In = "query";
        Assert.IsTrue(Validate(i).Any(e => e.Contains("{orderId}")), "placeholder must be a path parameter");

        foreach (var bad in new[] { "https://evil.example/x", "//evil.example", "orders", "/a?b=1", "/a#b", "/a/../b", "/a\\b" })
        {
            i = Lookup(); i.UrlTemplate = bad + "/{orderId}";
            Assert.IsTrue(Validate(i).Any(e => e.Contains("URL template")), bad);
        }
        i = Lookup(); i.UrlTemplate = "/a/{orderId";
        Assert.IsTrue(Validate(i).Any(e => e.Contains("braces")));
    }

    [TestMethod]
    public void Validation_CoversMethodsBodiesNamesAndLimits()
    {
        var i = Lookup(); i.Method = "TRACE";
        Assert.IsTrue(Validate(i).Any(e => e.Contains("Method")));

        i = Lookup(); i.Parameters.Add(new() { Name = "note", In = "body", Type = "string" });
        Assert.IsTrue(Validate(i).Any(e => e.Contains("cannot have a body")), "GET with body");
        i.Method = "POST";
        Assert.AreEqual(0, Validate(i).Count, string.Join(";", Validate(i)));

        i = Lookup(); i.Name = "bad name!";
        Assert.IsTrue(Validate(i).Any(e => e.Contains("Name must be")));
        i = Lookup(); i.Parameters.Add(new() { Name = "expand", In = "query" });
        Assert.IsTrue(Validate(i).Any(e => e.Contains("twice")));
        i = Lookup(); i.TimeoutSeconds = 0;
        Assert.IsTrue(Validate(i).Any(e => e.Contains("Timeout")));
        i = Lookup(); i.MaxBytes = 5_000_000;
        Assert.IsTrue(Validate(i).Any(e => e.Contains("Max response size")));
        i = Lookup(); i.ResponseSelect = ["items[*].sku", "bad path!"];
        Assert.IsTrue(Validate(i).Any(e => e.Contains("Response field")));
        i = Lookup(); i.StaticQuery = new() { ["api_key"] = "abc" };
        Assert.IsTrue(Validate(i).Any(e => e.Contains("looks like a credential")), "secrets do not belong in static query");
        i = Lookup(); i.StaticQuery = new() { ["expand"] = "x" };
        Assert.IsTrue(Validate(i).Any(e => e.Contains("clashes")));
    }

    [TestMethod]
    public void Validation_ChecksTheAdvancedSchemaAgainstTheParameters()
    {
        var draft = HttpToolRules.FromForm(Lookup());
        JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;
        List<string> Run(JsonObject s) => HttpToolRules.Validate(draft with { InputSchema = s });

        Assert.IsTrue(Run(Schema("""{"type":"array"}""")).Any(e => e.Contains("\"type\": \"object\"")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"type":"string"},"expand":{"type":"string"},"ghost":{"type":"string"}},"required":["orderId"]}""")).Any(e => e.Contains("'ghost'")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")).Any(e => e.Contains("'expand' is missing")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"type":"string"},"expand":{"type":"string"}}}""")).Any(e => e.Contains("not listed in the schema's 'required'")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"type":"object"},"expand":{"type":"string"}},"required":["orderId"]}""")).Any(e => e.Contains("must be a string")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"type":"string"},"expand":{"type":"nonsense"}},"required":["orderId"]}""")).Any(e => e.Contains("not a valid JSON Schema")));
        Assert.IsTrue(Run(Schema("""{"type":"object","properties":{"orderId":{"$ref":"https://evil.example/s.json"},"expand":{"type":"string"}},"required":["orderId"]}""")).Any(e => e.Contains("Remote $ref")));
        var big = new JsonObject { ["type"] = "object", ["description"] = new string('x', 40_000) };
        Assert.IsTrue(Run(big).Any(e => e.Contains("32 KB")));
    }

    [TestMethod]
    public void Advanced_RoundTripsAndRejectsUnknownMembers()
    {
        var draft = HttpToolRules.FromForm(Lookup());
        var json = HttpToolRules.AdvancedJson(draft.InputSchema.ToJsonString(), draft.Execution);
        Assert.IsTrue(HttpToolRules.TryParseAdvanced(json, out var schema, out var exec, out var error), error);
        Assert.AreEqual(draft.Execution.UrlTemplate, exec.UrlTemplate);
        Assert.AreEqual(0, HttpToolRules.Validate(draft with { InputSchema = schema, Execution = exec }).Count);

        Assert.IsFalse(HttpToolRules.TryParseAdvanced("""{"inputSchema":{},"execution":{"urlTemplat":"/x"}}""", out _, out _, out _));
        Assert.IsFalse(HttpToolRules.TryParseAdvanced("""{"inputSchema":{},"execution":{},"headers":{"Authorization":"x"}}""", out _, out _, out error));
        StringAssert.Contains(error, "Only 'inputSchema'");
        Assert.IsFalse(HttpToolRules.TryParseAdvanced("not json", out _, out _, out _));
        Assert.IsFalse(HttpToolRules.TryParseAdvanced("", out _, out _, out _));
    }

    [TestMethod]
    public void Form_RoundTripsThroughStoredRepresentation()
    {
        var input = Lookup();
        input.Parameters[1].EnumValues = "a, b";
        input.Parameters[1].Description = "Extra";
        var draft = HttpToolRules.FromForm(input);
        var back = HttpToolRules.ToForm(draft.Name, draft.DisplayName, draft.Description, draft.ConnectionId, draft.InputSchema.ToJsonString(), draft.Execution);
        Assert.AreEqual("a, b", back.Parameters[1].EnumValues);
        Assert.AreEqual("Extra", back.Parameters[1].Description);
        Assert.IsTrue(back.Parameters[0].Required);
        Assert.IsTrue(JsonNode.DeepEquals(draft.InputSchema, HttpToolRules.FromForm(back).InputSchema));
        Assert.IsFalse(HttpToolRules.DefaultRequiresConfirmation("GET"));
        Assert.IsTrue(HttpToolRules.DefaultRequiresConfirmation("DELETE"));
    }

    [TestMethod]
    public void Build_EncodesPathValues_SerializesQuery_AndKeepsTheOriginAndBasePath()
    {
        var exec = HttpToolRules.FromForm(Lookup()).Execution;
        exec.StaticQuery["format"] = "json";
        var (url, body) = HttpToolRunner.Build(Access(), exec, (JsonObject)JsonNode.Parse("""{"orderId":"a b&c%","expand":"x y"}""")!);
        Assert.IsNull(body);
        Assert.AreEqual("https://api.example.com/v2/v1/orders/a%20b%26c%25?format=json&expand=x%20y", url.AbsoluteUri);

        (url, _) = HttpToolRunner.Build(Access("https://api.example.com"), exec, (JsonObject)JsonNode.Parse("""{"orderId":42}""")!);
        Assert.AreEqual("https://api.example.com/v1/orders/42?format=json", url.AbsoluteUri);
    }

    [TestMethod]
    public void Build_RejectsPathTraversalAndNonScalars()
    {
        var exec = HttpToolRules.FromForm(Lookup()).Execution;
        foreach (var evil in new[] { "..", "../x", "a/b", "a\\b", "a?b", "a#b", ".", "", "x..y" })
        {
            var args = new JsonObject { ["orderId"] = evil };
            Assert.ThrowsExactly<InvalidToolInputException>(() => HttpToolRunner.Build(Access(), exec, args), evil);
        }
        Assert.ThrowsExactly<InvalidToolInputException>(() => HttpToolRunner.Build(Access(), exec, (JsonObject)JsonNode.Parse("""{"orderId":{"a":1}}""")!));
        Assert.ThrowsExactly<InvalidToolInputException>(() => HttpToolRunner.Build(Access(), exec, (JsonObject)JsonNode.Parse("""{"orderId":"1","expand":{"a":1}}""")!));
        Assert.ThrowsExactly<InvalidToolInputException>(() => HttpToolRunner.Build(Access(), exec, new JsonObject()));
    }

    [TestMethod]
    public void Build_QueryArraysRepeatTheKey_AndBodiesNestByPath()
    {
        var input = new HttpToolInput
        {
            Name = "t", DisplayName = "t", Description = "d", Method = "POST", ConnectionId = Guid.NewGuid(), UrlTemplate = "/things",
            Parameters =
            [
                new() { Name = "tag", In = "query", Type = "string" },
                new() { Name = "sku", In = "body", Type = "string", Required = true },
                new() { Name = "city", In = "body", Type = "string", BodyPath = "address.city" },
                new() { Name = "zip", In = "body", Type = "string", BodyPath = "address.zip" },
            ],
        };
        var exec = HttpToolRules.FromForm(input).Execution;
        var (url, body) = HttpToolRunner.Build(Access("https://api.example.com"), exec,
            (JsonObject)JsonNode.Parse("""{"tag":["a","b c"],"sku":"S1","city":"Paris","zip":"75001"}""")!);
        Assert.AreEqual("https://api.example.com/things?tag=a&tag=b%20c", url.AbsoluteUri);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(body!), JsonNode.Parse("""{"sku":"S1","address":{"city":"Paris","zip":"75001"}}""")));
    }

    [TestMethod]
    public void Redacted_ShowsPathButNeverTheQueryString()
    {
        Assert.AreEqual("https://api.example.com/v1/x?(redacted)", HttpToolRunner.Redacted(new Uri("https://api.example.com/v1/x?token=abc")));
        Assert.AreEqual("https://api.example.com/v1/x", HttpToolRunner.Redacted(new Uri("https://api.example.com/v1/x")));
    }

    [TestMethod]
    public void Projection_KeepsOnlySelectedPaths_AcrossObjectsAndArrays()
    {
        var doc = JsonNode.Parse("""
            {"id":"o1","status":"ok","secret":"x","customer":{"name":"Ada","email":"e"},
             "items":[{"sku":"A","qty":1,"cost":9},{"sku":"B","qty":2,"cost":8}]}
            """)!;
        var r = HttpProjection.Apply(doc, ["id", "customer.name", "items[*].sku", "items[*].qty"])!;
        Assert.IsTrue(JsonNode.DeepEquals(r, JsonNode.Parse("""{"id":"o1","customer":{"name":"Ada"},"items":[{"sku":"A","qty":1},{"sku":"B","qty":2}]}""")), r.ToJsonString());

        r = HttpProjection.Apply(doc, ["items"])!;
        Assert.AreEqual(2, r["items"]!.AsArray().Count);
        Assert.IsNotNull(r["items"]![0]!["cost"], "selecting the array keeps whole elements");

        r = HttpProjection.Apply(JsonNode.Parse("""[{"a":1,"b":2},{"a":3,"b":4}]""")!, ["a"])!;
        Assert.AreEqual("[{\"a\":1},{\"a\":3}]", r.ToJsonString(), "root arrays map element-wise");

        r = HttpProjection.Apply(doc, ["nope", "customer.missing"])!;
        Assert.AreEqual("{}", r.ToJsonString());
    }

    [TestMethod]
    public void SampleInput_IsPrefilledFromTheSchema()
    {
        var draft = HttpToolRules.FromForm(Lookup());
        var sample = JsonNode.Parse(HttpToolRules.SampleInput(draft.InputSchema.ToJsonString()))!.AsObject();
        Assert.IsTrue(sample.ContainsKey("orderId") && sample.ContainsKey("expand"));
    }
}
