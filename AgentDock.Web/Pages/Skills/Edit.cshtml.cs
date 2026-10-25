using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Skills;

public class EditModel(ISkillService skills, ICurrentAccountAccessor accounts) : PageModel
{
    public Guid SkillId { get; private set; }
    public string FileName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public int Version { get; private set; }
    public IReadOnlyList<SkillUsage> UsedBy { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public string PreviewHtml { get; private set; } = "";
    [BindProperty] public string Content { get; set; } = "";
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var skill = await skills.GetAsync(account.Id, id, ct);
        if (skill == null) return NotFound();
        Content = skill.Content;
        await FillAsync(account.Id, skill.Id, skill.FileName, skill.DisplayName, skill.Version, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var result = await skills.UpdateContentAsync(account.Id, id, Content ?? "", ct);
        if (result.Skill == null && result.Errors is ["Skill not found."]) return NotFound();
        if (result.Success) { Message = "Saved."; return RedirectToPage(new { id }); }
        var skill = await skills.GetAsync(account.Id, id, ct);
        if (skill == null) return NotFound();
        Errors = result.Errors;
        await FillAsync(account.Id, id, skill.FileName, skill.DisplayName, skill.Version, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (await skills.GetAsync(account.Id, id, ct) == null) return NotFound();
        return Content(SkillMarkdown.Render(Content ?? ""), "text/html");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool removeFromAll, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var result = await skills.DeleteAsync(account.Id, id, removeFromAll, ct);
        if (!result.Found) return NotFound();
        if (result.Deleted) { TempData["Message"] = "Skill deleted."; return RedirectToPage("Index"); }
        var skill = await skills.GetAsync(account.Id, id, ct);
        Errors = [$"This skill is used by {result.BlockedBy.Count} agent(s). Remove it from them first, or choose 'remove from all and delete'."];
        Content = skill.Content;
        await FillAsync(account.Id, id, skill!.FileName, skill.DisplayName, skill.Version, ct);
        return Page();
    }

    private async Task FillAsync(Guid accountId, Guid id, string fileName, string displayName, int version, CancellationToken ct)
    {
        SkillId = id; FileName = fileName; DisplayName = displayName; Version = version;
        UsedBy = await skills.UsedByAsync(accountId, id, ct);
        PreviewHtml = SkillMarkdown.Render(Content ?? "");
    }
}
