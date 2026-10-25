using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Mcp;
using AgentDock.Core.Outbound;

namespace AgentDock.Core.HttpTools;

public sealed record HttpToolResult(
    bool IsError, string Content, string Status, int? HttpStatus, int Bytes, string? ErrorCode, string? ErrorMessage, long DurationMs, string RequestUrl);

public interface IHttpToolRunner
{
    /// <summary>Runs one call. Never throws for upstream failures (they come back sanitized); rethrows only when <paramref name="ct"/> is cancelled.</summary>
    Task<HttpToolResult> RunAsync(ConnectionAccess access, HttpToolExecution exec, JsonObject args, CancellationToken ct);

    /// <summary>Reachability check for an HTTP connection.</summary>
    Task<(bool Ok, string Message)> PingAsync(ConnectionAccess access, CancellationToken ct);
}

public sealed class InvalidToolInputException(string message) : Exception(message);

public sealed class HttpToolRunner(IOutboundHttpClientFactory outbound) : IHttpToolRunner
{
    public async Task<HttpToolResult> RunAsync(ConnectionAccess access, HttpToolExecution exec, JsonObject args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Uri url;
        byte[]? body;
        try { (url, body) = Build(access, exec, args); }
        catch (InvalidToolInputException e)
        {
            return Fail("Blocked", "invalid_input", e.Message, null, sw, "");
        }

        var shown = Redacted(url);
        try
        {
            var response = await outbound.Create().SendAsync(new OutboundRequest
            {
                Method = new HttpMethod(exec.Method), Url = url,
                Headers = new Dictionary<string, string>(access.StaticHeaders) { ["Accept"] = "application/json, text/plain;q=0.8" },
                Body = body, ContentType = body == null ? null : "application/json",
                Credential = access.Credential, CredentialBaseUrl = access.BaseUrl,
            }, new OutboundCallOptions { MaxBytes = exec.Response.MaxBytes, Timeout = TimeSpan.FromSeconds(exec.TimeoutSeconds) }, ct);

            if (response.StatusCode is < 200 or >= 300)
            {
                var msg = response.StatusCode is 401 or 403 ? "The server rejected the credentials." : $"The server responded with HTTP {response.StatusCode}.";
                return new HttpToolResult(true, "The tool call failed: " + msg, "Failed", response.StatusCode, response.Body.Length, "http_" + response.StatusCode, msg, sw.ElapsedMilliseconds, shown);
            }

            var content = Shape(response, exec);
            return new HttpToolResult(false, content, "Succeeded", response.StatusCode, response.Body.Length, null, null, sw.ElapsedMilliseconds, shown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var err = McpErrors.Sanitize(e);
            var timedOut = err.Code == OutboundErrors.Timeout || e is OperationCanceledException;
            if (timedOut) err = new SanitizedError("timeout", "The server did not respond in time.", true);
            return new HttpToolResult(true, "The tool call failed: " + err.Message, timedOut ? "TimedOut" : "Failed", err.HttpStatus, 0, err.Code, err.Message, sw.ElapsedMilliseconds, shown);
        }
    }

    public async Task<(bool Ok, string Message)> PingAsync(ConnectionAccess access, CancellationToken ct)
    {
        try
        {
            var r = await outbound.Create().SendAsync(new OutboundRequest
            {
                Url = access.BaseUrl, Headers = new Dictionary<string, string>(access.StaticHeaders),
                Credential = access.Credential, CredentialBaseUrl = access.BaseUrl,
            }, new OutboundCallOptions { MaxBytes = 64 * 1024, Timeout = TimeSpan.FromSeconds(access.TimeoutSeconds), MaxRedirects = 0 }, ct);
            if (r.StatusCode is 401 or 403) return (false, "The server rejected the credentials.");
            if (r.StatusCode >= 500) return (false, $"The server responded with HTTP {r.StatusCode}.");
            return (true, $"Reachable (HTTP {r.StatusCode}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return (false, McpErrors.Sanitize(e).Message); }
    }

    private static HttpToolResult Fail(string status, string code, string message, int? http, Stopwatch sw, string url) =>
        new(true, "The tool call failed: " + message, status, http, 0, code, message, sw.ElapsedMilliseconds, url);

    /// <summary>Path and origin only; the query string is never shown or stored.</summary>
    public static string Redacted(Uri url) => url.GetLeftPart(UriPartial.Path) + (string.IsNullOrEmpty(url.Query) ? "" : "?(redacted)");

    public static (Uri Url, byte[]? Body) Build(ConnectionAccess access, HttpToolExecution exec, JsonObject args)
    {
        var path = HttpToolRules.Placeholder().Replace(exec.UrlTemplate, m =>
        {
            var name = m.Groups[1].Value;
            if (!args.TryGetPropertyValue(name, out var node) || node is null) throw new InvalidToolInputException($"Missing path value '{name}'.");
            return EncodeSegment(name, Scalar(name, node));
        });

        var basePath = access.BaseUrl.AbsolutePath.TrimEnd('/');
        var query = new List<string>();
        foreach (var (k, v) in exec.StaticQuery) query.Add(Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v));
        JsonObject? body = null;
        foreach (var p in exec.Parameters)
        {
            if (!args.TryGetPropertyValue(p.Name, out var node) || node is null)
            {
                if (p.Required) throw new InvalidToolInputException($"Missing required input '{p.Name}'.");
                continue;
            }
            switch (p.In)
            {
                case "query":
                    if (node is JsonArray arr) foreach (var item in arr) query.Add(Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(Scalar(p.Name, item)));
                    else query.Add(Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(Scalar(p.Name, node)));
                    break;
                case "body":
                    body ??= new JsonObject();
                    SetPath(body, p.BodyPath ?? p.Name, node.DeepClone());
                    break;
            }
        }

        var uri = new Uri(access.BaseUrl.GetLeftPart(UriPartial.Authority) + basePath + path + (query.Count > 0 ? "?" + string.Join("&", query) : ""));
        if (!OutboundUrlValidator.SameOrigin(uri, access.BaseUrl) || !uri.AbsolutePath.StartsWith(basePath + "/", StringComparison.Ordinal) && uri.AbsolutePath != basePath)
            throw new InvalidToolInputException("The request would leave the connection's address.");

        byte[]? bytes = null;
        if (exec.BodyMode == "json" && exec.Method is not ("GET" or "DELETE"))
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(body ?? new JsonObject());
            if (bytes.Length > 256 * 1024) throw new InvalidToolInputException("The request body is too large.");
        }
        return (uri, bytes);
    }

    private static string Scalar(string name, JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
            if (v.TryGetValue<decimal>(out var m)) return m.ToString(CultureInfo.InvariantCulture);
        }
        throw new InvalidToolInputException($"Input '{name}' must be a string, number or boolean.");
    }

    private static string EncodeSegment(string name, string value)
    {
        if (value.Length is 0 or > 200 || value is "." || value.Contains("..") || value.Any(c => c is '/' or '\\' or '?' or '#' || char.IsControl(c)))
            throw new InvalidToolInputException($"Input '{name}' contains characters that are not allowed in a URL path.");
        return Uri.EscapeDataString(value);
    }

    private static void SetPath(JsonObject root, string path, JsonNode value)
    {
        var parts = path.Split('.');
        var cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) { next = new JsonObject(); cur[parts[i]] = next; }
            cur = next;
        }
        cur[parts[^1]] = value;
    }

    private static string Shape(OutboundResponse response, HttpToolExecution exec)
    {
        var text = Encoding.UTF8.GetString(response.Body);
        response.Headers.TryGetValue("Content-Type", out var ct);
        JsonNode? data = null;
        var looksJson = (ct?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) || text.AsSpan().TrimStart() is ['{', ..] or ['[', ..];
        if (looksJson)
        {
            try { data = JsonNode.Parse(text); } catch (JsonException) { data = null; }
        }
        if (data != null && exec.Response.Select.Count > 0) data = HttpProjection.Apply(data, exec.Response.Select);
        data ??= JsonValue.Create(text);
        if (!exec.Response.IncludeStatusCode) return data.ToJsonString();
        return new JsonObject { ["status"] = response.StatusCode, ["data"] = data }.ToJsonString();
    }
}

/// <summary>Keeps only the selected dotted paths (with optional <c>[*]</c>) of a JSON document. Arrays are mapped element-wise.</summary>
public static class HttpProjection
{
    public static JsonNode? Apply(JsonNode source, IReadOnlyList<string> selects)
    {
        var paths = selects.Select(Parse).Where(p => p.Count > 0).ToList();
        return Project(source, paths) ?? new JsonObject();
    }

    private static List<string> Parse(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(seg => seg.EndsWith("[*]", StringComparison.Ordinal) ? seg[..^3] : seg).Where(seg => seg.Length > 0).ToList();

    private static JsonNode? Project(JsonNode node, List<List<string>> paths)
    {
        switch (node)
        {
            case JsonArray arr:
                var items = new JsonArray();
                foreach (var el in arr)
                    if (el is JsonObject or JsonArray && Project(el, paths) is { } p) items.Add(p);
                return items;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var group in paths.GroupBy(p => p[0]))
                {
                    if (!obj.TryGetPropertyValue(group.Key, out var child) || child == null) continue;
                    var rest = group.Where(p => p.Count > 1).Select(p => p.Skip(1).ToList()).ToList();
                    if (group.Any(p => p.Count == 1)) { result[group.Key] = child.DeepClone(); continue; }
                    if (child is JsonObject or JsonArray && Project(child, rest) is { } projected && projected is not JsonObject { Count: 0 }) result[group.Key] = projected;
                }
                return result;
            default:
                return null;
        }
    }
}
