using System.ComponentModel;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DhcpServer.App;

public partial class MainWindow : Window
{
    private readonly ServerModel model;
    private readonly WinForms.NotifyIcon tray = new();
    private readonly WinForms.ToolStripMenuItem trayStatus = new() { Enabled = false };
    private readonly WinForms.ToolStripMenuItem trayToggle = new();
    private readonly System.Drawing.Icon runningIcon = MakeIcon(System.Drawing.Color.FromArgb(40, 180, 80));
    private readonly System.Drawing.Icon stoppedIcon = MakeIcon(System.Drawing.Color.FromArgb(140, 140, 140));
    private bool quitting;
    private bool explainedTray;

    public MainWindow(ServerModel model)
    {
        InitializeComponent();
        DataContext = this.model = model;

        // The tray icon keeps the app (and the server) reachable after the window is closed.
        trayToggle.Click += async (_, _) => await model.ToggleAsync();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(trayStatus);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(trayToggle);
        menu.Items.Add("Open DHCP Server…", null, (_, _) => ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit DHCP Server", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();

        model.PropertyChanged += (_, _) => UpdateTray();
        UpdateTray();
        tray.Visible = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!quitting)
        {
            e.Cancel = true;
            Hide();
            if (!explainedTray)
            {
                explainedTray = true;
                tray.ShowBalloonTip(4000, "DHCP Server is still running",
                    "Use this icon to reopen the window or quit.", WinForms.ToolTipIcon.Info);
            }
        }
        base.OnClosing(e);
    }

    private void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        quitting = true;
        model.Stop();
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    private void UpdateTray()
    {
        tray.Icon = model.IsRunning ? runningIcon : stoppedIcon;
        // Windows limits tray tooltips to 127 characters.
        tray.Text = model.TrayText.Length > 120 ? model.TrayText[..120] : model.TrayText;
        trayStatus.Text = model.IsRunning ? $"{model.StatusTitle} · {model.PoolRange}" : model.StatusTitle;
        trayToggle.Text = model.IsRunning ? "Stop Server" : "Start Server";
        trayToggle.Enabled = model.CanToggle;
    }

    private static System.Drawing.Icon MakeIcon(System.Drawing.Color color)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(color);
            graphics.FillEllipse(brush, 3, 3, 26, 26);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private async void OnToggle(object sender, RoutedEventArgs e) => await model.ToggleAsync();

    private async void OnCheck(object sender, RoutedEventArgs e) => await model.CheckForServersAsync();

    private void OnAddReservation(object sender, RoutedEventArgs e) => model.AddReservation();

    private void OnRemoveReservation(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReservationRow row) model.RemoveReservation(row);
    }

    private void OnAllowFirewall(object sender, RoutedEventArgs e) => model.AllowThroughFirewall();

    private void OnDismissNotice(object sender, RoutedEventArgs e) => model.DismissNotice();

    private void OnClearLog(object sender, RoutedEventArgs e) => model.ClearLog();
}
