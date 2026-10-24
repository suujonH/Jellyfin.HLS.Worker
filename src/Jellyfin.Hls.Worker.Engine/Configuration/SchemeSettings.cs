using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// A line of a scheme: a directory a file is uploaded to, and whether the server is told the file is there.
/// </summary>
public sealed class SchemeEntry
{
    /// <summary>The connection that stands for the folder of the library, which the worker writes through the server.</summary>
    public static readonly Guid JellyfinLibrary = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Gets or sets the directory: a connection, or <see cref="JellyfinLibrary"/>; <c>null</c> while none is chosen.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Gets or sets a value indicating whether the server is told the file is in this directory.</summary>
    public bool Declare { get; set; }
}

/// <summary>
/// Where the files of a library are saved: the renditions to every HLS directory, the original files to every source directory.
/// At most one line of each list is declared to the server; with none, the server is told the files of this machine.
/// A directory of the server is not uploaded to: it is only declared, the files reaching it through another line.
/// </summary>
public sealed class SchemeSettings
{
    /// <summary>Gets or sets the id the libraries refer to the scheme by.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the directories of the renditions.</summary>
    public List<SchemeEntry> Hls { get; set; } = [];

    /// <summary>Gets or sets the directories of the original files.</summary>
    public List<SchemeEntry> Sources { get; set; } = [];

    /// <summary>Gets the lines of <see cref="Hls"/> that count: those with a directory.</summary>
    [JsonIgnore]
    public IEnumerable<SchemeEntry> HlsEntries => Hls.Where(e => e.ConnectionId is not null);

    /// <summary>Gets the lines of <see cref="Sources"/> that count: those with a directory.</summary>
    [JsonIgnore]
    public IEnumerable<SchemeEntry> SourceEntries => Sources.Where(e => e.ConnectionId is not null);

    /// <summary>
    /// Checks the scheme.
    /// </summary>
    /// <param name="settings">The settings it is part of.</param>
    /// <exception cref="ArgumentException">The scheme is not valid.</exception>
    public void Validate(WorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
        {
            throw new ArgumentException("方案的名称长度应为 1–64。");
        }

        var name = "方案 " + Name + "：";
        if ((GetEntryError(settings, Hls, true) ?? GetEntryError(settings, Sources, false)) is { } error)
        {
            throw new ArgumentException(name + error);
        }

        if (Sources.Count(e => e.ConnectionId.Equals(SchemeEntry.JellyfinLibrary)) > 1)
        {
            throw new ArgumentException(name + "只能有一行「Jellyfin 媒体库」。");
        }

        if (Hls.Count(e => e.Declare) > 1)
        {
            throw new ArgumentException(name + "HLS 只能申告一个目录。");
        }

        if (Sources.Count(e => e.Declare) > 1)
        {
            throw new ArgumentException(name + "源文件只能申告一个目录。");
        }

        if (GetHlsError(settings) is { } hls)
        {
            throw new ArgumentException(name + hls);
        }
    }

    /// <summary>
    /// Gets what is wrong with a line alone.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="entry">The line.</param>
    /// <param name="hls">Whether the line is one of <see cref="Hls"/>.</param>
    /// <returns>The error; <c>null</c> for none.</returns>
    public static string? GetError(WorkerSettings settings, SchemeEntry entry, bool hls)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ConnectionId is not { } id)
        {
            return "有一行没有选择目录。";
        }

        if (id.Equals(SchemeEntry.JellyfinLibrary))
        {
            return hls ? "HLS 不能保存到 Jellyfin 媒体库。" : entry.Declare ? null : "「Jellyfin 媒体库」行必须申告。";
        }

        var connection = settings.Connections.Find(c => c.Id.Equals(id));
        if (connection is null)
        {
            return "引用了不存在的远程目录。";
        }

        return connection.Type == ConnectionType.Server && !entry.Declare ? "服务器目录不能上传，只能用于申告，请勾选「申告此目录」。" : null;
    }

    /// <summary>
    /// Gets what is wrong with the HLS lines together: a declared directory of the server needs another line the renditions reach it by.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The error; <c>null</c> for none.</returns>
    public string? GetHlsError(WorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Hls.Find(e => e.Declare) is { } declared
            && IsServer(settings, declared)
            && !HlsEntries.Any(e => !IsServer(settings, e))
            ? "HLS 申告服务器目录时，还需要一个上传 HLS 的目录。"
            : null;
    }

    /// <summary>
    /// Gets whether a line is a directory of the server, which is only declared.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="entry">The line.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsServer(WorkerSettings settings, SchemeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ConnectionId is { } id && settings.Connections.Find(c => c.Id.Equals(id))?.Type == ConnectionType.Server;
    }

    private static string? GetEntryError(WorkerSettings settings, List<SchemeEntry> entries, bool hls)
        => entries.Select(e => GetError(settings, e, hls)).FirstOrDefault(e => e is not null);
}
