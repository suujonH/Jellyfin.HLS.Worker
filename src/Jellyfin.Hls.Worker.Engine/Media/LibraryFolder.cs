using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// A folder of a library, reached through an SFTP or local connection.
/// </summary>
/// <param name="Connection">The connection.</param>
/// <param name="ServerPath">The folder as the server knows it.</param>
/// <param name="Path">The folder in the connection, with forward slashes; empty for its root.</param>
public sealed record LibraryFolder(StorageConnection Connection, string ServerPath, string Path)
{
    /// <summary>
    /// Finds the first folder of a library that a connection reaches: one whose <see cref="ConnectionSettings.ServerRoot"/>
    /// (for a local connection without it, its <see cref="ConnectionSettings.LocalRoot"/>) is the folder or above it.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="connections">The connections.</param>
    /// <returns>The folder, or <c>null</c>.</returns>
    public static LibraryFolder? Find(VirtualFolder library, IEnumerable<StorageConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(library);
        var candidates = connections.Where(c => c.Settings.Type is ConnectionType.Sftp or ConnectionType.Local).ToList();
        foreach (var location in library.Locations)
        {
            foreach (var connection in candidates)
            {
                var root = connection.Settings.ServerRoot.Length > 0 ? connection.Settings.ServerRoot : connection.Settings.LocalRoot;
                if (TryGetRelative(root, location, out var path))
                {
                    return new LibraryFolder(connection, location, path);
                }
            }
        }

        return null;
    }

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
