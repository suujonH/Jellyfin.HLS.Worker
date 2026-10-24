namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// The input of a job is a file of a worker that this worker does not have: the job is given back, and the item is not claimed
/// again in this run.
/// </summary>
public sealed class InputUnreachableException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InputUnreachableException"/> class.
    /// </summary>
    /// <param name="itemId">The item of the job.</param>
    /// <param name="localPath">The file it names.</param>
    public InputUnreachableException(Guid itemId, string localPath)
        : base("本机找不到源文件：" + localPath)
    {
        ItemId = itemId;
        LocalPath = localPath;
    }

    /// <summary>Gets the item of the job.</summary>
    public Guid ItemId { get; }

    /// <summary>Gets the file it names.</summary>
    public string LocalPath { get; }
}
