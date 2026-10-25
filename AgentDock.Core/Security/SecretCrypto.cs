using System.Security.Cryptography;
using System.Text;

namespace AgentDock.Core.Security;

public enum SecretPurpose { AnthropicApiKey, ConnectionBearer, ConnectionHeader, EmbedSigningKey }

/// <summary>Bound into the AES-GCM tag so ciphertext can't be moved between rows, accounts or purposes.</summary>
public readonly record struct SecretAad(Guid AccountId, Guid SecretId, SecretPurpose Purpose)
{
    public byte[] ToBytes() => Encoding.UTF8.GetBytes($"{AccountId}|{SecretId}|{Purpose}");
}

public sealed record ProtectedPayload(byte[] Ciphertext, byte[] Nonce, byte[] Tag, byte[] WrappedDek, string KekId);

/// <summary>Envelope encryption: AES-256-GCM per secret, DEK wrapped by a KEK that lives outside the database.</summary>
public interface ISecretProtector
{
    Task<ProtectedPayload> ProtectAsync(ReadOnlyMemory<byte> plaintext, SecretAad aad, CancellationToken ct = default);
    Task<byte[]> UnprotectAsync(ProtectedPayload payload, SecretAad aad, CancellationToken ct = default);
}

/// <summary>Wraps and unwraps data-encryption keys with a key-encryption key.</summary>
public interface IKeyWrapper
{
    Task<(byte[] WrappedDek, string KekId)> WrapAsync(byte[] dek, CancellationToken ct);
    Task<byte[]> UnwrapAsync(byte[] wrappedDek, string kekId, CancellationToken ct);
}

public class SecretUnprotectException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EnvelopeSecretProtector(IKeyWrapper wrapper, TimeProvider? time = null) : ISecretProtector
{
    private static readonly TimeSpan DekCacheTtl = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, (byte[] Dek, DateTimeOffset Expires)> _cache = new();

    public async Task<ProtectedPayload> ProtectAsync(ReadOnlyMemory<byte> plaintext, SecretAad aad, CancellationToken ct = default)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(dek, 16))
            gcm.Encrypt(nonce, plaintext.Span, cipher, tag, aad.ToBytes());

        var (wrapped, kekId) = await wrapper.WrapAsync(dek, ct);
        CryptographicOperations.ZeroMemory(dek);
        return new ProtectedPayload(cipher, nonce, tag, wrapped, kekId);
    }

    public async Task<byte[]> UnprotectAsync(ProtectedPayload p, SecretAad aad, CancellationToken ct = default)
    {
        byte[] dek;
        try { dek = await GetDekAsync(p, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new SecretUnprotectException("The key that protects this secret is unavailable.", e);
        }

        try
        {
            var plain = new byte[p.Ciphertext.Length];
            using var gcm = new AesGcm(dek, 16);
            gcm.Decrypt(p.Nonce, p.Ciphertext, p.Tag, plain, aad.ToBytes());
            return plain;
        }
        catch (CryptographicException e)
        {
            throw new SecretUnprotectException("The stored secret failed authentication.", e);
        }
    }

    private async Task<byte[]> GetDekAsync(ProtectedPayload p, CancellationToken ct)
    {
        var cacheKey = p.KekId + "|" + Convert.ToBase64String(SHA256.HashData(p.WrappedDek));
        var now = _time.GetUtcNow();
        lock (_cache)
            if (_cache.TryGetValue(cacheKey, out var hit) && hit.Expires > now) return hit.Dek;

        var dek = await wrapper.UnwrapAsync(p.WrappedDek, p.KekId, ct);
        lock (_cache)
        {
            foreach (var k in _cache.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _cache.Remove(k);
            _cache[cacheKey] = (dek, now + DekCacheTtl);
        }
        return dek;
    }
}

/// <summary>Unit tests only: an in-memory KEK. Never registered by the app.</summary>
public sealed class LocalKeyWrapper : IKeyWrapper
{
    private readonly byte[] _kek = RandomNumberGenerator.GetBytes(32);
    public const string Id = "local:v1";

    public Task<(byte[], string)> WrapAsync(byte[] dek, CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct2 = new byte[dek.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_kek, 16);
        gcm.Encrypt(nonce, dek, ct2, tag);
        return Task.FromResult((nonce.Concat(tag).Concat(ct2).ToArray(), Id));
    }

    public Task<byte[]> UnwrapAsync(byte[] wrapped, string kekId, CancellationToken ct)
    {
        if (kekId != Id) throw new CryptographicException("Unknown KEK.");
        var plain = new byte[wrapped.Length - 28];
        using var gcm = new AesGcm(_kek, 16);
        gcm.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain);
        return Task.FromResult(plain);
    }
}
