using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// A connection being edited. Its name and type change what the window shows; the other fields are those of the settings.
/// </summary>
internal sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly Func<ConnectionViewModel, string, string, bool> _changed;
    private string _reference;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionViewModel"/> class.
    /// </summary>
    /// <param name="model">The settings of the connection.</param>
    /// <param name="changed">
    /// Told the old and the new name when the connection is renamed, and the name it is known by twice when its type changes;
    /// answers whether the new name was taken up, which it is not while it is empty or that of another connection. A name is
    /// retyped through such names, so the old name is the last one taken up.
    /// </param>
    public ConnectionViewModel(ConnectionSettings model, Func<ConnectionViewModel, string, string, bool> changed)
    {
        Model = model;
        _changed = changed;
        _reference = model.Name;
    }

    public ConnectionSettings Model { get; }

    /// <summary>Gets the name the settings know the connection by: the last one taken up.</summary>
    public string Reference => _reference;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (SetProperty(Model.Name, value, Model, (m, v) => m.Name = v))
            {
                TakeUpName();
            }
        }
    }

    public ConnectionType Type
    {
        get => Model.Type;
        set
        {
            if (SetProperty(Model.Type, value, Model, (m, v) => m.Type = v))
            {
                OnPropertyChanged(nameof(IsS3));
                OnPropertyChanged(nameof(IsSftp));
                OnPropertyChanged(nameof(IsLocal));
                _changed(this, _reference, _reference);
            }
        }
    }

    public bool IsS3 => Type == ConnectionType.S3;

    /// <summary>Makes the name the one the settings know the connection by, if it can be.</summary>
    public void TakeUpName()
    {
        // Known by the new name while the others are told, so that one waiting for the old name can take it up.
        if (_reference != Name)
        {
            var old = _reference;
            _reference = Name;
            if (!_changed(this, old, Name))
            {
                _reference = old;
            }
        }
    }

    public bool IsSftp => Type == ConnectionType.Sftp;

    public bool IsLocal => Type == ConnectionType.Local;
}
