using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// A directory a file is uploaded to.
/// </summary>
/// <param name="Connection">The connection; <c>null</c> for the folder of the library, written through the server.</param>
/// <param name="Path">The directory in the connection, with the prefix of the library, with forward slashes; may be empty.</param>
/// <param name="Label">How the directory is shown in the log.</param>
public sealed record PlanTarget(ConnectionSettings? Connection, string Path, string Label);

/// <summary>How an original file is declared to the server.</summary>
public enum DeclarationKind
{
    /// <summary>An object of a bucket, by a .strm file.</summary>
    S3 = 0,

    /// <summary>A path of the server, which is the file of the library itself.</summary>
    ServerPath = 1,

    /// <summary>A file of this machine, by a .strm file.</summary>
    WorkerLocal = 2,

    /// <summary>The file put into the folder of the library.</summary>
    JellyfinLibrary = 3
}

/// <summary>
/// Where an original file is declared.
/// </summary>
/// <param name="Kind">How.</param>
/// <param name="Connection">The connection; <c>null</c> for the folder of the library.</param>
/// <param name="Path">The directory in the connection, with the prefix of the library, with forward slashes; may be empty.</param>
public sealed record PlanDeclaration(DeclarationKind Kind, ConnectionSettings? Connection, string Path);

/// <summary>
/// Where the original files added to a library go.
/// </summary>
/// <param name="Uploads">The directories they are uploaded to, in order.</param>
/// <param name="Declaration">Where they are declared; <c>null</c> for the file of this machine it was added from.</param>
public sealed record SourcePlan(IReadOnlyList<PlanTarget> Uploads, PlanDeclaration? Declaration);

/// <summary>
/// Where the renditions of a library go.
/// </summary>
/// <param name="Uploads">The directories they are uploaded to, in order.</param>
/// <param name="Base">The root the server is told, as it names it; <c>null</c> to keep them on this machine as well and declare them there.</param>
/// <param name="DeclaredS3">The S3 connection of the root, for the access of the server; <c>null</c> for a path.</param>
/// <param name="LocalPath">The directory below <see cref="WorkerSettings.LocalHlsDirectory"/> when <paramref name="Base"/> is <c>null</c>, with forward slashes; may be empty.</param>
public sealed record HlsPlan(IReadOnlyList<PlanTarget> Uploads, string? Base, ConnectionSettings? DeclaredS3, string LocalPath = "");

/// <summary>
/// Turns the scheme chosen for a library into the directories its files go to.
/// </summary>
public static class SchemePlanner
{
    /// <summary>The label of the folder of the library.</summary>
    public const string JellyfinLibraryLabel = "Jellyfin 媒体库";

    /// <summary>
    /// Gets where the original files added to a library go.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="scheme">The scheme; <c>null</c> to upload nowhere.</param>
    /// <param name="library">The defaults of the library.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException">The scheme puts the files into the library without declaring them there.</exception>
    public static SourcePlan ResolveSource(WorkerSettings settings, SchemeSettings? scheme, LibrarySettings library)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        if (scheme is null)
        {
            return new SourcePlan([], null);
        }

        var prefix = library.PrefixSources ? library.Prefix.Trim('/') : string.Empty;
        var uploads = scheme.SourceEntries.Where(e => !SchemeSettings.IsServer(settings, e)).Select(e => Target(settings, e.ConnectionId!.Value, prefix)).ToList();
        PlanDeclaration? declaration = null;
        if (scheme.SourceEntries.FirstOrDefault(e => e.Declare) is { ConnectionId: { } declared })
        {
            if (declared.Equals(SchemeEntry.JellyfinLibrary))
            {
                declaration = new PlanDeclaration(DeclarationKind.JellyfinLibrary, null, prefix);
            }
            else
            {
                var connection = settings.GetConnection(declared);
                declaration = new PlanDeclaration(
                    connection.Type switch
                    {
                        ConnectionType.S3 => DeclarationKind.S3,
                        ConnectionType.Worker => DeclarationKind.WorkerLocal,
                        _ => DeclarationKind.ServerPath
                    },
                    connection,
                    prefix);
            }
        }

        // The library would have the original file and its .strm both.
        if (declaration?.Kind is not DeclarationKind.JellyfinLibrary && uploads.Any(u => u.Connection is null))
        {
            throw new ArgumentException("方案 " + scheme.Name + " 把原片放进 Jellyfin 媒体库时，必须申告 Jellyfin 媒体库。");
        }

        return new SourcePlan(uploads, declaration);
    }

    /// <summary>
    /// Gets where the renditions of a library go.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="scheme">The scheme; <c>null</c> to upload nowhere.</param>
    /// <param name="library">The defaults of the library.</param>
    /// <returns>The plan; <c>null</c> when the renditions are uploaded nowhere. When no directory is declared, they are declared on this machine.</returns>
    /// <exception cref="ArgumentException">A directory of the server is declared that nothing uploads to.</exception>
    public static HlsPlan? ResolveHls(WorkerSettings settings, SchemeSettings? scheme, LibrarySettings library)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        if (scheme is null || !scheme.HlsEntries.Any())
        {
            return null;
        }

        var prefix = library.PrefixHls ? library.Prefix.Trim('/') : string.Empty;
        var uploads = scheme.HlsEntries.Where(e => !SchemeSettings.IsServer(settings, e)).Select(e => Target(settings, e.ConnectionId!.Value, prefix)).ToList();
        if (scheme.HlsEntries.FirstOrDefault(e => e.Declare) is not { ConnectionId: { } declared })
        {
            return new HlsPlan(uploads, null, null, prefix);
        }

        var connection = settings.GetConnection(declared);
        if (connection.Type == ConnectionType.Server && uploads.Count == 0)
        {
            throw new ArgumentException("HLS 方案 " + scheme.Name + " 只申告了服务器目录，没有上传 HLS 的目录。");
        }

        return connection.Type == ConnectionType.S3
            ? new HlsPlan(uploads, connection.GetS3Address(prefix), connection)
            : new HlsPlan(uploads, connection.GetServerPath(prefix), null);
    }

    /// <summary>
    /// Joins paths with forward slashes, leaving out the empty ones.
    /// </summary>
    /// <param name="parts">The paths.</param>
    /// <returns>The joined path.</returns>
    public static string Join(params string[] parts)
        => string.Join('/', parts.Select(p => p.Trim('/')).Where(p => p.Length > 0));

    private static PlanTarget Target(WorkerSettings settings, Guid id, string path)
    {
        if (id.Equals(SchemeEntry.JellyfinLibrary))
        {
            return new PlanTarget(null, path, path.Length == 0 ? JellyfinLibraryLabel : JellyfinLibraryLabel + ":" + path);
        }

        var connection = settings.GetConnection(id);
        return new PlanTarget(connection, path, path.Length == 0 ? connection.Name : connection.Name + ":" + path);
    }
}
