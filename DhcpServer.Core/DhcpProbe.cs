using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DhcpServer.Core;

/// <summary>Asks the network whether a DHCP server is already present, the way a new device would.</summary>
public static class DhcpProbe
{
    /// <summary>
    /// Broadcasts a DHCPDISCOVER from the adapter with the given address and returns every server
    /// that makes an offer. Blocks for <paramref name="timeout"/>, so call it off the UI thread.
    /// </summary>
    public static List<IPv4> FindServers(IPv4 localAddress, TimeSpan timeout)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows' own DHCP client also uses port 68, so the port has to be shared.
        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        DhcpEngine.IgnoreUnreachableReplies(udp);
        udp.Bind(new IPEndPoint(localAddress.ToIPAddress(), 68));

        var discover = new DhcpPacket
        {
            Xid = (uint)Random.Shared.NextInt64(1, uint.MaxValue),
            // Ask for a broadcast reply: a server has no address it could answer this request on directly.
            Flags = 0x8000,
        };
        // A made-up, locally administered MAC, so no real device's lease is disturbed.
        Random.Shared.NextBytes(discover.ChAddr.AsSpan(0, 6));
        discover.ChAddr[0] = 0x02;
        discover.Options[DhcpOption.MessageType] = [(byte)DhcpMessageType.Discover];
        var bytes = discover.Encode();

        var servers = new SortedSet<IPv4>();
        var buffer = new byte[4096];
        var clock = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;

        while (clock.Elapsed < timeout)
        {
            // Repeat the request every second in case a packet is lost.
            if (clock.Elapsed >= nextSend)
            {
                udp.SendTo(bytes, new IPEndPoint(IPAddress.Broadcast, 67));
                nextSend = clock.Elapsed + TimeSpan.FromSeconds(1);
            }

            var wait = nextSend < timeout ? nextSend - clock.Elapsed : timeout - clock.Elapsed;
            if (wait <= TimeSpan.Zero || !udp.Poll(wait, SelectMode.SelectRead)) continue;

            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            int count;
            try
            {
                count = udp.ReceiveFrom(buffer, ref sender);
            }
            catch (SocketException)
            {
                continue;
            }
            if (DhcpPacket.Parse(buffer.AsSpan(0, count)) is not { Op: 2 } reply
                || reply.Xid != discover.Xid || reply.MessageType != DhcpMessageType.Offer) continue;
            if (reply.Address(DhcpOption.ServerID) is { } server) servers.Add(server);
            else if (reply.SiAddr != IPv4.Zero) servers.Add(reply.SiAddr);
        }
        return servers.ToList();
    }
}
