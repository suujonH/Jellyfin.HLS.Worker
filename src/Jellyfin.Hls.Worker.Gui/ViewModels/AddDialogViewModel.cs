using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>What the dialog is for.</summary>
internal enum AddDialogMode
{
    /// <summary>Adding files to a library.</summary>
    Add = 0,

    /// <summary>Claiming the jobs of the libraries pre-encoded.</summary>
    Start = 1
}

/// <summary>A library the dialog offers.</summary>
/// <param name="Id">The library.</param>
/// <param name="Name">Its name.</param>
/// <param name="Locations">Its folders.</param>
/// <param name="Qualities">The qualities the server pre-encodes it in; empty when it does not.</param>
internal sealed record DialogLibrary(Guid Id, string Name, IReadOnlyList<string> Locations, IReadOnlyList<int> Qualities);

/// <summary>
/// The dialog that adds files to a library, or starts claiming jobs, with the schemes chosen for it. A choice the user has not
/// made follows the defaults of the library chosen; none of the choices is written to the settings.
/// </summary>
internal sealed partial class AddDialogViewModel : ObservableObject
{
    private readonly WorkerSettings _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyList<VirtualFolder>>> _folders;
    private readonly Func<CancellationToken, Task<IReadOnlyList<WorkerLibrary>>> _encoded;
    private readonly Dictionary<Guid, SchemeOption> _choices = [];
    private bool _applying;
    private bool _touchedSource;
    private bool _touchedHls;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddDialogViewModel"/> class.
    /// </summary>
    /// <param name="mode">What the dialog is for.</param>
    /// <param name="settings">The settings the worker uses, which the choices are resolved with.</param>
    /// <param name="folders">Gets the libraries of the server.</param>
    /// <param name="encoded">Gets the libraries the server pre-encodes.</param>
    /// <param name="paths">The files and folders to add; none to start.</param>
    public AddDialogViewModel(
        AddDialogMode mode,
        WorkerSettings settings,
        Func<CancellationToken, Task<IReadOnlyList<VirtualFolder>>> folders,
        Func<CancellationToken, Task<IReadOnlyList<WorkerLibrary>>> encoded,
        IReadOnlyList<string> paths)
    {
        Mode = mode;
        _settings = settings;
        _folders = folders;
        _encoded = encoded;
        Paths = paths;
        SchemeOptions = [SchemeOption.None, .. settings.Schemes.Select(s => new SchemeOption(s.Id, s.Name))];

        // No choice of the user.
        _applying = true;
        SourceScheme = HlsScheme = SchemeOption.None;
        _applying = false;
    }

    public AddDialogMode Mode { get; }

    public bool IsAdd => Mode == AddDialogMode.Add;

    public bool IsStart => Mode == AddDialogMode.Start;

    public string Title => IsAdd ? "添加媒体" : "开始领取任务";

    public string AcceptText => IsAdd ? "加入列表" : "开始";

    public IReadOnlyList<string> Paths { get; }

    public string PathsText => "共 " + Paths.Count + " 项";

    public IReadOnlyList<SchemeOption> SchemeOptions { get; }

    public ObservableCollection<DialogLibrary> Libraries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Locations), nameof(ShowLocations), nameof(CanPreEncode), nameof(PrefixText))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial DialogLibrary? SelectedLibrary { get; set; }

    public IReadOnlyList<string> Locations => SelectedLibrary?.Locations ?? [];

    public bool ShowLocations => IsAdd && Locations.Count > 1;

    [ObservableProperty]
    public partial string? SelectedLocation { get; set; }

    [ObservableProperty]
    public partial SchemeOption SourceScheme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreEncode))]
    public partial SchemeOption HlsScheme { get; set; }

    /// <summary>Gets the qualities the media can be encoded in at once, with those chosen.</summary>
    public ObservableCollection<QualityOptionViewModel> PreEncodeQualities { get; } = [];

    /// <summary>Gets a value indicating whether the media can be encoded at once: the library is pre-encoded and the renditions go somewhere.</summary>
    public bool CanPreEncode => IsAdd
        && SelectedLibrary is { Qualities.Count: > 0 }
        && _settings.GetScheme(HlsScheme?.Id) is { } scheme
        && scheme.HlsEntries.Any();

    /// <summary>Gets the prefix of the library and what it is used for.</summary>
    public string PrefixText
    {
        get
        {
            if (SelectedLibrary is null)
            {
                return string.Empty;
            }

            var library = _settings.GetLibrary(SelectedLibrary.Id);
            var prefix = library.Prefix.Trim('/');
            if (prefix.Length == 0)
            {
                return "（无）";
            }

            var uses = new List<string>();
            if (library.PrefixSources)
            {
                uses.Add("源文件");
            }

            if (library.PrefixHls)
            {
                uses.Add("HLS");
            }

            return prefix + (uses.Count == 0 ? "（不使用）" : "（" + string.Join("、", uses) + " 使用）");
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Gets what to add, once confirmed in <see cref="AddDialogMode.Add"/>.</summary>
    public AddRequest? Request { get; private set; }

    /// <summary>Gets where the renditions of each library go, once confirmed in <see cref="AddDialogMode.Start"/>.</summary>
    public IReadOnlyDictionary<Guid, HlsPlan> Plans { get; private set; } = new Dictionary<Guid, HlsPlan>();

    /// <summary>Gets the names of the libraries whose jobs are skipped, once confirmed in <see cref="AddDialogMode.Start"/>.</summary>
    public IReadOnlyList<string> Unconfigured { get; private set; } = [];

    /// <summary>Raised when the dialog is confirmed and can close.</summary>
    public event Action? Accepted;

    /// <summary>
    /// Reads the libraries from the server; a failure is shown, and can be retried.
    /// </summary>
    /// <returns>A task.</returns>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var encoded = await _encoded(CancellationToken.None);
            var libraries = new List<DialogLibrary>();
            if (IsAdd)
            {
                foreach (var folder in await _folders(CancellationToken.None))
                {
                    if (Guid.TryParse(folder.ItemId, out var id))
                    {
                        libraries.Add(new DialogLibrary(id, folder.Name, folder.Locations, _settings.FilterQualities(id, encoded.FirstOrDefault(l => l.Id.Equals(id))?.Qualities ?? [])));
                    }
                }
            }
            else
            {
                // The libraries the user enabled, with a quality left on; one never refreshed is enabled, as it would be once it is.
                libraries.AddRange(encoded
                    .Where(l => _settings.PreEncodes.FirstOrDefault(p => p.LibraryId.Equals(l.Id)) is not { Enabled: false })
                    .Select(l => new DialogLibrary(l.Id, l.Name, [], _settings.FilterQualities(l.Id, l.Qualities)))
                    .Where(l => l.Qualities.Count > 0));
            }

            var chosen = SelectedLibrary?.Id;
            Libraries.Clear();
            foreach (var library in libraries)
            {
                Libraries.Add(library);
            }

            SelectedLibrary = Libraries.FirstOrDefault(l => l.Id == chosen) ?? Libraries.FirstOrDefault();
            if (Libraries.Count == 0)
            {
                LoadError = IsAdd ? "服务器上没有媒体库。" : "没有启用的预编码媒体库，或档位已全部关闭（见「设置 → 预编码配置」）。";
            }
        }
        catch (Exception ex) when (ex is ServerException or HttpRequestException or IOException or TaskCanceledException)
        {
            LoadError = "读取媒体库失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedLibraryChanged(DialogLibrary? oldValue, DialogLibrary? newValue)
    {
        if (IsStart && oldValue is not null)
        {
            _choices[oldValue.Id] = HlsScheme;
        }

        if (newValue is null)
        {
            return;
        }

        _applying = true;
        try
        {
            var library = _settings.GetLibrary(newValue.Id);
            SelectedLocation = library.Location is { } location && newValue.Locations.Contains(location) ? location : newValue.Locations.FirstOrDefault();
            if (IsStart)
            {
                HlsScheme = _choices.TryGetValue(newValue.Id, out var choice) ? choice : Defaults(library);
            }
            else
            {
                var defaults = Defaults(library);
                if (!_touchedSource)
                {
                    SourceScheme = defaults;
                }

                if (!_touchedHls)
                {
                    HlsScheme = defaults;
                }

            }

            // The qualities to encode in at once are the library's, whatever was chosen before.
            PreEncodeQualities.Clear();
            foreach (var quality in newValue.Qualities)
            {
                PreEncodeQualities.Add(new QualityOptionViewModel(quality, CanPreEncode && library.PreEncodeQualities.Contains(quality)));
            }
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnSourceSchemeChanged(SchemeOption value) => _touchedSource |= !_applying;

    partial void OnHlsSchemeChanged(SchemeOption value)
    {
        _touchedHls |= !_applying;
        if (!CanPreEncode)
        {
            foreach (var option in PreEncodeQualities)
            {
                option.IsChecked = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept() => TryAccept();

    private bool CanAccept() => !IsLoading && (IsStart ? Libraries.Count > 0 : SelectedLibrary is not null);

    /// <summary>
    /// Resolves the choices: what to add, or where the renditions of each library go. What cannot be resolved for an addition
    /// is shown, and the dialog stays.
    /// </summary>
    /// <returns>Whether the dialog can close.</returns>
    public bool TryAccept()
    {
        Error = null;
        if (IsAdd)
        {
            if (SelectedLibrary is not { } library)
            {
                return false;
            }

            if (ShowLocations && SelectedLocation is null)
            {
                Error = "这个媒体库有多个目录，请选择写入哪一个。";
                return false;
            }

            try
            {
                var defaults = _settings.GetLibrary(library.Id);
                var source = SchemePlanner.ResolveSource(_settings, _settings.GetScheme(SourceScheme.Id), defaults);
                var hls = SchemePlanner.ResolveHls(_settings, _settings.GetScheme(HlsScheme.Id), defaults);
                IReadOnlyList<int> qualities = hls is null || !CanPreEncode ? [] : [.. PreEncodeQualities.Where(o => o.IsChecked).Select(o => o.Quality)];
                Request = new AddRequest(library.Id, ShowLocations ? SelectedLocation : null, Paths, source, hls, qualities);
            }
            catch (ArgumentException ex)
            {
                Error = ex.Message;
                return false;
            }
        }
        else
        {
            if (SelectedLibrary is { } current)
            {
                _choices[current.Id] = HlsScheme;
            }

            var plans = new Dictionary<Guid, HlsPlan>();
            var unconfigured = new List<string>();
            foreach (var library in Libraries)
            {
                var defaults = _settings.GetLibrary(library.Id);
                var choice = _choices.TryGetValue(library.Id, out var chosen) ? chosen : Defaults(defaults);
                try
                {
                    if (SchemePlanner.ResolveHls(_settings, _settings.GetScheme(choice.Id), defaults) is { } plan)
                    {
                        plans[library.Id] = plan;
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    // Not configured either: the renditions cannot be declared.
                }

                unconfigured.Add(library.Name);
            }

            Plans = plans;
            Unconfigured = unconfigured;
        }

        Accepted?.Invoke();
        return true;
    }

    // The default scheme of the library.
    private SchemeOption Defaults(LibrarySettings library)
        => SchemeOptions.FirstOrDefault(o => o.Id is not null && o.Id == library.SchemeId) ?? SchemeOption.None;
}
