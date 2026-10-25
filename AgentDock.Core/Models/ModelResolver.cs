namespace AgentDock.Core.Models;

/// <summary>Pure family resolution (plan section 5.2). Never infers a line from an id; never hardcodes ids.</summary>
public static class ModelResolver
{
    public static IReadOnlyDictionary<ModelFamily, ResolvedModel> ResolveFamilies(IEnumerable<ModelInfoDto> models, DateTimeOffset now)
    {
        var list = models.ToList();
        var result = new Dictionary<ModelFamily, ResolvedModel>();
        foreach (var family in Enum.GetValues<ModelFamily>())
        {
            var line = family.ToString().ToLowerInvariant();
            var inLine = list.Where(m => string.Equals(m.Line, line, StringComparison.Ordinal)).ToList();

            var pick = Newest(inLine.Where(m => m.Lifecycle == "active"));
            var fallback = false;
            if (pick == null)
            {
                pick = Newest(inLine.Where(m => m.Lifecycle == "deprecated" && (m.RetiresAt == null || m.RetiresAt > now)));
                fallback = pick != null;
            }
            if (pick != null) result[family] = ToResolved(pick, family, fallback);
        }
        return result;
    }

    /// <summary>A pinned model wins if present and not retired; null means fall back to the family rule.</summary>
    public static ResolvedModel? ResolvePinned(IEnumerable<ModelInfoDto> models, string pinnedId)
    {
        var m = models.FirstOrDefault(x => x.Id == pinnedId && x.Lifecycle != "retired");
        if (m == null || !Enum.TryParse<ModelFamily>(m.Line, ignoreCase: true, out var family)) return null;
        return ToResolved(m, family, false);
    }

    /// <summary>default, then sonnet, haiku, opus; skipping unavailable families.</summary>
    public static ModelFamily? FallbackOrder(ModelFamily preferred, IReadOnlyDictionary<ModelFamily, ResolvedModel> available)
    {
        foreach (var f in new[] { preferred, ModelFamily.Sonnet, ModelFamily.Haiku, ModelFamily.Opus })
            if (available.ContainsKey(f)) return f;
        return null;
    }

    private static ModelInfoDto? Newest(IEnumerable<ModelInfoDto> models) =>
        models.OrderByDescending(m => m.CreatedAt ?? DateTimeOffset.MinValue)
              .ThenByDescending(m => m.Id, StringComparer.Ordinal).FirstOrDefault();

    private static ResolvedModel ToResolved(ModelInfoDto m, ModelFamily f, bool fallback) =>
        new(m.Id, f, m.DisplayName, m.MaxTokens ?? 4096, m.SupportsWebSearch, m.SupportsProgrammaticToolCalling, fallback, m.CreatedAt);
}
