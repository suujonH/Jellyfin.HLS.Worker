using Avalonia;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui;

internal static class Program
{
    /// <summary>Gets where the worker keeps its data: <c>--data</c>, or the folder of the user's application data.</summary>
    public static WorkerPaths Paths { get; private set; } = WorkerPaths.Default;

    [STAThread]
    public static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--data");
        if (index >= 0 && index + 1 < args.Length)
        {
            Paths = new WorkerPaths(Path.GetFullPath(args[index + 1]));
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
