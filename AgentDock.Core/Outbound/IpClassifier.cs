using System.Net;
using System.Net.Sockets;

namespace AgentDock.Core.Outbound;

public interface IIpPolicy
{
    bool IsBlocked(IPAddress address);
}

public sealed class DefaultIpPolicy : IIpPolicy
{
    public bool IsBlocked(IPAddress address) => IpClassifier.IsBlocked(address);
}

/// <summary>Classifies addresses an outbound call must never reach (private, loopback, link-local, metadata, multicast, reserved).</summary>
public static class IpClassifier
{
    private static readonly (IPAddress Net, int Bits)[] V4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8), (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8), (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24), (IPAddress.Parse("192.168.0.0"), 16), (IPAddress.Parse("198.18.0.0"), 15),
        (IPAddress.Parse("224.0.0.0"), 4), (IPAddress.Parse("240.0.0.0"), 4),
        (IPAddress.Parse("168.63.129.16"), 32),
    ];

    private static readonly (IPAddress Net, int Bits)[] V6 =
    [
        (IPAddress.Parse("::"), 128), (IPAddress.Parse("::1"), 128), (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("fe80::"), 10), (IPAddress.Parse("fec0::"), 10), (IPAddress.Parse("ff00::"), 8),
        (IPAddress.Parse("100::"), 64), (IPAddress.Parse("2001::"), 32), (IPAddress.Parse("2001:db8::"), 32),
        (IPAddress.Parse("64:ff9b:1::"), 48),
    ];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork) return InRanges(address, V4);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;

        if (address.IsIPv4MappedToIPv6) return IsBlocked(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (InRanges(address, V6)) return true;
        // IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96) and 6to4 (2002::/16) embed an IPv4 address; classify that too.
        if (b.Take(12).All(x => x == 0)) return IsBlocked(new IPAddress(b[12..16]));
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0)) return IsBlocked(new IPAddress(b[12..16]));
        if (b[0] == 0x20 && b[1] == 0x02) return IsBlocked(new IPAddress(b[2..6]));
        return false;
    }

    private static bool InRanges(IPAddress address, (IPAddress Net, int Bits)[] ranges)
    {
        var a = address.GetAddressBytes();
        foreach (var (net, bits) in ranges)
        {
            var n = net.GetAddressBytes();
            if (n.Length != a.Length) continue;
            if (Matches(a, n, bits)) return true;
        }
        return false;
    }

    private static bool Matches(byte[] a, byte[] n, int bits)
    {
        var full = bits / 8;
        for (var i = 0; i < full; i++) if (a[i] != n[i]) return false;
        var rem = bits % 8;
        if (rem == 0) return true;
        var mask = (byte)(0xFF << (8 - rem));
        return (a[full] & mask) == (n[full] & mask);
    }
}
