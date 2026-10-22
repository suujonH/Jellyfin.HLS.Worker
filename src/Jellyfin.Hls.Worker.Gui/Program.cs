using Avalonia;

namespace Jellyfin.Hls.Worker.Gui;

internal static class Program
{
    /// <summary>Gets the settings file: <c>--config</c>, or worker.json in the folder of the user's application data.</summary>
    public static string ConfigPath { get; private set; } = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        ConfigPath = Path.GetFullPath(index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin.HLS.Worker", "worker.json"));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
