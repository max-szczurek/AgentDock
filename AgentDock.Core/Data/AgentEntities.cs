namespace AgentDock.Core.Data;

public class Agent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string SystemPrompt { get; set; } = "";
    public string DefaultModelFamily { get; set; } = "sonnet";
    public string? PinnedModelId { get; set; }
    public bool ShowModelSelector { get; set; }
    public string AllowedModelFamilies { get; set; } = "haiku,sonnet,opus";
    public int MaxOutputTokens { get; set; } = 4096;
    public bool WebSearchEnabled { get; set; }
    public int WebSearchMaxUses { get; set; } = 5;
    public string WebSearchDomainMode { get; set; } = "None";
    public string? WebSearchDomainsJson { get; set; }
    public string IdentityMode { get; set; } = "Public";
    public string AppearanceJson { get; set; } = "{}";
    public string? LimitsJson { get; set; }
    public string Status { get; set; } = "Disabled";
    public byte[] ConcurrencyStamp { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
    public List<AgentAllowedOrigin> Origins { get; set; } = [];
}

public class AgentAllowedOrigin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public string Origin { get; set; } = "";
}
