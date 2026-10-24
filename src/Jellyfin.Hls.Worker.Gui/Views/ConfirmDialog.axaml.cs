using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jellyfin.Hls.Worker.Gui.Views;

/// <summary>
/// Asks for a confirmation; it closes with whether it was given.
/// </summary>
internal sealed partial class ConfirmDialog : Window
{
    // For the designer.
    public ConfirmDialog()
        : this(string.Empty, "确定", "取消")
    {
    }

    public ConfirmDialog(string message, string accept, string cancel)
    {
        InitializeComponent();
        Message.Text = message;
        AcceptButton.Content = accept;
        CancelButton.Content = cancel;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
