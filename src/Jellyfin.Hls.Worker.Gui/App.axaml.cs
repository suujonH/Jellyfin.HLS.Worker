using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Jellyfin.Hls.Worker.Gui.ViewModels;
using Jellyfin.Hls.Worker.Gui.Views;

namespace Jellyfin.Hls.Worker.Gui;

internal sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(Program.ConfigPath) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
