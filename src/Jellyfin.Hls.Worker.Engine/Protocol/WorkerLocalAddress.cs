using System.Globalization;

namespace Jellyfin.Hls.Worker.Protocol;

/// <summary>
/// The target of a .strm file of an original file only a worker has: <c>worker-local://{id of the worker}/{path of the worker}</c>,
/// as the server reads it. The path is kept as it is, without encoding.
/// </summary>
public static class WorkerLocalAddress
{
    /// <summary>The scheme of the address.</summary>
    public const string Prefix = "worker-local://";

    /// <summary>
    /// Reads an address.
    /// </summary>
    /// <param name="value">The address.</param>
    /// <param name="workerId">The worker that has the file.</param>
    /// <param name="localPath">The file on the worker.</param>
    /// <returns>Whether it is such an address.</returns>
    public static bool TryParse(string? value, out Guid workerId, out string localPath)
    {
        workerId = Guid.Empty;
        localPath = string.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = value[Prefix.Length..];
        if (rest.Length < 34 || !Guid.TryParseExact(rest[..32], "N", out var id) || id.Equals(Guid.Empty) || rest[32] != '/')
        {
            return false;
        }

        var path = rest[33..];
        if (path.Any(char.IsControl))
        {
            return false;
        }

        (workerId, localPath) = (id, path);
        return true;
    }

    /// <summary>
    /// Writes an address.
    /// </summary>
    /// <param name="workerId">The worker that has the file.</param>
    /// <param name="localPath">The file on the worker, an absolute path.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentException">The worker or the path is not valid.</exception>
    public static string Format(Guid workerId, string localPath)
    {
        if (workerId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Worker 还没有 Id，不能写本机文件的地址。", nameof(workerId));
        }

        if (string.IsNullOrEmpty(localPath) || localPath.Any(char.IsControl) || !Path.IsPathFullyQualified(localPath))
        {
            throw new ArgumentException("本机文件应为绝对路径：" + localPath, nameof(localPath));
        }

        return Prefix + workerId.ToString("N", CultureInfo.InvariantCulture) + "/" + localPath;
    }
}
