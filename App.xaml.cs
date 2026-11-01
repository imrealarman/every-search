using Microsoft.UI.Xaml;

namespace EverySearch;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        if (!SingleInstance.Acquire()) Environment.Exit(0); // already running: it was asked to show itself
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // "--background" is what the Start-with-Windows entry passes: sit in the tray until the hotkey is pressed.
        bool background = Environment.GetCommandLineArgs().Contains("--background");
        window = new MainWindow(background);
        SingleInstance.Listen(() => window.DispatcherQueue.TryEnqueue(window.ShowPalette));
    }
}
