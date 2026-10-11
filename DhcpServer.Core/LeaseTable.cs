namespace DhcpServer.Core;

public enum LeaseState
{
    Offered, Active, Declined,
}

public sealed record Lease(IPv4 Ip, string Mac, string Hostname, DateTime Expires, LeaseState State);

public sealed record DhcpReply(DhcpPacket Packet, IPv4 Destination);

/// <summary>The address-allocation logic. Not thread-safe; the engine drives it from one thread.</summary>
public sealed class LeaseTable
{
    private static readonly TimeSpan OfferHold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeclineHold = TimeSpan.FromSeconds(600);

    private readonly Dictionary<IPv4, Lease> leases = new();
    private readonly Dictionary<IPv4, string> reservedOwners;

    public ResolvedConfig Config { get; }
    public Action<string> Log { get; set; } = _ => { };
    /// <summary>Set whenever the leases change; the caller clears it.</summary>
    public bool Changed { get; set; }

    public LeaseTable(ResolvedConfig config, IEnumerable<Lease>? saved = null)
    {
        Config = config;
        reservedOwners = config.Reservations.ToDictionary(pair => pair.Value, pair => pair.Key);
        // Keep earlier leases only where they still agree with the current settings.
        foreach (var lease in saved ?? [])
        {
            if (lease.State != LeaseState.Active) continue;
            var reservedHere = config.Reservations.TryGetValue(lease.Mac, out var reserved) && reserved == lease.Ip;
            if (reservedHere || (config.InPool(lease.Ip) && IsFree(lease.Ip, lease.Mac, DateTime.MinValue)))
            {
                leases[lease.Ip] = lease;
            }
        }
    }

    public IReadOnlyList<Lease> Snapshot() => leases.Values.OrderBy(lease => lease.Ip).ToList();

    public Lease? LeaseFor(IPv4 ip) => leases.GetValueOrDefault(ip);

    public DhcpReply? Handle(DhcpPacket request, DateTime? at = null)
    {
        if (request.Op != 1 || request.HType != 1 || request.HLen != 6 || request.MessageType is not { } type) return null;
        var now = at ?? DateTime.UtcNow;
        foreach (var stale in leases.Values.Where(l => l.State != LeaseState.Active && l.Expires <= now).ToList())
        {
            leases.Remove(stale.Ip);
            Changed = true;
        }

        switch (type)
        {
            case DhcpMessageType.Discover: return Discover(request, now);
            case DhcpMessageType.Request: return Request(request, now);
            case DhcpMessageType.Decline: Decline(request, now); break;
            case DhcpMessageType.Release: Release(request); break;
            case DhcpMessageType.Inform: return Inform(request);
        }
        return null;
    }

    // Messages

    private DhcpReply? Discover(DhcpPacket request, DateTime now)
    {
        var mac = request.Mac;
        if (ChooseAddress(mac, request.Address(DhcpOption.RequestedIP), now) is not { } ip)
        {
            Log($"No free address for {Describe(request)} — the pool is full");
            return null;
        }
        Forget(mac, ip);
        Set(new Lease(ip, mac, request.Hostname, now + OfferHold, LeaseState.Offered));
        Log($"Offered {ip} to {Describe(request)}");
        return Reply(request, DhcpMessageType.Offer, ip, withLease: true);
    }

    private DhcpReply? Request(DhcpPacket request, DateTime now)
    {
        var mac = request.Mac;
        if (request.Address(DhcpOption.ServerID) is { } chosen && chosen != Config.ServerIP)
        {
            // The client accepted another server's offer.
            foreach (var offer in leases.Values.Where(l => l.Mac == mac && l.State == LeaseState.Offered).ToList())
            {
                leases.Remove(offer.Ip);
                Changed = true;
            }
            return null;
        }
        var wanted = request.Address(DhcpOption.RequestedIP) ?? (request.CiAddr != IPv4.Zero ? request.CiAddr : (IPv4?)null);
        if (wanted is not { } ip) return null;

        var acceptable = Config.Reservations.TryGetValue(mac, out var reserved) && IsFree(reserved, mac, now)
            ? ip == reserved
            : Config.InPool(ip) && IsFree(ip, mac, now);
        if (!acceptable)
        {
            Log($"Refused {ip} for {Describe(request)}");
            return Reply(request, DhcpMessageType.Nak, IPv4.Zero, withLease: false);
        }

        Forget(mac, ip);
        Set(new Lease(ip, mac, request.Hostname, now + TimeSpan.FromSeconds(Config.LeaseSeconds), LeaseState.Active));
        Log($"Assigned {ip} to {Describe(request)}");
        return Reply(request, DhcpMessageType.Ack, ip, withLease: true);
    }

    private void Decline(DhcpPacket request, DateTime now)
    {
        if (request.Address(DhcpOption.RequestedIP) is not { } ip || LeaseFor(ip)?.Mac != request.Mac) return;
        // The client found the address already in use, so keep it out of circulation for a while.
        Set(new Lease(ip, "", "In use by another device", now + DeclineHold, LeaseState.Declined));
        Log($"{ip} is already in use on the network (reported by {Describe(request)})");
    }

    private void Release(DhcpPacket request)
    {
        if (LeaseFor(request.CiAddr)?.Mac != request.Mac) return;
        leases.Remove(request.CiAddr);
        Changed = true;
        Log($"{Describe(request)} released {request.CiAddr}");
    }

    private DhcpReply? Inform(DhcpPacket request)
    {
        if (request.CiAddr == IPv4.Zero) return null;
        return Reply(request, DhcpMessageType.Ack, IPv4.Zero, withLease: false) with { Destination = request.CiAddr };
    }

    // Allocation

    private IPv4? ChooseAddress(string mac, IPv4? requested, DateTime now)
    {
        if (Config.Reservations.TryGetValue(mac, out var reserved) && IsFree(reserved, mac, now)) return reserved;
        if (leases.Values.FirstOrDefault(l => l.Mac == mac) is { } current && Config.InPool(current.Ip) && IsFree(current.Ip, mac, now))
            return current.Ip;
        if (requested is { } wanted && Config.InPool(wanted) && IsFree(wanted, mac, now)) return wanted;

        // Never-used addresses first, then the lease that expired longest ago.
        Lease? oldestExpired = null;
        for (var raw = Config.PoolStart.Raw; raw <= Config.PoolEnd.Raw; raw++)
        {
            var ip = new IPv4(raw);
            if (!IsFree(ip, mac, now)) continue;
            if (LeaseFor(ip) is not { } previous) return ip;
            if (oldestExpired == null || previous.Expires < oldestExpired.Expires) oldestExpired = previous;
        }
        return oldestExpired?.Ip;
    }

    private bool IsFree(IPv4 ip, string mac, DateTime now)
    {
        if (ip == Config.ServerIP || ip == Config.Router) return false;
        if (reservedOwners.TryGetValue(ip, out var owner) && owner != mac) return false;
        return LeaseFor(ip) is not { } lease || lease.Mac == mac || lease.Expires <= now;
    }

    private void Set(Lease lease)
    {
        leases[lease.Ip] = lease;
        Changed = true;
    }

    private void Forget(string mac, IPv4 except)
    {
        foreach (var other in leases.Values.Where(l => l.Mac == mac && l.Ip != except).ToList())
        {
            leases.Remove(other.Ip);
            Changed = true;
        }
    }

    // Replies

    private DhcpReply Reply(DhcpPacket request, DhcpMessageType type, IPv4 yiaddr, bool withLease)
    {
        var packet = new DhcpPacket
        {
            Op = 2,
            Xid = request.Xid,
            Flags = request.Flags,
            GiAddr = request.GiAddr,
            ChAddr = request.ChAddr,
            YiAddr = yiaddr,
        };
        packet.Options[DhcpOption.MessageType] = [(byte)type];
        packet.Options[DhcpOption.ServerID] = Config.ServerIP.Bytes;

        if (type != DhcpMessageType.Nak)
        {
            packet.SiAddr = Config.ServerIP;
            packet.CiAddr = request.CiAddr;
            packet.Options[DhcpOption.SubnetMask] = Config.Mask.Bytes;
            packet.Options[DhcpOption.BroadcastAddress] = Config.Broadcast.Bytes;
            if (Config.Router is { } router) packet.Options[DhcpOption.Router] = router.Bytes;
            if (Config.Dns.Count > 0) packet.Options[DhcpOption.Dns] = Config.Dns.SelectMany(dns => dns.Bytes).ToArray();
        }
        if (withLease)
        {
            var seconds = Config.LeaseSeconds;
            packet.Options[DhcpOption.LeaseTime] = new IPv4(seconds).Bytes;
            packet.Options[DhcpOption.RenewalTime] = new IPv4(seconds / 2).Bytes;
            packet.Options[DhcpOption.RebindingTime] = new IPv4(seconds / 8 * 7).Bytes;
        }

        // Clients that already hold a working address can be answered directly; everyone else
        // has no address yet and is reached by broadcast.
        var canUnicast = type != DhcpMessageType.Nak && request.CiAddr != IPv4.Zero
            && (request.Flags & 0x8000) == 0 && Config.InSubnet(request.CiAddr);
        return new DhcpReply(packet, canUnicast ? request.CiAddr : IPv4.Broadcast);
    }

    private static string Describe(DhcpPacket request)
    {
        var name = request.Hostname;
        return name.Length == 0 ? request.Mac : $"{name} ({request.Mac})";
    }
}
