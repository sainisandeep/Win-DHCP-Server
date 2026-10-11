using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DhcpServer.Core;

public sealed record NetInterface(string Id, string Name, string Description, IPv4? Address, IPv4? Netmask, bool IsActive)
{
    public string Label => Description.Length > 0 && Description != Name ? $"{Name} ({Description})" : Name;

    /// <summary>Ethernet and Wi-Fi adapters that are connected or configured.</summary>
    public static List<NetInterface> All()
    {
        var found = new List<NetInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit)) continue;

            IPv4? address = null, mask = null;
            try
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    // Prefer a real address over a self-assigned one if the adapter has both.
                    if (address is { IsLinkLocal: false }) break;
                    address = IPv4.From(unicast.Address);
                    mask = IPv4.From(unicast.IPv4Mask);
                }
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var active = adapter.OperationalStatus == OperationalStatus.Up;
            if (active || address != null)
            {
                found.Add(new NetInterface(adapter.Id, adapter.Name, adapter.Description, address, mask, active));
            }
        }
        return found;
    }
}
