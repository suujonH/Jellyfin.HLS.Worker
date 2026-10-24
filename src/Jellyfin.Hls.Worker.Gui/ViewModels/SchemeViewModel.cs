using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The choices of the lines of the schemes: the directories each kind of line can be.
/// </summary>
internal sealed class TargetOptions
{
    /// <summary>The choice of the folder of the library.</summary>
    public static readonly TargetOption JellyfinLibrary = new(SchemeEntry.JellyfinLibrary, Media.SchemePlanner.JellyfinLibraryLabel, null);

    /// <summary>Gets what an HLS line can be: any directory.</summary>
    public OptionList<TargetOption> HlsTargets { get; } = new();

    /// <summary>Gets what a source line can be: the library, and any directory.</summary>
    public OptionList<TargetOption> SourceTargets { get; } = new();

    /// <summary>
    /// Makes the choices of the connections anew.
    /// </summary>
    /// <param name="connections">The connections, in the order they are shown.</param>
    public void Rebuild(IEnumerable<ConnectionSettings> connections)
    {
        var all = connections.Select(c => new TargetOption(c.Id, c.Name, c.Type)).ToList();
        HlsTargets.Set(all);
        SourceTargets.Set([JellyfinLibrary, .. all]);
    }
}

/// <summary>
/// A scheme being edited: its lines of HLS directories and of source directories, which tell which line can be declared and
/// what is wrong with a line. At most one line of each list is declared.
/// </summary>
internal sealed partial class SchemeViewModel : ObservableObject
{
    private readonly TargetOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemeViewModel"/> class.
    /// </summary>
    /// <param name="model">The scheme in the settings.</param>
    /// <param name="options">The choices of its lines.</param>
    public SchemeViewModel(SchemeSettings model, TargetOptions options)
    {
        Model = model;
        _options = options;
        foreach (var entry in model.Hls)
        {
            Hls.Add(new SchemeEntryViewModel(this, entry, isHls: true, options));
        }

        foreach (var entry in model.Sources)
        {
            Sources.Add(new SchemeEntryViewModel(this, entry, isHls: false, options));
        }

        Refresh();
    }

    public SchemeSettings Model { get; }

    public ObservableCollection<SchemeEntryViewModel> Hls { get; } = [];

    public ObservableCollection<SchemeEntryViewModel> Sources { get; } = [];

    public string Name
    {
        get => Model.Name;
        set => SetProperty(Model.Name, value, Model, (m, v) => m.Name = v);
    }

    /// <summary>Gets what is wrong with the scheme as a whole; <c>null</c> when nothing is.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>
    /// Tells every line whether it can be declared and what is wrong with it, by the rules the settings are checked with.
    /// </summary>
    public void Refresh()
    {
        foreach (var entry in Hls)
        {
            entry.CanDeclare = true;
            entry.Error = GetError(entry);
        }

        // The file in the library is what the server is told: no other line is declared.
        var library = Sources.FirstOrDefault(e => e.IsLibrary);
        foreach (var entry in Sources)
        {
            entry.CanDeclare = library is null;
            entry.Error = GetError(entry) ?? (entry.IsLibrary && entry != library ? "只能有一行「Jellyfin 媒体库」。" : null);
        }

        Error = Hls.FirstOrDefault(e => e.Declare) is { IsServer: true } && !Hls.Any(e => e.SelectedTarget is not null && !e.IsServer)
            ? "HLS 申告服务器目录时，还需要一个上传 HLS 的目录。"
            : null;
    }

    /// <summary>
    /// Makes a line the one declared of its list.
    /// </summary>
    /// <param name="entry">The line.</param>
    public void Declare(SchemeEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var line in entry.IsHls ? Hls : Sources)
        {
            line.Model.Declare = line == entry;
            line.NotifyDeclare();
        }
    }

    /// <summary>Stops following the choices; the scheme is gone.</summary>
    public void Detach()
    {
        foreach (var entry in Hls.Concat(Sources))
        {
            entry.Detach();
        }
    }

    [RelayCommand]
    private void AddHls() => AddEntry(Hls, Model.Hls, isHls: true);

    [RelayCommand]
    private void AddSource() => AddEntry(Sources, Model.Sources, isHls: false);

    [RelayCommand]
    private void RemoveEntry(SchemeEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.Detach();
        if (Hls.Remove(entry))
        {
            Model.Hls.Remove(entry.Model);
        }
        else if (Sources.Remove(entry))
        {
            Model.Sources.Remove(entry.Model);
        }

        Refresh();
    }

    private static string? GetError(SchemeEntryViewModel entry)
    {
        if (entry.SelectedTarget is null)
        {
            return "请选择目录。";
        }

        if (entry.Declare)
        {
            return null;
        }

        return entry.IsServer ? "服务器目录不能上传，只能用于申告，请勾选「申告此目录」。"
            : entry.IsLibrary ? "「Jellyfin 媒体库」行必须申告。"
            : null;
    }

    private void AddEntry(ObservableCollection<SchemeEntryViewModel> entries, List<SchemeEntry> models, bool isHls)
    {
        var model = new SchemeEntry();
        models.Add(model);
        entries.Add(new SchemeEntryViewModel(this, model, isHls, _options));
        Refresh();
    }
}

/// <summary>
/// A line of a scheme being edited: the directory, and whether it is declared.
/// </summary>
internal sealed partial class SchemeEntryViewModel : ObservableObject
{
    private readonly SchemeViewModel _scheme;
    private readonly OptionList<TargetOption> _targets;
    private bool _rebuilding;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemeEntryViewModel"/> class.
    /// </summary>
    /// <param name="scheme">The scheme of the line.</param>
    /// <param name="model">The line in the settings.</param>
    /// <param name="isHls">Whether it is a line of the HLS directories.</param>
    /// <param name="options">The choices.</param>
    public SchemeEntryViewModel(SchemeViewModel scheme, SchemeEntry model, bool isHls, TargetOptions options)
    {
        _scheme = scheme;
        Model = model;
        IsHls = isHls;
        _targets = isHls ? options.HlsTargets : options.SourceTargets;
        _targets.Changed += OnOptionsChanged;
    }

    public SchemeEntry Model { get; }

    public bool IsHls { get; }

    public SchemeViewModel Scheme => _scheme;

    public IReadOnlyList<TargetOption> TargetOptions => _targets.Items;

    /// <summary>Gets or sets the directory; <c>null</c> while none is chosen, or the one chosen is gone.</summary>
    public TargetOption? SelectedTarget
    {
        get => Model.ConnectionId is { } id ? TargetOptions.FirstOrDefault(o => o.ConnectionId.Equals(id)) : null;
        set
        {
            // A drop-down whose choices are replaced lets go of its choice; that is no choice of the user.
            if (_rebuilding || value is null)
            {
                return;
            }

            Model.ConnectionId = value.ConnectionId;

            // The file in the library is what the server is told.
            if (IsLibrary)
            {
                _scheme.Declare(this);
            }

            Changed();
        }
    }

    /// <summary>Gets a value indicating whether the line is the folder of the library.</summary>
    public bool IsLibrary => Model.ConnectionId.Equals(SchemeEntry.JellyfinLibrary);

    /// <summary>Gets a value indicating whether the line is a directory of the server, which is only declared.</summary>
    public bool IsServer => SelectedTarget?.Type == ConnectionType.Server;

    /// <summary>Gets or sets whether the server is told the file is in this directory; declaring it undeclares the other lines.</summary>
    public bool Declare
    {
        get => Model.Declare;
        set
        {
            if (value == Model.Declare || !CanDeclare)
            {
                return;
            }

            if (value)
            {
                _scheme.Declare(this);
            }
            else
            {
                Model.Declare = false;
            }

            Changed();
        }
    }

    /// <summary>Gets or sets a value indicating whether the user can change <see cref="Declare"/>; not beside the folder of the library.</summary>
    [ObservableProperty]
    public partial bool CanDeclare { get; set; }

    /// <summary>Gets or sets what is wrong with the line; <c>null</c> when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Tells the view <see cref="Declare"/> may have changed.</summary>
    public void NotifyDeclare() => OnPropertyChanged(nameof(Declare));

    /// <summary>Stops following the choices; the line is gone.</summary>
    public void Detach() => _targets.Changed -= OnOptionsChanged;

    // A directory that is gone is no choice: the line has none.
    private void OnOptionsChanged()
    {
        if (Model.ConnectionId is { } id && !TargetOptions.Any(o => o.ConnectionId.Equals(id)))
        {
            Model.ConnectionId = null;
        }

        _rebuilding = true;
        try
        {
            OnPropertyChanged(nameof(TargetOptions));
        }
        finally
        {
            _rebuilding = false;
        }

        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(SelectedTarget));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsServer));
        OnPropertyChanged(nameof(Declare));
        _scheme.Refresh();
    }
}
