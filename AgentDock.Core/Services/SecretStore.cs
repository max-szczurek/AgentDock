using AgentDock.Core.Data;
using AgentDock.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Services;

/// <summary>The only component that sees plaintext secrets; plaintext never leaves Core services.</summary>
public interface ISecretStore
{
    Task<Guid> CreateAsync(Guid accountId, SecretPurpose purpose, string plaintext, CancellationToken ct = default);
    Task<string> RevealForUseAsync(Guid accountId, Guid secretId, SecretPurpose purpose, CancellationToken ct = default);
    Task ShredAsync(Guid accountId, Guid secretId, CancellationToken ct = default);
}

public sealed class SecretStore(AgentDockDbContext db, ISecretProtector protector) : ISecretStore
{
    public static string MakeHint(string plaintext) =>
        plaintext.Length <= 12 ? "…" : $"{plaintext[..7]}…{plaintext[^4..]}";

    public async Task<Guid> CreateAsync(Guid accountId, SecretPurpose purpose, string plaintext, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var payload = await protector.ProtectAsync(System.Text.Encoding.UTF8.GetBytes(plaintext), new SecretAad(accountId, id, purpose), ct);
        db.Secrets.Add(new Secret
        {
            Id = id, AccountId = accountId, Purpose = purpose.ToString(),
            Ciphertext = payload.Ciphertext, Nonce = payload.Nonce, Tag = payload.Tag,
            WrappedDek = payload.WrappedDek, KekId = payload.KekId, Hint = MakeHint(plaintext),
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    internal async Task<string> RevealCoreAsync(Guid accountId, Guid secretId, SecretPurpose purpose, CancellationToken ct)
    {
        var s = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == secretId && x.AccountId == accountId, ct)
            ?? throw new SecretUnprotectException("Secret not found.");
        if (s.ShreddedUtc != null) throw new SecretUnprotectException("Secret was removed.");
        if (!string.Equals(s.Purpose, purpose.ToString(), StringComparison.Ordinal))
            throw new SecretUnprotectException("Secret purpose mismatch.");
        var plain = await protector.UnprotectAsync(
            new ProtectedPayload(s.Ciphertext, s.Nonce, s.Tag, s.WrappedDek, s.KekId), new SecretAad(accountId, secretId, purpose), ct);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    public Task<string> RevealForUseAsync(Guid accountId, Guid secretId, SecretPurpose purpose, CancellationToken ct = default) =>
        RevealCoreAsync(accountId, secretId, purpose, ct);

    public async Task ShredAsync(Guid accountId, Guid secretId, CancellationToken ct = default)
    {
        var s = await db.Secrets.FirstOrDefaultAsync(x => x.Id == secretId && x.AccountId == accountId, ct);
        if (s == null) return;
        s.Ciphertext = [];
        s.Nonce = new byte[12];
        s.Tag = new byte[16];
        s.WrappedDek = [];
        s.Hint = null;
        s.ShreddedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
