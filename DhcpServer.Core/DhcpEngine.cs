using System.Net;
using System.Net.Sockets;

namespace DhcpServer.Core;

public sealed class EngineException(string message) : Exception(message);

/// <summary>Owns the UDP socket on port 67 and feeds incoming messages to a <see cref="LeaseTable"/>.</summary>
public sealed class DhcpEngine
{
    private Socket? socket;

    /// <summary>Raised on a background thread.</summary>
    public event Action<string>? Log;
    /// <summary>Raised on a background thread.</summary>
    public event Action<IReadOnlyList<Lease>>? LeasesChanged;

    public void Start(ResolvedConfig config, IEnumerable<Lease> leases)
    {
        Stop();

        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            udp.EnableBroadcast = true;
            IgnoreUnreachableReplies(udp);
            // Binding to the adapter's own address ties the server to that network only.
            udp.Bind(new IPEndPoint(config.ServerIP.ToIPAddress(), 67));
        }
        catch (SocketException error)
        {
            udp.Dispose();
            throw new EngineException(error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                ? "Another DHCP server on this PC is already using port 67. Turn off Internet Connection Sharing, Mobile hotspot or any other DHCP server and try again."
                : $"Could not listen on the DHCP port: {error.Message}");
        }

        var table = new LeaseTable(config, leases) { Log = message => Log?.Invoke(message) };
        socket = udp;
        new Thread(() => Receive(udp, table)) { IsBackground = true, Name = "DHCP" }.Start();
        LeasesChanged?.Invoke(table.Snapshot());
    }

    public void Stop()
    {
        // Closing the socket also ends the receive thread.
        socket?.Dispose();
        socket = null;
    }

    private void Receive(Socket udp, LeaseTable table)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int count;
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                count = udp.ReceiveFrom(buffer, ref sender);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                return;
            }

            // Relayed requests (giaddr set) belong to other subnets, which this server doesn't cover.
            if (DhcpPacket.Parse(buffer.AsSpan(0, count)) is not { } request || request.GiAddr != IPv4.Zero) continue;

            var reply = table.Handle(request);
            if (table.Changed)
            {
                table.Changed = false;
                LeasesChanged?.Invoke(table.Snapshot());
            }
            if (reply == null) continue;
            try
            {
                udp.SendTo(reply.Packet.Encode(), new IPEndPoint(reply.Destination.ToIPAddress(), 68));
            }
            catch (SocketException error)
            {
                Log?.Invoke($"Could not send a reply: {error.Message}");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// On Windows, a reply sent to a device that is no longer listening comes back as a
    /// "connection reset" error on the next receive. This turns that behaviour off.
    /// </summary>
    internal static void IgnoreUnreachableReplies(Socket udp)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
    }
}
