using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DhcpServer.Core;

/// <summary>An IPv4 address held in host byte order.</summary>
[JsonConverter(typeof(IPv4JsonConverter))]
public readonly record struct IPv4(uint Raw) : IComparable<IPv4>
{
    public static readonly IPv4 Zero = new(0);
    public static readonly IPv4 Broadcast = new(0xFFFF_FFFF);

    public static IPv4? Parse(string? text)
    {
        var parts = (text ?? "").Trim().Split('.');
        if (parts.Length != 4) return null;
        uint value = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet)) return null;
            value = value << 8 | octet;
        }
        return new IPv4(value);
    }

    public static IPv4? FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 4 ? new IPv4((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) : null;

    public static IPv4? From(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? FromBytes(address.GetAddressBytes()) : null;

    public byte[] Bytes => [(byte)(Raw >> 24), (byte)(Raw >> 16), (byte)(Raw >> 8), (byte)Raw];

    /// <summary>169.254.0.0/16 — what Windows self-assigns when no DHCP server answers.</summary>
    public bool IsLinkLocal => Raw >> 16 == 0xA9FE;

    public IPAddress ToIPAddress() => new(Bytes);

    public int CompareTo(IPv4 other) => Raw.CompareTo(other.Raw);

    public override string ToString() => string.Join('.', Bytes);

    public static bool operator <(IPv4 a, IPv4 b) => a.Raw < b.Raw;
    public static bool operator >(IPv4 a, IPv4 b) => a.Raw > b.Raw;
    public static bool operator <=(IPv4 a, IPv4 b) => a.Raw <= b.Raw;
    public static bool operator >=(IPv4 a, IPv4 b) => a.Raw >= b.Raw;
}

public sealed class IPv4JsonConverter : JsonConverter<IPv4>
{
    public override IPv4 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        IPv4.Parse(reader.GetString()) ?? throw new JsonException("Invalid IPv4 address");

    public override void Write(Utf8JsonWriter writer, IPv4 value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
