using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The defaults of a library of the server being edited. A library the server no longer has is still shown, struck through,
/// until its defaults are no longer wanted.
/// </summary>
internal sealed partial class LibraryViewModel : ObservableObject
{
    private readonly OptionList<SchemeOption> _schemes;
    private bool _rebuilding;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryViewModel"/> class.
    /// </summary>
    /// <param name="model">The defaults in the settings.</param>
    /// <param name="schemes">The choices of the default scheme.</param>
    public LibraryViewModel(LibrarySettings model, OptionList<SchemeOption> schemes)
    {
        Model = model;
        _schemes = schemes;
        _schemes.Changed += OnSchemesChanged;
    }

    public LibrarySettings Model { get; }

    public Guid Id => Model.Id;

    public string Name => Model.Name.Length > 0 ? Model.Name : Model.Id.ToString("N");

    public IReadOnlyList<string> Locations => Model.Locations;

    public string LocationsText => string.Join("、", Model.Locations);

    public bool HasSeveralLocations => Model.Locations.Count > 1;

    /// <summary>Gets or sets the folder media is added to by default: the one chosen, or the first.</summary>
    public string? Location
    {
        get => Model.Location is { } location && Model.Locations.Contains(location) ? location : Model.Locations.FirstOrDefault();
        set
        {
            if (value is not null && value != Model.Location)
            {
                Model.Location = value;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<SchemeOption> SchemeOptions => _schemes.Items;

    /// <summary>Gets or sets the default scheme; the first choice is none.</summary>
    public SchemeOption? Scheme
    {
        get => SchemeOptions.FirstOrDefault(o => o.Id == Model.SchemeId) ?? (SchemeOptions.Count > 0 ? SchemeOptions[0] : null);
        set
        {
            if (_rebuilding || value is null || value.Id == Model.SchemeId)
            {
                return;
            }

            Model.SchemeId = value.Id;
            OnPropertyChanged();
        }
    }

    public string Prefix
    {
        get => Model.Prefix;
        set => SetProperty(Model.Prefix, value, Model, (m, v) => m.Prefix = v);
    }

    public bool PrefixSources
    {
        get => Model.PrefixSources;
        set => SetProperty(Model.PrefixSources, value, Model, (m, v) => m.PrefixSources = v);
    }

    public bool PrefixHls
    {
        get => Model.PrefixHls;
        set => SetProperty(Model.PrefixHls, value, Model, (m, v) => m.PrefixHls = v);
    }

    /// <summary>Gets the qualities the media added can be encoded in at once: those of the server the worker did not turn off.</summary>
    public ObservableCollection<QualityOptionViewModel> PreEncodeQualities { get; } = [];

    /// <summary>Gets or sets a value indicating whether there is a quality to encode in at once.</summary>
    [ObservableProperty]
    public partial bool CanPreEncode { get; set; }

    /// <summary>Gets or sets a value indicating whether the server no longer has the library.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; set; }

    /// <summary>
    /// Takes what the server now tells of the library.
    /// </summary>
    /// <param name="name">Its name.</param>
    /// <param name="locations">Its folders.</param>
    public void Update(string name, IReadOnlyList<string> locations)
    {
        Model.Name = name;
        Model.Locations = [.. locations];
        IsMissing = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Locations));
        OnPropertyChanged(nameof(LocationsText));
        OnPropertyChanged(nameof(HasSeveralLocations));
        OnPropertyChanged(nameof(Location));
    }

    /// <summary>
    /// Offers the qualities the media added can be encoded in at once; the default keeps those it had that are not offered.
    /// </summary>
    /// <param name="available">The qualities.</param>
    public void SetPreEncodeQualities(IReadOnlyList<int> available)
    {
        PreEncodeQualities.Clear();
        foreach (var quality in available)
        {
            PreEncodeQualities.Add(new QualityOptionViewModel(quality, Model.PreEncodeQualities.Contains(quality), (q, on) => QualityOptions.Toggle(Model.PreEncodeQualities, q, on)));
        }

        CanPreEncode = available.Count > 0;
    }

    /// <summary>Stops following the choices; the defaults are gone.</summary>
    public void Detach() => _schemes.Changed -= OnSchemesChanged;

    private void OnSchemesChanged()
    {
        _rebuilding = true;
        try
        {
            OnPropertyChanged(nameof(SchemeOptions));
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(Scheme));
    }
}

/// <summary>
/// A library the server pre-encodes, as last read, and whether this worker claims its jobs.
/// </summary>
internal sealed partial class PreEncodeViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreEncodeViewModel"/> class.
    /// </summary>
    /// <param name="model">The library in the settings.</param>
    public PreEncodeViewModel(PreEncodeSettings model)
    {
        Model = model;
        RebuildQualities();
    }

    /// <summary>Raised when the qualities the worker encodes change.</summary>
    public event Action? QualitiesChanged;

    public PreEncodeSettings Model { get; }

    public Guid Id => Model.LibraryId;

    public string IdText => Model.LibraryId.ToString("N");

    public string Name => Model.Name.Length > 0 ? Model.Name : IdText;

    /// <summary>Gets the qualities of the server, on unless the worker turned them off.</summary>
    public ObservableCollection<QualityOptionViewModel> Qualities { get; } = [];

    public bool Enabled
    {
        get => Model.Enabled;
        set => SetProperty(Model.Enabled, value, Model, (m, v) => m.Enabled = v);
    }

    /// <summary>Gets or sets a value indicating whether the server no longer pre-encodes the library.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; set; }

    /// <summary>
    /// Takes what the server now tells of the library.
    /// </summary>
    /// <param name="name">Its name.</param>
    /// <param name="qualities">The qualities it is encoded in.</param>
    public void Update(string name, IReadOnlyList<int> qualities)
    {
        Model.Name = name;
        Model.Qualities = [.. qualities];
        IsMissing = false;
        OnPropertyChanged(nameof(Name));
        RebuildQualities();
        QualitiesChanged?.Invoke();
    }

    private void RebuildQualities()
    {
        Qualities.Clear();
        foreach (var quality in Model.Qualities)
        {
            Qualities.Add(new QualityOptionViewModel(quality, !Model.DisabledQualities.Contains(quality), (q, on) =>
            {
                QualityOptions.Toggle(Model.DisabledQualities, q, !on);
                QualitiesChanged?.Invoke();
            }));
        }
    }
}
