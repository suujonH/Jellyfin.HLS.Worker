namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// The programs the worker runs: the ones published next to it, or the ones on the PATH.
/// </summary>
public static class ProgramPaths
{
    /// <summary>The TrueCache tool by its name, which is the one published with the worker when there is one.</summary>
    public const string DefaultTrueCacheTool = "Jellyfin.TrueCache.Tool";

    /// <summary>Gets ffmpeg.</summary>
    public static string Ffmpeg => FindProgram("ffmpeg");

    /// <summary>Gets ffprobe.</summary>
    public static string Ffprobe => FindProgram("ffprobe");

    /// <summary>Gets the TrueCache tool, which writes the entries of the media referred to by .strm files.</summary>
    public static string TrueCacheTool => TrueCacheToolOverride ?? (File.Exists(BundledTrueCacheTool) ? BundledTrueCacheTool : DefaultTrueCacheTool);

    /// <summary>Gets or sets the TrueCache tool run instead, for tests; the real one needs a server.</summary>
    internal static string? TrueCacheToolOverride { get; set; }

    /// <summary>Gets the TrueCache tool published with the worker, in the folder truecache next to it.</summary>
    internal static string BundledTrueCacheTool => Path.Combine(
        AppContext.BaseDirectory,
        "truecache",
        OperatingSystem.IsWindows() ? DefaultTrueCacheTool + ".exe" : DefaultTrueCacheTool);

    /// <summary>
    /// Gets a program next to the worker, or its name to be looked up on the PATH.
    /// </summary>
    /// <param name="name">The name, without extension.</param>
    /// <returns>The program.</returns>
    public static string FindProgram(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(bundled) ? bundled : name;
    }
}
