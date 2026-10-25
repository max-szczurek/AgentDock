using AgentDock.Core.Data;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Web.Pages.Agents;

public class TranscriptsModel(AgentDockDbContext db, ICurrentAccountAccessor accounts) : PageModel
{
    public Guid AgentId { get; private set; }
    public string AgentName { get; private set; } = "";
    public IReadOnlyList<Conversation> Conversations { get; private set; } = [];
    public IReadOnlyDictionary<Guid, int> ToolCounts { get; private set; } = new Dictionary<Guid, int>();
    public Conversation? Selected { get; private set; }
    public IReadOnlyList<ConversationMessage> Messages { get; private set; } = [];
    public IReadOnlyList<ToolExecution> Executions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, Guid? conversationId, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.AccountId == account.Id && a.DeletedUtc == null, ct);
        if (agent == null) return NotFound();
        AgentId = id; AgentName = agent.Name;

        if (conversationId == null)
        {
            Conversations = await db.Conversations.AsNoTracking()
                .Where(c => c.AccountId == account.Id && c.AgentId == id && c.DeletedUtc == null)
                .OrderByDescending(c => c.UpdatedUtc).Take(100).ToListAsync(ct);
            var ids = Conversations.Select(c => c.Id).ToList();
            ToolCounts = await db.ToolExecutions.AsNoTracking().Where(t => t.AccountId == account.Id && ids.Contains(t.ConversationId))
                .GroupBy(t => t.ConversationId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return Page();
        }

        Selected = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.AccountId == account.Id && c.AgentId == id && c.DeletedUtc == null, ct);
        if (Selected == null) return NotFound();
        Messages = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == Selected.Id && m.AccountId == account.Id && m.DisplayText != "")
            .OrderBy(m => m.Sequence).ToListAsync(ct);
        Executions = await db.ToolExecutions.AsNoTracking()
            .Where(t => t.ConversationId == Selected.Id && t.AccountId == account.Id).OrderBy(t => t.RequestedUtc).ToListAsync(ct);
        return Page();
    }
}
