using AgentDock.Core.HttpTools;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.HttpTools;

public class IndexModel(IHttpToolService tools, ICurrentAccountAccessor accounts) : PageModel
{
    public IReadOnlyList<HttpToolSummary> Items { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        Items = await tools.ListAsync(account.Id, ct);
        return Page();
    }
}
