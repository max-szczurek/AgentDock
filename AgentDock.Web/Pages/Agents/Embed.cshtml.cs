using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Agents;

public class EmbedModel(IAgentService agents, IAnthropicCredentialService credentials, ICurrentAccountAccessor accounts, IConfiguration config) : PageModel
{
    public Guid AgentId { get; private set; }
    public string AgentName { get; private set; } = "";
    public bool Enabled { get; private set; }
    public bool KeyValid { get; private set; }
    public IReadOnlyList<string> Origins { get; private set; } = [];
    public bool HasOrigins => Origins.Count > 0;
    public int OriginCount => Origins.Count;
    public string Snippet { get; private set; } = "";
    public bool AllowSessionId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var agent = await agents.GetAsync(account.Id, id, ct);
        if (agent == null) return NotFound();
        AgentId = agent.Id; AgentName = agent.Name; Enabled = agent.Status == "Enabled";
        Origins = agent.Origins.Select(o => o.Origin).OrderBy(o => o).ToList();
        KeyValid = (await credentials.GetActiveAsync(account.Id, ct))?.Status == "Valid";
        var baseUrl = config["AGENTDOCK_PUBLIC_BASE_URL"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = $"{Request.Scheme}://{Request.Host}";
        Snippet = $"<script src=\"{baseUrl}/widget/v1/agentdock.js\"\n        data-agent-id=\"{agent.Id}\" async></script>";
        AllowSessionId = agent.AllowExternalSessionId;
        return Page();
    }
}
