using AgentDock.Core.HttpTools;
using AgentDock.Core.Mcp;
using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Agents;

public class ToolsModel(IAgentService agents, IAgentToolService tools, IHttpToolService httpTools, ICurrentAccountAccessor accounts) : PageModel
{
    public class ToolRow { public string Name { get; set; } = ""; public bool Allowed { get; set; } public bool Confirm { get; set; } }
    public class ConnectionRow
    {
        public Guid ConnectionId { get; set; }
        public bool Assigned { get; set; }
        public bool Enabled { get; set; }
        public string? Alias { get; set; }
        public List<ToolRow> Tools { get; set; } = [];
    }

    public class HttpRow { public Guid HttpToolId { get; set; } public bool Assigned { get; set; } public bool Enabled { get; set; } public bool Confirm { get; set; } }

    [BindProperty] public List<ConnectionRow> Rows { get; set; } = [];
    [BindProperty] public List<HttpRow> HttpRows { get; set; } = [];
    public IReadOnlyList<HttpToolAssignmentView> HttpItems { get; private set; } = [];
    public Guid AgentId { get; private set; }
    public string AgentName { get; private set; } = "";
    public IReadOnlyList<ConnectionAssignmentView> Items { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        return await LoadAsync(account.Id, id, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (await agents.GetAsync(account.Id, id, ct) == null) return NotFound();
        var input = Rows.Where(r => r.Assigned).Select(r => new ConnectionAssignmentInput(
            r.ConnectionId, (r.Alias ?? "").Trim(), r.Enabled,
            r.Tools.Where(t => t.Allowed).Select(t => new ToolSelection(t.Name, t.Confirm)).ToList())).ToList();
        var errors = (await tools.SaveAsync(account.Id, id, input, ct)).ToList();
        if (errors.Count == 0)
            errors.AddRange(await httpTools.SaveAssignmentsAsync(account.Id, id,
                HttpRows.Where(r => r.Assigned).Select(r => new HttpToolAssignmentInput(r.HttpToolId, r.Enabled, r.Confirm)).ToList(), ct));
        if (errors.Count == 0) { Message = "Tools saved."; return RedirectToPage(new { id }); }
        await LoadAsync(account.Id, id, ct);
        Errors = errors;
        return Page();
    }

    private async Task<bool> LoadAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        var agent = await agents.GetAsync(accountId, id, ct);
        if (agent == null) return false;
        AgentId = id; AgentName = agent.Name;
        Items = await tools.GetAsync(accountId, id, ct);
        HttpItems = await httpTools.GetAssignmentsAsync(accountId, id, ct);
        return true;
    }
}
