using System.Text.Json;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AgentDock.Core.Services;

public sealed record CredentialView(
    Guid Id, string Status, string? Hint, DateTime? LastVerifiedUtc, string? LastErrorCode, bool NeedsReentry);

public sealed record SaveKeyResult(bool Success, string? Error, CredentialView? Credential);

public sealed record CatalogView(
    bool Available, string? Error, bool FromSnapshot, DateTime? FetchedUtc,
    IReadOnlyDictionary<ModelFamily, ResolvedModel> Families,
    IReadOnlyList<ModelInfoDto>? Models = null);

public interface IAnthropicCredentialService
{
    Task<CredentialView?> GetActiveAsync(Guid accountId, CancellationToken ct = default);
    Task<SaveKeyResult> SaveKeyAsync(Guid accountId, string apiKey, CancellationToken ct = default);
    Task RemoveAsync(Guid accountId, CancellationToken ct = default);
    Task<CatalogView> GetCatalogAsync(Guid accountId, bool forceRefresh, CancellationToken ct = default);
    Task<string?> RevealActiveKeyAsync(Guid accountId, CancellationToken ct = default);
    Task<bool> HasActiveKeyAsync(Guid accountId, CancellationToken ct = default);
    Task MarkInvalidAsync(Guid accountId, string errorCode, CancellationToken ct = default);
}

public sealed class AnthropicCredentialService(
    AgentDockDbContext db,
    SecretStore secrets,
    IAnthropicModelsClient models,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<AnthropicCredentialService> logger) : IAnthropicCredentialService
{
    private const string Provider = "Anthropic";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CredentialView?> GetActiveAsync(Guid accountId, CancellationToken ct = default)
    {
        var c = await ActiveAsync(accountId, ct);
        if (c == null) return null;
        var s = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.SecretId && x.AccountId == accountId, ct);
        var needsReentry = false;
        try { await secrets.RevealCoreAsync(accountId, c.SecretId, SecretPurpose.AnthropicApiKey, ct); }
        catch (SecretUnprotectException e) { needsReentry = true; logger.LogWarning(e, "Credential {Id} cannot be unwrapped", c.Id); }
        return new CredentialView(c.Id, c.Status, s?.Hint, c.LastVerifiedUtc, c.LastErrorCode, needsReentry);
    }

    public async Task<SaveKeyResult> SaveKeyAsync(Guid accountId, string apiKey, CancellationToken ct = default)
    {
        apiKey = (apiKey ?? "").Trim();
        if (apiKey.Length < 20 || apiKey.Any(char.IsWhiteSpace))
            return new SaveKeyResult(false, "That doesn't look like an Anthropic API key.", null);

        var check = await models.ListModelsAsync(apiKey, ct);
        if (check.Status == ModelsFetchStatus.Unauthorized)
            return new SaveKeyResult(false, "Anthropic rejected this key (Invalid). It was not saved.", null);

        var status = check.Status == ModelsFetchStatus.Ok ? "Valid" : "Unverified";
        var now = time.GetUtcNow().UtcDateTime;
        var secretId = await secrets.CreateAsync(accountId, SecretPurpose.AnthropicApiKey, apiKey, ct);
        var cred = new ProviderCredential
        {
            AccountId = accountId, Provider = Provider, SecretId = secretId, Status = status,
            LastVerifiedUtc = check.Status == ModelsFetchStatus.Ok ? now : null, LastErrorCode = check.ErrorCode,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var old = await ActiveAsync(accountId, ct);
        if (old != null)
        {
            old.ReplacedUtc = now;
            await db.SaveChangesAsync(ct);
        }
        db.ProviderCredentials.Add(cred);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (old != null)
        {
            await secrets.ShredAsync(accountId, old.SecretId, ct);
            cache.Remove(CacheKey(old.Id));
        }
        if (check.Status == ModelsFetchStatus.Ok) await StoreCatalogAsync(cred.Id, check.Models, ct);

        return new SaveKeyResult(true, null, await GetActiveAsync(accountId, ct));
    }

    public async Task RemoveAsync(Guid accountId, CancellationToken ct = default)
    {
        var c = await ActiveAsync(accountId, ct);
        if (c == null) return;
        c.ReplacedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await secrets.ShredAsync(accountId, c.SecretId, ct);
        cache.Remove(CacheKey(c.Id));
        var snap = await db.ModelCatalogSnapshots.FindAsync([c.Id], ct);
        if (snap != null) { db.ModelCatalogSnapshots.Remove(snap); await db.SaveChangesAsync(ct); }
    }

    public async Task MarkInvalidAsync(Guid accountId, string errorCode, CancellationToken ct = default)
    {
        var c = await ActiveAsync(accountId, ct);
        if (c == null) return;
        c.Status = "Invalid"; c.LastErrorCode = errorCode;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(c.Id));
    }

    public async Task<bool> HasActiveKeyAsync(Guid accountId, CancellationToken ct = default) =>
        await ActiveAsync(accountId, ct) != null;

    public async Task<string?> RevealActiveKeyAsync(Guid accountId, CancellationToken ct = default)
    {
        var c = await ActiveAsync(accountId, ct);
        if (c == null) return null;
        try { return await secrets.RevealCoreAsync(accountId, c.SecretId, SecretPurpose.AnthropicApiKey, ct); }
        catch (SecretUnprotectException) { return null; }
    }

    public async Task<CatalogView> GetCatalogAsync(Guid accountId, bool forceRefresh, CancellationToken ct = default)
    {
        var c = await ActiveAsync(accountId, ct);
        if (c == null) return new CatalogView(false, "No Anthropic API key saved.", false, null, new Dictionary<ModelFamily, ResolvedModel>());

        if (!forceRefresh && cache.TryGetValue(CacheKey(c.Id), out CatalogView? cached) && cached != null) return cached;

        string? key;
        try { key = await secrets.RevealCoreAsync(accountId, c.SecretId, SecretPurpose.AnthropicApiKey, ct); }
        catch (SecretUnprotectException) { return new CatalogView(false, "The saved key needs to be re-entered.", false, null, new Dictionary<ModelFamily, ResolvedModel>()); }

        var fetch = await models.ListModelsAsync(key, ct);
        var now = time.GetUtcNow().UtcDateTime;

        if (fetch.Status == ModelsFetchStatus.Ok)
        {
            c.Status = "Valid"; c.LastVerifiedUtc = now; c.LastErrorCode = null;
            await db.SaveChangesAsync(ct);
            await StoreCatalogAsync(c.Id, fetch.Models, ct);
            var view = new CatalogView(true, null, false, now, ModelResolver.ResolveFamilies(fetch.Models, time.GetUtcNow()), fetch.Models);
            cache.Set(CacheKey(c.Id), view, CacheTtl);
            return view;
        }

        if (fetch.Status == ModelsFetchStatus.Unauthorized)
        {
            c.Status = "Invalid"; c.LastErrorCode = fetch.ErrorCode;
            await db.SaveChangesAsync(ct);
            cache.Remove(CacheKey(c.Id));
            return new CatalogView(false, "Anthropic rejected the saved key.", false, null, new Dictionary<ModelFamily, ResolvedModel>());
        }

        var snap = await db.ModelCatalogSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.ProviderCredentialId == c.Id, ct);
        if (snap != null && time.GetUtcNow().UtcDateTime - snap.FetchedUtc < SnapshotMaxAge)
        {
            var list = JsonSerializer.Deserialize<List<ModelInfoDto>>(snap.ModelsJson, Json) ?? [];
            logger.LogWarning("Model discovery failed ({Code}); using snapshot from {Fetched}", fetch.ErrorCode, snap.FetchedUtc);
            return new CatalogView(true, null, true, snap.FetchedUtc, ModelResolver.ResolveFamilies(list, time.GetUtcNow()), list);
        }
        return new CatalogView(false, "Could not reach Anthropic to list models.", false, null, new Dictionary<ModelFamily, ResolvedModel>());
    }

    private async Task StoreCatalogAsync(Guid credentialId, IReadOnlyList<ModelInfoDto> list, CancellationToken ct)
    {
        var families = ModelResolver.ResolveFamilies(list, time.GetUtcNow());
        var modelsJson = JsonSerializer.Serialize(list, Json);
        var resolvedJson = JsonSerializer.Serialize(families.ToDictionary(k => k.Key.ToString(), v => v.Value), Json);
        var snap = await db.ModelCatalogSnapshots.FindAsync([credentialId], ct);
        if (snap == null) db.ModelCatalogSnapshots.Add(snap = new ModelCatalogSnapshot { ProviderCredentialId = credentialId });
        snap.FetchedUtc = time.GetUtcNow().UtcDateTime;
        snap.ModelsJson = modelsJson;
        snap.ResolvedJson = resolvedJson;
        await db.SaveChangesAsync(ct);
    }

    private Task<ProviderCredential?> ActiveAsync(Guid accountId, CancellationToken ct) =>
        db.ProviderCredentials.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Provider == Provider && x.ReplacedUtc == null, ct);

    private static string CacheKey(Guid credentialId) => "models:" + credentialId;
}
