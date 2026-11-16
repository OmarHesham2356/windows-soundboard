using Microsoft.UI.Xaml;
using WindowsSoundboard.Services;

namespace WindowsSoundboard;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Log.Info($"startup, log at {Log.FilePath}");
        UnhandledException += (_, args) => Log.Error("unhandled exception", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
