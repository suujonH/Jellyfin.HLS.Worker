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
            var viewModel = new MainViewModel(Program.Paths);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // What the server has may have changed since the settings were saved.
            viewModel.RefreshInBackground();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
