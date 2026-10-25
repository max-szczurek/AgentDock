using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Agents;

public class PreviewModel(IAgentService agents, IVisitorSessionService sessions, ICurrentAccountAccessor accounts) : PageModel
{
    public Guid AgentId { get; private set; }
    public string AgentName { get; private set; } = "";
    public bool Enabled { get; private set; }
    public int Width { get; private set; } = 380;
    public int Height { get; private set; } = 600;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var agent = await agents.GetAsync(account.Id, id, ct);
        if (agent == null) return NotFound();
        AgentId = agent.Id; AgentName = agent.Name; Enabled = agent.Status == "Enabled";
        var a = AppearanceSettings.FromJson(agent.AppearanceJson);
        Width = a.PanelWidth; Height = a.PanelHeight;
        return Page();
    }

    public async Task<IActionResult> OnPostSessionAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var agent = await agents.GetAsync(account.Id, id, ct);
        if (agent == null) return NotFound();
        var issued = await sessions.CreateAsync(agent, "Preview", $"{Request.Scheme}://{Request.Host}", HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return new JsonResult(new { token = issued.Token });
    }
}
