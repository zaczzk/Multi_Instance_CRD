using System.Threading;
using System.Windows;

namespace CRDLightweightARM;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ProcessJobTracker.Initialize();

        const string appName = "CRDLightweightARM_SingleInstanceMutex";
        _mutex = new Mutex(true, appName, out bool createdNew);

        if (!createdNew)
        {
            // Already running - exit duplicate instance
            MessageBox.Show(
                "Chrome Remote Desktop Lightweight ARM is already running.\nCheck your system tray or active windows.",
                "Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
