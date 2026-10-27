using System.Net;
using System.Net.Sockets;

namespace SocklessMail.Api.Mail;

/// <summary>
/// Which IP addresses a user-supplied mail server may resolve to. Any Nextcloud user can make
/// this app open connections, so without a policy it would be a probe into the network it runs
/// in (HaRP, Nextcloud, cloud metadata endpoints). Private and loopback ranges need the admin
/// setting SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS; link-local, multicast and unspecified addresses
/// are never allowed.
/// </summary>
public sealed class ServerAddressPolicy(bool allowLocal)
{
    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IsAlwaysBlocked(address))
        {
            return false;
        }

        return allowLocal || !IsLocal(address);
    }

    private static bool IsAlwaysBlocked(IPAddress a)
    {
        if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return a.IsIPv6LinkLocal || a.IsIPv6Multicast || a.IsIPv6SiteLocal;
        }

        var b = a.GetAddressBytes();
        return b[0] == 0                          // 0.0.0.0/8 "this network"
            || (b[0] == 169 && b[1] == 254)      // link-local, incl. cloud metadata 169.254.169.254
            || b[0] >= 224;                       // multicast and reserved
    }

    private static bool IsLocal(IPAddress a)
    {
        if (IPAddress.IsLoopback(a))
        {
            return true;
        }

        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return a.IsIPv6UniqueLocal; // fc00::/7
        }

        var b = a.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT
    }
}
