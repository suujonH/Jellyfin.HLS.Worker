using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The defaults of the libraries of the server and the libraries it pre-encodes, as last read from it.
/// </summary>
internal sealed partial class MainViewModel
{
    public ObservableCollection<LibraryViewModel> Libraries { get; } = [];

    public ObservableCollection<PreEncodeViewModel> PreEncodes { get; } = [];

    [ObservableProperty]
    public partial LibraryViewModel? SelectedLibrary { get; set; }

    [ObservableProperty]
    public partial PreEncodeViewModel? SelectedPreEncode { get; set; }

    /// <summary>
    /// Reads the libraries of the server: each one gets defaults, and the defaults of one it no longer has are struck through.
    /// </summary>
    /// <returns>A task.</returns>
    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshLibrariesAsync()
    {
        if (CreateClient() is not { } client)
        {
            return;
        }

        using (client)
        {
            try
            {
                var folders = await client.GetVirtualFoldersAsync(CancellationToken.None);
                var found = new List<LibraryViewModel>();
                foreach (var folder in folders)
                {
                    if (!Guid.TryParse(folder.ItemId, out var id))
                    {
                        continue;
                    }

                    var library = Libraries.FirstOrDefault(l => l.Id.Equals(id));
                    if (library is null)
                    {
                        var model = new LibrarySettings { Id = id };
                        Settings.Libraries.Add(model);
                        library = new LibraryViewModel(model, SchemeChoices);
                        Libraries.Add(library);
                    }

                    library.Update(folder.Name, folder.Locations);
                    found.Add(library);
                }

                foreach (var library in Libraries.Except(found))
                {
                    library.IsMissing = true;
                }

                // The libraries of the server first, as it orders them.
                Reorder(Libraries, [.. found, .. Libraries.Except(found)]);
                SelectedLibrary ??= Libraries.FirstOrDefault();
                UpdatePreEncodeQualities();
                Status = "服务器上有 " + found.Count + " 个媒体库";
            }
            catch (Exception ex) when (ex is ServerException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogError("读取媒体库失败：{Message}", ex.Message);
                Status = "读取媒体库失败：" + ex.Message;
            }
        }
    }

    /// <summary>
    /// Reads the libraries the server pre-encodes: one found the first time is enabled, and one it no longer pre-encodes is
    /// struck through.
    /// </summary>
    /// <returns>A task.</returns>
    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshPreEncodesAsync()
    {
        if (CreateClient() is not { } client)
        {
            return;
        }

        using (client)
        {
            try
            {
                var libraries = await client.GetLibrariesAsync(CancellationToken.None);
                var found = new List<PreEncodeViewModel>();
                foreach (var library in libraries)
                {
                    var preEncode = PreEncodes.FirstOrDefault(p => p.Id.Equals(library.Id));
                    if (preEncode is null)
                    {
                        var model = new PreEncodeSettings { LibraryId = library.Id, Enabled = true };
                        Settings.PreEncodes.Add(model);
                        preEncode = AddPreEncodeViewModel(model);
                    }

                    preEncode.Update(library.Name, library.Qualities);
                    found.Add(preEncode);
                }

                foreach (var preEncode in PreEncodes.Except(found))
                {
                    preEncode.IsMissing = true;
                }

                Reorder(PreEncodes, [.. found, .. PreEncodes.Except(found)]);
                SelectedPreEncode ??= PreEncodes.FirstOrDefault();
                UpdatePreEncodeQualities();
                Status = "服务器预编码 " + found.Count + " 个媒体库";
            }
            catch (Exception ex) when (ex is ServerException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogError("读取预编码媒体库失败：{Message}", ex.Message);
                Status = "读取预编码媒体库失败：" + ex.Message;
            }
        }
    }

    // Moving rather than clearing keeps what each list has selected.
    private static void Reorder<T>(ObservableCollection<T> items, IReadOnlyList<T> order)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var current = items.IndexOf(order[index]);
            if (current != index)
            {
                items.Move(current, index);
            }
        }
    }

    // The libraries follow the qualities of the pre-encodes; the pre-encodes are never taken out.
    private PreEncodeViewModel AddPreEncodeViewModel(PreEncodeSettings model)
    {
        var preEncode = new PreEncodeViewModel(model);
        preEncode.QualitiesChanged += UpdatePreEncodeQualities;
        PreEncodes.Add(preEncode);
        return preEncode;
    }

    // A library can be encoded at once in the qualities the server pre-encodes it in, less those the worker turned off.
    private void UpdatePreEncodeQualities()
    {
        foreach (var library in Libraries)
        {
            var preEncode = PreEncodes.FirstOrDefault(p => p.Id.Equals(library.Id) && !p.IsMissing);
            library.SetPreEncodeQualities(preEncode?.Model.EnabledQualities ?? []);
        }
    }
}
