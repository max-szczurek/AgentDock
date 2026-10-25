using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Runtime;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Services;

public sealed class AgentInput
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string SystemPrompt { get; set; } = "";
    public ModelFamily DefaultModelFamily { get; set; } = ModelFamily.Sonnet;
    public string? PinnedModelId { get; set; }
    public bool ShowModelSelector { get; set; }
    public List<ModelFamily> AllowedModelFamilies { get; set; } = [ModelFamily.Haiku, ModelFamily.Sonnet, ModelFamily.Opus];
    public int MaxOutputTokens { get; set; } = 4096;
    public AppearanceSettings Appearance { get; set; } = new();
    public AgentLimits Limits { get; set; } = new();
    public List<string> Origins { get; set; } = [];
    public bool Enabled { get; set; }
    public bool WebSearchEnabled { get; set; }
    public int WebSearchMaxUses { get; set; } = 5;
    public string WebSearchDomainMode { get; set; } = "None";
    public List<string> WebSearchDomains { get; set; } = [];
    public byte[]? ConcurrencyStamp { get; set; }
}

public sealed record AgentResult(bool Success, IReadOnlyList<string> Errors, Agent? Agent, bool Conflict = false)
{
    public static AgentResult Fail(params string[] errors) => new(false, errors, null);
}

public interface IAgentService
{
    Task<IReadOnlyList<Agent>> ListAsync(Guid accountId, CancellationToken ct = default);
    Task<Agent?> GetAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task<AgentResult> CreateAsync(Guid accountId, AgentInput input, CancellationToken ct = default);
    Task<AgentResult> UpdateAsync(Guid accountId, Guid agentId, AgentInput input, CancellationToken ct = default);
    Task<Agent?> DuplicateAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task<bool> SetEnabledAsync(Guid accountId, Guid agentId, bool enabled, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
}

public sealed partial class AgentService(AgentDockDbContext db, TimeProvider time) : IAgentService
{
    public const int MaxOrigins = 20;
    public const int MaxSystemPromptChars = 20_000;
    public static AgentInput ToInput(Agent a) => new()
    {
        Name = a.Name, Description = a.Description, SystemPrompt = a.SystemPrompt,
        DefaultModelFamily = Enum.Parse<ModelFamily>(a.DefaultModelFamily, true),
        PinnedModelId = a.PinnedModelId, ShowModelSelector = a.ShowModelSelector,
        AllowedModelFamilies = a.AllowedModelFamilies.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Enum.Parse<ModelFamily>(f, true)).ToList(),
        MaxOutputTokens = a.MaxOutputTokens,
        Appearance = AppearanceSettings.FromJson(a.AppearanceJson),
        Limits = AgentLimits.FromJson(a.LimitsJson),
        Origins = a.Origins.Select(o => o.Origin).OrderBy(o => o).ToList(),
        Enabled = a.Status == "Enabled", ConcurrencyStamp = a.ConcurrencyStamp,
        WebSearchEnabled = a.WebSearchEnabled, WebSearchMaxUses = a.WebSearchMaxUses,
        WebSearchDomainMode = a.WebSearchDomainMode, WebSearchDomains = WebSearchTool.ParseDomains(a.WebSearchDomainsJson).ToList(),
    };

    public async Task<IReadOnlyList<Agent>> ListAsync(Guid accountId, CancellationToken ct = default) =>
        await db.Agents.AsNoTracking().Include(a => a.Origins)
            .Where(a => a.AccountId == accountId && a.DeletedUtc == null)
            .OrderByDescending(a => a.UpdatedUtc).ToListAsync(ct);

    public Task<Agent?> GetAsync(Guid accountId, Guid agentId, CancellationToken ct = default) =>
        db.Agents.Include(a => a.Origins)
            .FirstOrDefaultAsync(a => a.Id == agentId && a.AccountId == accountId && a.DeletedUtc == null, ct);

    public async Task<AgentResult> CreateAsync(Guid accountId, AgentInput input, CancellationToken ct = default)
    {
        var errors = Validate(input, out var origins, out var allowed);
        if (errors.Count > 0) return new AgentResult(false, errors, null);

        var agent = new Agent { AccountId = accountId, Status = input.Enabled ? "Enabled" : "Disabled" };
        Apply(agent, input, allowed);
        foreach (var o in origins) agent.Origins.Add(new AgentAllowedOrigin { AccountId = accountId, AgentId = agent.Id, Origin = o });
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return new AgentResult(true, [], agent);
    }

    public async Task<AgentResult> UpdateAsync(Guid accountId, Guid agentId, AgentInput input, CancellationToken ct = default)
    {
        var agent = await GetAsync(accountId, agentId, ct);
        if (agent == null) return AgentResult.Fail("Agent not found.");
        var errors = Validate(input, out var origins, out var allowed);
        if (errors.Count == 0)
        {
            var skillChars = await (from a in db.AgentSkills
                                    join f in db.SkillFiles on a.SkillFileId equals f.Id
                                    where a.AgentId == agentId && f.DeletedUtc == null
                                    select f.Content.Length).SumAsync(ct);
            if ((input.SystemPrompt ?? "").Length + skillChars > SkillRules.MaxPromptPlusSkillsChars)
                errors.Add($"Instructions plus assigned skills ({(input.SystemPrompt ?? "").Length + skillChars:N0} characters) exceed the {SkillRules.MaxPromptPlusSkillsChars:N0} limit.");
        }
        if (errors.Count > 0) return new AgentResult(false, errors, agent);

        if (input.ConcurrencyStamp != null) db.Entry(agent).Property(a => a.ConcurrencyStamp).OriginalValue = input.ConcurrencyStamp;
        Apply(agent, input, allowed);
        agent.Status = input.Enabled ? "Enabled" : "Disabled";

        var keep = origins.ToHashSet();
        foreach (var o in agent.Origins.Where(o => !keep.Contains(o.Origin)).ToList())
        {
            agent.Origins.Remove(o);
            db.AgentAllowedOrigins.Remove(o);
        }
        var have = agent.Origins.Select(o => o.Origin).ToHashSet();
        foreach (var o in origins.Where(o => !have.Contains(o)))
            agent.Origins.Add(new AgentAllowedOrigin { AccountId = accountId, AgentId = agent.Id, Origin = o });

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return new AgentResult(false, ["This agent was changed elsewhere. Reload and try again."], null, Conflict: true);
        }
        return new AgentResult(true, [], agent);
    }

    public async Task<Agent?> DuplicateAsync(Guid accountId, Guid agentId, CancellationToken ct = default)
    {
        var src = await db.Agents.AsNoTracking().Include(a => a.Origins)
            .FirstOrDefaultAsync(a => a.Id == agentId && a.AccountId == accountId && a.DeletedUtc == null, ct);
        if (src == null) return null;

        var name = "Copy of " + src.Name;
        var copy = new Agent
        {
            AccountId = accountId, Name = name.Length > 200 ? name[..200] : name, Description = src.Description,
            SystemPrompt = src.SystemPrompt, DefaultModelFamily = src.DefaultModelFamily, PinnedModelId = src.PinnedModelId,
            ShowModelSelector = src.ShowModelSelector, AllowedModelFamilies = src.AllowedModelFamilies,
            MaxOutputTokens = src.MaxOutputTokens, WebSearchEnabled = src.WebSearchEnabled, WebSearchMaxUses = src.WebSearchMaxUses,
            WebSearchDomainMode = src.WebSearchDomainMode, WebSearchDomainsJson = src.WebSearchDomainsJson,
            IdentityMode = src.IdentityMode, AppearanceJson = src.AppearanceJson, LimitsJson = src.LimitsJson,
            Status = "Disabled",
        };
        foreach (var o in src.Origins) copy.Origins.Add(new AgentAllowedOrigin { AccountId = accountId, AgentId = copy.Id, Origin = o.Origin });
        db.Agents.Add(copy);
        var links = await db.AgentSkills.AsNoTracking().Where(a => a.AgentId == agentId).ToListAsync(ct);
        foreach (var l in links)
            db.AgentSkills.Add(new AgentSkill { AccountId = accountId, AgentId = copy.Id, SkillFileId = l.SkillFileId, SortOrder = l.SortOrder });
        var conns = await db.AgentConnections.AsNoTracking().Where(a => a.AgentId == agentId && a.AccountId == accountId).ToListAsync(ct);
        foreach (var c in conns)
        {
            var nc = new AgentConnection { AccountId = accountId, AgentId = copy.Id, ConnectionId = c.ConnectionId, Alias = c.Alias, Enabled = c.Enabled };
            db.AgentConnections.Add(nc);
            var tools = await db.AgentMcpTools.AsNoTracking().Where(t => t.AgentConnectionId == c.Id).ToListAsync(ct);
            foreach (var t in tools)
                db.AgentMcpTools.Add(new AgentMcpTool { AccountId = accountId, AgentConnectionId = nc.Id, ToolName = t.ToolName, RequiresConfirmation = t.RequiresConfirmation, ApprovedSchemaHash = t.ApprovedSchemaHash });
        }
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task<bool> SetEnabledAsync(Guid accountId, Guid agentId, bool enabled, CancellationToken ct = default)
    {
        var a = await db.Agents.FirstOrDefaultAsync(x => x.Id == agentId && x.AccountId == accountId && x.DeletedUtc == null, ct);
        if (a == null) return false;
        a.Status = enabled ? "Enabled" : "Disabled";
        a.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid accountId, Guid agentId, CancellationToken ct = default)
    {
        var a = await db.Agents.FirstOrDefaultAsync(x => x.Id == agentId && x.AccountId == accountId && x.DeletedUtc == null, ct);
        if (a == null) return false;
        a.DeletedUtc = time.GetUtcNow().UtcDateTime;
        a.Status = "Disabled";
        a.UpdatedUtc = a.DeletedUtc.Value;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private void Apply(Agent a, AgentInput i, List<ModelFamily> allowed)
    {
        a.Name = i.Name.Trim();
        a.Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim();
        a.SystemPrompt = i.SystemPrompt ?? "";
        a.DefaultModelFamily = i.DefaultModelFamily.ToString().ToLowerInvariant();
        a.PinnedModelId = string.IsNullOrWhiteSpace(i.PinnedModelId) ? null : i.PinnedModelId.Trim();
        a.ShowModelSelector = i.ShowModelSelector;
        a.AllowedModelFamilies = string.Join(",", allowed.Select(f => f.ToString().ToLowerInvariant()));
        a.MaxOutputTokens = i.MaxOutputTokens;
        a.WebSearchEnabled = i.WebSearchEnabled;
        a.WebSearchMaxUses = i.WebSearchMaxUses;
        a.WebSearchDomainMode = i.WebSearchDomainMode;
        var domains = i.WebSearchDomainMode == "None" ? [] : NormalizeDomains(i.WebSearchDomains, out _);
        a.WebSearchDomainsJson = domains.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(domains);
        a.AppearanceJson = i.Appearance.ToJson();
        a.LimitsJson = i.Limits.ToJson();
        a.UpdatedUtc = time.GetUtcNow().UtcDateTime;
    }

    public static List<string> Validate(AgentInput i, out List<string> origins, out List<ModelFamily> allowed)
    {
        var e = new List<string>();
        var name = (i.Name ?? "").Trim();
        if (name.Length is 0 or > 200) e.Add("Name is required (max 200 characters).");
        if ((i.Description ?? "").Length > 1000) e.Add("Description is limited to 1000 characters.");
        if ((i.SystemPrompt ?? "").Length > MaxSystemPromptChars) e.Add($"Instructions are limited to {MaxSystemPromptChars:N0} characters.");
        if (!Enum.IsDefined(i.DefaultModelFamily)) e.Add("Choose a valid default model family.");
        if (!string.IsNullOrWhiteSpace(i.PinnedModelId) && !PinnedIdPattern().IsMatch(i.PinnedModelId.Trim()))
            e.Add("Pinned model id has invalid characters.");
        if (i.MaxOutputTokens is < 256 or > 128_000) e.Add("Max output tokens must be 256-128,000.");

        allowed = i.AllowedModelFamilies.Where(Enum.IsDefined).Distinct().OrderBy(f => f).ToList();
        if (allowed.Count == 0) e.Add("Allow at least one model family.");
        else if (!allowed.Contains(i.DefaultModelFamily)) e.Add("The default model family must be one of the allowed families.");

        if (i.WebSearchMaxUses is < 1 or > 20) e.Add("Web search max uses must be 1-20.");
        if (i.WebSearchDomainMode is not ("None" or "Allow" or "Block")) e.Add("Choose a valid web search domain mode.");
        else
        {
            var searchDomains = NormalizeDomains(i.WebSearchDomains, out var domainErrors);
            e.AddRange(domainErrors);
            if (searchDomains.Count > WebSearchTool.MaxDomains) e.Add($"At most {WebSearchTool.MaxDomains} web search domains.");
            if (i.WebSearchDomainMode != "None" && searchDomains.Count == 0 && domainErrors.Count == 0)
                e.Add("Add at least one domain, or set the domain mode to 'Any site'.");
        }

        e.AddRange(i.Appearance.Validate());
        e.AddRange(i.Limits.Validate());

        origins = [];
        var seen = new HashSet<string>();
        foreach (var raw in i.Origins.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            if (!OriginNormalizer.TryNormalize(raw, out var n, out var err)) { e.Add(err!); continue; }
            if (seen.Add(n)) origins.Add(n);
        }
        if (origins.Count > MaxOrigins) e.Add($"At most {MaxOrigins} allowed origins.");
        return e;
    }

    private static List<string> NormalizeDomains(IEnumerable<string> raw, out List<string> errors)
    {
        errors = [];
        var list = new List<string>();
        foreach (var r in raw.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!WebSearchTool.TryNormalizeDomain(r, out var d, out var err)) { errors.Add(err!); continue; }
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,100}$")]
    private static partial Regex PinnedIdPattern();
}
