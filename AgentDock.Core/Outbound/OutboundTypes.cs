using System.Net;

namespace AgentDock.Core.Outbound;

public sealed class OutboundOptions
{
    /// <summary>Only honoured when <see cref="IsDevelopment"/> is true.</summary>
    public bool AllowHttpForDevelopment { get; set; }
    public bool IsDevelopment { get; set; }
    public List<int> AllowedPorts { get; set; } = [443];
    /// <summary>Host names of AgentDock itself; outbound calls to them are refused.</summary>
    public List<string> SelfHosts { get; set; } = [];
    public int MaxRedirects { get; set; } = 3;
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool AllowsHttp => IsDevelopment && AllowHttpForDevelopment;
}

public sealed class OutboundCallOptions
{
    public int MaxBytes { get; set; } = 256 * 1024;
    public TimeSpan? Timeout { get; set; }
    /// <summary>0 means a 3xx response is an error (MCP). Otherwise capped by <see cref="OutboundOptions.MaxRedirects"/>.</summary>
    public int MaxRedirects { get; set; } = 3;
}

public sealed record OutboundCredential(string HeaderName, string Value);

public sealed class OutboundRequest
{
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public required Uri Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }
    /// <summary>Attached only to requests whose origin equals <see cref="CredentialBaseUrl"/>'s origin, on every hop.</summary>
    public OutboundCredential? Credential { get; init; }
    public Uri? CredentialBaseUrl { get; init; }
}

public sealed record OutboundResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body, Uri FinalUrl, int Redirects);

public static class OutboundErrors
{
    public const string BlockedScheme = "blocked_scheme";
    public const string BlockedPort = "blocked_port";
    public const string BlockedHost = "blocked_host";
    public const string BlockedAddress = "blocked_address";
    public const string DnsFailed = "dns_failed";
    public const string TooManyRedirects = "too_many_redirects";
    public const string RedirectNotAllowed = "redirect_not_allowed";
    public const string ResponseTooLarge = "response_too_large";
    public const string Timeout = "timeout";
    public const string ConnectionFailed = "connection_failed";
    public const string InvalidRequest = "invalid_request";
}

/// <summary>Expected outbound failure. The message never contains the URL path/query or credentials.</summary>
public sealed class OutboundException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

public interface IOutboundClient
{
    Task<OutboundResponse> SendAsync(OutboundRequest request, OutboundCallOptions? options = null, CancellationToken ct = default);
}
