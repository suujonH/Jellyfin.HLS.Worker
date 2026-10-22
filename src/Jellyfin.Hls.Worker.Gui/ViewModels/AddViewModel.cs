using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>A library of the server.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Locations">Its folders on the server.</param>
/// <param name="Qualities">The qualities pre-encoded for it; empty when it is not.</param>
internal sealed record LibraryOption(Guid Id, string Name, string Locations, IReadOnlyList<int> Qualities);

/// <summary>Where the videos go: an S3 connection, or the folder of the library when <paramref name="Name"/> is empty.</summary>
/// <param name="Name">The S3 connection.</param>
/// <param name="Label">What is shown.</param>
internal sealed record DestinationOption(string Name, string Label);

/// <summary>A quality to encode at once, or not.</summary>
internal sealed partial class QualityOption(int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Label => Value + "p";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// The page that adds files to a library, as the add command does.
/// </summary>
internal sealed partial class AddViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cancel;
    private Task? _adding;

    public AddViewModel(MainViewModel main, ILogger logger)
    {
        _main = main;
        _logger = logger;
        Files.CollectionChanged += (_, _) => AddCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<LibraryOption> Libraries { get; } = [];

    public ObservableCollection<DestinationOption> Destinations { get; } = [];

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsEncoded))]
    public partial LibraryOption? SelectedLibrary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsS3))]
    public partial DestinationOption? SelectedDestination { get; set; }

    [ObservableProperty]
    public partial string Prefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Into { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedFile { get; set; }

    /// <summary>Gets or sets whether the destination and the prefix become the defaults of the library.</summary>
    [ObservableProperty]
    public partial bool Remember { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(CancelCommand), nameof(RefreshCommand))]
    public partial bool IsAdding { get; private set; }

    public bool IsS3 => SelectedDestination is { Name.Length: > 0 };

    public bool IsEncoded => SelectedLibrary is { Qualities.Count: > 0 };

    /// <summary>Rebuilds the destinations when the connections changed, keeping the one chosen if it is still there.</summary>
    /// <param name="renamed">A connection renamed, or whose type changed.</param>
    /// <param name="name">Its name now.</param>
    public void OnSettingsChanged(string? renamed = null, string? name = null)
    {
        var chosen = SelectedDestination?.Name;
        if (renamed is not null && chosen == renamed)
        {
            chosen = name;
        }

        Destinations.Clear();
        Destinations.Add(new DestinationOption(string.Empty, "直接放进媒体库目录"));
        foreach (var connection in _main.Connections.Where(c => c.Type == ConnectionType.S3))
        {
            // Known by the name the settings know, which is the one before while a rename waits.
            Destinations.Add(new DestinationOption(connection.Reference, "S3：" + connection.Name + "（媒体库中写 .strm）"));
        }

        SelectedDestination = Destinations.FirstOrDefault(d => d.Name == chosen) ?? Destinations[0];
    }

    /// <summary>Forgets the libraries, which are of the server of the settings before.</summary>
    public void ClearLibraries()
    {
        SelectedLibrary = null;
        Libraries.Clear();
    }

    /// <summary>Chooses the destination and the prefix the settings keep for the library chosen.</summary>
    public void ApplyDefaults()
    {
        if (SelectedLibrary is { } library)
        {
            var defaults = _main.Settings.GetLibrary(library.Id);
            SelectedDestination = Destinations.FirstOrDefault(d => d.Name == defaults.Connection) ?? Destinations.FirstOrDefault();
            Prefix = defaults.Prefix;
        }
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !Files.Contains(p)))
        {
            Files.Add(path);
        }
    }

    /// <summary>Cancels an addition in progress and waits for it.</summary>
    /// <returns>A task.</returns>
    public async Task CancelAndWaitAsync()
    {
        Cancel();
        if (_adding is { } adding)
        {
            await adding;
        }
    }

    public void Dispose() => _cancel?.Dispose();

    partial void OnSelectedLibraryChanged(LibraryOption? value)
    {
        Qualities.Clear();
        if (value is null)
        {
            return;
        }

        foreach (var quality in value.Qualities)
        {
            Qualities.Add(new QualityOption(quality));
        }

        ApplyDefaults();
    }

    [RelayCommand]
    private void RemoveFile()
    {
        if (SelectedFile is { } file)
        {
            Files.Remove(file);
        }
    }

    [RelayCommand]
    private void ClearFiles() => Files.Clear();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!_main.TrySave() || _main.AcquireEngine() is not { } engine)
        {
            return;
        }

        try
        {
            var folders = await engine.Engine.Server.GetVirtualFoldersAsync(CancellationToken.None);
            var encoded = await engine.Engine.Server.GetLibrariesAsync(CancellationToken.None);
            var chosen = SelectedLibrary?.Id;
            Libraries.Clear();
            foreach (var folder in folders)
            {
                if (Guid.TryParse(folder.ItemId, out var id))
                {
                    var qualities = encoded.FirstOrDefault(l => l.Id.Equals(id))?.Qualities ?? [];
                    Libraries.Add(new LibraryOption(id, folder.Name, string.Join("、", folder.Locations), qualities));
                }
            }

            SelectedLibrary = Libraries.FirstOrDefault(l => l.Id == chosen) ?? Libraries.FirstOrDefault();
            _main.Status = "服务器上有 " + Libraries.Count + " 个媒体库";
        }
        catch (Exception ex)
        {
            _logger.LogError("读取媒体库失败：{Message}", ex.Message);
            _main.Status = "读取媒体库失败：" + ex.Message;
        }
        finally
        {
            _main.ReleaseEngine();
        }
    }

    private bool CanRefresh() => !IsAdding;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (SelectedLibrary is not { } library || SelectedDestination is not { } destination)
        {
            return;
        }

        if (Remember)
        {
            var settings = _main.Settings;
            settings.Libraries.RemoveAll(l => l.Id.Equals(library.Id));
            settings.Libraries.Add(new LibrarySettings { Id = library.Id, Connection = destination.Name, Prefix = destination.Name.Length > 0 ? Prefix : string.Empty });
        }

        if (!_main.TrySave() || _main.AcquireEngine() is not { } engine)
        {
            return;
        }

        // The worker running keeps the connections it started with.
        if (destination.Name.Length > 0 && !engine.Settings.Connections.Any(c => c.Name == destination.Name && c.Type == ConnectionType.S3))
        {
            _main.Status = "正在运行的 Worker 还没有 S3 连接 " + destination.Name + "：停止后再开始即可使用";
            _main.ReleaseEngine();
            return;
        }

        IsAdding = true;
        _cancel = new CancellationTokenSource();
        var request = new AddRequest(
            library.Id,
            Files.ToList(),
            Into,
            destination.Name,
            Qualities.Where(q => q.IsChecked).Select(q => q.Value).ToList(),
            destination.Name.Length > 0 ? Prefix : null);
        _adding = RunAsync(engine.Engine, request, _cancel.Token);
        await _adding;
    }

    private bool CanAdd() => !IsAdding && SelectedLibrary is not null && Files.Count > 0;

    [RelayCommand(CanExecute = nameof(IsAdding))]
    private void Cancel() => _cancel?.Cancel();

    private async Task RunAsync(WorkerEngine engine, AddRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // On the thread pool: a folder is listed before anything is awaited.
            var result = await Task.Run(() => engine.AddAsync(request, cancellationToken), cancellationToken);
            _main.Status = "添加结束：成功 " + result.Added + "，失败 " + result.Failed;
            if (result.Failed == 0)
            {
                foreach (var path in request.Paths)
                {
                    Files.Remove(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("添加已取消");
            _main.Status = "添加已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError("添加失败：{Message}", ex.Message);
            _main.Status = "添加失败：" + ex.Message;
        }
        finally
        {
            IsAdding = false;
            _adding = null;
            _cancel?.Dispose();
            _cancel = null;
            _main.ReleaseEngine();
        }
    }
}
