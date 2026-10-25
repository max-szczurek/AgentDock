namespace AgentDock.Core.Data;

public class SkillFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public string Content { get; set; } = "";
    public byte[] ContentSha256 { get; set; } = [];
    public int SizeBytes { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedUtc { get; set; }
}

public class AgentSkill
{
    public Guid AccountId { get; set; }
    public Guid AgentId { get; set; }
    public Guid SkillFileId { get; set; }
    public int SortOrder { get; set; }
}
