using Avalonia;
using DroidLens.Client.Services;

namespace DroidLens.Client;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance guard: a second launch must NOT open a duplicate window —
        // it signals the already-running process (which restores its window) and
        // exits immediately, before any Avalonia window is even created.
        if (!SingleInstanceService.TryClaimFirstInstance())
        {
            SingleInstanceService.SignalFirstInstance();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
