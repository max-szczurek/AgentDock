using System.Collections.Concurrent;
using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace AgentDock.Core.Security;

/// <summary>
/// Wraps DEKs with an Azure Key Vault RSA key (RSA-OAEP-256). The stored KekId is the key *version* URI,
/// so rotating the key never strands existing secrets.
/// </summary>
public sealed class KeyVaultKeyWrapper(Uri keyId, TokenCredential credential) : IKeyWrapper
{
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new();

    public async Task<(byte[], string)> WrapAsync(byte[] dek, CancellationToken ct)
    {
        var client = _clients.GetOrAdd(keyId.ToString(), k => new CryptographyClient(new Uri(k), credential));
        var result = await client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, dek, ct);
        return (result.EncryptedKey, result.KeyId);
    }

    public async Task<byte[]> UnwrapAsync(byte[] wrappedDek, string kekId, CancellationToken ct)
    {
        var client = _clients.GetOrAdd(kekId, k => new CryptographyClient(new Uri(k), credential));
        var result = await client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrappedDek, ct);
        return result.Key;
    }
}
