using System.Net;
using System.Net.Sockets;

namespace SocklessMail.Api.Mail;

/// <summary>
/// Opens TCP connections to user-supplied hosts only after every resolved address passed
/// <see cref="ServerAddressPolicy"/>. Callers get the connected socket, so the address used is
/// the one that was checked (no DNS rebinding between check and connect). Used for mail servers
/// and for the remote image proxy.
/// </summary>
public sealed class PolicySocketConnector(ServerAddressPolicy policy)
{
    public async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException e)
            {
                throw new MailConnectionException("host_not_found", $"The server name \"{host}\" could not be resolved.", e);
            }
        }

        var allowed = addresses.Where(policy.IsAllowed).ToArray();
        if (addresses.Length > 0 && allowed.Length == 0)
        {
            throw new MailConnectionException("address_not_allowed",
                $"\"{host}\" points to a private or reserved network address. An administrator can allow local mail servers with SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS.");
        }

        SocketException? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, port, ct);
                return socket;
            }
            catch (SocketException e)
            {
                last = e;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }
}
