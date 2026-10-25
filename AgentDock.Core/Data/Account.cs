namespace AgentDock.Core.Data;

public class Account
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string OwnerUserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
