using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// A quality that can be turned on or off.
/// </summary>
internal sealed partial class QualityOptionViewModel : ObservableObject
{
    private readonly Action<int, bool>? _changed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityOptionViewModel"/> class.
    /// </summary>
    /// <param name="quality">The height of the quality.</param>
    /// <param name="isChecked">Whether it is on.</param>
    /// <param name="changed">Told when the user turns it on or off.</param>
    public QualityOptionViewModel(int quality, bool isChecked, Action<int, bool>? changed = null)
    {
        Quality = quality;
        IsChecked = isChecked;
        _changed = changed;
    }

    public int Quality { get; }

    public string Label => Quality.ToString(CultureInfo.InvariantCulture) + "p";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => _changed?.Invoke(Quality, value);
}

/// <summary>
/// Keeps lists of qualities in the settings.
/// </summary>
internal static class QualityOptions
{
    /// <summary>
    /// Puts a quality in a list, or takes it out.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="quality">The quality.</param>
    /// <param name="on">Whether it is in the list.</param>
    public static void Toggle(List<int> list, int quality, bool on)
    {
        if (on)
        {
            if (!list.Contains(quality))
            {
                list.Add(quality);
            }
        }
        else
        {
            list.RemoveAll(q => q == quality);
        }
    }
}
