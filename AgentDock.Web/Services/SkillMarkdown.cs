using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentDock.Web.Services;

/// <summary>Owner-only preview of skill Markdown: raw HTML disabled, images dropped, only http(s)/mailto/# links kept.</summary>
public static class SkillMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string Render(string markdown)
    {
        var doc = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
            {
                var text = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                link.ReplaceBy(new LiteralInline(string.IsNullOrEmpty(text) ? "[image]" : $"[image: {text}]"));
            }
            else if (!SafeUrl(link.Url)) link.Url = "#";
            else { link.GetAttributes().AddProperty("rel", "noopener noreferrer nofollow"); link.GetAttributes().AddProperty("target", "_blank"); }
        }
        return doc.ToHtml(Pipeline);
    }

    private static bool SafeUrl(string? url) =>
        !string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith('#'));
}
