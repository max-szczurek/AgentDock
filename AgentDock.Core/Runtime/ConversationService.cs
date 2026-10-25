using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Runtime;

public sealed record MessageView(Guid Id, string Role, string Text, bool Incomplete, DateTime CreatedUtc, IReadOnlyList<CitationSource>? Citations = null);
public sealed record ConversationView(Guid Id, string Title, DateTime UpdatedUtc);

public interface IConversationService
{
    Task<Conversation> CreateAsync(VisitorSession session, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationView>> ListAsync(VisitorSession session, CancellationToken ct = default);
    Task<Conversation?> GetOwnedAsync(VisitorSession session, Guid conversationId, CancellationToken ct = default);
    Task<IReadOnlyList<MessageView>> MessagesAsync(Conversation conversation, CancellationToken ct = default);
    Task<bool> TryBeginTurnAsync(Conversation c, Guid turnId, TimeSpan lease, CancellationToken ct = default);
    Task EndTurnAsync(Conversation c, Guid turnId, CancellationToken ct = default);
    Task<ConversationMessage> AppendAsync(Conversation c, ConversationMessage m, CancellationToken ct = default);
    Task<IReadOnlyList<JsonNode>> HistoryAsync(Conversation c, int charBudget, CancellationToken ct = default);
}

public sealed class ConversationService(AgentDockDbContext db, TimeProvider time) : IConversationService
{
    public const int DefaultHistoryChars = 400_000;

    public async Task<Conversation> CreateAsync(VisitorSession session, CancellationToken ct = default)
    {
        var c = new Conversation
        {
            AccountId = session.AccountId, AgentId = session.AgentId, VisitorSessionId = session.Id,
            ExternalUserId = session.ExternalUserId, ExternalSessionId = session.ExternalSessionId,
        };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<ConversationView>> ListAsync(VisitorSession session, CancellationToken ct = default) =>
        await db.Conversations.AsNoTracking()
            .Where(c => c.AgentId == session.AgentId && c.DeletedUtc == null && c.MessageCount > 0
                && (session.ExternalSessionId == null ? c.VisitorSessionId == session.Id : c.ExternalSessionId == session.ExternalSessionId))
            .OrderByDescending(c => c.UpdatedUtc).Take(50)
            .Select(c => new ConversationView(c.Id, c.Title, c.UpdatedUtc)).ToListAsync(ct);

    /// <summary>Returns the conversation only if it belongs to this exact session; callers answer 404 otherwise.</summary>
    public Task<Conversation?> GetOwnedAsync(VisitorSession session, Guid conversationId, CancellationToken ct = default) =>
        db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.AgentId == session.AgentId && c.DeletedUtc == null
            && (session.ExternalSessionId == null ? c.VisitorSessionId == session.Id : c.ExternalSessionId == session.ExternalSessionId), ct);

    public async Task<IReadOnlyList<MessageView>> MessagesAsync(Conversation conversation, CancellationToken ct = default)
    {
        var rows = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Id, m.Role, m.DisplayText, m.ContentJson, m.IsIncomplete, m.CreatedUtc, m.CitationsJson }).ToListAsync(ct);
        return rows.Where(r => r.Role == "assistant" || IsVisitorText(r.ContentJson))
            .Where(r => r.DisplayText.Length > 0 || r.Role == "assistant" && r.IsIncomplete)
            .Select(r => new MessageView(r.Id, r.Role, r.DisplayText, r.IsIncomplete, r.CreatedUtc, ParseCitations(r.CitationsJson))).ToList();
    }

    public async Task<bool> TryBeginTurnAsync(Conversation c, Guid turnId, TimeSpan lease, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now + lease;
        var rows = await db.Conversations
            .Where(x => x.Id == c.Id && x.AccountId == c.AccountId && (x.ActiveTurnId == null || x.ActiveTurnExpiresUtc < now))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveTurnId, turnId).SetProperty(x => x.ActiveTurnExpiresUtc, expires), ct);
        return rows == 1;
    }

    public Task EndTurnAsync(Conversation c, Guid turnId, CancellationToken ct = default) =>
        db.Conversations.Where(x => x.Id == c.Id && x.ActiveTurnId == turnId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ActiveTurnId, (Guid?)null).SetProperty(x => x.ActiveTurnExpiresUtc, (DateTime?)null), ct);

    /// <summary>Appends under the per-conversation turn lock, which serialises sequence allocation.</summary>
    public async Task<ConversationMessage> AppendAsync(Conversation c, ConversationMessage m, CancellationToken ct = default)
    {
        var next = (await db.ConversationMessages.Where(x => x.ConversationId == c.Id).MaxAsync(x => (int?)x.Sequence, ct) ?? 0) + 1;
        m.AccountId = c.AccountId; m.ConversationId = c.Id; m.Sequence = next;
        db.ConversationMessages.Add(m);
        await db.SaveChangesAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var title = c.Title.Length == 0 && m.Role == "user" && IsVisitorText(m.ContentJson) ? Truncate(m.DisplayText, 80) : null;
        await db.Conversations.Where(x => x.Id == c.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.MessageCount, x => x.MessageCount + 1)
                .SetProperty(x => x.UpdatedUtc, now)
                .SetProperty(x => x.Title, x => title ?? x.Title), ct);
        if (title != null) c.Title = title;
        return m;
    }

    /// <summary>
    /// Complete messages in order, with whole turns dropped oldest-first when over budget so tool_use /
    /// tool_result pairs are never split. Incomplete assistant messages are display-only.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> HistoryAsync(Conversation c, int charBudget, CancellationToken ct = default)
    {
        var rows = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == c.Id && !m.IsIncomplete)
            .OrderBy(m => m.Sequence).Select(m => new { m.TurnId, m.Role, m.ContentJson }).ToListAsync(ct);
        var turns = rows.GroupBy(r => r.TurnId).Select(g => g.ToList()).ToList();
        var total = rows.Sum(r => r.ContentJson.Length);
        var skip = 0;
        while (total > charBudget && skip < turns.Count - 1)
            total -= turns[skip++].Sum(r => r.ContentJson.Length);
        return turns.Skip(skip).SelectMany(t => t)
            .Select(r => (JsonNode)new JsonObject { ["role"] = r.Role, ["content"] = JsonNode.Parse(r.ContentJson) }).ToList();
    }

    private static IReadOnlyList<CitationSource>? ParseCitations(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<CitationSource>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static bool IsVisitorText(string contentJson) =>
        !contentJson.Contains("\"tool_result\"", StringComparison.Ordinal);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
