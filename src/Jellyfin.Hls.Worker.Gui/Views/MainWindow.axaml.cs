using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jellyfin.Hls.Worker.Gui.ViewModels;

namespace Jellyfin.Hls.Worker.Gui.Views;

internal sealed partial class MainWindow : Window, IDialogs
{
    private const int UploadsPageIndex = 3;

    // The times of the steps being added move on every second.
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closing;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        Pages.SelectionChanged += (_, _) =>
        {
            RunPage.IsVisible = Pages.SelectedIndex == 0;
            AddPage.IsVisible = Pages.SelectedIndex == 1;
            SettingsPage.IsVisible = Pages.SelectedIndex == 2;
            UploadsPage.IsVisible = Pages.SelectedIndex == UploadsPageIndex;
            if (Pages.SelectedIndex == UploadsPageIndex && DataContext is MainViewModel viewModel)
            {
                viewModel.Uploads.RefreshCommand.Execute(null);
            }
        };

        // The sections are hidden rather than taken out, as the pages are.
        Control[] sections = [GlobalSection, ConnectionsSection, SchemesSection, LibrariesSection, PreEncodesSection];
        SettingsSections.SelectionChanged += (_, _) =>
        {
            for (var index = 0; index < sections.Length; index++)
            {
                sections[index].IsVisible = SettingsSections.SelectedIndex == index;
            }
        };
        AddPage.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddPage.AddHandler(DragDrop.DropEvent, OnDrop);
        LogList.KeyDown += OnLogKeyDown;
        _clock.Tick += (_, _) => (DataContext as MainViewModel)?.TickAdds();
        _clock.Start();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    public Task<bool> ShowAddDialogAsync(AddDialogViewModel dialog) => new AddDialog { DataContext = dialog }.ShowDialog<bool>(this);

    public Task<bool> ConfirmAsync(string message, string accept, string cancel) => new ConfirmDialog(message, accept, cancel).ShowDialog<bool>(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel viewModel)
        {
            // The fields of every page: the pages not shown are hidden, not taken out.
            viewModel.HasInputErrors = () => this.GetVisualDescendants().OfType<Control>().Any(DataValidationErrors.GetHasErrors);
            viewModel.Dialogs = this;

            // The newest line stays in view.
            viewModel.Log.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && viewModel.Log.Count > 0)
                {
                    LogList.ScrollIntoView(viewModel.Log.Count - 1);
                }
            };
        }
    }

    // The worker and an addition stop before the window closes: their jobs are handed back to the server.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
        {
            return;
        }

        // Asked again while stopping: the window still waits.
        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        IsEnabled = false;
        await ViewModel.ShutdownAsync();
        _clock.Stop();
        ViewModel.Dispose();
        _closed = true;
        Close();
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "添加文件", AllowMultiple = true });
        await ViewModel.OpenAddDialogAsync([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "添加文件夹", AllowMultiple = true });
        await ViewModel.OpenAddDialogAsync([.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()]);
    }

    // The selected lines are copied in the order of the log, whatever the order they were selected in.
    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(g => g.Matches(e)) != true || Clipboard is null)
        {
            return;
        }

        e.Handled = true;
        var log = ViewModel.Log;
        var lines = LogList.Selection.SelectedIndexes.Where(i => i >= 0 && i < log.Count).Order().Select(i => log[i]).ToList();
        if (lines.Count > 0)
        {
            await Clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ?DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } items)
        {
            await ViewModel.OpenAddDialogAsync([.. items.Select(i => i.TryGetLocalPath()).OfType<string>()]);
        }
    }
}
