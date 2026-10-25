namespace AgentDock.Core.Data;

public class Secret
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Purpose { get; set; } = "";
    public string Algorithm { get; set; } = "A256GCM";
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = new byte[12];
    public byte[] Tag { get; set; } = new byte[16];
    public byte[] WrappedDek { get; set; } = [];
    public string KekId { get; set; } = "";
    public string? Hint { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ShreddedUtc { get; set; }
}

public class ProviderCredential
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AccountId { get; set; }
    public string Provider { get; set; } = "Anthropic";
    public Guid SecretId { get; set; }
    public string Status { get; set; } = "Unverified";
    public DateTime? LastVerifiedUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReplacedUtc { get; set; }
}

public class ModelCatalogSnapshot
{
    public Guid ProviderCredentialId { get; set; }
    public DateTime FetchedUtc { get; set; }
    public string ModelsJson { get; set; } = "[]";
    public string ResolvedJson { get; set; } = "{}";
}
