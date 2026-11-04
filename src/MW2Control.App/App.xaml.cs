using System.Windows;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MW2Control.App.Tests")]

namespace MW2Control.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--firewall")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await ElevatedFirewall.ServeAsync(e.Args));
            return;
        }
        if (e.Args.Length != 0) { Shutdown(2); return; }
        try
        {
            _instance = SingleInstance.ForCurrentUser();
            if (!_instance.IsPrimary)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await _instance.ActivatePrimaryAsync();
                Shutdown();
                return;
            }
            MainWindow = new MainWindow();
            _instance.Listen(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
                MainWindow.Show();
                MainWindow.Activate();
            })));
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("MW2 Control could not open. " + ex.Message, "MW2 Control", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
