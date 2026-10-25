using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentDock.Core.Services;

public static class OriginNormalizer
{
    /// <summary>
    /// Accepts only scheme://host[:port]. https everywhere; http only for localhost/loopback.
    /// No wildcards, userinfo, path, query or fragment. Lowercases and drops default ports.
    /// </summary>
    public static bool TryNormalize(string? input, out string origin, out string? error)
    {
        origin = "";
        error = null;
        var raw = (input ?? "").Trim();
        if (raw.Length == 0) { error = "Origin is empty."; return false; }
        if (raw.Length > 300) { error = "Origin is too long."; return false; }
        if (raw.Contains('*')) { error = "Wildcards are not allowed."; return false; }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) { error = $"'{raw}' is not a valid origin (use https://example.com)."; return false; }
        if (uri.Scheme is not ("http" or "https")) { error = "Only http and https origins are allowed."; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "Origins cannot contain credentials."; return false; }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) { error = "Origins cannot have a query or fragment."; return false; }
        if (uri.AbsolutePath != "/" ) { error = "Origins cannot have a path (use scheme://host[:port])."; return false; }

        var host = uri.IdnHost.ToLowerInvariant();
        var loopback = host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        if (uri.Scheme == "http" && !loopback) { error = "Use https (http is only allowed for localhost)."; return false; }
        if (!loopback && !host.Contains('.')) { error = "Host must be a fully qualified domain name."; return false; }

        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        origin = $"{uri.Scheme}://{host}{port}";
        return true;
    }
}

public sealed record AppearanceSettings
{
    public string Title { get; init; } = "Ask us";
    public string PrimaryColor { get; init; } = "#4F46E5";
    public string TextOnPrimary { get; init; } = "#FFFFFF";
    public string PanelBackground { get; init; } = "#FFFFFF";
    public string IconPreset { get; init; } = "chat";
    public string Position { get; init; } = "bottom-right";
    public int PanelWidth { get; init; } = 380;
    public int PanelHeight { get; init; } = 600;
    public string WelcomeMessage { get; init; } = "Hi! How can I help?";
    public string Placeholder { get; init; } = "Type a message…";

    public static readonly string[] IconPresets = ["chat", "sparkle", "help"];
    public static readonly string[] Positions = ["bottom-right", "bottom-left"];
    private static readonly Regex Color = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 60) e.Add("Title must be 1-60 characters.");
        foreach (var (n, v) in new[] { ("Primary color", PrimaryColor), ("Text color", TextOnPrimary), ("Panel background", PanelBackground) })
            if (!Color.IsMatch(v ?? "")) e.Add($"{n} must look like #RRGGBB.");
        if (!IconPresets.Contains(IconPreset)) e.Add("Icon must be one of: " + string.Join(", ", IconPresets) + ".");
        if (!Positions.Contains(Position)) e.Add("Position must be bottom-right or bottom-left.");
        if (PanelWidth is < 320 or > 520) e.Add("Panel width must be 320-520.");
        if (PanelHeight is < 400 or > 800) e.Add("Panel height must be 400-800.");
        if ((WelcomeMessage ?? "").Length > 500) e.Add("Welcome message is limited to 500 characters.");
        if ((Placeholder ?? "").Length > 100) e.Add("Placeholder is limited to 100 characters.");
        return e;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AppearanceSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppearanceSettings();
        try { return JsonSerializer.Deserialize<AppearanceSettings>(json, Json) ?? new AppearanceSettings(); }
        catch (JsonException) { return new AppearanceSettings(); }
    }
}

/// <summary>Per-agent overrides of the usage defaults, bounded by hard caps (plan section 7.6).</summary>
public sealed record AgentLimits
{
    public int TurnsPerSessionPerHour { get; init; } = 30;
    public int TurnsPerIpPerHour { get; init; } = 60;
    public int TurnsPerDay { get; init; } = 500;
    public int MaxMessageChars { get; init; } = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (TurnsPerSessionPerHour is < 1 or > 120) e.Add("Turns per session per hour must be 1-120.");
        if (TurnsPerIpPerHour is < 1 or > 300) e.Add("Turns per IP per hour must be 1-300.");
        if (TurnsPerDay is < 1 or > 5000) e.Add("Turns per day must be 1-5000.");
        if (MaxMessageChars is < 100 or > 8000) e.Add("Message length must be 100-8000 characters.");
        return e;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AgentLimits FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AgentLimits();
        try { return JsonSerializer.Deserialize<AgentLimits>(json, Json) ?? new AgentLimits(); }
        catch (JsonException) { return new AgentLimits(); }
    }
}
