using Microsoft.UI.Xaml;
using WindowsSoundboard.Services;

namespace WindowsSoundboard;

public partial class App : Application
{
    internal const string ShowEventName = @"Local\WindowsSoundboard.ShowWindow";

    private const string MutexName = @"Local\WindowsSoundboard.SingleInstance";

    // Kept for the lifetime of the process: the first instance owns the name.
    private static Mutex? _instanceMutex;

    private readonly bool _alreadyRunning;
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // A second copy would install a second keyboard hook, so one key press would
        // fire the same sound twice. Ask the running copy to show itself instead.
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);
        _alreadyRunning = !createdNew;

        if (_alreadyRunning)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowEventName);
                signal.Set();
            }
            catch (Exception ex)
            {
                Log.Warn($"could not reach the running instance: {ex.GetType().Name}");
            }

            Log.Info("another instance is already running; asked it to show its window");
        }

        Log.Info($"startup, log at {Log.FilePath}");
        UnhandledException += (_, args) => Log.Error("unhandled exception", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_alreadyRunning)
        {
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
