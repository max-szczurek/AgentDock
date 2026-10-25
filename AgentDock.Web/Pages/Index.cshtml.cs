using AgentDock.Core.Data;
using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages;

public class IndexModel(IAgentService agents, IAnthropicCredentialService credentials, ICurrentAccountAccessor accounts) : PageModel
{
    public IReadOnlyList<Agent> Agents { get; private set; } = [];
    public bool HasKey { get; private set; }
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        Agents = await agents.ListAsync(account.Id, ct);
        HasKey = await credentials.HasActiveKeyAsync(account.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDuplicateAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var copy = await agents.DuplicateAsync(account.Id, id, ct);
        if (copy == null) return NotFound();
        Message = "Duplicated as a disabled copy.";
        return RedirectToPage("/Agents/Edit", new { id = copy.Id });
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool enable, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (!await agents.SetEnabledAsync(account.Id, id, enable, ct)) return NotFound();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (!await agents.DeleteAsync(account.Id, id, ct)) return NotFound();
        Message = "Agent deleted.";
        return RedirectToPage();
    }
}
