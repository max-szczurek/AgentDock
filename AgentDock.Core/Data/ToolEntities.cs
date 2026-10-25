namespace AgentDock.Core.Data;

public class Connection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Mcp";
    public string BaseUrl { get; set; } = "";
    public string AuthType { get; set; } = "None";
    public string? AuthHeaderName { get; set; }
    public Guid? SecretId { get; set; }
    public string? StaticHeadersJson { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string Status { get; set; } = "Untested";
    public DateTime? LastTestedUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
}

public class McpToolSnapshot
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ToolName { get; set; } = "";
    public string? Title { get; set; }
    public string Description { get; set; } = "";
    public string InputSchemaJson { get; set; } = "{}";
    public string? AnnotationsJson { get; set; }
    public byte[] SchemaHash { get; set; } = [];
    public bool IsPresent { get; set; } = true;
    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;
}

public class AgentConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Alias { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class AgentMcpTool
{
    public Guid AccountId { get; set; }
    public Guid AgentConnectionId { get; set; }
    public string ToolName { get; set; } = "";
    public bool RequiresConfirmation { get; set; } = true;
    public byte[] ApprovedSchemaHash { get; set; } = [];
}

public class ToolExecution
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid TurnId { get; set; }
    public string ToolUseId { get; set; } = "";
    public string ExposedName { get; set; } = "";
    public string Kind { get; set; } = "Mcp";
    public Guid? ConnectionId { get; set; }
    public Guid? HttpToolId { get; set; }
    public string? UpstreamToolName { get; set; }
    public string? InputJson { get; set; }
    public string Status { get; set; } = "Running";
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? HttpStatus { get; set; }
    public string? ResultPreview { get; set; }
    public int? ResultBytes { get; set; }
    public int? DurationMs { get; set; }
    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public string? ResultContent { get; set; }
    public bool? ResultIsError { get; set; }
}
