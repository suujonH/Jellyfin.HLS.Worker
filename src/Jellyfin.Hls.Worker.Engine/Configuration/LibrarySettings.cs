using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// What a library of the server uses by default when media is added to it or its jobs are claimed.
/// </summary>
public sealed class LibrarySettings
{
    /// <summary>Gets or sets the library.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name the server gave the library when it was last read.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the folders the server gave the library when it was last read.</summary>
    public List<string> Locations { get; set; } = [];

    /// <summary>Gets or sets the folder media is added to by default when the library has several; <c>null</c> for the first.</summary>
    public string? Location { get; set; }

    /// <summary>Gets or sets the scheme used by default; <c>null</c> for none.</summary>
    public Guid? SchemeId { get; set; }

    /// <summary>Gets or sets the directory below the directories of the scheme, with forward slashes; may be empty.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the original files are put below <see cref="Prefix"/>.</summary>
    public bool PrefixSources { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the renditions are put below <see cref="Prefix"/>.</summary>
    public bool PrefixHls { get; set; } = true;

    /// <summary>Gets or sets the qualities the media added is encoded in at once by default; those the worker turned off are kept but not used.</summary>
    public List<int> PreEncodeQualities { get; set; } = [];

    /// <summary>
    /// Checks the default.
    /// </summary>
    /// <param name="settings">The settings it is part of.</param>
    /// <exception cref="ArgumentException">The default is not valid.</exception>
    public void Validate(WorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Id.Equals(Guid.Empty))
        {
            throw new ArgumentException("媒体库配置中每一项都需要媒体库的 Id。");
        }

        var name = "媒体库 " + (Name.Length > 0 ? Name : Id.ToString("N"));
        var prefix = Prefix.Trim('/');
        if (prefix.Length > 0 && !StoragePath.IsValidRelative(prefix))
        {
            throw new ArgumentException(name + " 的前缀应为相对路径，使用 /。");
        }

        if (SchemeId is { } scheme && settings.GetScheme(scheme) is null)
        {
            throw new ArgumentException(name + " 的默认方案不存在。");
        }
    }
}

/// <summary>
/// A library the server pre-encodes, as last read, and whether the worker claims its jobs.
/// </summary>
public sealed class PreEncodeSettings
{
    /// <summary>Gets or sets the library.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the name the server gave the library.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the qualities the server encodes for the library.</summary>
    public List<int> Qualities { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the worker claims the jobs of the library.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the qualities of the server the worker does not encode for the library.</summary>
    public List<int> DisabledQualities { get; set; } = [];

    /// <summary>Gets the qualities of the server the worker encodes for the library.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> EnabledQualities => [.. Qualities.Where(q => !DisabledQualities.Contains(q))];
}
