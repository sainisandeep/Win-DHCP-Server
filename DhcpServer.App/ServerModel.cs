using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DhcpServer.Core;
using Microsoft.Win32;

namespace DhcpServer.App;

public sealed record LeaseRow(string Address, string Device, string Mac, string Status);

public sealed record LogLine(string Time, string Text);

public sealed class ReservationRow : INotifyPropertyChanged
{
    private string mac = "";
    private string ip = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Mac
    {
        get => mac;
        set { mac = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mac))); }
    }

    public string Ip
    {
        get => ip;
        set { ip = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ip))); }
    }
}

public sealed class ServerModel : INotifyPropertyChanged
{
    private sealed class Settings
    {
        public ServerConfig Config { get; set; } = new();
        public bool StartOnLaunch { get; set; }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DHCP Server";

    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DHCP Server");
    private static readonly string SettingsFile = Path.Combine(DataFolder, "settings.json");
    private static readonly string LeaseFile = Path.Combine(DataFolder, "leases.json");

    private readonly DhcpEngine engine = new();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer timer;
    private readonly Settings settings;
    private IReadOnlyList<Lease> leases;
    private List<NetInterface> interfaces;
    /// <summary>The settings the server is currently running with; null while stopped.</summary>
    private ResolvedConfig? running;
    private string poolSizeText;
    private string? notice;
    private (string Text, bool Conflict)? checkResult;
    private bool loading = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerModel()
    {
        settings = Load<Settings>(SettingsFile) ?? new Settings();
        leases = Load<List<Lease>>(LeaseFile) ?? [];
        interfaces = NetInterface.All();
        poolSizeText = Config.PoolSize.ToString();

        foreach (var reservation in Config.Reservations)
        {
            AddReservationRow(new ReservationRow { Mac = reservation.Mac, Ip = reservation.Ip });
        }
        if (Config.InterfaceId.Length == 0
            && (interfaces.FirstOrDefault(i => i.Address is { IsLinkLocal: false }) ?? interfaces.FirstOrDefault()) is { } first)
        {
            Select(first);
        }
        loading = false;

        engine.Log += message => dispatcher.BeginInvoke(() => Append(message));
        engine.LeasesChanged += current => dispatcher.BeginInvoke(() =>
        {
            leases = current;
            Save(LeaseFile, leases.Where(lease => lease.State == LeaseState.Active).ToList());
            Raise();
        });

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private ServerConfig Config => settings.Config;

    // Settings shown in the Setup tab

    public IReadOnlyList<NetInterface> Interfaces => interfaces;

    public NetInterface? SelectedInterface
    {
        get => interfaces.FirstOrDefault(i => i.Id == Config.InterfaceId);
        set
        {
            // WPF pushes null here while the list is being refreshed; that is not a user choice.
            if (value == null || value.Id == Config.InterfaceId) return;
            Select(value);
        }
    }

    public string PoolStart
    {
        get => Config.PoolStart;
        set { Config.PoolStart = value; Changed(); }
    }

    public string PoolSizeText
    {
        get => poolSizeText;
        set
        {
            poolSizeText = value;
            Config.PoolSize = int.TryParse(value.Trim(), out var size) ? size : 0;
            Changed();
        }
    }

    public IReadOnlyList<KeyValuePair<string, int>> LeaseTimes { get; } =
    [
        new("1 hour", 3_600), new("8 hours", 28_800), new("1 day", 86_400), new("1 week", 604_800),
    ];

    public int LeaseSeconds
    {
        get => Config.LeaseSeconds;
        set { Config.LeaseSeconds = value; Changed(); }
    }

    public string Router
    {
        get => Config.Router;
        set { Config.Router = value; Changed(); }
    }

    public string Dns
    {
        get => Config.Dns;
        set { Config.Dns = value; Changed(); }
    }

    public ObservableCollection<ReservationRow> Reservations { get; } = [];

    public bool StartOnLaunch
    {
        get => settings.StartOnLaunch;
        set { settings.StartOnLaunch = value; Changed(); }
    }

    public bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) != null;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
                else key.DeleteValue(RunName, throwOnMissingValue: false);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                notice = $"Could not change the sign-in setting: {error.Message}";
            }
            Raise();
        }
    }

    // State shown in the header, tabs and tray

    public bool IsRunning => running != null;
    public bool IsStopped => running == null;
    public bool IsChecking { get; private set; }

    /// <summary>Why the current settings can't be used, or null when they are fine.</summary>
    public string? Problem
    {
        get
        {
            try
            {
                Config.Resolve(SelectedInterface);
                return null;
            }
            catch (ConfigException error)
            {
                return error.Message;
            }
        }
    }

    public string? PoolRange
    {
        get
        {
            var resolved = running;
            if (resolved == null)
            {
                try { resolved = Config.Resolve(SelectedInterface); }
                catch (ConfigException) { return null; }
            }
            return resolved.PoolStart == resolved.PoolEnd ? $"{resolved.PoolStart}" : $"{resolved.PoolStart} – {resolved.PoolEnd}";
        }
    }

    public string PoolRangeText => PoolRange ?? "—";

    public int ActiveLeaseCount => leases.Count(lease => lease.State == LeaseState.Active && lease.Expires > DateTime.UtcNow);

    public string StatusTitle => IsRunning ? $"Running on {SelectedInterface?.Name ?? "the network"}" : "Server is stopped";

    public string Subtitle
    {
        get
        {
            if (IsRunning) return $"Handing out {PoolRange} · {Devices(ActiveLeaseCount)}";
            if (Problem is { } problem) return problem;
            return IsChecking ? "Checking for other DHCP servers on this network…" : $"Ready to hand out {PoolRange}";
        }
    }

    public bool SubtitleIsWarning => !IsRunning && Problem != null;
    public string StartButtonText => IsRunning ? "Stop" : IsChecking ? "Checking…" : "Start";
    public bool CanToggle => IsRunning || (Problem == null && !IsChecking);

    public string? Notice => notice;
    public bool HasNotice => notice != null;

    public string PcAddressSummary =>
        SelectedInterface is not { } adapter ? "—"
        : adapter.Address is { } address && adapter.Netmask is { } mask ? $"{address}  ·  mask {mask}"
        : "No address";

    public string CheckButtonText => IsChecking ? "Checking…" : "Check for Existing DHCP Servers";
    public bool CanCheck => !IsRunning && !IsChecking && SelectedInterface?.Address != null;
    public bool HasCheckResult => checkResult != null;
    public string CheckResultText => checkResult?.Text ?? "";
    public bool CheckResultConflict => checkResult?.Conflict ?? false;

    public string DevicesHeader => $"Devices ({ActiveLeaseCount})";
    public bool HasNoLeases => leases.Count == 0;
    public string NoLeasesText => IsRunning ? "No devices have asked for an address yet." : "Start the server to hand out addresses.";

    public IReadOnlyList<LeaseRow> LeaseRows => leases.Select(lease => new LeaseRow(
        lease.Ip.ToString(),
        lease.Hostname.Length == 0 ? "—" : lease.Hostname,
        lease.Mac,
        lease.State switch
        {
            LeaseState.Offered => "Offered",
            LeaseState.Declined => "Address conflict",
            _ => lease.Expires > DateTime.UtcNow ? $"Until {lease.Expires.ToLocalTime():g}" : "Expired",
        })).ToList();

    public ObservableCollection<LogLine> Log { get; } = [];

    public string BackgroundFooter => IsRunning
        ? "Stop the server to change its settings. Closing this window leaves it running — use the icon in the notification area, next to the clock, to reopen or quit."
        : "Closing this window leaves the server running — use the icon in the notification area, next to the clock, to reopen or quit.";

    public string TrayText => IsRunning ? $"DHCP Server — running, {Devices(ActiveLeaseCount)}" : "DHCP Server — stopped";

    // Actions

    public void AddReservation()
    {
        AddReservationRow(new ReservationRow());
        Changed();
    }

    public void RemoveReservation(ReservationRow row)
    {
        Reservations.Remove(row);
        Changed();
    }

    /// <summary>
    /// Adds a Windows Firewall rule letting devices reach this app. Windows asks for
    /// administrator approval; without the rule, requests from devices never arrive.
    /// </summary>
    public void AllowThroughFirewall()
    {
        try
        {
            using var netsh = Process.Start(new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"DHCP Server\" dir=in action=allow protocol=UDP profile=any program=\"{Environment.ProcessPath}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            netsh?.WaitForExit();
            Append(netsh?.ExitCode == 0
                ? "Windows Firewall now allows devices to reach DHCP Server."
                : "The Windows Firewall rule could not be added.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // The user declined the administrator prompt.
            Append("The Windows Firewall rule was not added.");
        }
        Raise();
    }

    public void DismissNotice()
    {
        notice = null;
        Raise();
    }

    public void ClearLog() => Log.Clear();

    public Task ToggleAsync()
    {
        if (!IsRunning) return StartAfterCheckAsync(interactive: true);
        Stop();
        return Task.CompletedTask;
    }

    public Task StartOnLaunchIfWantedAsync() =>
        settings.StartOnLaunch ? StartAfterCheckAsync(interactive: false) : Task.CompletedTask;

    /// <summary>Looks for DHCP servers already on the selected network and reports what it finds.</summary>
    public async Task CheckForServersAsync()
    {
        if (IsRunning || IsChecking) return;
        var (servers, error) = await FindServersAsync();
        checkResult = error != null ? ($"Could not check: {error}", true)
            : servers.Count == 0 ? ("No other DHCP server answered on this network.", false)
            : ($"Already handing out addresses here: {string.Join(", ", servers)}", true);
        Raise();
    }

    /// <summary>
    /// Starts the server unless another one is already on the network. When interactive, the
    /// user is asked whether to start anyway; otherwise the server stays stopped.
    /// </summary>
    public async Task StartAfterCheckAsync(bool interactive)
    {
        if (IsRunning || IsChecking) return;
        if (Problem is { } problem)
        {
            notice = problem;
            Raise();
            return;
        }

        var (servers, error) = await FindServersAsync();
        if (error != null)
        {
            Append($"Could not check for other DHCP servers: {error}");
        }
        else if (servers.Count == 0)
        {
            checkResult = ("No other DHCP server answered on this network.", false);
        }
        else
        {
            var found = string.Join(", ", servers);
            checkResult = ($"Already handing out addresses here: {found}", true);
            Append($"Found another DHCP server on {SelectedInterface?.Name}: {found}");
            if (!interactive)
            {
                notice = $"The server was not started because another DHCP server ({found}) is already on this network.";
                Raise();
                return;
            }
            var answer = MessageBox.Show(
                $"Another DHCP server is already on this network.\n\n{found} is already handing out addresses on {SelectedInterface?.Name}. "
                + "Running a second server gives devices conflicting addresses and can knock them offline.\n\nStart anyway?",
                "DHCP Server", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                Raise();
                return;
            }
        }
        Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        engine.Stop();
        running = null;
        Append("Server stopped");
        Raise();
    }

    // Internals

    private void Start()
    {
        RefreshInterfaces();
        try
        {
            var resolved = Config.Resolve(SelectedInterface);
            engine.Start(resolved, leases);
            running = resolved;
            notice = null;
            Append($"Server started on {SelectedInterface?.Name} ({resolved.ServerIP}), pool {resolved.PoolStart} – {resolved.PoolEnd}");
        }
        catch (Exception error) when (error is ConfigException or EngineException)
        {
            notice = error.Message;
            Append($"Could not start: {error.Message}");
        }
        Raise();
    }

    private async Task<(List<IPv4> Servers, string? Error)> FindServersAsync()
    {
        if (SelectedInterface?.Address is not { } address) return ([], "the interface has no IP address.");
        IsChecking = true;
        Raise();
        try
        {
            return (await Task.Run(() => DhcpProbe.FindServers(address, TimeSpan.FromSeconds(3))), null);
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return ([], error.Message);
        }
        finally
        {
            IsChecking = false;
            Raise();
        }
    }

    private void Select(NetInterface adapter)
    {
        Config.InterfaceId = adapter.Id;
        checkResult = null;
        // Keep the user's pool if it already fits this network.
        var fits = IPv4.Parse(Config.PoolStart) is { } start && adapter.Address is { } ip && adapter.Netmask is { } mask
            && (start.Raw & mask.Raw) == (ip.Raw & mask.Raw);
        if (!fits && ServerConfig.SuggestedPool(adapter) is { } suggestion)
        {
            Config.PoolStart = suggestion.Start.ToString();
            Config.PoolSize = suggestion.Size;
            poolSizeText = suggestion.Size.ToString();
        }
        Changed();
    }

    private void AddReservationRow(ReservationRow row)
    {
        row.PropertyChanged += (_, _) => Changed();
        Reservations.Add(row);
    }

    private void Tick()
    {
        RefreshInterfaces();
        if (running != null && SelectedInterface?.Address != running.ServerIP)
        {
            Stop();
            notice = "The server was stopped because the network interface was disconnected or its address changed.";
        }
        Raise();
    }

    private void RefreshInterfaces()
    {
        var current = NetInterface.All();
        if (current.SequenceEqual(interfaces)) return;
        interfaces = current;
        // Swapping the list makes the drop-down forget its selection, so tell it about the
        // list first and the selection second.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Interfaces)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedInterface)));
    }

    private void Changed()
    {
        if (loading) return;
        Config.Reservations = Reservations.Select(row => new Reservation { Mac = row.Mac, Ip = row.Ip }).ToList();
        Save(SettingsFile, settings);
        Raise();
    }

    private void Append(string text)
    {
        Log.Insert(0, new LogLine(DateTime.Now.ToString("T"), text));
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
    }

    /// <summary>Most of what the window shows is derived, so refresh every binding at once.</summary>
    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static string Devices(int count) => $"{count} device{(count == 1 ? "" : "s")}";

    private static T? Load<T>(string file) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(file, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Settings that can't be saved are still usable for this session.
        }
    }
}
