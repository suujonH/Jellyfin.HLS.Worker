namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// The work directory of a worker: the cached inputs and a directory per job. It is locked, so two workers never share it.
/// </summary>
public sealed class WorkArea : IDisposable
{
    private const string OwnerFile = ".worker-owned";

    private readonly FileStream _lock;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkArea"/> class, and removes the job directories a former run left behind.
    /// </summary>
    /// <param name="root">The work directory.</param>
    /// <param name="keepFailedJobs">Whether the directories of failed jobs are kept, for a look at them.</param>
    /// <exception cref="IOException">Another worker uses the directory.</exception>
    public WorkArea(string root, bool keepFailedJobs)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        try
        {
            _lock = new FileStream(Path.Combine(root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("WorkDirectory 正被另一个 Worker 使用：" + root, ex);
        }

        Jobs = Path.Combine(root, "jobs");
        Inputs = Path.Combine(root, "inputs");
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Inputs);
        if (!keepFailedJobs)
        {
            foreach (var directory in Directory.EnumerateDirectories(Jobs))
            {
                if (IsOwned(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }

    /// <summary>Gets the directory of the cached inputs.</summary>
    public string Inputs { get; }

    /// <summary>Gets the directory of the jobs.</summary>
    public string Jobs { get; }

    /// <summary>
    /// Creates the directory of a job.
    /// </summary>
    /// <returns>The directory.</returns>
    public string CreateJob()
    {
        var path = Path.Combine(Jobs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, OwnerFile), "jellyfin-hls-worker");
        return path;
    }

    /// <summary>
    /// Removes the directory of a job; only one this class created.
    /// </summary>
    /// <param name="path">The directory.</param>
    public void RemoveJob(string path)
    {
        var resolved = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(resolved), Jobs, comparison) || !IsOwned(resolved))
        {
            throw new ArgumentException("只能删除 Worker 自己创建的任务目录：" + path, nameof(path));
        }

        Directory.Delete(resolved, true);
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static bool IsOwned(string directory)
        => Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && File.Exists(Path.Combine(directory, OwnerFile));
}
