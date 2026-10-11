# DHCP Server for Windows

A small app that turns a Windows PC into a DHCP server. Pick a network interface, choose an address pool, press Start, and every device on that network gets an IP address from your PC.

It is meant for networks that have no DHCP server of their own: a lab bench, a switch full of test devices, a direct Ethernet link to a camera or a Raspberry Pi.

There is also a [macOS version](https://github.com/sainisandeep/mac-dhcp-server) with the same features and layout.

> **Run only one DHCP server per network.** If a router is already handing out addresses on the network you select, turn its DHCP off first. Two servers on one network give devices conflicting answers. The app checks for an existing server before it starts and warns you if it finds one.

> **Status:** this app is new. It is built and started on Windows automatically for every change, but it has not yet been tested against real devices. Please report what you find.

## Features

- Choose the interface to serve (Ethernet, Wi-Fi, USB adapters)
- Set the first address, pool size and lease time; the app suggests a pool that fits the interface's subnet
- Optional router and DNS servers sent to each device
- Reserved addresses: a given MAC address always receives the same IP
- Checks for an existing DHCP server on the network, on demand and automatically before starting
- Devices tab listing every lease, and an Activity tab logging each offer and assignment
- Runs in the background from the notification area, with optional open-at-sign-in and auto-start
- Leases are remembered across restarts
- A single `.exe`; nothing to install

## Download

Requires 64-bit Windows 10 or 11.

1. Download `DHCPServer.exe` from the [latest release](../../releases/latest) and put it somewhere permanent, such as a folder in Documents.
2. Double-click it. Windows SmartScreen will warn that the app is unrecognised, because it is not signed. Click **More info**, then **Run anyway**.
3. In the Setup tab, click **Allow DHCP Server Through Windows Firewall** and approve the administrator prompt. Without this, the server runs but no device receives an address.

If you later move `DHCPServer.exe` to another folder, repeat step 3 and switch "Open when I sign in to Windows" off and on again.

## Usage

1. **Give the PC a manual IP address on the interface.** In Settings › Network & internet, open the adapter, choose **Edit** next to IP assignment, and set a manual address such as `192.168.10.1` with mask `255.255.255.0`. The app will not start on an interface that has no address or only a self-assigned `169.254.x.x` one.
2. **Select the interface** in the Setup tab.
3. **Adjust the pool** (first address, size, lease time). Fill in the router and DNS servers if the network has internet access; leave them blank otherwise.
4. **Press Start.** The app first spends about three seconds checking whether another DHCP server is already on the network, and asks before continuing if it finds one.
5. **Close the window** to leave the server running. The icon in the notification area, next to the clock, starts and stops the server, reopens the window, and quits the app.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| "Another DHCP server on this PC is already using port 67" | Internet Connection Sharing, Mobile hotspot or another DHCP server is running. Turn it off. |
| Start button is disabled | The message beside it says what to fix, usually a missing manual IP or a pool that does not fit the subnet. |
| The server runs but devices get no address | Windows Firewall is blocking the app. Use the **Allow DHCP Server Through Windows Firewall** button. If you once clicked Cancel on a firewall prompt, also remove the blocked "DHCP Server" entry under Windows Security › Firewall & network protection › Allow an app through firewall. |
| The interface shows "No address" | Windows only activates a manual address once the cable is connected to a powered device or switch. |
| The check says "Could not check" | Windows would not share the port the check needs. The server can still be started; make sure yourself that no other DHCP server is on the network. |

## Limitations

- IPv4 only
- One interface and one subnet at a time; requests forwarded by DHCP relays are ignored
- Replies to devices that do not yet have an address are sent by broadcast
- Sends subnet mask, broadcast address, router, DNS and lease times; no other DHCP options (no PXE boot, domain name, NTP, etc.)
- The check for existing servers can miss one that is slow to answer or ignores unknown devices

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app can be built on Windows, macOS or Linux, but only runs on Windows.

```bash
dotnet publish DhcpServer.App -c Release -o out
```

This produces `out/DHCPServer.exe`, a single self-contained program. Run the logic checks, which work on any operating system, with:

```bash
dotnet run --project DhcpServer.SelfTest
```

Every push is built, checked and started on a Windows machine by GitHub Actions. Raising `<Version>` in `DhcpServer.App/DhcpServer.App.csproj` and pushing to `main` publishes a new release automatically.

## Project layout

```
DhcpServer.Core/       DHCP packet format, address allocation, sockets, interface discovery
DhcpServer.App/        WPF window, notification-area icon, settings storage
DhcpServer.SelfTest/   Checks for the allocation logic (no network access)
build.sh               Convenience wrapper around the commands above
```

Settings and leases are stored in `%AppData%\DHCP Server\`.

## Contributing

Improvements are welcome.

- **Found a bug or have an idea?** Open an issue describing what you expected and what happened, including your Windows version and the Activity log if relevant.
- **Want to change the code?** Fork the repository, make your change on a branch, and open a pull request. Run the logic checks first, and add a check when you change how addresses are allocated.

Please test on an isolated network, not one other people depend on.

## License

MIT. See [LICENSE](LICENSE).
