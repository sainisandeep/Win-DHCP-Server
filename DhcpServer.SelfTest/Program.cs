using DhcpServer.Core;

// Exercises the allocation logic without touching the network. Runs on any OS:
//   dotnet run --project DhcpServer.SelfTest

var failures = 0;
void Check(bool condition, string what)
{
    Console.WriteLine(condition ? $"  ok   {what}" : $"  FAIL {what}");
    if (!condition) failures++;
}

IPv4 Ip(string text) => IPv4.Parse(text)!.Value;

DhcpPacket Client(byte last, DhcpMessageType type, IPv4? requested = null, IPv4? server = null, IPv4? ciaddr = null)
{
    var packet = new DhcpPacket { Xid = 0xABCD_0000 | last, CiAddr = ciaddr ?? IPv4.Zero };
    packet.ChAddr[0] = 0x02;
    packet.ChAddr[5] = last;
    packet.Options[DhcpOption.MessageType] = [(byte)type];
    packet.Options[DhcpOption.Hostname] = System.Text.Encoding.UTF8.GetBytes($"host{last}");
    if (requested is { } wanted) packet.Options[DhcpOption.RequestedIP] = wanted.Bytes;
    if (server is { } id) packet.Options[DhcpOption.ServerID] = id.Bytes;
    // Round-trip through the wire format so the codec is covered too.
    return DhcpPacket.Parse(packet.Encode())!;
}

var adapter = new NetInterface("test", "Test", "", Ip("192.168.10.1"), Ip("255.255.255.0"), true);
var settings = new ServerConfig
{
    PoolStart = "192.168.10.100",
    PoolSize = 3,
    Router = "192.168.10.1",
    Dns = "1.1.1.1, 8.8.8.8",
    Reservations = [new Reservation { Mac = "02-00-00-00-00-09", Ip = "192.168.10.50" }],
};
bool Rejected(ServerConfig config, NetInterface on)
{
    try { config.Resolve(on); return false; }
    catch (ConfigException) { return true; }
}

var config = settings.Resolve(adapter);
var serverIP = config.ServerIP;
var table = new LeaseTable(config);
var now = DateTime.UtcNow;

Console.WriteLine("Configuration");
Check(config.PoolEnd == Ip("192.168.10.102"), "pool end computed from size");
Check(config.Reservations["02:00:00:00:00:09"] == Ip("192.168.10.50"), "MAC normalised");
settings.PoolSize = 500;
Check(Rejected(settings, adapter), "oversized pool rejected");
settings.PoolSize = 3;
Check(Rejected(settings, adapter with { Address = Ip("169.254.3.4") }), "self-assigned interface rejected");

Console.WriteLine("Discover / request");
var offer = table.Handle(Client(1, DhcpMessageType.Discover), now);
Check(offer?.Packet.MessageType == DhcpMessageType.Offer && offer.Packet.YiAddr == Ip("192.168.10.100"), "first client offered first address");
Check(offer?.Destination == IPv4.Broadcast, "offer is broadcast");
Check(offer?.Packet.Address(DhcpOption.SubnetMask) == Ip("255.255.255.0"), "offer carries subnet mask");
Check(offer?.Packet.Options[DhcpOption.Dns].Length == 8, "offer carries both DNS servers");
Check(offer?.Packet.Xid == 0xABCD_0001, "transaction id echoed");
var ack = table.Handle(Client(1, DhcpMessageType.Request, Ip("192.168.10.100"), serverIP), now);
Check(ack?.Packet.MessageType == DhcpMessageType.Ack && table.LeaseFor(Ip("192.168.10.100"))?.State == LeaseState.Active, "request acknowledged");
Check(table.LeaseFor(Ip("192.168.10.100"))?.Hostname == "host1", "hostname recorded");

Console.WriteLine("Second client, reservation, renewals");
Check(table.Handle(Client(2, DhcpMessageType.Discover), now)?.Packet.YiAddr == Ip("192.168.10.101"), "second client gets next address");
Check(table.Handle(Client(9, DhcpMessageType.Discover), now)?.Packet.YiAddr == Ip("192.168.10.50"), "reserved client gets its fixed address");
Check(table.Handle(Client(1, DhcpMessageType.Discover), now)?.Packet.YiAddr == Ip("192.168.10.100"), "returning client keeps its address");
var renew = table.Handle(Client(1, DhcpMessageType.Request, ciaddr: Ip("192.168.10.100")), now);
Check(renew?.Packet.MessageType == DhcpMessageType.Ack && renew.Destination == Ip("192.168.10.100"), "renewal acknowledged by unicast");
Check(table.Handle(Client(3, DhcpMessageType.Request, Ip("192.168.10.100")), now)?.Packet.MessageType == DhcpMessageType.Nak, "taken address refused");
Check(table.Handle(Client(3, DhcpMessageType.Request, Ip("10.0.0.5")), now)?.Packet.MessageType == DhcpMessageType.Nak, "foreign address refused");
Check(table.Handle(Client(3, DhcpMessageType.Request, Ip("192.168.10.102"), Ip("192.168.10.200")), now) == null, "request for another server ignored");

Console.WriteLine("Exhaustion, decline, release, expiry");
table.Handle(Client(2, DhcpMessageType.Request, Ip("192.168.10.101"), serverIP), now);
table.Handle(Client(3, DhcpMessageType.Discover), now);
table.Handle(Client(3, DhcpMessageType.Request, Ip("192.168.10.102"), serverIP), now);
Check(table.Handle(Client(4, DhcpMessageType.Discover), now) == null, "full pool gives no offer");
table.Handle(Client(3, DhcpMessageType.Decline, Ip("192.168.10.102"), serverIP), now);
Check(table.Handle(Client(4, DhcpMessageType.Discover), now) == null, "declined address is held back");
table.Handle(Client(2, DhcpMessageType.Release, ciaddr: Ip("192.168.10.101")), now);
Check(table.Handle(Client(4, DhcpMessageType.Discover), now)?.Packet.YiAddr == Ip("192.168.10.101"), "released address is reused");
var later = now.AddSeconds(config.LeaseSeconds + 1);
Check(table.Handle(Client(5, DhcpMessageType.Discover), later) != null, "expired leases are reclaimed");

Console.WriteLine("Persistence");
var restored = new LeaseTable(config,
[
    new Lease(Ip("192.168.10.101"), "02:00:00:00:00:07", "", now.AddHours(1), LeaseState.Active),
    new Lease(Ip("192.168.10.200"), "02:00:00:00:00:08", "", now.AddHours(1), LeaseState.Active),
]);
Check(restored.Snapshot().Count == 1, "saved leases outside the pool are dropped");
Check(restored.Handle(Client(1, DhcpMessageType.Discover), now)?.Packet.YiAddr == Ip("192.168.10.100"), "restored lease is not handed out again");
var json = System.Text.Json.JsonSerializer.Serialize(restored.Snapshot());
Check(System.Text.Json.JsonSerializer.Deserialize<List<Lease>>(json)?[0] == restored.Snapshot()[0], "leases survive saving and loading");

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
