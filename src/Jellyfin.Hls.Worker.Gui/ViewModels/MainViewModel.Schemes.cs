using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The schemes of the settings.
/// </summary>
internal sealed partial class MainViewModel
{
    public ObservableCollection<SchemeViewModel> Schemes { get; } = [];

    /// <summary>Gets the choices of the default scheme of a library: none, then every scheme.</summary>
    public OptionList<SchemeOption> SchemeChoices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneSchemeCommand), nameof(RemoveSchemeCommand))]
    public partial SchemeViewModel? SelectedScheme { get; set; }

    private void RebuildSchemeChoices()
        => SchemeChoices.Set([SchemeOption.None, .. Settings.Schemes.Select(s => new SchemeOption(s.Id, s.Name))]);

    [RelayCommand]
    private void NewScheme()
    {
        var number = Schemes.Count + 1;
        while (Schemes.Any(s => s.Name == "方案" + number))
        {
            number++;
        }

        var model = new SchemeSettings { Id = Guid.NewGuid(), Name = "方案" + number };
        Settings.Schemes.Add(model);
        SelectedScheme = AddSchemeViewModel(model);
        RebuildSchemeChoices();
    }

    private SchemeViewModel AddSchemeViewModel(SchemeSettings model)
    {
        var scheme = new SchemeViewModel(model, TargetOptions);
        scheme.PropertyChanged += OnSchemeChanged;
        Schemes.Add(scheme);
        return scheme;
    }

    // The libraries show the schemes by name.
    private void OnSchemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchemeViewModel.Name))
        {
            RebuildSchemeChoices();
        }
    }

    private bool HasSelectedScheme() => SelectedScheme is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedScheme))]
    private void CloneScheme()
    {
        if (SelectedScheme is not { } source)
        {
            return;
        }

        var name = source.Name + " 副本";
        for (var number = 2; Schemes.Any(s => s.Name == name); number++)
        {
            name = source.Name + " 副本" + number;
        }

        // A round trip through JSON copies every field, including those added later.
        var clone = JsonSerializer.Deserialize<SchemeSettings>(JsonSerializer.Serialize(source.Model))!;
        clone.Id = Guid.NewGuid();
        clone.Name = name;
        Settings.Schemes.Add(clone);
        SelectedScheme = AddSchemeViewModel(clone);
        RebuildSchemeChoices();
    }

    // The libraries that used it by default have none.
    [RelayCommand(CanExecute = nameof(HasSelectedScheme))]
    private void RemoveScheme()
    {
        if (SelectedScheme is not { } scheme)
        {
            return;
        }

        foreach (var library in Settings.Libraries.Where(l => l.SchemeId == scheme.Model.Id))
        {
            library.SchemeId = null;
        }

        scheme.Detach();
        scheme.PropertyChanged -= OnSchemeChanged;
        Settings.Schemes.Remove(scheme.Model);
        Schemes.Remove(scheme);
        SelectedScheme = Schemes.FirstOrDefault();
        RebuildSchemeChoices();
    }
}
