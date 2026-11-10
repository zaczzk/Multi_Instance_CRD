using System.Threading;
using System.Windows;

namespace CRDLightweightARM;

public partial class App : Application
{
    private const string MutexName = "CRDLightweightARM_SingleInstanceMutex";
    private const string ShowEventName = "CRDLightweightARM_ShowExistingInstance";

    private static Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _mainWindowLoaded;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Already running: ask the existing instance to come to the front, then quit.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                // This process was just started by the user, so it may hand its right to take the foreground over.
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                existing.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Unhandled UI exception", args.Exception);
            args.Handled = true;

            // Errors after startup are logged and survived so one bad WebView event can't close every session.
            if (!_mainWindowLoaded)
            {
                MessageBox.Show(
                    $"Multi-Instance CRD could not start:\n\n{args.Exception.Message}\n\nDetails: {CrashLog.FilePath}",
                    "Multi-Instance CRD", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write("Unhandled exception", args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        ProcessJobTracker.Initialize();
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Loaded += (_, _) => _mainWindowLoaded = true;
        mainWindow.Show();

        ListenForShowRequests(mainWindow);
    }

    private void ListenForShowRequests(MainWindow window)
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(window.ShowFromOtherInstance);
                }
            }
            catch (ObjectDisposedException) { }
        })
        { IsBackground = true, Name = "ShowInstanceListener" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
