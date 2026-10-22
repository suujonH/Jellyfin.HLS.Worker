using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Jellyfin.Hls.Worker.Gui.ViewModels;

namespace Jellyfin.Hls.Worker.Gui.Views;

internal sealed partial class MainWindow : Window
{
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
        };
        FileList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FileList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel viewModel)
        {
            // The fields of every page: the pages not shown are hidden, not taken out.
            viewModel.HasInputErrors = () => this.GetVisualDescendants().OfType<Control>().Any(DataValidationErrors.GetHasErrors);

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
        ViewModel.Dispose();
        _closed = true;
        Close();
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "添加文件", AllowMultiple = true });
        ViewModel.Add.AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "添加文件夹", AllowMultiple = true });
        ViewModel.Add.AddFiles(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void OnOpenConfigClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开配置",
            FileTypeFilter = [new FilePickerFileType("Worker 配置") { Patterns = ["*.json"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            ViewModel.Open(path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } items)
        {
            ViewModel.Add.AddFiles(items.Select(i => i.TryGetLocalPath()).OfType<string>());
        }
    }
}
