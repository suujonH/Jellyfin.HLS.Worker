using System.Security.Cryptography;
using System.Text;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// What to add to a library.
/// </summary>
/// <param name="LibraryId">The library.</param>
/// <param name="Paths">Files and folders of this machine; a folder is added with its name and everything in it.</param>
/// <param name="Into">The folder below the folder of the library to add them to, with forward slashes; empty for the folder itself.</param>
/// <param name="Connection">The S3 connection of the media, empty for the folder of the library, <c>null</c> for the default of the library.</param>
/// <param name="Qualities">The qualities to encode at once; the others are encoded once the server scanned the media.</param>
/// <param name="Prefix">The folder of the bucket to put media on S3 in, <c>null</c> for the default of the library.</param>
public sealed record AddRequest(
    Guid LibraryId,
    IReadOnlyList<string> Paths,
    string Into = "",
    string? Connection = null,
    IReadOnlyList<int>? Qualities = null,
    string? Prefix = null);

/// <summary>
/// What an addition did.
/// </summary>
/// <param name="Added">The files added.</param>
/// <param name="Failed">The files that failed.</param>
public sealed record AddResult(int Added, int Failed);

/// <summary>
/// Adds files of this machine to a library. A video put on S3 becomes an object, referred to by a .strm file in the folder of the
/// library with its TrueCache entry next to it; anything else, and a video put directly into the library, is copied into the folder
/// of the library. The library is scanned once the batch is done.
/// </summary>
public sealed class MediaAdder
{
    /// <summary>The extensions of the files added as media; the other files are copied as they are.</summary>
    public static readonly IReadOnlyList<string> VideoExtensions = [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".flv", ".mpg", ".mpeg"];

    private const string TrueCacheDirectory = "__true_cache__";

    private readonly WorkerSettings _settings;
    private readonly ServerClient _server;
    private readonly IReadOnlyDictionary<string, StorageConnection> _connections;
    private readonly HlsTarget _target;
    private readonly JobRunner _runner;
    private readonly MediaDatabase _database;
    private readonly WorkArea _area;
    private readonly SemaphoreSlim _encoding;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAdder"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="server">The server.</param>
    /// <param name="connections">The connections by name.</param>
    /// <param name="target">The root of the HLS store.</param>
    /// <param name="runner">Runs the jobs of what is encoded at once.</param>
    /// <param name="database">The local copies.</param>
    /// <param name="area">The work directory.</param>
    /// <param name="encoding">The encode slots.</param>
    /// <param name="logger">The logger.</param>
    public MediaAdder(
        WorkerSettings settings,
        ServerClient server,
        IReadOnlyDictionary<string, StorageConnection> connections,
        HlsTarget target,
        JobRunner runner,
        MediaDatabase database,
        WorkArea area,
        SemaphoreSlim encoding,
        ILogger logger)
    {
        _settings = settings;
        _server = server;
        _connections = connections;
        _target = target;
        _runner = runner;
        _database = database;
        _area = area;
        _encoding = encoding;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a file is added as media.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether it is a video.</returns>
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the files of a request with the paths they get below the folder of the library, in the order they are added: the other
    /// files first, so the subtitles and images of a video are there when the server finds it. Hidden files and TrueCache entries
    /// are left out; the entries of the media put on S3 are written anew.
    /// </summary>
    /// <param name="paths">The files and folders.</param>
    /// <param name="into">The folder below the folder of the library.</param>
    /// <returns>The files.</returns>
    /// <exception cref="ArgumentException">A path does not exist, or a name is not one the server accepts.</exception>
    public static IReadOnlyList<(string LocalPath, string Path)> Expand(IEnumerable<string> paths, string into)
    {
        ArgumentNullException.ThrowIfNull(paths);
        into = (into ?? string.Empty).Trim('/');
        if (into.Length > 0 && !StoragePath.IsValidRelative(into))
        {
            throw new ArgumentException("目标目录应为相对路径，使用 /：" + into);
        }

        var files = new List<(string LocalPath, string Path)>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                files.Add((full, Join(into, Path.GetFileName(full))));
            }
            else if (Directory.Exists(full))
            {
                var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) ?? full;
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Split('/').Any(part => part.StartsWith('.') || part == TrueCacheDirectory))
                    {
                        continue;
                    }

                    files.Add((file, Join(into, relative)));
                }
            }
            else
            {
                throw new ArgumentException("路径不存在：" + path);
            }
        }

        foreach (var (_, relative) in files)
        {
            if (!StoragePath.IsValidRelative(relative))
            {
                throw new ArgumentException("服务器不接受这个文件名（不能以点或空格结尾，不能含 : 或控制字符）：" + relative);
            }
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("没有可添加的文件。");
        }

        return [.. files.Where(f => !IsVideo(f.LocalPath)), .. files.Where(f => IsVideo(f.LocalPath))];
    }

    /// <summary>
    /// Adds files to a library: copies the other files, puts each video where the library keeps its media, records it, encodes the
    /// qualities asked for at once, and asks the server to scan the library. A file that fails does not stop the others.
    /// </summary>
    /// <param name="request">What to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentException">The request cannot be carried out.</exception>
    public async Task<AddResult> AddAsync(AddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = Expand(request.Paths, request.Into);
        var library = (await _server.GetVirtualFoldersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id.Equals(request.LibraryId))
            ?? throw new ArgumentException("服务器上没有媒体库 " + request.LibraryId + "。");
        var folder = LibraryFolder.Find(library, _connections.Values)
            ?? throw new ArgumentException("媒体库 " + library.Name + " 的目录（" + string.Join("、", library.Locations) + "）没有对应的 SFTP 或本地连接；请检查连接的 ServerRoot。");
        var qualities = await GetQualitiesAsync(request, library, cancellationToken).ConfigureAwait(false);
        var defaults = _settings.GetLibrary(request.LibraryId);
        var name = request.Connection ?? defaults.Connection;
        var prefix = (request.Prefix ?? defaults.Prefix).Trim('/');
        if (prefix.Length > 0 && !StoragePath.IsValidRelative(prefix))
        {
            throw new ArgumentException("S3 前缀应为相对路径，使用 /：" + prefix);
        }
        StorageConnection? s3 = null;
        if (name.Length > 0)
        {
            s3 = _connections.TryGetValue(name, out var connection) && connection.Settings.Type == ConnectionType.S3
                ? connection
                : throw new ArgumentException("媒体的连接应为 S3 连接：" + name);
            await EnsureAccessAsync(_server, s3.Settings, cancellationToken).ConfigureAwait(false);
        }

        // Two files of the same name from different folders would be written to one; on S3 a video is in the library as its
        // .strm, which Film.mkv and Film.mp4 would share.
        var shared = files.Select(f => s3 is not null && IsVideo(f.LocalPath) ? GetShortcut(f.Path) : f.Path)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (shared is not null)
        {
            throw new ArgumentException("多个文件会写成媒体库中的同一个 " + shared.Key + (s3 is null ? "。" : "；S3 媒体以去掉扩展名的 .strm 进入媒体库。"));
        }

        _logger.LogInformation(
            "添加 {Count} 个文件到媒体库 {Library}（{Folder}），媒体放在 {Where}",
            files.Count,
            library.Name,
            folder.ServerPath,
            s3 is null ? "媒体库目录" : "s3://" + s3.Settings.Bucket);
        int added = 0, failed = 0;
        foreach (var (localPath, path) in files)
        {
            try
            {
                await AddFileAsync(folder, s3, prefix, localPath, path, request.LibraryId, qualities, cancellationToken).ConfigureAwait(false);
                added++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("添加 {Path} 失败：{Message}", localPath, ex.Message);
                failed++;
            }
        }

        if (added > 0)
        {
            try
            {
                await _server.RefreshAsync(request.LibraryId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("已请求服务器扫描媒体库 {Library}", library.Name);
            }
            catch (Exception ex) when (ex is ServerException or HttpRequestException)
            {
                _logger.LogWarning("请求扫描媒体库 {Library} 失败，请在服务器上手动扫描：{Message}", library.Name, ex.Message);
            }
        }

        _logger.LogInformation("添加结束：成功 {Added}，失败 {Failed}", added, failed);
        return new AddResult(added, failed);
    }

    /// <summary>
    /// Makes sure the server has an access to a bucket, so it can read what the worker put there; one is created with the
    /// credentials of the connection when there is none. An access of the bucket that exists is left as it is.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="connection">The S3 connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether an access was created.</returns>
    internal static async Task<bool> EnsureAccessAsync(ServerClient server, ConnectionSettings connection, CancellationToken cancellationToken)
    {
        if (await HasAccessAsync(server, connection, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await server.AddAccessAsync(
                new NetworkStorageAccess(
                    Guid.Empty,
                    "s3",
                    "HLS Worker: " + connection.Name,
                    connection.Endpoint,
                    connection.Bucket,
                    connection.Region,
                    UsePathStyle: true,
                    connection.AccessKey,
                    connection.SecretKey,
                    IsArchive: false),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ServerException ex) when (ex.StatusCode is { } status && (int)status < 500)
        {
            // Another worker may have created it in the meantime.
            if (await HasAccessAsync(server, connection, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }
    }

    private static async Task<bool> HasAccessAsync(ServerClient server, ConnectionSettings connection, CancellationToken cancellationToken)
        => (await server.GetAccessesAsync(cancellationToken).ConfigureAwait(false))
            .Any(a => string.Equals(a.Protocol, "s3", StringComparison.OrdinalIgnoreCase) && string.Equals(a.Bucket, connection.Bucket, StringComparison.Ordinal));

    private static string Join(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "/" + second;

    // A file added from where a local connection keeps it is not copied onto itself.
    private static bool IsInPlace(LibraryFolder folder, string inFolder, string localPath)
    {
        var settings = folder.Connection.Settings;
        if (settings.Type != ConnectionType.Local)
        {
            return false;
        }

        var destination = Path.GetFullPath(Path.Combine(settings.LocalRoot, inFolder.Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(destination, Path.GetFullPath(localPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string GetShortcut(string path)
    {
        var (directory, name) = Split(path);
        return Join(directory, Path.GetFileNameWithoutExtension(name) + ".strm");
    }

    private static (string Directory, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? (string.Empty, path) : (path[..slash], path[(slash + 1)..]);
    }

    // The qualities asked for must be pre-encoded for the library; none is no error.
    private async Task<IReadOnlyList<int>> GetQualitiesAsync(AddRequest request, VirtualFolder library, CancellationToken cancellationToken)
    {
        if (request.Qualities is not { Count: > 0 } qualities)
        {
            return [];
        }

        var enabled = (await _server.GetLibrariesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(l => l.Id.Equals(request.LibraryId))
            ?? throw new ArgumentException("媒体库 " + library.Name + " 没有启用预编码，不能随添加编码。");
        var unknown = qualities.Where(q => !enabled.Qualities.Contains(q)).ToList();
        return unknown.Count == 0
            ? qualities.Distinct().ToList()
            : throw new ArgumentException("媒体库 " + library.Name + " 的档位为 " + string.Join("、", enabled.Qualities) + "，不含 " + string.Join("、", unknown) + "。");
    }

    // The library is written through a session opened when it is written to: one left open while a video is hashed, put on S3
    // or encoded may be closed by then.
    private async Task AddFileAsync(
        LibraryFolder folder,
        StorageConnection? s3,
        string prefix,
        string localPath,
        string path,
        Guid libraryId,
        IReadOnlyList<int> qualities,
        CancellationToken cancellationToken)
    {
        var inFolder = Join(folder.Path, path);
        var inPlace = IsInPlace(folder, inFolder, localPath);
        if (!IsVideo(localPath))
        {
            if (!inPlace)
            {
                using var writer = await folder.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await writer.PutAsync(inFolder, localPath, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("已复制 {Path}", path);
            return;
        }

        var original = new FileInfo(localPath);
        var (size, ticks) = (original.Length, original.LastWriteTimeUtc.Ticks);
        var sha256 = await HashAsync(localPath, cancellationToken).ConfigureAwait(false);
        string target;
        if (s3 is null)
        {
            // Under a temporary name and renamed, so the server never scans half a file. The TrueCache entry of a video it
            // replaces, or one there is already, may declare another file, for which the server would hide the video; without it,
            // the video is probed again.
            var (directory, fileName) = Split(inFolder);
            using var writer = await folder.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await writer.DeleteDirectoryAsync(Join(Join(directory, TrueCacheDirectory), fileName), cancellationToken).ConfigureAwait(false);
            if (!inPlace)
            {
                await writer.PutAsync(inFolder, localPath, cancellationToken).ConfigureAwait(false);
            }

            target = StoragePath.JoinServer(folder.ServerPath, path);
        }
        else
        {
            // The entry is made before the object is put: when it cannot be, a video added before is left as it was.
            var key = Join(prefix.Trim('/'), path);
            target = "s3://" + s3.Settings.Bucket + "/" + key;
            var (directory, shortcut) = Split(GetShortcut(inFolder));
            var work = _area.CreateJob();
            try
            {
                var entry = await CreateEntryAsync(work, shortcut, localPath, cancellationToken).ConfigureAwait(false);
                using (var session = await s3.OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    await session.PutAsync(key, localPath, cancellationToken).ConfigureAwait(false);
                }

                // Should the library not be written, the object is new and the entry old; adding the video again mends it.
                using var writer = await folder.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await WriteShortcutAsync(writer, directory, shortcut, entry, work, target, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _area.RemoveJob(work);
            }
        }

        // The hash is of what was put only when the file did not change meanwhile.
        original.Refresh();
        if (original.Length != size || original.LastWriteTimeUtc.Ticks != ticks)
        {
            throw new InvalidDataException("文件在添加期间被修改，请重新添加。");
        }

        _database.Record(target, size, new LocalCopy(localPath, sha256));
        _logger.LogInformation("已添加 {Path}（{Target}）", path, target);
        if (qualities.Count > 0)
        {
            // The file is in the library either way; what is not encoded now is encoded once the server scanned it.
            try
            {
                await EncodeAsync(localPath, sha256, libraryId, qualities, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{Path} 没能随添加编码，扫描入库后由服务器安排：{Message}", path, ex.Message);
            }
        }
    }

    // The TrueCache entry of a video, made from the local file in a job directory. The .strm is named after the video without its
    // extension, so the subtitles and images named after the video belong to it.
    private async Task<string> CreateEntryAsync(string work, string shortcut, string localPath, CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync(
            _settings.TrueCacheTool,
            ["strm", "--ffmpeg", _settings.Ffmpeg, "--server-url", _settings.Server, "--api-key", _settings.ApiKey, "--name", shortcut, "--output", work, localPath],
            cancellationToken).ConfigureAwait(false);
        var entry = Path.Combine(work, shortcut);
        return Directory.Exists(entry) ? entry : throw new InvalidDataException("TrueCache 工具没有写出条目 " + shortcut + "。");
    }

    // The entry first, written under a hidden name and renamed, then the .strm file: when the server finds the .strm, its entry
    // is complete. An entry there before is kept under a hidden name until the .strm is written, and put back when it cannot be.
    private async Task WriteShortcutAsync(
        IStorageSession writer,
        string directory,
        string shortcut,
        string entry,
        string work,
        string target,
        CancellationToken cancellationToken)
    {
        var entries = Join(directory, TrueCacheDirectory);
        var temporary = Join(entries, "." + shortcut + "-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
            {
                await writer.PutAsync(Join(temporary, Path.GetRelativePath(entry, file).Replace('\\', '/')), file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await TryDeleteDirectoryAsync(writer, temporary).ConfigureAwait(false);
            throw;
        }

        var text = Path.Combine(work, "shortcut.txt");
        await File.WriteAllTextAsync(text, target + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var final = Join(entries, shortcut);
        var previous = temporary + "-old";
        var replaced = await writer.MoveDirectoryAsync(final, previous, cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.MoveDirectoryAsync(temporary, final, cancellationToken).ConfigureAwait(false);
            await writer.PutAsync(Join(directory, shortcut), text, cancellationToken).ConfigureAwait(false);
        }
        catch when (!replaced)
        {
            // The .strm there, if any, is not of the new entry.
            await TryDeleteDirectoryAsync(writer, temporary).ConfigureAwait(false);
            await TryDeleteDirectoryAsync(writer, final).ConfigureAwait(false);
            throw;
        }
        catch
        {
            // The .strm there is still the one the previous entry is of.
            await TryDeleteDirectoryAsync(writer, temporary).ConfigureAwait(false);
            await writer.DeleteDirectoryAsync(final, CancellationToken.None).ConfigureAwait(false);
            await writer.MoveDirectoryAsync(previous, final, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (replaced)
        {
            try
            {
                await writer.DeleteDirectoryAsync(previous, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("没能删除旧的 TrueCache 条目 {Path}：{Message}", previous, ex.Message);
            }
        }
    }

    private async Task TryDeleteDirectoryAsync(IStorageSession writer, string path)
    {
        try
        {
            await writer.DeleteDirectoryAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("没能删除 {Path}：{Message}", path, ex.Message);
        }
    }

    // A content the server has everything of, or that has a job already, is left to the server.
    private async Task EncodeAsync(string localPath, string sha256, Guid libraryId, IReadOnlyList<int> qualities, CancellationToken cancellationToken)
    {
        var probe = await _runner.ProbeAsync(localPath, cancellationToken).ConfigureAwait(false);

        // The slot is taken before the lease, which has no heartbeat until the encoding starts.
        await _encoding.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkLease lease;
        EncodePlan plan;
        try
        {
            var answer = await _server.StartEagerJobAsync(new EagerRequest(_settings.Name, libraryId, sha256, probe, _target.Base, qualities), cancellationToken)
                .ConfigureAwait(false);
            if (answer.Done || answer.Lease is null || answer.Plan is null)
            {
                _logger.LogInformation("{Path} 的这些档位已经存在", localPath);
                _encoding.Release();
                return;
            }

            (lease, plan) = (answer.Lease, answer.Plan);
        }
        catch (ServerException ex) when (ex.IsConflict)
        {
            _logger.LogInformation("{Path} 已有任务在处理，不随添加编码", localPath);
            _encoding.Release();
            return;
        }
        catch
        {
            _encoding.Release();
            throw;
        }

        if (!await _runner.RunEagerAsync(lease, plan, localPath, sha256, _encoding, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("编码任务失败。");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }
}
