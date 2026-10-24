using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// The folders of a library as the server names them.
/// </summary>
public static class LibraryFolder
{
    /// <summary>
    /// Gets the path below a root of the server, with forward slashes. Windows paths are compared without case.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="path">The path.</param>
    /// <param name="relative">The path below the root; empty for the root itself.</param>
    /// <returns>Whether the path is the root or below it.</returns>
    internal static bool TryGetRelative(string root, string path, out string relative)
    {
        relative = string.Empty;
        if (!StoragePath.IsAbsoluteServerPath(root) || !StoragePath.IsAbsoluteServerPath(path))
        {
            return false;
        }

        var windows = !root.StartsWith('/');
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Normalize(root, windows);
        var normalizedPath = Normalize(path, windows);
        if (string.Equals(normalizedRoot, normalizedPath, comparison))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith('/') ? normalizedRoot : normalizedRoot + "/";
        if (!normalizedPath.StartsWith(prefix, comparison))
        {
            return false;
        }

        relative = normalizedPath[prefix.Length..];
        return StoragePath.IsValidRelative(relative);
    }

    // Forward slashes, without a trailing one except for a root such as / or D:/.
    private static string Normalize(string path, bool windows)
    {
        var normalized = windows ? path.Replace('\\', '/') : path;
        var trimmed = normalized.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "/" : trimmed;
    }
}
