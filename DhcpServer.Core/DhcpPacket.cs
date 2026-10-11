using System.Text;

namespace DhcpServer.Core;

public enum DhcpMessageType : byte
{
    Discover = 1, Offer, Request, Decline, Ack, Nak, Release, Inform,
}

public static class DhcpOption
{
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte Dns = 6;
    public const byte Hostname = 12;
    public const byte BroadcastAddress = 28;
    public const byte RequestedIP = 50;
    public const byte LeaseTime = 51;
    public const byte MessageType = 53;
    public const byte ServerID = 54;
    public const byte RenewalTime = 58;
    public const byte RebindingTime = 59;
}

/// <summary>A BOOTP/DHCP message (RFC 2131).</summary>
public sealed class DhcpPacket
{
    private static readonly byte[] MagicCookie = [99, 130, 83, 99];
    private const int HeaderLength = 236;

    public byte Op { get; set; } = 1;
    public byte HType { get; set; } = 1;
    public byte HLen { get; set; } = 6;
    public byte Hops { get; set; }
    public uint Xid { get; set; }
    public ushort Secs { get; set; }
    public ushort Flags { get; set; }
    public IPv4 CiAddr { get; set; }
    public IPv4 YiAddr { get; set; }
    public IPv4 SiAddr { get; set; }
    public IPv4 GiAddr { get; set; }
    public byte[] ChAddr { get; set; } = new byte[16];
    public Dictionary<byte, byte[]> Options { get; } = new();

    public static DhcpPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength + 4 || !data.Slice(HeaderLength, 4).SequenceEqual(MagicCookie)) return null;

        var packet = new DhcpPacket
        {
            Op = data[0],
            HType = data[1],
            HLen = data[2],
            Hops = data[3],
            Xid = IPv4.FromBytes(data.Slice(4, 4))!.Value.Raw,
            Secs = (ushort)(data[8] << 8 | data[9]),
            Flags = (ushort)(data[10] << 8 | data[11]),
            CiAddr = IPv4.FromBytes(data.Slice(12, 4))!.Value,
            YiAddr = IPv4.FromBytes(data.Slice(16, 4))!.Value,
            SiAddr = IPv4.FromBytes(data.Slice(20, 4))!.Value,
            GiAddr = IPv4.FromBytes(data.Slice(24, 4))!.Value,
            ChAddr = data.Slice(28, 16).ToArray(),
        };

        var index = HeaderLength + 4;
        while (index < data.Length)
        {
            var code = data[index++];
            if (code == 0) continue;
            if (code == 255) break;
            if (index >= data.Length) return null;
            int length = data[index++];
            if (index + length > data.Length) return null;
            // A repeated option is a continuation of the earlier one (RFC 3396).
            var value = data.Slice(index, length).ToArray();
            packet.Options[code] = packet.Options.TryGetValue(code, out var earlier) ? [.. earlier, .. value] : value;
            index += length;
        }
        return packet;
    }

    public byte[] Encode()
    {
        var output = new List<byte>(300) { Op, HType, HLen, Hops };
        output.AddRange(new IPv4(Xid).Bytes);
        output.AddRange([(byte)(Secs >> 8), (byte)Secs, (byte)(Flags >> 8), (byte)Flags]);
        output.AddRange(CiAddr.Bytes);
        output.AddRange(YiAddr.Bytes);
        output.AddRange(SiAddr.Bytes);
        output.AddRange(GiAddr.Bytes);
        var hardware = new byte[16];
        Array.Copy(ChAddr, hardware, Math.Min(ChAddr.Length, 16));
        output.AddRange(hardware);
        output.AddRange(new byte[64 + 128]); // sname + file
        output.AddRange(MagicCookie);

        // Message type goes first; some clients expect it there.
        foreach (var code in Options.Keys.OrderBy(code => code == DhcpOption.MessageType ? 0 : code))
        {
            var value = Options[code];
            var length = Math.Min(value.Length, 255);
            output.Add(code);
            output.Add((byte)length);
            output.AddRange(value.AsSpan(0, length));
        }
        output.Add(255);

        // BOOTP minimum size; some older clients drop anything shorter.
        while (output.Count < 300) output.Add(0);
        return output.ToArray();
    }

    public DhcpMessageType? MessageType =>
        Options.TryGetValue(DhcpOption.MessageType, out var value) && value.Length > 0
            && Enum.IsDefined(typeof(DhcpMessageType), value[0])
            ? (DhcpMessageType)value[0] : null;

    public string Mac => string.Join(':', ChAddr.Take(Math.Min((int)HLen, 16)).Select(b => b.ToString("x2")));

    public string Hostname =>
        Options.TryGetValue(DhcpOption.Hostname, out var value)
            ? new string(Encoding.UTF8.GetString(value).Where(c => !char.IsControl(c)).ToArray())
            : "";

    public IPv4? Address(byte option) =>
        Options.TryGetValue(option, out var value) ? IPv4.FromBytes(value) : null;
}
