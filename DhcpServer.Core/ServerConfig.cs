namespace DhcpServer.Core;

public sealed class Reservation
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";
}

/// <summary>What the user edits. Everything is kept as text so half-typed values survive.</summary>
public sealed class ServerConfig
{
    public string InterfaceId { get; set; } = "";
    public string PoolStart { get; set; } = "";
    public int PoolSize { get; set; } = 100;
    public string Router { get; set; } = "";
    public string Dns { get; set; } = "";
    public int LeaseSeconds { get; set; } = 86_400;
    public List<Reservation> Reservations { get; set; } = [];

    /// <summary>Validates the settings against the chosen interface.</summary>
    public ResolvedConfig Resolve(NetInterface? adapter)
    {
        if (adapter == null) throw new ConfigException("Choose a network interface.");
        if (adapter.Address is not { } serverIP || adapter.Netmask is not { } mask)
            throw new ConfigException($"{adapter.Name} has no IP address. Give it a manual address in Windows network settings first.");
        if (serverIP.IsLinkLocal)
            throw new ConfigException($"{adapter.Name} only has a self-assigned address ({serverIP}). Set a manual address such as 192.168.10.1 in Windows network settings first.");

        var hostBits = ~mask.Raw;
        if ((hostBits & unchecked(hostBits + 1)) != 0 || hostBits < 3)
            throw new ConfigException($"The subnet mask {mask} on {adapter.Name} leaves no room for other devices.");
        var network = new IPv4(serverIP.Raw & mask.Raw);
        var broadcast = new IPv4(network.Raw | hostBits);
        bool InSubnet(IPv4 ip) => ip > network && ip < broadcast;

        if (IPv4.Parse(PoolStart) is not { } start)
            throw new ConfigException($"Enter the first address of the pool, for example {new IPv4(network.Raw + 1)}.");
        if (!InSubnet(start))
            throw new ConfigException($"The first address must be on this PC's network ({new IPv4(network.Raw + 1)} – {new IPv4(broadcast.Raw - 1)}).");
        if (PoolSize < 1) throw new ConfigException("The pool needs at least one address.");
        var available = (long)broadcast.Raw - start.Raw;
        if (PoolSize > available)
            throw new ConfigException($"Only {available} address{(available == 1 ? "" : "es")} fit after {start} on this network. Reduce the pool size.");
        var end = new IPv4(start.Raw + (uint)PoolSize - 1);

        IPv4? routerIP = null;
        if (!string.IsNullOrWhiteSpace(Router))
        {
            if (IPv4.Parse(Router) is not { } ip || !InSubnet(ip))
                throw new ConfigException("The router must be an address on this PC's network, or left blank.");
            routerIP = ip;
        }

        var dnsServers = new List<IPv4>();
        foreach (var entry in Dns.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            dnsServers.Add(IPv4.Parse(entry) ?? throw new ConfigException($"“{entry}” is not a valid DNS server address."));
        }

        var reserved = new Dictionary<string, IPv4>();
        foreach (var reservation in Reservations)
        {
            var mac = NormalizeMac(reservation.Mac)
                ?? throw new ConfigException($"“{reservation.Mac}” is not a valid MAC address (expected aa:bb:cc:dd:ee:ff).");
            if (IPv4.Parse(reservation.Ip) is not { } ip || !InSubnet(ip) || ip == serverIP)
                throw new ConfigException($"The reserved address for {mac} must be a free address on this PC's network.");
            if (reserved.ContainsKey(mac) || reserved.ContainsValue(ip))
                throw new ConfigException($"Each reserved device and address may only be listed once ({mac} → {ip}).");
            reserved[mac] = ip;
        }

        return new ResolvedConfig(serverIP, mask, start, end, routerIP, dnsServers, (uint)Math.Max(60, LeaseSeconds), reserved);
    }

    /// <summary>A sensible default pool for the interface's subnet.</summary>
    public static (IPv4 Start, int Size)? SuggestedPool(NetInterface adapter)
    {
        if (adapter.Address is not { IsLinkLocal: false } ip || adapter.Netmask is not { } mask) return null;
        var hostBits = ~mask.Raw;
        if (hostBits < 3) return null;
        var network = ip.Raw & mask.Raw;
        var hosts = hostBits - 1;
        return hosts >= 200
            ? (new IPv4(network + 100), 100)
            : (new IPv4(network + hosts / 2 + 1), Math.Max(1, (int)(hosts / 2) - 1));
    }

    public static string? NormalizeMac(string text)
    {
        if (!text.All(c => Uri.IsHexDigit(c) || ":-. ".Contains(c))) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }
}

public sealed record ResolvedConfig(
    IPv4 ServerIP, IPv4 Mask, IPv4 PoolStart, IPv4 PoolEnd, IPv4? Router,
    IReadOnlyList<IPv4> Dns, uint LeaseSeconds,
    // MAC (aa:bb:cc:dd:ee:ff) → fixed address.
    IReadOnlyDictionary<string, IPv4> Reservations)
{
    public IPv4 Broadcast => new(ServerIP.Raw | ~Mask.Raw);

    public bool InPool(IPv4 ip) => ip >= PoolStart && ip <= PoolEnd;

    public bool InSubnet(IPv4 ip) => (ip.Raw & Mask.Raw) == (ServerIP.Raw & Mask.Raw);
}

public sealed class ConfigException(string message) : Exception(message);
