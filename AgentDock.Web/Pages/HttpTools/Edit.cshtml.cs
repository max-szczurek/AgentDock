using AgentDock.Core.Data;
using AgentDock.Core.HttpTools;
using AgentDock.Core.Mcp;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.HttpTools;

public class EditModel(IHttpToolService tools, IConnectionService connections, ICurrentAccountAccessor accounts) : PageModel
{
    public const int BlankRows = 4;

    [BindProperty] public HttpToolInput Input { get; set; } = new();
    [BindProperty] public string? StaticQueryText { get; set; }
    [BindProperty] public string? SelectText { get; set; }
    [BindProperty] public string? AdvancedText { get; set; }
    [BindProperty] public string? SampleInput { get; set; }

    public Guid? ToolId { get; private set; }
    public int Version { get; private set; }
    public int UsedBy { get; private set; }
    public IReadOnlyList<Connection> HttpConnections { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public HttpToolTestResult? Test { get; private set; }
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        await LoadConnectionsAsync(account.Id, ct);
        if (id == null)
        {
            Input = new HttpToolInput { ConnectionId = HttpConnections.FirstOrDefault()?.Id ?? Guid.Empty };
            AdvancedText = null;
            return Page();
        }
        return await LoadAsync(account.Id, id.Value, ct) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (id != null && await tools.GetAsync(account.Id, id.Value, ct) == null) return NotFound();
        Input.StaticQuery = ParsePairs(StaticQueryText);
        Input.ResponseSelect = (SelectText ?? "").Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return await SaveAsync(account.Id, id, HttpToolRules.FromForm(Input), ct);
    }

    public async Task<IActionResult> OnPostAdvancedAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (id != null && await tools.GetAsync(account.Id, id.Value, ct) == null) return NotFound();
        if (!HttpToolRules.TryParseAdvanced(AdvancedText, out var schema, out var exec, out var error))
        {
            Errors = [error!];
            await ReloadAsync(account.Id, id, ct);
            return Page();
        }
        var draft = new HttpToolDraft((Input.Name ?? "").Trim(), (Input.DisplayName ?? "").Trim(), (Input.Description ?? "").Trim(), Input.ConnectionId, schema, exec);
        return await SaveAsync(account.Id, id, draft, ct, advanced: true);
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        Test = await tools.TestAsync(account.Id, id, SampleInput, ct);
        if (Test == null) return NotFound();
        await LoadConnectionsAsync(account.Id, ct);
        await LoadAsync(account.Id, id, ct, keepSample: true);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool removeFromAll, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        var r = await tools.DeleteAsync(account.Id, id, removeFromAll, ct);
        if (!r.Found) return NotFound();
        if (!r.Deleted)
        {
            Message = "Still used by: " + string.Join(", ", r.BlockedBy) + ". Tick 'Remove from all agents' to delete anyway.";
            return RedirectToPage(new { id });
        }
        TempData["Message"] = "HTTP tool deleted.";
        return RedirectToPage("Index");
    }

    private async Task<IActionResult> SaveAsync(Guid accountId, Guid? id, HttpToolDraft draft, CancellationToken ct, bool advanced = false)
    {
        var result = await tools.SaveAsync(accountId, id, draft, ct);
        if (result.Success)
        {
            Message = "Saved.";
            return RedirectToPage(new { id = result.Tool!.Id });
        }
        Errors = result.Errors;
        await ReloadAsync(accountId, id, ct);
        if (!advanced) AdvancedText = HttpToolRules.AdvancedJson(draft.InputSchema.ToJsonString(), draft.Execution);
        return Page();
    }

    private async Task ReloadAsync(Guid accountId, Guid? id, CancellationToken ct)
    {
        await LoadConnectionsAsync(accountId, ct);
        if (id != null)
        {
            var t = await tools.GetAsync(accountId, id.Value, ct);
            if (t != null) { ToolId = t.Id; Version = t.Version; }
        }
    }

    private async Task LoadConnectionsAsync(Guid accountId, CancellationToken ct) =>
        HttpConnections = (await connections.ListAsync(accountId, ct)).Where(c => c.Connection.Kind == "Http").Select(c => c.Connection).ToList();

    private async Task<bool> LoadAsync(Guid accountId, Guid id, CancellationToken ct, bool keepSample = false)
    {
        var t = await tools.GetAsync(accountId, id, ct);
        if (t == null) return false;
        ToolId = t.Id; Version = t.Version;
        UsedBy = (await tools.ListAsync(accountId, ct)).FirstOrDefault(s => s.Tool.Id == id)?.UsedByAgents ?? 0;
        if (!HttpToolRules.TryParseExecution(t.ExecutionConfigJson, out var exec)) exec = new HttpToolExecution();
        Input = HttpToolRules.ToForm(t.Name, t.DisplayName, t.Description, t.ConnectionId, t.InputSchemaJson, exec);
        StaticQueryText = string.Join("\n", Input.StaticQuery.Select(kv => $"{kv.Key}={kv.Value}"));
        SelectText = string.Join("\n", Input.ResponseSelect);
        AdvancedText = HttpToolRules.AdvancedJson(t.InputSchemaJson, exec);
        if (!keepSample) SampleInput = HttpToolRules.SampleInput(t.InputSchemaJson);
        ModelState.Clear();
        return true;
    }

    private static Dictionary<string, string> ParsePairs(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf('=');
            if (i <= 0) d[raw] = "";
            else d[raw[..i].Trim()] = raw[(i + 1)..].Trim();
        }
        return d;
    }
}
