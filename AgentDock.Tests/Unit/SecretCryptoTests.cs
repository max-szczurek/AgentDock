using System.Text;
using AgentDock.Core.Security;
using AgentDock.Core.Services;

namespace AgentDock.Tests.Unit;

[TestClass]
public class SecretCryptoTests
{
    private static readonly SecretAad Aad = new(Guid.NewGuid(), Guid.NewGuid(), SecretPurpose.AnthropicApiKey);

    private static EnvelopeSecretProtector New() => new(new LocalKeyWrapper());

    [TestMethod]
    public async Task RoundTrip_ReturnsPlaintext_AndCiphertextHasNoPlaintext()
    {
        var p = New();
        var plain = "sk-ant-api03-supersecretvalue1234567890";
        var payload = await p.ProtectAsync(Encoding.UTF8.GetBytes(plain), Aad);
        CollectionAssert.AreNotEqual(Encoding.UTF8.GetBytes(plain), payload.Ciphertext);
        Assert.IsFalse(Encoding.UTF8.GetString(payload.Ciphertext).Contains("supersecret"));
        Assert.AreEqual(plain, Encoding.UTF8.GetString(await p.UnprotectAsync(payload, Aad)));
    }

    [TestMethod]
    public async Task EachSecret_GetsItsOwnDekAndNonce()
    {
        var p = New();
        var a = await p.ProtectAsync("same"u8.ToArray(), Aad);
        var b = await p.ProtectAsync("same"u8.ToArray(), Aad);
        CollectionAssert.AreNotEqual(a.Ciphertext, b.Ciphertext);
        CollectionAssert.AreNotEqual(a.WrappedDek, b.WrappedDek);
    }

    [TestMethod]
    public async Task Aad_TamperingAnyComponent_Fails()
    {
        var p = New();
        var payload = await p.ProtectAsync("secret-value-123456789"u8.ToArray(), Aad);
        var wrongs = new[]
        {
            Aad with { AccountId = Guid.NewGuid() },
            Aad with { SecretId = Guid.NewGuid() },
            Aad with { Purpose = SecretPurpose.ConnectionBearer },
        };
        foreach (var w in wrongs)
            await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => p.UnprotectAsync(payload, w));
    }

    [TestMethod]
    public async Task CiphertextOrTagTampering_Fails()
    {
        var p = New();
        var payload = await p.ProtectAsync("secret-value-123456789"u8.ToArray(), Aad);
        var bad = payload with { Ciphertext = (byte[])payload.Ciphertext.Clone() };
        bad.Ciphertext[0] ^= 1;
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => p.UnprotectAsync(bad, Aad));
        var badTag = payload with { Tag = (byte[])payload.Tag.Clone() };
        badTag.Tag[0] ^= 1;
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => p.UnprotectAsync(badTag, Aad));
    }

    [TestMethod]
    public async Task WrongOrMissingKek_ReportsUnavailableNotCrash()
    {
        var payload = await New().ProtectAsync("secret-value-123456789"u8.ToArray(), Aad);
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => New().UnprotectAsync(payload, Aad));
        await Assert.ThrowsExactlyAsync<SecretUnprotectException>(() => New().UnprotectAsync(payload with { KekId = "other" }, Aad));
    }

    [TestMethod]
    public async Task UnwrappedDek_IsCachedForFiveMinutes()
    {
        var counting = new CountingWrapper();
        var time = new Clock();
        var p = new EnvelopeSecretProtector(counting, time);
        var payload = await p.ProtectAsync("secret-value-123456789"u8.ToArray(), Aad);
        await p.UnprotectAsync(payload, Aad);
        await p.UnprotectAsync(payload, Aad);
        Assert.AreEqual(1, counting.Unwraps);
        time.Now += TimeSpan.FromMinutes(6);
        await p.UnprotectAsync(payload, Aad);
        Assert.AreEqual(2, counting.Unwraps);
    }

    [TestMethod]
    public void Hint_MasksMiddle()
    {
        Assert.AreEqual("sk-ant-…9fQx", SecretStore.MakeHint("sk-ant-api03-abcdefghijklmnop9fQx"));
        Assert.AreEqual("…", SecretStore.MakeHint("short"));
    }

    private sealed class CountingWrapper : IKeyWrapper
    {
        private readonly LocalKeyWrapper _inner = new();
        public int Unwraps;
        public Task<(byte[] WrappedDek, string KekId)> WrapAsync(byte[] dek, CancellationToken ct) => _inner.WrapAsync(dek, ct);
        public Task<byte[]> UnwrapAsync(byte[] w, string k, CancellationToken ct) { Unwraps++; return _inner.UnwrapAsync(w, k, ct); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
