using Avalonia.Controls;
using Avalonia.Interactivity;
using Jellyfin.Hls.Worker.Gui.ViewModels;

namespace Jellyfin.Hls.Worker.Gui.Views;

/// <summary>
/// The dialog of an addition or of a start; it closes with whether it was confirmed.
/// </summary>
internal sealed partial class AddDialog : Window
{
    public AddDialog() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AddDialogViewModel dialog)
        {
            dialog.Accepted += () => Close(true);
        }
    }

    // What it shows is read once it is shown.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is AddDialogViewModel dialog)
        {
            dialog.LoadCommand.Execute(null);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
