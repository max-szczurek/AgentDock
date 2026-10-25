using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Outbound;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentDock.Core.Mcp;

public sealed record ConnectionAccess(
    Guid ConnectionId, Uri BaseUrl, OutboundCredential? Credential, IReadOnlyDictionary<string, string> StaticHeaders, int TimeoutSeconds);

public sealed record McpToolInfo(string Name, string? Title, string Description, string InputSchemaJson, string? AnnotationsJson);

public sealed record McpCallResult(bool IsError, string Text, int Bytes);

public interface IMcpGateway
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(ConnectionAccess access, CancellationToken ct);
    Task<McpCallResult> CallToolAsync(ConnectionAccess access, string toolName, JsonObject arguments, CancellationToken ct);
}

/// <summary>Talks to a remote MCP server through the SSRF-guarded HttpClient (the SDK never sees a raw network client).</summary>
public sealed class McpGateway(IOutboundHttpClientFactory outbound) : IMcpGateway
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(ConnectionAccess access, CancellationToken ct)
    {
        await using var client = await ConnectAsync(access, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new McpToolInfo(
            t.Name, string.IsNullOrWhiteSpace(t.ProtocolTool.Title) ? null : t.ProtocolTool.Title, t.Description ?? "",
            t.JsonSchema.GetRawText(), t.ProtocolTool.Annotations is { } a ? JsonSerializer.Serialize(a, ModelContextProtocol.McpJsonUtilities.DefaultOptions) : null)).ToList();
    }

    public async Task<McpCallResult> CallToolAsync(ConnectionAccess access, string toolName, JsonObject arguments, CancellationToken ct)
    {
        await using var client = await ConnectAsync(access, ct);
        var args = arguments.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.DeepClone());
        var result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (sb.Length > 0) sb.Append('\n');
            switch (block)
            {
                case TextContentBlock t: sb.Append(t.Text); break;
                case ImageContentBlock: sb.Append("[image omitted]"); break;
                case AudioContentBlock: sb.Append("[audio omitted]"); break;
                case EmbeddedResourceBlock: sb.Append("[embedded resource omitted]"); break;
                case ResourceLinkBlock l: sb.Append("[resource link: ").Append(l.Name).Append(']'); break;
                default: sb.Append("[unsupported content omitted]"); break;
            }
        }
        var text = sb.ToString();
        return new McpCallResult(result.IsError == true, text, Encoding.UTF8.GetByteCount(text));
    }

    private async Task<McpClient> ConnectAsync(ConnectionAccess access, CancellationToken ct)
    {
        var http = outbound.CreateHttpClient(new OutboundHttpClientOptions
        {
            BaseUrl = access.BaseUrl, Credential = access.Credential, StaticHeaders = access.StaticHeaders, MaxResponseBytes = MaxResponseBytes,
        }, Timeout.InfiniteTimeSpan);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = access.BaseUrl, Name = "agentdock" }, http, ownsHttpClient: true);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            return await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "AgentDock", Version = "1.0" },
            }, cancellationToken: connect.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The MCP server did not respond while connecting.");
        }
    }
}
