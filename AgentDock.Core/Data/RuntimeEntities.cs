namespace AgentDock.Core.Data;

public class VisitorSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string Mode { get; set; } = "Public";
    public string? ExternalUserId { get; set; }
    public string? ExternalDisplayName { get; set; }
    public string HostOrigin { get; set; } = "";
    public byte[] IpHash { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public Guid? ExternalSessionId { get; set; }
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public Guid VisitorSessionId { get; set; }
    public string? ExternalUserId { get; set; }
    public string Title { get; set; } = "";
    public Guid? ActiveTurnId { get; set; }
    public DateTime? ActiveTurnExpiresUtc { get; set; }
    public Guid? ExternalSessionId { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
}

public class ConversationMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public Guid ConversationId { get; set; }
    public int Sequence { get; set; }
    public Guid TurnId { get; set; }
    public string Role { get; set; } = "user";
    public string ContentJson { get; set; } = "[]";
    public string DisplayText { get; set; } = "";
    public string? CitationsJson { get; set; }
    public string? ModelId { get; set; }
    public string? ModelFamily { get; set; }
    public string? StopReason { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public int WebSearchRequests { get; set; }
    public bool IsIncomplete { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class UsageDaily
{
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public DateOnly DateUtc { get; set; }
    public int Turns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int WebSearches { get; set; }
    public int ToolCalls { get; set; }
    public int RejectedRequests { get; set; }
}
