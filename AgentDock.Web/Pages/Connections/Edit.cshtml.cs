using AgentDock.Core.Data;
using AgentDock.Core.Mcp;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Connections;

public class EditModel(IConnectionService connections, ICurrentAccountAccessor accounts) : PageModel
{
    public const string GitHubUrl = "https://api.githubcopilot.com/mcp/";

    [BindProperty] public ConnectionInput Input { get; set; } = new();
    [BindProperty] public string? HeadersText { get; set; }
    public Guid? ConnectionId { get; private set; }
    public string? SecretHint { get; private set; }
    public string Status { get; private set; } = "Untested";
    public string? LastError { get; private set; }
    public int UsedBy { get; private set; }
    public IReadOnlyList<McpToolSnapshot> Tools { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (id == null) { Input = new ConnectionInput(); return Page(); }
        return await LoadAsync(account.Id, id.Value, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostTemplateAsync()
    {
        Input = new ConnectionInput
        {
            Name = "GitHub", BaseUrl = GitHubUrl, AuthType = "Bearer",
            StaticHeaders = new() { ["X-MCP-Toolsets"] = "repos,issues,pull_requests,context", ["X-MCP-Readonly"] = "true" },
        };
        HeadersText = FormatHeaders(Input.StaticHeaders);
        await Task.CompletedTask;
        ModelState.Clear();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var errors = new List<string>();
        Input.StaticHeaders = ParseHeaders(HeadersText, errors);
        if (errors.Count == 0)
        {
            var result = id == null ? await connections.CreateAsync(account.Id, Input, ct) : await connections.UpdateAsync(account.Id, id.Value, Input, ct);
            if (result.Success)
            {
                Message = "Saved.";
                return RedirectToPage(new { id = result.Connection!.Id });
            }
            errors.AddRange(result.Errors);
        }
        Errors = errors;
        if (id != null) await LoadAsync(account.Id, id.Value, ct, keepInput: true);
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var r = await connections.TestAsync(account.Id, id, ct);
        if (r == null) return NotFound();
        Message = (r.Ok ? "Test passed. " : "Test failed. ") + r.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDiscoverAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var (r, _) = await connections.DiscoverAsync(account.Id, id, ct);
        if (r == null) return NotFound();
        Message = (r.Ok ? "Discovery finished. " : "Discovery failed. ") + r.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool removeFromAll, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var r = await connections.DeleteAsync(account.Id, id, removeFromAll, ct);
        if (!r.Found) return NotFound();
        if (!r.Deleted && r.HttpTools is { Count: > 0 })
        {
            Message = "Used by HTTP tools: " + string.Join(", ", r.HttpTools) + ". Delete or change those tools first.";
            return RedirectToPage(new { id });
        }
        if (!r.Deleted)
        {
            Message = "Still used by: " + string.Join(", ", r.BlockedBy) + ". Tick 'Remove from all agents' to delete anyway.";
            return RedirectToPage(new { id });
        }
        TempData["Message"] = "Connection deleted.";
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync(Guid accountId, Guid id, CancellationToken ct, bool keepInput = false)
    {
        var c = await connections.GetAsync(accountId, id, ct);
        if (c == null) return false;
        ConnectionId = id; Status = c.Status; LastError = c.LastError;
        var summary = (await connections.ListAsync(accountId, ct)).FirstOrDefault(s => s.Connection.Id == id);
        SecretHint = summary?.SecretHint; UsedBy = summary?.UsedByAgents ?? 0;
        Tools = await connections.SnapshotsAsync(accountId, id, ct);
        if (!keepInput)
        {
            Input = (await connections.GetInputAsync(accountId, id, ct))!;
            HeadersText = FormatHeaders(Input.StaticHeaders);
        }
        return true;
    }

    private static string FormatHeaders(Dictionary<string, string> h) => string.Join("\n", h.Select(kv => $"{kv.Key}: {kv.Value}"));

    private static Dictionary<string, string> ParseHeaders(string? text, List<string> errors)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf(':');
            if (i <= 0) { errors.Add($"Header line '{raw}' must look like 'X-Name: value'."); continue; }
            d[raw[..i].Trim()] = raw[(i + 1)..].Trim();
        }
        return d;
    }
}
