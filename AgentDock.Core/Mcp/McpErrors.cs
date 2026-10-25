using AgentDock.Core.Outbound;

namespace AgentDock.Core.Mcp;

public sealed record SanitizedError(string Code, string Message, bool TimedOut = false, int? HttpStatus = null);

/// <summary>Maps any exception from an outbound/MCP call to a message safe for owners and visitors (never a URL, header or server text).</summary>
public static class McpErrors
{
    public static SanitizedError Sanitize(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
        {
            switch (x)
            {
                case OutboundException o: return new(o.Code, OutboundMessage(o));
                case TimeoutException: return new("timeout", "The server did not respond in time.", true);
                case HttpRequestException { StatusCode: { } s }:
                    var code = (int)s;
                    return new("http_" + code, code is 401 or 403 ? "The server rejected the credentials." : $"The server responded with HTTP {code}.", HttpStatus: code);
                case ModelContextProtocol.McpException: return new("mcp_error", "The MCP server returned an error.");
            }
        }
        return new("failed", "Could not talk to the server.");
    }

    private static string OutboundMessage(OutboundException o) => o.Code switch
    {
        OutboundErrors.Timeout => "The server did not respond in time.",
        OutboundErrors.ResponseTooLarge => "The server's response was too large.",
        OutboundErrors.RedirectNotAllowed => "The server redirected the request, which is not allowed.",
        OutboundErrors.BlockedAddress or OutboundErrors.BlockedHost or OutboundErrors.BlockedPort or OutboundErrors.BlockedScheme =>
            "The address is not allowed (it must be a public https host).",
        OutboundErrors.DnsFailed => "The host name could not be resolved.",
        OutboundErrors.ConnectionFailed => "Could not connect to the server.",
        _ => "The request could not be sent.",
    };
}
