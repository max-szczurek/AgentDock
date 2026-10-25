using System.Text.Json.Nodes;

namespace AgentDock.Core.Runtime;

public sealed record CitationSource(int Index, string Url, string Title, string CitedText);

/// <summary>
/// Numbers web sources across a whole turn (several model calls when a search pauses the turn), de-duplicated by URL.
/// Markers like ⟦2⟧ are inserted after the cited text; the widget turns them into superscript links.
/// </summary>
public sealed class CitationRegistry
{
    private readonly List<CitationSource> _sources = [];
    public IReadOnlyList<CitationSource> Sources => _sources;

    public static string Marker(int index) => $"\u27E6{index}\u27E7";

    public CitationSource? Register(JsonObject citation)
    {
        if (citation["type"]?.GetValue<string>() != "web_search_result_location") return null;
        var url = citation["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url)) return null;
        var existing = _sources.FirstOrDefault(s => s.Url == url);
        if (existing != null) return existing;
        var cited = citation["cited_text"]?.GetValue<string>() ?? "";
        var source = new CitationSource(_sources.Count + 1, url, citation["title"]?.GetValue<string>() ?? url,
            cited.Length > 300 ? cited[..300] : cited);
        _sources.Add(source);
        return source;
    }
}
