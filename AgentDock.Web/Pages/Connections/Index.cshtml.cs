using AgentDock.Core.Mcp;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Connections;

public class IndexModel(IConnectionService connections, ICurrentAccountAccessor accounts) : PageModel
{
    public IReadOnlyList<ConnectionSummary> Items { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        Items = await connections.ListAsync(account.Id, ct);
        return Page();
    }
}
