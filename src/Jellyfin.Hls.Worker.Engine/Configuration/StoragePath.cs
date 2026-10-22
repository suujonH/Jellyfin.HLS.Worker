namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// The rules of the server for the paths of an HLS store and the names of buckets.
/// </summary>
public static class StoragePath
{
    /// <summary>
    /// Gets whether a path is relative, with forward slashes, without empty parts, parts that end with a dot or a space,
    /// colons or control characters: the rule of the server for a path in an HLS store.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValidRelative(string? path)
        => !string.IsNullOrEmpty(path)
            && !path.Contains('\\', StringComparison.Ordinal)
            && !path.Contains(':', StringComparison.Ordinal)
            && !path.Any(char.IsControl)
            && path.Split('/').All(part => part.Length > 0 && part[^1] is not ('.' or ' '));

    /// <summary>
    /// Gets whether a bucket name can be written in an address the server reads: letters, digits, dots, dashes and underscores.
    /// </summary>
    /// <param name="bucket">The name.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValidBucket(string? bucket)
        => !string.IsNullOrEmpty(bucket) && bucket.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>
    /// Gets whether a path is absolute on the server, in POSIX (<c>/srv/hls</c>), Windows (<c>D:\HLS</c>) or UNC style.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is absolute.</returns>
    public static bool IsAbsoluteServerPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.StartsWith('/')
                || path.StartsWith(@"\\", StringComparison.Ordinal)
                || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'));

    /// <summary>
    /// Appends a relative path with forward slashes to a path of the server, in the style of that path.
    /// </summary>
    /// <param name="root">The path of the server.</param>
    /// <param name="relative">The relative path; may be empty.</param>
    /// <returns>The joined path.</returns>
    public static string JoinServer(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        var separator = root.StartsWith('/') ? '/' : '\\';
        var trimmed = root.Length > 1 ? root.TrimEnd('/', '\\') : root;

        // A drive root such as D:\ keeps its separator.
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            trimmed += separator;
        }

        if (relative.Length == 0)
        {
            return trimmed;
        }

        var tail = separator == '/' ? relative : relative.Replace('/', '\\');
        return trimmed[^1] is '/' or '\\' ? trimmed + tail : trimmed + separator + tail;
    }
}
