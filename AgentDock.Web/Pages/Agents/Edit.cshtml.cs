using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using AgentDock.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentDock.Web.Pages.Agents;

public class EditModel(IAgentService agents, IAnthropicCredentialService credentials, ICurrentAccountAccessor accounts, IWebSearchProbe searchProbe, ISkillService skillService) : PageModel
{
    public class Form
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public string SystemPrompt { get; set; } = "";
        public string DefaultFamily { get; set; } = "Sonnet";
        public string? PinnedModelId { get; set; }
        public bool ShowModelSelector { get; set; }
        public bool AllowHaiku { get; set; } = true;
        public bool AllowSonnet { get; set; } = true;
        public bool AllowOpus { get; set; } = true;
        public int MaxOutputTokens { get; set; } = 4096;
        public string Title { get; set; } = "Ask us";
        public string PrimaryColor { get; set; } = "#4F46E5";
        public string TextOnPrimary { get; set; } = "#FFFFFF";
        public string PanelBackground { get; set; } = "#FFFFFF";
        public string IconPreset { get; set; } = "chat";
        public string Position { get; set; } = "bottom-right";
        public int PanelWidth { get; set; } = 380;
        public int PanelHeight { get; set; } = 600;
        public string? WelcomeMessage { get; set; }
        public string? Placeholder { get; set; }
        public string? OriginsText { get; set; }
        public int TurnsPerSessionPerHour { get; set; } = 30;
        public int TurnsPerIpPerHour { get; set; } = 60;
        public int TurnsPerDay { get; set; } = 500;
        public int MaxMessageChars { get; set; } = 4000;
        public bool WebSearchEnabled { get; set; }
        public int WebSearchMaxUses { get; set; } = 5;
        public string WebSearchDomainMode { get; set; } = "None";
        public string? WebSearchDomainsText { get; set; }
        public string? ConcurrencyStamp { get; set; }
    }

    public class SkillPick
    {
        public Guid Id { get; set; }
        public bool Selected { get; set; }
        public int Order { get; set; }
    }

    [BindProperty] public Form Input { get; set; } = new();
    [BindProperty] public List<SkillPick> SkillPicks { get; set; } = [];
    public IReadOnlyList<SkillSummary> AllSkills { get; private set; } = [];
    public IReadOnlyList<Guid> AssignedSkillIds { get; private set; } = [];
    public string AssembledPrompt { get; private set; } = "";
    public int PromptChars { get; private set; }
    public int SkillChars { get; private set; }
    public IReadOnlyList<string> SkillErrors { get; private set; } = [];
    public Guid? AgentId { get; private set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public CatalogView? Catalog { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? SearchTestMessage { get; set; }
    [TempData] public bool SearchTestOk { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        AgentId = id;
        if (id != null)
        {
            var agent = await agents.GetAsync(account.Id, id.Value, ct);
            if (agent == null) return NotFound();
            Input = FromInput(AgentService.ToInput(agent), agent.ConcurrencyStamp);
        }
        else
        {
            Input = FromInput(new AgentInput { Appearance = new AppearanceSettings(), Enabled = false }, null);
        }
        await LoadCatalogAsync(account.Id, ct);
        await LoadSkillsAsync(account.Id, id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveSkillsAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (await agents.GetAsync(account.Id, id, ct) == null) return NotFound();
        var ordered = SkillPicks.Where(p => p.Selected).OrderBy(p => p.Order).Select(p => p.Id).ToList();
        var errors = await skillService.SetForAgentAsync(account.Id, id, ordered, ct);
        if (errors.Count == 0) { Message = "Skills saved."; return RedirectToPage(new { id }); }
        AgentId = id;
        var agent = await agents.GetAsync(account.Id, id, ct);
        Input = FromInput(AgentService.ToInput(agent!), agent!.ConcurrencyStamp);
        await LoadCatalogAsync(account.Id, ct);
        await LoadSkillsAsync(account.Id, id, ct);
        SkillErrors = errors;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        AgentId = id;

        var input = ToInput(Input);
        AgentResult result;
        if (id == null) result = await agents.CreateAsync(account.Id, input, ct);
        else
        {
            if (await agents.GetAsync(account.Id, id.Value, ct) == null) return NotFound();
            result = await agents.UpdateAsync(account.Id, id.Value, input, ct);
        }

        if (result.Success)
        {
            Message = "Saved.";
            return RedirectToPage(new { id = result.Agent!.Id });
        }

        Errors = result.Errors;
        await LoadCatalogAsync(account.Id, ct);
        await LoadSkillsAsync(account.Id, id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestSearchAsync(Guid id, CancellationToken ct)
    {
        var account = await accounts.GetAsync(ct);
        if (account == null) return Forbid();
        if (await agents.GetAsync(account.Id, id, ct) == null) return NotFound();
        var result = await searchProbe.RunAsync(account.Id, ct);
        SearchTestMessage = result.Message;
        SearchTestOk = result.Ok;
        return RedirectToPage(new { id });
    }

    private async Task LoadSkillsAsync(Guid accountId, Guid? agentId, CancellationToken ct)
    {
        AllSkills = await skillService.ListAsync(accountId, ct);
        if (agentId == null) return;
        var assigned = await skillService.ForAgentAsync(accountId, agentId.Value, ct);
        AssignedSkillIds = assigned.Select(a => a.Id).ToList();
        var agent = await agents.GetAsync(accountId, agentId.Value, ct);
        var prompt = agent?.SystemPrompt ?? "";
        PromptChars = prompt.Length;
        SkillChars = assigned.Sum(a => a.Content.Length);
        var blocks = InstructionAssembler.Assemble(prompt, assigned.Select(a => new SkillText(a.DisplayName, a.FileName, a.Version, a.Content)).ToList(), DateTimeOffset.UtcNow);
        AssembledPrompt = string.Join("\n\n---\n\n", blocks.Select(b => b["text"]!.GetValue<string>()));
    }

    private async Task LoadCatalogAsync(Guid accountId, CancellationToken ct)
    {
        try { Catalog = await credentials.GetCatalogAsync(accountId, false, ct); }
        catch { Catalog = null; }
    }

    private static Form FromInput(AgentInput i, byte[]? stamp) => new()
    {
        Name = i.Name, Description = i.Description, Enabled = i.Enabled, SystemPrompt = i.SystemPrompt,
        DefaultFamily = i.DefaultModelFamily.ToString(), PinnedModelId = i.PinnedModelId, ShowModelSelector = i.ShowModelSelector,
        AllowHaiku = i.AllowedModelFamilies.Contains(ModelFamily.Haiku), AllowSonnet = i.AllowedModelFamilies.Contains(ModelFamily.Sonnet),
        AllowOpus = i.AllowedModelFamilies.Contains(ModelFamily.Opus), MaxOutputTokens = i.MaxOutputTokens,
        Title = i.Appearance.Title, PrimaryColor = i.Appearance.PrimaryColor, TextOnPrimary = i.Appearance.TextOnPrimary,
        PanelBackground = i.Appearance.PanelBackground, IconPreset = i.Appearance.IconPreset, Position = i.Appearance.Position,
        PanelWidth = i.Appearance.PanelWidth, PanelHeight = i.Appearance.PanelHeight,
        WelcomeMessage = i.Appearance.WelcomeMessage, Placeholder = i.Appearance.Placeholder,
        OriginsText = string.Join("\n", i.Origins),
        WebSearchEnabled = i.WebSearchEnabled, WebSearchMaxUses = i.WebSearchMaxUses,
        WebSearchDomainMode = i.WebSearchDomainMode, WebSearchDomainsText = string.Join("\n", i.WebSearchDomains),
        TurnsPerSessionPerHour = i.Limits.TurnsPerSessionPerHour, TurnsPerIpPerHour = i.Limits.TurnsPerIpPerHour,
        TurnsPerDay = i.Limits.TurnsPerDay, MaxMessageChars = i.Limits.MaxMessageChars,
        ConcurrencyStamp = stamp == null ? null : Convert.ToBase64String(stamp),
    };

    private static AgentInput ToInput(Form f)
    {
        var allowed = new List<ModelFamily>();
        if (f.AllowHaiku) allowed.Add(ModelFamily.Haiku);
        if (f.AllowSonnet) allowed.Add(ModelFamily.Sonnet);
        if (f.AllowOpus) allowed.Add(ModelFamily.Opus);
        var familyOk = Enum.TryParse<ModelFamily>(f.DefaultFamily, true, out var family);
        byte[]? stamp = null;
        if (!string.IsNullOrEmpty(f.ConcurrencyStamp))
            try { stamp = Convert.FromBase64String(f.ConcurrencyStamp); } catch (FormatException) { }

        return new AgentInput
        {
            Name = f.Name ?? "", Description = f.Description, SystemPrompt = f.SystemPrompt ?? "",
            DefaultModelFamily = familyOk && Enum.IsDefined(family) ? family : (ModelFamily)(-1),
            PinnedModelId = f.PinnedModelId, ShowModelSelector = f.ShowModelSelector, AllowedModelFamilies = allowed,
            MaxOutputTokens = f.MaxOutputTokens, Enabled = f.Enabled, ConcurrencyStamp = stamp,
            WebSearchEnabled = f.WebSearchEnabled, WebSearchMaxUses = f.WebSearchMaxUses,
            WebSearchDomainMode = f.WebSearchDomainMode ?? "None",
            WebSearchDomains = (f.WebSearchDomainsText ?? "").Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Appearance = new AppearanceSettings
            {
                Title = f.Title ?? "", PrimaryColor = f.PrimaryColor ?? "", TextOnPrimary = f.TextOnPrimary ?? "",
                PanelBackground = f.PanelBackground ?? "", IconPreset = f.IconPreset ?? "", Position = f.Position ?? "",
                PanelWidth = f.PanelWidth, PanelHeight = f.PanelHeight,
                WelcomeMessage = f.WelcomeMessage ?? "", Placeholder = f.Placeholder ?? "",
            },
            Limits = new AgentLimits
            {
                TurnsPerSessionPerHour = f.TurnsPerSessionPerHour, TurnsPerIpPerHour = f.TurnsPerIpPerHour,
                TurnsPerDay = f.TurnsPerDay, MaxMessageChars = f.MaxMessageChars,
            },
            Origins = (f.OriginsText ?? "").Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
    }
}
