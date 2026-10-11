using System.Windows;

namespace DhcpServer.App;

public partial class App : Application
{
    private Mutex? singleInstance;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Two copies would fight over the DHCP port and the settings file.
        singleInstance = new Mutex(true, "DhcpServer.App.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("DHCP Server is already running. Look for its icon in the notification area, next to the clock.",
                "DHCP Server", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var model = new ServerModel();
        var window = new MainWindow(model);
        // "--background" is how the app is started at sign-in: tray icon only, no window.
        if (!e.Args.Contains("--background"))
        {
            window.Show();
        }
        _ = model.StartOnLaunchIfWantedAsync();
    }
}
