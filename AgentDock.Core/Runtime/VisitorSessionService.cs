using System.Security.Cryptography;
using System.Text;
using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Runtime;

public sealed record IssuedSession(string Token, VisitorSession Session);

public interface IVisitorSessionService
{
    Task<IssuedSession> CreateAsync(Agent agent, string mode, string hostOrigin, string? ip, CancellationToken ct = default);
    Task<VisitorSession?> AuthenticateAsync(string token, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, CancellationToken ct = default);
}

/// <summary>Opaque 256-bit bearer tokens; only the SHA-256 hash is stored.</summary>
public sealed class VisitorSessionService(AgentDockDbContext db, TimeProvider time) : IVisitorSessionService
{
    public const string TokenPrefix = "adv_";
    public static readonly TimeSpan PublicLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(8);

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public async Task<IssuedSession> CreateAsync(Agent agent, string mode, string hostOrigin, string? ip, CancellationToken ct = default)
    {
        var token = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow().UtcDateTime;
        var s = new VisitorSession
        {
            AccountId = agent.AccountId, AgentId = agent.Id, TokenHash = Hash(token), Mode = mode,
            HostOrigin = hostOrigin.Length > 300 ? hostOrigin[..300] : hostOrigin,
            IpHash = Hash("ip:" + (ip ?? "")), CreatedUtc = now, LastSeenUtc = now,
            ExpiresUtc = now + (mode == "Preview" ? PreviewLifetime : PublicLifetime),
        };
        db.VisitorSessions.Add(s);
        await db.SaveChangesAsync(ct);
        return new IssuedSession(token, s);
    }

    public async Task<VisitorSession?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 100) return null;
        var hash = Hash(token);
        var s = await db.VisitorSessions.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (s == null || s.RevokedUtc != null || s.ExpiresUtc <= now) return null;
        if (s.Mode == "Public" && now - s.LastSeenUtc > TimeSpan.FromMinutes(5))
        {
            s.LastSeenUtc = now;
            s.ExpiresUtc = now + PublicLifetime;
            await db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct = default) =>
        await db.VisitorSessions.Where(x => x.Id == sessionId && x.RevokedUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.RevokedUtc, time.GetUtcNow().UtcDateTime), ct);

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
