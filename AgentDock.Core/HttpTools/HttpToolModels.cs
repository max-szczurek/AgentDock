using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Json.Schema;

namespace AgentDock.Core.HttpTools;

/// <summary>One parameter in the stored execution config: where the model-supplied value goes.</summary>
public sealed class HttpParam
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("in")] public string In { get; set; } = "query";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("bodyPath")] public string? BodyPath { get; set; }
}

public sealed class HttpResponseConfig
{
    [JsonPropertyName("maxBytes")] public int MaxBytes { get; set; } = HttpToolRules.DefaultMaxBytes;
    [JsonPropertyName("select")] public List<string> Select { get; set; } = [];
    [JsonPropertyName("includeStatusCode")] public bool IncludeStatusCode { get; set; } = true;
}

/// <summary>Server-side half of a tool (never shown to the model). The connection supplies the base URL and credentials.</summary>
public sealed class HttpToolExecution
{
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("urlTemplate")] public string UrlTemplate { get; set; } = "/";
    [JsonPropertyName("parameters")] public List<HttpParam> Parameters { get; set; } = [];
    [JsonPropertyName("bodyMode")] public string BodyMode { get; set; } = "none";
    [JsonPropertyName("staticQuery")] public Dictionary<string, string> StaticQuery { get; set; } = [];
    [JsonPropertyName("response")] public HttpResponseConfig Response { get; set; } = new();
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = HttpToolRules.DefaultTimeoutSeconds;
}

/// <summary>A parameter as shown in the form builder (schema + execution parts together).</summary>
public sealed class HttpToolFormParameter
{
    public string Name { get; set; } = "";
    public string In { get; set; } = "query";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }
    /// <summary>Comma-separated allowed values (strings only).</summary>
    public string? EnumValues { get; set; }
    public string? BodyPath { get; set; }
}

/// <summary>Form-builder view of a tool. The JSON input schema is generated from <see cref="Parameters"/>.</summary>
public sealed class HttpToolInput
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Method { get; set; } = "GET";
    public Guid ConnectionId { get; set; }
    public string UrlTemplate { get; set; } = "/";
    public List<HttpToolFormParameter> Parameters { get; set; } = [];
    public Dictionary<string, string> StaticQuery { get; set; } = [];
    public List<string> ResponseSelect { get; set; } = [];
    public bool IncludeStatusCode { get; set; } = true;
    public int TimeoutSeconds { get; set; } = HttpToolRules.DefaultTimeoutSeconds;
    public int MaxBytes { get; set; } = HttpToolRules.DefaultMaxBytes;
}

/// <summary>What gets stored: both representations (form and advanced JSON) reduce to this.</summary>
public sealed record HttpToolDraft(
    string Name, string DisplayName, string Description, Guid ConnectionId, JsonObject InputSchema, HttpToolExecution Execution);

public static partial class HttpToolRules
{
    public const int DefaultMaxBytes = 256 * 1024;
    public const int MaxBytesCeiling = 1024 * 1024;
    public const int DefaultTimeoutSeconds = 20;
    public const int MaxParameters = 20;
    public const int MaxSchemaChars = 32 * 1024;
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    public static readonly string[] Locations = ["path", "query", "body"];
    public static readonly string[] ScalarTypes = ["string", "integer", "number", "boolean"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions JsonPretty = new(Json) { WriteIndented = true };

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,64}$")] public static partial Regex ToolName();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")] public static partial Regex ParamName();
    [GeneratedRegex(@"\{([^{}]*)\}")] public static partial Regex Placeholder();
    [GeneratedRegex(@"^[A-Za-z0-9_.\-\[\]\*]+$")] private static partial Regex SelectPath();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}(\.[A-Za-z_][A-Za-z0-9_]{0,63}){0,4}$")] private static partial Regex BodyPathRx();
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$")] private static partial Regex QueryKey();

    private static readonly string[] Credentialish = ["auth", "token", "secret", "key", "password", "cookie", "credential", "session", "sig"];

    /// <summary>Confirmation defaults to on for anything that is not a plain GET.</summary>
    public static bool DefaultRequiresConfirmation(string method) => !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

    public static List<string> Validate(HttpToolDraft d)
    {
        var e = new List<string>();
        if (!ToolName().IsMatch(d.Name ?? "")) e.Add("Name must be 1-64 characters: letters, digits, '_' and '-'.");
        if (string.IsNullOrWhiteSpace(d.DisplayName) || d.DisplayName.Length > 100) e.Add("Display name is required (max 100 characters).");
        if (string.IsNullOrWhiteSpace(d.Description)) e.Add("Describe when the agent should use this tool.");
        else if (d.Description.Length > 2000) e.Add("Description is at most 2000 characters.");

        var x = d.Execution;
        if (!Methods.Contains(x.Method)) e.Add("Method must be GET, POST, PUT, PATCH or DELETE.");
        if (x.TimeoutSeconds is < 1 or > 60) e.Add("Timeout must be 1-60 seconds.");
        if (x.Response.MaxBytes is < 1024 or > MaxBytesCeiling) e.Add($"Max response size must be 1 KB to {MaxBytesCeiling / 1024} KB.");
        if (x.BodyMode is not ("none" or "json")) e.Add("Body mode must be 'none' or 'json'.");
        if (x.Parameters.Count > MaxParameters) e.Add($"At most {MaxParameters} parameters.");

        var template = x.UrlTemplate ?? "";
        if (!template.StartsWith('/') || template.StartsWith("//") || template.Contains('?') || template.Contains('#') || template.Contains('\\')
            || template.Contains("..") || template.Any(char.IsControl) || template.Length > 500)
            e.Add("URL template must be a path starting with '/', without '?', '#', '..' or a host. Put fixed query values in 'static query'.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in x.Parameters)
        {
            if (!ParamName().IsMatch(p.Name ?? "")) { e.Add($"Parameter name '{p.Name}' is invalid (letters, digits, '_', starting with a letter or '_')."); continue; }
            if (!names.Add(p.Name)) e.Add($"Parameter '{p.Name}' is listed twice.");
            if (!Locations.Contains(p.In)) e.Add($"Parameter '{p.Name}' must go in the path, query or body.");
            if (p.In == "path" && !p.Required) e.Add($"Path parameter '{p.Name}' must be required.");
            if (p.In == "body")
            {
                if (x.Method is "GET" or "DELETE") e.Add($"'{p.Name}': {x.Method} requests cannot have a body.");
                if (x.BodyMode != "json") e.Add($"'{p.Name}' is a body parameter, so body mode must be 'json'.");
                if (p.BodyPath != null && !BodyPathRx().IsMatch(p.BodyPath)) e.Add($"Body path '{p.BodyPath}' is invalid (dotted names, up to 5 levels).");
            }
            else if (p.BodyPath != null) e.Add($"'{p.Name}' has a body path but is not a body parameter.");
        }

        var placeholders = Placeholder().Matches(template).Select(m => m.Groups[1].Value).ToList();
        if (Placeholder().Replace(template, "").AsSpan().IndexOfAny('{', '}') >= 0) e.Add("URL template has unbalanced or empty braces.");
        foreach (var ph in placeholders)
        {
            var p = x.Parameters.FirstOrDefault(p => p.Name == ph);
            if (p == null || p.In != "path" || !p.Required) e.Add($"'{{{ph}}}' in the URL must be a required path parameter.");
        }
        foreach (var p in x.Parameters.Where(p => p.In == "path" && !placeholders.Contains(p.Name)))
            e.Add($"Path parameter '{p.Name}' does not appear in the URL template as '{{{p.Name}}}'.");

        if (x.StaticQuery.Count > 10) e.Add("At most 10 static query values.");
        foreach (var (k, v) in x.StaticQuery)
        {
            if (!QueryKey().IsMatch(k)) e.Add($"Static query name '{k}' is invalid.");
            else if (Credentialish.Any(k.ToLowerInvariant().Contains)) e.Add($"Static query '{k}' looks like a credential. Put credentials in the connection's bearer token instead.");
            else if (x.Parameters.Any(p => p.In == "query" && p.Name == k)) e.Add($"Static query '{k}' clashes with a query parameter.");
            if ((v ?? "").Length > 200 || (v ?? "").Any(char.IsControl)) e.Add($"Static query '{k}' has an invalid value.");
        }

        foreach (var path in x.Response.Select)
            if (path.Length is 0 or > 200 || !SelectPath().IsMatch(path)) e.Add($"Response field '{path}' is invalid. Use dotted names such as items[*].sku.");
        if (x.Response.Select.Count > 50) e.Add("At most 50 response fields.");

        ValidateSchema(d.InputSchema, x, e);
        return e;
    }

    private static void ValidateSchema(JsonObject schema, HttpToolExecution x, List<string> e)
    {
        var text = schema.ToJsonString();
        if (text.Length > MaxSchemaChars) { e.Add("The input schema is larger than 32 KB."); return; }
        if (schema["type"]?.GetValue<string>() != "object") { e.Add("The input schema must have \"type\": \"object\"."); return; }
        try
        {
            var meta = MetaSchemas.Draft202012.Evaluate(JsonSerializer.SerializeToElement(schema), new EvaluationOptions { OutputFormat = OutputFormat.Flag });
            if (!meta.IsValid) { e.Add("The input schema is not a valid JSON Schema (draft 2020-12)."); return; }
            JsonSchema.FromText(text);
        }
        catch (Exception) { e.Add("The input schema is not a valid JSON Schema (draft 2020-12)."); return; }

        if (text.Contains("\"$ref\"") && text.Contains("://")) e.Add("Remote $ref values are not allowed in the input schema.");
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        var required = (schema["required"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet() ?? [];
        foreach (var p in x.Parameters)
        {
            if (!props.ContainsKey(p.Name)) e.Add($"Parameter '{p.Name}' is missing from the input schema's properties.");
            if (p.Required && !required.Contains(p.Name)) e.Add($"Parameter '{p.Name}' is required but not listed in the schema's 'required'.");
            if (!p.Required && required.Contains(p.Name)) e.Add($"Parameter '{p.Name}' is optional but the schema lists it as required.");
            if (p.In is "path" or "query" && props[p.Name] is JsonObject po && po["type"] is JsonNode t && !IsScalarish(t, p.In))
                e.Add($"Parameter '{p.Name}' goes in the {p.In}, so its type must be a string, number, integer or boolean{(p.In == "query" ? " (or an array of those)" : "")}.");
        }
        foreach (var name in props.Select(kv => kv.Key).Where(n => x.Parameters.All(p => p.Name != n)))
            e.Add($"Schema property '{name}' has no parameter mapping, so its value would be ignored.");
    }

    private static bool IsScalarish(JsonNode type, string location)
    {
        var t = type is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return t != null && (ScalarTypes.Contains(t) || (location == "query" && t == "array"));
    }

    /// <summary>Generates the model-facing schema from a parameter table.</summary>
    public static JsonObject BuildSchema(IEnumerable<HttpToolFormParameter> parameters)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            var prop = new JsonObject { ["type"] = ScalarTypes.Contains(p.Type) ? p.Type : "string" };
            if (!string.IsNullOrWhiteSpace(p.Description)) prop["description"] = p.Description.Trim();
            var values = (p.EnumValues ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length > 0 && prop["type"]!.GetValue<string>() == "string") prop["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
            props[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        schema["additionalProperties"] = false;
        return schema;
    }

    public static HttpToolDraft FromForm(HttpToolInput i)
    {
        var parameters = i.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => { p.Name = p.Name.Trim(); return p; }).ToList();
        var exec = new HttpToolExecution
        {
            Method = (i.Method ?? "GET").ToUpperInvariant(),
            UrlTemplate = (i.UrlTemplate ?? "").Trim(),
            Parameters = parameters.Select(p => new HttpParam
            {
                Name = p.Name, In = p.In, Required = p.Required || p.In == "path",
                BodyPath = p.In == "body" && !string.IsNullOrWhiteSpace(p.BodyPath) && p.BodyPath.Trim() != p.Name ? p.BodyPath.Trim() : null,
            }).ToList(),
            BodyMode = parameters.Any(p => p.In == "body") ? "json" : "none",
            StaticQuery = i.StaticQuery.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).ToDictionary(kv => kv.Key.Trim(), kv => (kv.Value ?? "").Trim()),
            Response = new HttpResponseConfig
            {
                MaxBytes = i.MaxBytes, IncludeStatusCode = i.IncludeStatusCode,
                Select = i.ResponseSelect.Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            },
            TimeoutSeconds = i.TimeoutSeconds,
        };
        foreach (var p in parameters) p.Required = p.Required || p.In == "path";
        return new HttpToolDraft((i.Name ?? "").Trim(), (i.DisplayName ?? "").Trim(), (i.Description ?? "").Trim(), i.ConnectionId, BuildSchema(parameters), exec);
    }

    /// <summary>Best-effort form view of a stored tool (advanced-only schema details are not representable and are dropped on a form save).</summary>
    public static HttpToolInput ToForm(string name, string displayName, string description, Guid connectionId, string schemaJson, HttpToolExecution x)
    {
        var props = (JsonNode.Parse(schemaJson) as JsonObject)?["properties"] as JsonObject ?? new JsonObject();
        return new HttpToolInput
        {
            Name = name, DisplayName = displayName, Description = description, ConnectionId = connectionId,
            Method = x.Method, UrlTemplate = x.UrlTemplate, StaticQuery = new(x.StaticQuery),
            ResponseSelect = [.. x.Response.Select], IncludeStatusCode = x.Response.IncludeStatusCode, TimeoutSeconds = x.TimeoutSeconds, MaxBytes = x.Response.MaxBytes,
            Parameters = x.Parameters.Select(p =>
            {
                var prop = props[p.Name] as JsonObject;
                var type = prop?["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) && ScalarTypes.Contains(ts) ? ts : "string";
                return new HttpToolFormParameter
                {
                    Name = p.Name, In = p.In, Required = p.Required, Type = type, BodyPath = p.BodyPath,
                    Description = prop?["description"]?.GetValue<string>(),
                    EnumValues = prop?["enum"] is JsonArray en ? string.Join(", ", en.Select(n => n?.ToString())) : null,
                };
            }).ToList(),
        };
    }

    public static string SerializeExecution(HttpToolExecution x) => JsonSerializer.Serialize(x, Json);

    public static bool TryParseExecution(string json, out HttpToolExecution exec)
    {
        try { exec = JsonSerializer.Deserialize<HttpToolExecution>(json, Json) ?? new(); return true; }
        catch (JsonException) { exec = new(); return false; }
    }

    public static string AdvancedJson(string schemaJson, HttpToolExecution x)
    {
        var doc = new JsonObject { ["inputSchema"] = JsonNode.Parse(schemaJson), ["execution"] = JsonSerializer.SerializeToNode(x, Json) };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool TryParseAdvanced(string? text, out JsonObject schema, out HttpToolExecution exec, out string? error)
    {
        schema = new(); exec = new(); error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 128 * 1024) { error = "Enter the advanced JSON (max 128 KB)."; return false; }
            if (JsonNode.Parse(text) is not JsonObject root) { error = "The advanced JSON must be an object with 'inputSchema' and 'execution'."; return false; }
            if (root.Any(kv => kv.Key is not ("inputSchema" or "execution"))) { error = "Only 'inputSchema' and 'execution' are allowed at the top level."; return false; }
            if (root["inputSchema"] is not JsonObject s) { error = "'inputSchema' must be a JSON object."; return false; }
            if (root["execution"] is not JsonObject ex) { error = "'execution' must be a JSON object."; return false; }
            schema = s.DeepClone().AsObject();
            exec = JsonSerializer.Deserialize<HttpToolExecution>(ex.ToJsonString(), Json) ?? new();
            return true;
        }
        catch (JsonException ex) { error = "The advanced JSON is not valid: " + ex.Message.Split('.')[0] + "."; return false; }
    }

    /// <summary>Prefills a sample input from the schema for the test panel.</summary>
    public static string SampleInput(string schemaJson)
    {
        var sample = new JsonObject();
        if (JsonNode.Parse(schemaJson) is JsonObject s && s["properties"] is JsonObject props)
            foreach (var (name, node) in props)
            {
                var o = node as JsonObject;
                var type = o?["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "string";
                sample[name] = o?["enum"] is JsonArray { Count: > 0 } en ? en[0]!.DeepClone() : type switch
                {
                    "integer" or "number" => 1, "boolean" => true, "array" => new JsonArray(), "object" => new JsonObject(), _ => "",
                };
            }
        return sample.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
