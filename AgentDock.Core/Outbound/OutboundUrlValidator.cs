using System.Net;

namespace AgentDock.Core.Outbound;

public static class OutboundUrlValidator
{
    private static readonly string[] BlockedSuffixes = [".internal", ".local", ".localhost", ".localdomain", ".home.arpa"];

    /// <summary>Returns null when the URL may be called, otherwise an OutboundException describing why not.</summary>
    public static OutboundException? Validate(Uri url, OutboundOptions options)
    {
        if (!url.IsAbsoluteUri) return new(OutboundErrors.InvalidRequest, "The URL must be absolute.");
        if (!string.IsNullOrEmpty(url.UserInfo)) return new(OutboundErrors.InvalidRequest, "URLs with credentials are not allowed.");

        var isHttps = url.Scheme == Uri.UriSchemeHttps;
        var isHttp = url.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !(isHttp && options.AllowsHttp)) return new(OutboundErrors.BlockedScheme, "Only https URLs are allowed.");
        if (!options.AllowedPorts.Contains(url.Port)) return new(OutboundErrors.BlockedPort, $"Port {url.Port} is not allowed.");

        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(url.Host.Trim('[', ']'), out _))
            return new(OutboundErrors.BlockedHost, "IP address hosts are not allowed.");

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || !host.Contains('.')) return new(OutboundErrors.BlockedHost, "The host name is not allowed.");
        if (BlockedSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) || host == "localhost")
            return new(OutboundErrors.BlockedHost, "Internal host names are not allowed.");
        if (options.SelfHosts.Any(s => string.Equals(s.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase)))
            return new(OutboundErrors.BlockedHost, "Calling AgentDock itself is not allowed.");
        return null;
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
