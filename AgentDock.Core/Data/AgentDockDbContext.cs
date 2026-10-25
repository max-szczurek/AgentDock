using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Core.Data;

public class AgentDockDbContext(DbContextOptions<AgentDockDbContext> options)
    : IdentityDbContext<IdentityUser>(options), IDataProtectionKeyContext
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentAllowedOrigin> AgentAllowedOrigins => Set<AgentAllowedOrigin>();
    public DbSet<SkillFile> SkillFiles => Set<SkillFile>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<McpToolSnapshot> McpToolSnapshots => Set<McpToolSnapshot>();
    public DbSet<AgentConnection> AgentConnections => Set<AgentConnection>();
    public DbSet<AgentMcpTool> AgentMcpTools => Set<AgentMcpTool>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<HttpTool> HttpTools => Set<HttpTool>();
    public DbSet<AgentHttpTool> AgentHttpTools => Set<AgentHttpTool>();
    public DbSet<VisitorSession> VisitorSessions => Set<VisitorSession>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<UsageDaily> UsageDailies => Set<UsageDaily>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<ModelCatalogSnapshot> ModelCatalogSnapshots => Set<ModelCatalogSnapshot>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Account>(e =>
        {
            e.ToTable("Account");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedUtc).HasColumnType("datetime2(3)");
            e.HasIndex(x => x.OwnerUserId).IsUnique();
            e.HasOne<IdentityUser>().WithOne().HasForeignKey<Account>(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Agent>(e =>
        {
            e.ToTable("Agent");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.DefaultModelFamily).HasMaxLength(10);
            e.Property(x => x.PinnedModelId).HasMaxLength(100);
            e.Property(x => x.AllowedModelFamilies).HasMaxLength(30);
            e.Property(x => x.WebSearchDomainMode).HasMaxLength(10);
            e.Property(x => x.IdentityMode).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.ConcurrencyStamp).IsRowVersion();
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.DeletedUtc).HasColumnType("datetime2(3)");
            e.HasMany(x => x.Origins).WithOne().HasForeignKey(o => o.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AgentAllowedOrigin>(e =>
        {
            e.ToTable("AgentAllowedOrigin");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Origin).HasMaxLength(300);
        });

        builder.Entity<SkillFile>(e =>
        {
            e.ToTable("SkillFile");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.ContentSha256).HasColumnType("binary(32)");
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.DeletedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<AgentSkill>(e =>
        {
            e.ToTable("AgentSkill");
            e.HasKey(x => new { x.AgentId, x.SkillFileId });
        });

        builder.Entity<Connection>(e =>
        {
            e.ToTable("Connection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.BaseUrl).HasMaxLength(2048);
            e.Property(x => x.AuthType).HasMaxLength(20);
            e.Property(x => x.AuthHeaderName).HasMaxLength(100);
            e.Property(x => x.Status).HasMaxLength(10);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.LastTestedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.DeletedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<McpToolSnapshot>(e =>
        {
            e.ToTable("McpToolSnapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.SchemaHash).HasColumnType("binary(32)");
            e.Property(x => x.DiscoveredUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<AgentConnection>(e =>
        {
            e.ToTable("AgentConnection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Alias).HasMaxLength(24);
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<AgentMcpTool>(e =>
        {
            e.ToTable("AgentMcpTool");
            e.HasKey(x => new { x.AgentConnectionId, x.ToolName });
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.ApprovedSchemaHash).HasColumnType("binary(32)");
        });

        builder.Entity<HttpTool>(e =>
        {
            e.ToTable("HttpTool");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.DeletedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<AgentHttpTool>(e =>
        {
            e.ToTable("AgentHttpTool");
            e.HasKey(x => new { x.AgentId, x.HttpToolId });
        });

        builder.Entity<ToolExecution>(e =>
        {
            e.ToTable("ToolExecution");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ToolUseId).HasMaxLength(100);
            e.Property(x => x.ExposedName).HasMaxLength(128);
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.UpstreamToolName).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.ErrorCode).HasMaxLength(60);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.ResultPreview).HasMaxLength(4000);
            e.Property(x => x.RequestedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.DecidedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.StartedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.CompletedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.ExpiresUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<VisitorSession>(e =>
        {
            e.ToTable("VisitorSession");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TokenHash).HasColumnType("binary(32)");
            e.Property(x => x.IpHash).HasColumnType("binary(32)");
            e.Property(x => x.Mode).HasMaxLength(10);
            e.Property(x => x.ExternalUserId).HasMaxLength(200);
            e.Property(x => x.ExternalDisplayName).HasMaxLength(200);
            e.Property(x => x.HostOrigin).HasMaxLength(300);
            foreach (var p in new[] { nameof(VisitorSession.CreatedUtc), nameof(VisitorSession.LastSeenUtc), nameof(VisitorSession.ExpiresUtc), nameof(VisitorSession.RevokedUtc) })
                e.Property(p).HasColumnType("datetime2(3)");
        });

        builder.Entity<Conversation>(e =>
        {
            e.ToTable("Conversation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ExternalUserId).HasMaxLength(200);
            foreach (var p in new[] { nameof(Conversation.CreatedUtc), nameof(Conversation.UpdatedUtc), nameof(Conversation.DeletedUtc), nameof(Conversation.ActiveTurnExpiresUtc) })
                e.Property(p).HasColumnType("datetime2(3)");
        });

        builder.Entity<ConversationMessage>(e =>
        {
            e.ToTable("ConversationMessage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Role).HasMaxLength(10);
            e.Property(x => x.ModelId).HasMaxLength(100);
            e.Property(x => x.ModelFamily).HasMaxLength(10);
            e.Property(x => x.StopReason).HasMaxLength(30);
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<UsageDaily>(e =>
        {
            e.ToTable("UsageDaily");
            e.HasKey(x => new { x.AgentId, x.DateUtc });
            e.Property(x => x.DateUtc).HasColumnType("date");
        });

        builder.Entity<Secret>(e =>
        {
            e.ToTable("Secret");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Purpose).HasMaxLength(40);
            e.Property(x => x.Algorithm).HasMaxLength(20);
            e.Property(x => x.Nonce).HasColumnType("binary(12)");
            e.Property(x => x.Tag).HasColumnType("binary(16)");
            e.Property(x => x.WrappedDek).HasMaxLength(512);
            e.Property(x => x.KekId).HasMaxLength(400);
            e.Property(x => x.Hint).HasMaxLength(32);
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.ShreddedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<ProviderCredential>(e =>
        {
            e.ToTable("ProviderCredential");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.LastErrorCode).HasMaxLength(60);
            e.Property(x => x.CreatedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.LastVerifiedUtc).HasColumnType("datetime2(3)");
            e.Property(x => x.ReplacedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<ModelCatalogSnapshot>(e =>
        {
            e.ToTable("ModelCatalogSnapshot");
            e.HasKey(x => x.ProviderCredentialId);
            e.Property(x => x.ProviderCredentialId).ValueGeneratedNever();
            e.Property(x => x.FetchedUtc).HasColumnType("datetime2(3)");
        });

        builder.Entity<DataProtectionKey>().ToTable("DataProtectionKeys");
    }
}
