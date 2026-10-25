using System.Net;
using System.Net.Sockets;

namespace AgentDock.Core.Outbound;

/// <summary>
/// SocketsHttpHandler for every user-configured outbound call. DNS is resolved once in the connect callback, every
/// address must pass the IP policy, and the socket connects to the validated address (no second lookup, so no rebinding).
/// </summary>
public static class SafeOutboundHandler
{
    public static SocketsHttpHandler Create(OutboundOptions options, IDnsResolver resolver, IIpPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            if (!options.AllowedPorts.Contains(port)) throw new OutboundException(OutboundErrors.BlockedPort, $"Port {port} is not allowed.");

            IPAddress[] addresses;
            try { addresses = await resolver.ResolveAsync(host, ct); }
            catch (Exception e) when (e is SocketException or ArgumentException) { throw new OutboundException(OutboundErrors.DnsFailed, "The host name could not be resolved.", e); }
            if (addresses.Length == 0) throw new OutboundException(OutboundErrors.DnsFailed, "The host name could not be resolved.");
            if (addresses.Any(policy.IsBlocked)) throw new OutboundException(OutboundErrors.BlockedAddress, "The host resolves to a blocked address.");

            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e) when (e is SocketException)
                {
                    socket.Dispose();
                    last = e;
                }
            }
            throw new OutboundException(OutboundErrors.ConnectionFailed, "Could not connect to the host.", last);
        },
    };
}
