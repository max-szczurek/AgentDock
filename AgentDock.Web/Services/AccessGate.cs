using System.Security.Cryptography;
using System.Text;

namespace AgentDock.Web.Services;

/// <summary>
/// Pre-launch access controls (plan section 0.4): the sign-in allowlist and the dev sign-in switch.
/// Values are read from configuration on each call so a settings change takes effect without code changes.
/// </summary>
public class AccessGate(IConfiguration config, IWebHostEnvironment env)
{
    public const string DevLoginAuthMethod = "DevLogin";
    public const int MinSecretLength = 32;

    public static HashSet<string> ParseList(string? csv) =>
        (csv ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()).ToHashSet();

    public bool IsEmailAllowed(string? email)
    {
        var allowed = ParseList(config["AGENTDOCK_ALLOWED_EMAILS"]);
        if (allowed.Count == 0) return env.IsDevelopment();
        return !string.IsNullOrWhiteSpace(email) && allowed.Contains(email.Trim().ToLowerInvariant());
    }

    public bool DevLoginEnabled
    {
        get
        {
            if (!(env.IsDevelopment() || env.IsStaging())) return false;
            if (string.Equals(config["AgentDock:DevLogin:Disabled"], "true", StringComparison.OrdinalIgnoreCase)) return false;
            return (config["AGENTDOCK_DEV_LOGIN_SECRET"] ?? "").Length >= MinSecretLength;
        }
    }

    public IReadOnlyList<string> DevLoginEmails =>
        ParseList(config["AGENTDOCK_DEV_LOGIN_EMAILS"]).OrderBy(e => e).ToList();

    public bool IsDevLoginEmail(string email) => DevLoginEmails.Contains(email.Trim().ToLowerInvariant());

    public bool SecretMatches(string? candidate)
    {
        var secret = config["AGENTDOCK_DEV_LOGIN_SECRET"] ?? "";
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? ""));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
