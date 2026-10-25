namespace AgentDock.Core.Models;

public enum ModelFamily { Haiku, Sonnet, Opus }

public sealed record ModelInfoDto(
    string Id, string DisplayName, DateTimeOffset? CreatedAt, string Lifecycle, string? Line,
    int? MaxTokens, int? MaxInputTokens, DateTimeOffset? RetiresAt, bool SupportsWebSearch, bool SupportsProgrammaticToolCalling);

public sealed record ResolvedModel(
    string ModelId, ModelFamily Family, string DisplayName, int MaxOutputTokens,
    bool SupportsWebSearch, bool SupportsProgrammaticToolCalling, bool IsFallback, DateTimeOffset? CreatedAt);
