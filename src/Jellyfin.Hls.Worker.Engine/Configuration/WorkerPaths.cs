namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// Where the worker keeps its data: the database of the settings, the media and the uploads, and the temporary files.
/// </summary>
/// <param name="DataDirectory">The directory of the data.</param>
public sealed record WorkerPaths(string DataDirectory)
{
    /// <summary>Gets the paths of the user: <c>%APPDATA%\Jellyfin.HLS.Worker</c>.</summary>
    public static WorkerPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Jellyfin.HLS.Worker"));

    /// <summary>Gets the database.</summary>
    public string Database => Path.Combine(DataDirectory, "sqlite.db");

    /// <summary>Gets the settings file of former versions, imported once.</summary>
    public string LegacySettings => Path.Combine(DataDirectory, "worker.json");

    /// <summary>Gets the directory of the temporary files when <see cref="WorkerSettings.TempDirectory"/> is empty.</summary>
    public string DefaultTempDirectory => Path.Combine(DataDirectory, "work");
}
