using AutoDark.Services;

namespace AutoDark;

public partial class App : Application
{
    internal const string SingleInstanceMutexName = @"Local\AutoDark.WinUI";
    internal const string ShowRequestEventName = @"Local\AutoDark.WinUI.Show";

    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstance;
    private Window? _window;

    /// <summary>
    /// Created right after the single-instance mutex is acquired — before the
    /// (comparatively slow) MainWindow construction — so a second launch
    /// during startup finds something to signal instead of exiting silently.
    /// </summary>
    internal static EventWaitHandle? ShowRequestEvent { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash(e.Message, e.Exception);

        // Keep the tray utility alive on a recoverable exception instead of dying
        // silently, but let the process exit while no window exists yet — otherwise
        // a startup failure leaves a windowless zombie holding the single-instance
        // mutex and blocking every future launch.
        e.Handled = _window is not null;
    }

    private static void LogCrash(string message, Exception? exception)
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "crash.log");
            var entry = $"{DateTimeOffset.Now:o}\t{message}\n{exception}\n\n";

            // A recurring per-minute failure must not grow the log unbounded.
            var logInfo = new FileInfo(logPath);
            if (logInfo.Exists && logInfo.Length > 1024 * 1024)
            {
                File.WriteAllText(logPath, entry);
            }
            else
            {
                File.AppendAllText(logPath, entry);
            }
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out _ownsSingleInstance);

        if (!_ownsSingleInstance)
        {
            SignalExistingInstance();
            Exit();
            return;
        }

        ShowRequestEvent = TryCreateShowRequestEvent();

        LegacyAppDataMigrationService.MigrateAndCleanUp();

        var startMinimized = Environment.GetCommandLineArgs()
            .Any(static arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        try
        {
            _window = new MainWindow();
        }
        catch (Exception ex)
        {
            LogCrash("Startup failed", ex);
            // Application.Exit is not enough here: a partially constructed
            // window's close-to-tray handler can cancel the close and leave a
            // windowless process holding the single-instance mutex.
            Environment.Exit(1);
        }

        if (!startMinimized)
        {
            _window.Activate();
        }
    }

    private static EventWaitHandle? TryCreateShowRequestEvent()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        }
        catch
        {
            return null;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (System.Threading.EventWaitHandle.TryOpenExisting(ShowRequestEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
        }
    }
}
