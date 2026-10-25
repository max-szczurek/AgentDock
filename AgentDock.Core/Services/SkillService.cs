using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Services;

public static partial class SkillRules
{
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxFilesPerAccount = 100;
    public const int MaxSkillsPerAgent = 20;
    public const int MaxSkillChars = 120_000;
    public const int MaxPromptPlusSkillsChars = 150_000;

    private static readonly UTF8Encoding Strict = new(false, true);

    public static bool TryNormalizeFileName(string? raw, out string name, out string? error)
    {
        name = "";
        error = null;
        var n = Path.GetFileName((raw ?? "").Replace('\\', '/').Trim());
        if (!n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) { error = $"'{raw}' is not a .md file."; return false; }
        if (!FileNamePattern().IsMatch(n)) { error = $"'{raw}' has an invalid file name (letters, digits, space, '.', '_' and '-' only, max 200)."; return false; }
        name = n;
        return true;
    }

    /// <summary>Strict UTF-8 decode (BOM dropped); rejects oversize, invalid UTF-8 and NUL bytes.</summary>
    public static bool TryDecode(byte[] bytes, out string text, out string? error)
    {
        text = "";
        error = null;
        if (bytes.Length > MaxFileBytes) { error = $"File is larger than {MaxFileBytes / 1024} KB."; return false; }
        try { text = Strict.GetString(bytes); }
        catch (DecoderFallbackException) { error = "File is not valid UTF-8."; return false; }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (text.Contains('\0')) { error = "File contains NUL characters."; return false; }
        return true;
    }

    public static string? ValidateText(string text)
    {
        if (text.Contains('\0')) return "Content contains NUL characters.";
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) return $"Content is larger than {MaxFileBytes / 1024} KB.";
        if (text.Trim().Length == 0) return "Content is empty.";
        return null;
    }

    /// <summary>Optional front matter: a leading '---' block with simple "name:" and "description:" lines.</summary>
    public static (string? Name, string? Description) ParseFrontMatter(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return (null, null);
        string? name = null, desc = null;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") return (name, desc);
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            var key = lines[i][..idx].Trim().ToLowerInvariant();
            var val = lines[i][(idx + 1)..].Trim().Trim('"', '\'');
            if (key == "name" && val.Length > 0) name = val.Length > 200 ? val[..200] : val;
            else if (key == "description" && val.Length > 0) desc = val.Length > 1000 ? val[..1000] : val;
        }
        return (null, null);
    }

    public static byte[] Hash(string content) => SHA256.HashData(Encoding.UTF8.GetBytes(content));

    [GeneratedRegex(@"^[A-Za-z0-9._ -]{1,200}$")]
    private static partial Regex FileNamePattern();
}

public sealed record SkillUpload(string FileName, byte[] Bytes);
public sealed record SkillUploadResult(string FileName, bool Success, string? Error, Guid? Id);
public sealed record SkillSummary(Guid Id, string FileName, string DisplayName, string? Description, int SizeBytes, int Version, DateTime UpdatedUtc, int UsedByAgents);
public sealed record SkillUsage(Guid AgentId, string AgentName);
public sealed record SkillSaveResult(bool Success, IReadOnlyList<string> Errors, SkillFile? Skill = null);
public sealed record SkillDeleteResult(bool Found, bool Deleted, IReadOnlyList<SkillUsage> BlockedBy);

public interface ISkillService
{
    Task<IReadOnlyList<SkillSummary>> ListAsync(Guid accountId, CancellationToken ct = default);
    Task<SkillFile?> GetAsync(Guid accountId, Guid skillId, CancellationToken ct = default);
    Task<IReadOnlyList<SkillUploadResult>> UploadAsync(Guid accountId, IReadOnlyList<SkillUpload> files, CancellationToken ct = default);
    Task<SkillSaveResult> UpdateContentAsync(Guid accountId, Guid skillId, string content, CancellationToken ct = default);
    Task<IReadOnlyList<SkillUsage>> UsedByAsync(Guid accountId, Guid skillId, CancellationToken ct = default);
    Task<SkillDeleteResult> DeleteAsync(Guid accountId, Guid skillId, bool removeFromAll, CancellationToken ct = default);
    Task<IReadOnlyList<SkillFile>> ForAgentAsync(Guid accountId, Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SetForAgentAsync(Guid accountId, Guid agentId, IReadOnlyList<Guid> orderedSkillIds, CancellationToken ct = default);
}

public sealed class SkillService(AgentDockDbContext db, TimeProvider time) : ISkillService
{
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(Guid accountId, CancellationToken ct = default)
    {
        var rows = await db.SkillFiles.AsNoTracking()
            .Where(s => s.AccountId == accountId && s.DeletedUtc == null)
            .OrderBy(s => s.FileName)
            .Select(s => new
            {
                s.Id, s.FileName, s.DisplayName, s.Description, s.SizeBytes, s.Version, s.UpdatedUtc,
                Used = db.AgentSkills.Count(a => a.SkillFileId == s.Id && db.Agents.Any(g => g.Id == a.AgentId && g.DeletedUtc == null)),
            }).ToListAsync(ct);
        return rows.Select(r => new SkillSummary(r.Id, r.FileName, r.DisplayName, r.Description, r.SizeBytes, r.Version, r.UpdatedUtc, r.Used)).ToList();
    }

    public Task<SkillFile?> GetAsync(Guid accountId, Guid skillId, CancellationToken ct = default) =>
        db.SkillFiles.FirstOrDefaultAsync(s => s.Id == skillId && s.AccountId == accountId && s.DeletedUtc == null, ct);

    public async Task<IReadOnlyList<SkillUploadResult>> UploadAsync(Guid accountId, IReadOnlyList<SkillUpload> files, CancellationToken ct = default)
    {
        var results = new List<SkillUploadResult>();
        var count = await db.SkillFiles.CountAsync(s => s.AccountId == accountId && s.DeletedUtc == null, ct);
        foreach (var f in files)
        {
            if (!SkillRules.TryNormalizeFileName(f.FileName, out var name, out var err)) { results.Add(new(f.FileName, false, err, null)); continue; }
            if (!SkillRules.TryDecode(f.Bytes, out var text, out err)) { results.Add(new(name, false, err, null)); continue; }
            if (SkillRules.ValidateText(text) is { } bad) { results.Add(new(name, false, bad, null)); continue; }

            var existing = await db.SkillFiles.FirstOrDefaultAsync(s => s.AccountId == accountId && s.FileName == name && s.DeletedUtc == null, ct);
            if (existing != null)
            {
                var r = await UpdateContentAsync(accountId, existing.Id, text, ct);
                results.Add(new(name, r.Success, r.Success ? null : string.Join(" ", r.Errors), existing.Id));
                continue;
            }
            if (count >= SkillRules.MaxFilesPerAccount) { results.Add(new(name, false, $"Limit of {SkillRules.MaxFilesPerAccount} skill files reached.", null)); continue; }

            var skill = new SkillFile { AccountId = accountId, FileName = name };
            Fill(skill, text, name);
            db.SkillFiles.Add(skill);
            await db.SaveChangesAsync(ct);
            count++;
            results.Add(new(name, true, null, skill.Id));
        }
        return results;
    }

    public async Task<SkillSaveResult> UpdateContentAsync(Guid accountId, Guid skillId, string content, CancellationToken ct = default)
    {
        var skill = await GetAsync(accountId, skillId, ct);
        if (skill == null) return new(false, ["Skill not found."]);
        content = content.Replace("\r\n", "\n");
        if (SkillRules.ValidateText(content) is { } bad) return new(false, [bad]);
        if (SkillRules.Hash(content).AsSpan().SequenceEqual(skill.ContentSha256)) return new(true, [], skill);

        var growth = content.Length - skill.Content.Length;
        if (growth > 0)
        {
            var agentIds = await db.AgentSkills.Where(a => a.SkillFileId == skillId).Select(a => a.AgentId).ToListAsync(ct);
            foreach (var agentId in agentIds)
            {
                var (skillChars, promptChars, name) = await AgentTotalsAsync(accountId, agentId, ct);
                if (skillChars + growth > SkillRules.MaxSkillChars || promptChars + skillChars + growth > SkillRules.MaxPromptPlusSkillsChars)
                    return new(false, [$"This change would push agent '{name}' over its instruction budget."]);
            }
        }

        Fill(skill, content, skill.FileName);
        skill.Version++;
        await db.SaveChangesAsync(ct);
        return new(true, [], skill);
    }

    public async Task<IReadOnlyList<SkillUsage>> UsedByAsync(Guid accountId, Guid skillId, CancellationToken ct = default) =>
        await db.AgentSkills.AsNoTracking()
            .Where(a => a.AccountId == accountId && a.SkillFileId == skillId)
            .Join(db.Agents.Where(g => g.DeletedUtc == null), a => a.AgentId, g => g.Id, (a, g) => new { g.Id, g.Name })
            .OrderBy(u => u.Name).Select(u => new SkillUsage(u.Id, u.Name)).ToListAsync(ct);

    public async Task<SkillDeleteResult> DeleteAsync(Guid accountId, Guid skillId, bool removeFromAll, CancellationToken ct = default)
    {
        var skill = await GetAsync(accountId, skillId, ct);
        if (skill == null) return new(false, false, []);
        var links = await db.AgentSkills.Where(a => a.AccountId == accountId && a.SkillFileId == skillId).ToListAsync(ct);
        if (links.Count > 0)
        {
            var users = await UsedByAsync(accountId, skillId, ct);
            if (users.Count > 0 && !removeFromAll) return new(true, false, users);
            db.AgentSkills.RemoveRange(links);
        }
        skill.DeletedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new(true, true, []);
    }

    public async Task<IReadOnlyList<SkillFile>> ForAgentAsync(Guid accountId, Guid agentId, CancellationToken ct = default) =>
        await (from a in db.AgentSkills.AsNoTracking()
               join s in db.SkillFiles.AsNoTracking() on a.SkillFileId equals s.Id
               where a.AccountId == accountId && a.AgentId == agentId && s.AccountId == accountId && s.DeletedUtc == null
               orderby a.SortOrder, s.FileName
               select s).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> SetForAgentAsync(Guid accountId, Guid agentId, IReadOnlyList<Guid> orderedSkillIds, CancellationToken ct = default)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.AccountId == accountId && a.DeletedUtc == null, ct);
        if (agent == null) return ["Agent not found."];
        var ids = orderedSkillIds.Distinct().ToList();
        if (ids.Count > SkillRules.MaxSkillsPerAgent) return [$"At most {SkillRules.MaxSkillsPerAgent} skills per agent."];

        var skills = await db.SkillFiles.AsNoTracking()
            .Where(s => s.AccountId == accountId && s.DeletedUtc == null && ids.Contains(s.Id))
            .Select(s => new { s.Id, Chars = s.Content.Length }).ToListAsync(ct);
        if (skills.Count != ids.Count) return ["One or more skills were not found."];
        var total = skills.Sum(s => s.Chars);
        if (total > SkillRules.MaxSkillChars) return [$"Skills total {total:N0} characters; the limit is {SkillRules.MaxSkillChars:N0}."];
        if (agent.SystemPrompt.Length + total > SkillRules.MaxPromptPlusSkillsChars)
            return [$"Instructions plus skills would be {agent.SystemPrompt.Length + total:N0} characters; the limit is {SkillRules.MaxPromptPlusSkillsChars:N0}."];

        var existing = await db.AgentSkills.Where(a => a.AgentId == agentId).ToListAsync(ct);
        db.AgentSkills.RemoveRange(existing.Where(e => !ids.Contains(e.SkillFileId)));
        for (var i = 0; i < ids.Count; i++)
        {
            var row = existing.FirstOrDefault(e => e.SkillFileId == ids[i]);
            if (row == null) db.AgentSkills.Add(new AgentSkill { AccountId = accountId, AgentId = agentId, SkillFileId = ids[i], SortOrder = i });
            else row.SortOrder = i;
        }
        await db.SaveChangesAsync(ct);
        return [];
    }

    private void Fill(SkillFile s, string content, string fileName)
    {
        var (name, desc) = SkillRules.ParseFrontMatter(content);
        s.DisplayName = name ?? fileName[..^3];
        s.Description = desc;
        s.Content = content;
        s.ContentSha256 = SkillRules.Hash(content);
        s.SizeBytes = Encoding.UTF8.GetByteCount(content);
        s.UpdatedUtc = time.GetUtcNow().UtcDateTime;
    }

    private async Task<(int SkillChars, int PromptChars, string Name)> AgentTotalsAsync(Guid accountId, Guid agentId, CancellationToken ct)
    {
        var skillChars = await (from a in db.AgentSkills
                                join s in db.SkillFiles on a.SkillFileId equals s.Id
                                where a.AgentId == agentId && s.DeletedUtc == null
                                select s.Content.Length).SumAsync(ct);
        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == agentId && a.AccountId == accountId)
            .Select(a => new { a.Name, Len = a.SystemPrompt.Length }).FirstAsync(ct);
        return (skillChars, agent.Len, agent.Name);
    }
}
