using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Settings;

public class IndexModel(IAnthropicCredentialService credentials, ICurrentAccountAccessor accounts) : PageModel
{
    public CredentialView? Credential { get; private set; }
    public CatalogView? Catalog { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        await LoadAsync(account.Id, false, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string? apiKey, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var result = await credentials.SaveKeyAsync(account.Id, apiKey ?? "", ct);
        if (result.Success) Message = "Key saved."; else Error = result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        await credentials.RemoveAsync(account.Id, ct);
        Message = "Key removed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        await credentials.GetCatalogAsync(account.Id, forceRefresh: true, ct);
        Message = "Models refreshed.";
        return RedirectToPage();
    }

    private async Task LoadAsync(Guid accountId, bool refresh, CancellationToken ct)
    {
        Credential = await credentials.GetActiveAsync(accountId, ct);
        if (Credential != null && !Credential.NeedsReentry) Catalog = await credentials.GetCatalogAsync(accountId, refresh, ct);
    }
}
