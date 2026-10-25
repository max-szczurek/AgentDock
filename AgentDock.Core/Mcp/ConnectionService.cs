using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using AgentDock.Core.HttpTools;
using AgentDock.Core.Outbound;
using AgentDock.Core.Security;
using AgentDock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Mcp;

public sealed class ConnectionInput
{
    public string Name { get; set; } = "";
    /// <summary>"Mcp" or "Http". Fixed at creation.</summary>
    public string Kind { get; set; } = "Mcp";
    public string BaseUrl { get; set; } = "";
    public string AuthType { get; set; } = "None";
    /// <summary>Write-only. Null/blank on update keeps the stored token.</summary>
    public string? BearerToken { get; set; }
    public Dictionary<string, string> StaticHeaders { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record ConnectionSaveResult(bool Success, IReadOnlyList<string> Errors, Connection? Connection = null);
public sealed record TestResult(bool Ok, string Message, int ToolCount);
public sealed record ConnectionSummary(Connection Connection, string? SecretHint, int ToolCount, int UsedByAgents);
public sealed record ConnectionDeleteResult(bool Found, bool Deleted, IReadOnlyList<string> BlockedBy, IReadOnlyList<string>? HttpTools = null);

public interface IConnectionService
{
    Task<IReadOnlyList<ConnectionSummary>> ListAsync(Guid accountId, CancellationToken ct = default);
    Task<Connection?> GetAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<ConnectionInput?> GetInputAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<ConnectionSaveResult> CreateAsync(Guid accountId, ConnectionInput input, CancellationToken ct = default);
    Task<ConnectionSaveResult> UpdateAsync(Guid accountId, Guid id, ConnectionInput input, CancellationToken ct = default);
    Task<TestResult?> TestAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<(TestResult? Result, IReadOnlyList<McpToolSnapshot> Tools)> DiscoverAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<McpToolSnapshot>> SnapshotsAsync(Guid accountId, Guid id, CancellationToken ct = default);
    Task<ConnectionDeleteResult> DeleteAsync(Guid accountId, Guid id, bool removeFromAll, CancellationToken ct = default);
    Task<ConnectionAccess?> ResolveAccessAsync(Guid accountId, Guid id, CancellationToken ct = default);
}

public static partial class ConnectionRules
{
    public static bool TryValidateHeader(string name, string value, out string? error)
    {
        error = null;
        if (!HeaderName().IsMatch(name)) { error = $"Header '{name}' is not a valid name (letters, digits and '-', max 100)."; return false; }
        if (!name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)) { error = $"Only X-* headers are allowed ('{name}')."; return false; }
        var lower = name.ToLowerInvariant();
        if (Credentialish.Any(lower.Contains)) { error = $"Header '{name}' looks like a credential. Use the bearer token field instead."; return false; }
        if (value.Length > 500 || value.Any(c => c is '\r' or '\n' or '\0')) { error = $"Header '{name}' has an invalid value."; return false; }
        return true;
    }

    private static readonly string[] Credentialish = ["auth", "token", "secret", "key", "password", "cookie", "credential", "session"];

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex HeaderName();

    /// <summary>Hash over name + description + canonical (key-sorted) schema, so any change the model could see changes the hash.</summary>
    public static byte[] SchemaHash(string name, string description, string schemaJson)
    {
        var canonical = Canonical(JsonNode.Parse(schemaJson));
        return SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + description + "\n" + canonical));
    }

    private static string Canonical(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => n.ToJsonString(),
    };
}

public sealed class ConnectionService(AgentDockDbContext db, ISecretStore secrets, IMcpGateway gateway, IHttpToolRunner httpRunner, OutboundOptions outbound, TimeProvider time) : IConnectionService
{
    public const int MaxConnections = 50;

    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        var rows = await db.Connections.AsNoTracking().Where(c => c.AccountId == accountId && c.DeletedUtc == null).OrderBy(c => c.Name)
            .Select(c => new
            {
                Conn = c,
                Hint = db.Secrets.Where(s => s.Id == c.SecretId).Select(s => s.Hint).FirstOrDefault(),
                Tools = c.Kind == "Http" ? db.HttpTools.Count(t => t.ConnectionId == c.Id && t.DeletedUtc == null) : db.McpToolSnapshots.Count(t => t.ConnectionId == c.Id && t.IsPresent),
                Used = db.AgentConnections.Count(a => a.ConnectionId == c.Id && db.Agents.Any(g => g.Id == a.AgentId && g.DeletedUtc == null)),
            }).ToListAsync(ct);
        return rows.Select(r => new ConnectionSummary(r.Conn, r.Hint, r.Tools, r.Used)).ToList();
    }

    public Task<Connection?> GetAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
        db.Connections.FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId && c.DeletedUtc == null, ct);

    public async Task<ConnectionInput?> GetInputAsync(Guid accountId, Guid id, CancellationToken ct = default)
    {
        var c = await GetAsync(accountId, id, ct);
        if (c == null) return null;
        return new ConnectionInput
        {
            Name = c.Name, Kind = c.Kind, BaseUrl = c.BaseUrl, AuthType = c.AuthType, TimeoutSeconds = c.TimeoutSeconds,
            StaticHeaders = string.IsNullOrEmpty(c.StaticHeadersJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(c.StaticHeadersJson) ?? [],
        };
    }

    public async Task<ConnectionSaveResult> CreateAsync(Guid accountId, ConnectionInput input, CancellationToken ct = default)
    {
        var errors = Validate(input, requireToken: true);
        if (input.Kind is not ("Mcp" or "Http")) errors.Add("Choose a valid connection kind.");
        if (await db.Connections.CountAsync(c => c.AccountId == accountId && c.DeletedUtc == null, ct) >= MaxConnections) errors.Add($"Limit of {MaxConnections} connections reached.");
        if (await NameTakenAsync(accountId, input.Name.Trim(), null, ct)) errors.Add("A connection with this name already exists.");
        if (errors.Count > 0) return new(false, errors);

        var c = new Connection { AccountId = accountId, Kind = input.Kind == "Http" ? "Http" : "Mcp" };
        Apply(c, input);
        if (input.AuthType == "Bearer") c.SecretId = await secrets.CreateAsync(accountId, SecretPurpose.ConnectionBearer, input.BearerToken!.Trim(), ct);
        db.Connections.Add(c);
        await db.SaveChangesAsync(ct);
        return new(true, [], c);
    }

    public async Task<ConnectionSaveResult> UpdateAsync(Guid accountId, Guid id, ConnectionInput input, CancellationToken ct = default)
    {
        var c = await GetAsync(accountId, id, ct);
        if (c == null) return new(false, ["Connection not found."]);
        var hadToken = c.SecretId != null;
        var errors = Validate(input, requireToken: input.AuthType == "Bearer" && !hadToken);
        if (await NameTakenAsync(accountId, input.Name.Trim(), id, ct)) errors.Add("A connection with this name already exists.");
        if (errors.Count > 0) return new(false, errors, c);

        var urlChanged = !string.Equals(c.BaseUrl, input.BaseUrl.Trim(), StringComparison.Ordinal);
        Apply(c, input);
        if (urlChanged) { c.Status = "Untested"; c.LastError = null; }

        var oldSecret = c.SecretId;
        if (input.AuthType != "Bearer")
        {
            c.SecretId = null;
            c.AuthType = "None";
        }
        else if (!string.IsNullOrWhiteSpace(input.BearerToken))
        {
            c.SecretId = await secrets.CreateAsync(accountId, SecretPurpose.ConnectionBearer, input.BearerToken.Trim(), ct);
        }
        await db.SaveChangesAsync(ct);
        if (oldSecret != null && oldSecret != c.SecretId) await secrets.ShredAsync(accountId, oldSecret.Value, ct);
        return new(true, [], c);
    }

    public async Task<TestResult?> TestAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
        (await DiscoverCoreAsync(accountId, id, persistTools: false, ct)).Result;

    public async Task<(TestResult? Result, IReadOnlyList<McpToolSnapshot> Tools)> DiscoverAsync(Guid accountId, Guid id, CancellationToken ct = default)
    {
        var (result, _) = await DiscoverCoreAsync(accountId, id, persistTools: true, ct);
        return (result, result == null ? [] : await SnapshotsAsync(accountId, id, ct));
    }

    public async Task<IReadOnlyList<McpToolSnapshot>> SnapshotsAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
        await db.McpToolSnapshots.AsNoTracking().Where(t => t.AccountId == accountId && t.ConnectionId == id && t.IsPresent).OrderBy(t => t.ToolName).ToListAsync(ct);

    public async Task<ConnectionDeleteResult> DeleteAsync(Guid accountId, Guid id, bool removeFromAll, CancellationToken ct = default)
    {
        var c = await GetAsync(accountId, id, ct);
        if (c == null) return new(false, false, []);
        var httpTools = await db.HttpTools.Where(t => t.AccountId == accountId && t.ConnectionId == id && t.DeletedUtc == null).OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
        if (httpTools.Count > 0) return new(true, false, [], httpTools);
        var links = await db.AgentConnections.Where(a => a.AccountId == accountId && a.ConnectionId == id).ToListAsync(ct);
        if (links.Count > 0)
        {
            var agentIds = links.Select(l => l.AgentId).ToList();
            var names = await db.Agents.Where(a => agentIds.Contains(a.Id) && a.DeletedUtc == null).OrderBy(a => a.Name).Select(a => a.Name).ToListAsync(ct);
            if (names.Count > 0 && !removeFromAll) return new(true, false, names);
            db.AgentConnections.RemoveRange(links);
        }
        c.DeletedUtc = time.GetUtcNow().UtcDateTime;
        var secretId = c.SecretId;
        c.SecretId = null;
        await db.SaveChangesAsync(ct);
        if (secretId != null) await secrets.ShredAsync(accountId, secretId.Value, ct);
        return new(true, true, []);
    }

    public async Task<ConnectionAccess?> ResolveAccessAsync(Guid accountId, Guid id, CancellationToken ct = default)
    {
        var c = await GetAsync(accountId, id, ct);
        if (c == null) return null;
        OutboundCredential? credential = null;
        if (c.AuthType == "Bearer" && c.SecretId != null)
            credential = new("Authorization", "Bearer " + await secrets.RevealForUseAsync(accountId, c.SecretId.Value, SecretPurpose.ConnectionBearer, ct));
        var headers = string.IsNullOrEmpty(c.StaticHeadersJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(c.StaticHeadersJson) ?? [];
        return new ConnectionAccess(c.Id, new Uri(c.BaseUrl), credential, headers, Math.Clamp(c.TimeoutSeconds, 5, 60));
    }

    private async Task<(TestResult? Result, int Count)> DiscoverCoreAsync(Guid accountId, Guid id, bool persistTools, CancellationToken ct)
    {
        var c = await GetAsync(accountId, id, ct);
        if (c == null) return (null, 0);
        var now = time.GetUtcNow().UtcDateTime;
        if (c.Kind == "Http")
        {
            if (persistTools) return (new TestResult(false, "Tool discovery applies to MCP connections only.", 0), 0);
            var httpAccess = (await ResolveAccessAsync(accountId, id, ct))!;
            var (ok, message) = await httpRunner.PingAsync(httpAccess, ct);
            c.Status = ok ? "Ok" : "Error"; c.LastError = ok ? null : message; c.LastTestedUtc = now;
            await db.SaveChangesAsync(CancellationToken.None);
            return (new TestResult(ok, message, 0), 0);
        }
        try
        {
            var access = (await ResolveAccessAsync(accountId, id, ct))!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(access.TimeoutSeconds));
            var tools = await gateway.ListToolsAsync(access, timeout.Token);
            if (persistTools) await UpsertSnapshotsAsync(c, tools, now, ct);
            c.Status = "Ok"; c.LastError = null; c.LastTestedUtc = now;
            await db.SaveChangesAsync(ct);
            return (new TestResult(true, $"Connected. {tools.Count} tool{(tools.Count == 1 ? "" : "s")} available.", tools.Count), tools.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var err = McpErrors.Sanitize(e);
            c.Status = "Error"; c.LastError = err.Message; c.LastTestedUtc = now;
            await db.SaveChangesAsync(CancellationToken.None);
            return (new TestResult(false, err.Message, 0), 0);
        }
    }

    private async Task UpsertSnapshotsAsync(Connection c, IReadOnlyList<McpToolInfo> tools, DateTime now, CancellationToken ct)
    {
        var existing = await db.McpToolSnapshots.Where(t => t.ConnectionId == c.Id).ToListAsync(ct);
        var seen = new HashSet<string>();
        foreach (var t in tools.Where(t => t.Name.Length is > 0 and <= 128).Take(500))
        {
            if (!seen.Add(t.Name)) continue;
            var schema = SchemaOrDefault(t.InputSchemaJson);
            var row = existing.FirstOrDefault(e => e.ToolName == t.Name);
            if (row == null) { row = new McpToolSnapshot { AccountId = c.AccountId, ConnectionId = c.Id, ToolName = t.Name }; db.McpToolSnapshots.Add(row); }
            row.Title = t.Title is { Length: > 200 } ? t.Title[..200] : t.Title;
            row.Description = t.Description;
            row.InputSchemaJson = schema;
            row.AnnotationsJson = t.AnnotationsJson;
            row.SchemaHash = ConnectionRules.SchemaHash(t.Name, t.Description, schema);
            row.IsPresent = true;
            row.DiscoveredUtc = now;
        }
        foreach (var gone in existing.Where(e => !seen.Contains(e.ToolName))) gone.IsPresent = false;
    }

    private static string SchemaOrDefault(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject o ? o.ToJsonString() : """{"type":"object"}"""; }
        catch (JsonException) { return """{"type":"object"}"""; }
    }

    private Task<bool> NameTakenAsync(Guid accountId, string name, Guid? exceptId, CancellationToken ct) =>
        db.Connections.AnyAsync(c => c.AccountId == accountId && c.DeletedUtc == null && c.Name == name && c.Id != exceptId, ct);

    private void Apply(Connection c, ConnectionInput i)
    {
        c.Name = i.Name.Trim();
        c.BaseUrl = i.BaseUrl.Trim();
        c.AuthType = i.AuthType == "Bearer" ? "Bearer" : "None";
        c.AuthHeaderName = c.AuthType == "Bearer" ? "Authorization" : null;
        c.TimeoutSeconds = i.TimeoutSeconds;
        var headers = i.StaticHeaders.Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        c.StaticHeadersJson = headers.Count == 0 ? null : JsonSerializer.Serialize(headers);
        c.UpdatedUtc = time.GetUtcNow().UtcDateTime;
    }

    private List<string> Validate(ConnectionInput i, bool requireToken)
    {
        var e = new List<string>();
        var name = (i.Name ?? "").Trim();
        if (name.Length is 0 or > 100) e.Add("Name is required (max 100 characters).");
        if (!Uri.TryCreate((i.BaseUrl ?? "").Trim(), UriKind.Absolute, out var uri)) e.Add("Enter a valid URL.");
        else if (OutboundUrlValidator.Validate(uri, outbound) is { } bad) e.Add(bad.Message);
        if (i.AuthType is not ("None" or "Bearer")) e.Add("Choose a valid authentication type.");
        if (i.AuthType == "Bearer")
        {
            if (requireToken && string.IsNullOrWhiteSpace(i.BearerToken)) e.Add("Enter the bearer token.");
            if (i.BearerToken is { } t && (t.Length > 4096 || t.Any(c => c is '\r' or '\n' or '\0'))) e.Add("The bearer token is invalid.");
        }
        if (i.TimeoutSeconds is < 5 or > 60) e.Add("Timeout must be 5-60 seconds.");
        if (i.StaticHeaders.Count > 10) e.Add("At most 10 static headers.");
        foreach (var (k, v) in i.StaticHeaders.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
            if (!ConnectionRules.TryValidateHeader(k.Trim(), v ?? "", out var he)) e.Add(he!);
        return e;
    }
}
