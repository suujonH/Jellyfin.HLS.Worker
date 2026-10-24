using System.Diagnostics;
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
/// <param name="Location">The folder of the library to add to; <c>null</c> when it has only one.</param>
/// <param name="Paths">Files and folders of this machine; a folder is added with its name and everything in it.</param>
/// <param name="Source">Where the original files go.</param>
/// <param name="Hls">Where the renditions go; <c>null</c> for nowhere.</param>
/// <param name="PreEncodeQualities">The qualities encoded at once, some of those of the library; the others are encoded once the server scanned the media.</param>
public sealed record AddRequest(
    Guid LibraryId,
    string? Location,
    IReadOnlyList<string> Paths,
    SourcePlan Source,
    HlsPlan? Hls = null,
    IReadOnlyList<int>? PreEncodeQualities = null);

/// <summary>
/// What an addition did.
/// </summary>
/// <param name="Added">The files added.</param>
/// <param name="Failed">The files that failed.</param>
public sealed record AddResult(int Added, int Failed);

/// <summary>
/// Adds files of this machine to a library. A video is uploaded to every source directory of the plan, and declared where the
/// plan declares it: as the file of the library itself (put there by the server, or by a directory of the server inside the
/// library), or by a .strm file with its TrueCache entry, referring to an object of a bucket or to a file of this worker. The
/// other files, and the .strm files, are written into the folder of the library by the server. The library is scanned once the
/// batch is done.
/// </summary>
public sealed class MediaAdder
{
    /// <summary>The extensions of the files added as media; the other files are copied as they are.</summary>
    public static readonly IReadOnlyList<string> VideoExtensions = [".mkv", ".mp4", ".m4v", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".flv", ".mpg", ".mpeg"];

    private const string TrueCacheDirectory = "__true_cache__";
    private const string WorkingDirectory = "__working__";
    private const string TakenOver = "另一个任务接管了这个文件，本机已跳过。";

    private readonly WorkerSettings _settings;
    private readonly ServerClient _server;
    private readonly IReadOnlyDictionary<Guid, StorageConnection> _connections;
    private readonly JobRunner _runner;
    private readonly MediaDatabase _database;
    private readonly WorkArea _area;
    private readonly SemaphoreSlim _encoding;
    private readonly LibraryWriter _writer;
    private readonly UploadLog _uploadLog;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAdder"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="server">The server.</param>
    /// <param name="connections">The connections by id.</param>
    /// <param name="runner">Runs the jobs of what is encoded at once.</param>
    /// <param name="database">The local copies.</param>
    /// <param name="area">The work directory.</param>
    /// <param name="encoding">The encode slots.</param>
    /// <param name="writer">Writes into the folders of the libraries.</param>
    /// <param name="uploadLog">The log of the uploads.</param>
    /// <param name="logger">The logger.</param>
    public MediaAdder(
        WorkerSettings settings,
        ServerClient server,
        IReadOnlyDictionary<Guid, StorageConnection> connections,
        JobRunner runner,
        MediaDatabase database,
        WorkArea area,
        SemaphoreSlim encoding,
        LibraryWriter writer,
        UploadLog uploadLog,
        ILogger logger)
    {
        _settings = settings;
        _server = server;
        _connections = connections;
        _runner = runner;
        _database = database;
        _area = area;
        _encoding = encoding;
        _writer = writer;
        _uploadLog = uploadLog;
        _logger = logger;
    }

    /// <summary>Gets or sets how often an addition checks it still holds its file; tests set it shorter.</summary>
    public static TimeSpan WorkingPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets whether a file is added as media.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether it is a video.</returns>
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the files of a request with the paths they get below the folder of the library, in the order they are added: the other
    /// files first, so the subtitles and images of a video are there when the server finds it. Hidden files, TrueCache entries
    /// and working marks are left out; the entries of the media referred to by .strm files are written anew.
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
                    if (relative.Split('/').Any(part => part.StartsWith('.') || part == TrueCacheDirectory || part == WorkingDirectory))
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
    /// Adds files to a library: writes the other files into its folder, uploads and declares each video, records it, encodes it
    /// at once when asked to, and asks the server to scan the library. A file that fails does not stop the others.
    /// </summary>
    /// <param name="request">What to add.</param>
    /// <param name="destination">Where the renditions go, checked; <c>null</c> to encode nothing at once.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Told the files, and the steps of each as they go, or <c>null</c>.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentException">The request cannot be carried out.</exception>
    public async Task<AddResult> AddAsync(AddRequest request, HlsDestination? destination, CancellationToken cancellationToken, IProgress<AddEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = Expand(request.Paths, string.Empty);
        var library = (await _server.GetVirtualFoldersAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(f => Guid.TryParse(f.ItemId, out var id) && id.Equals(request.LibraryId))
            ?? throw new ArgumentException("服务器上没有媒体库 " + request.LibraryId + "。");
        var location = GetLocation(library, request.Location);
        var placement = Placement.Create(request.Source.Declaration, location);
        foreach (var upload in request.Source.Uploads.Where(u => u.Connection is not null && !_connections.ContainsKey(u.Connection.Id)))
        {
            throw new ArgumentException("原片不能上传到 " + upload.Label + "。");
        }

        if (request.Source.Declaration is { Kind: DeclarationKind.S3, Connection: { } bucket })
        {
            await EnsureAccessAsync(_server, bucket, cancellationToken).ConfigureAwait(false);
        }

        var qualities = destination is null ? [] : await GetQualitiesAsync(request.LibraryId, library.Name, request.PreEncodeQualities ?? [], cancellationToken).ConfigureAwait(false);

        // Two files of the same name from different folders would be written to one; a video referred to by a .strm file is in
        // the library as the .strm, which Film.mkv and Film.mp4 would share.
        var shared = files.Select(f => placement.Shortcut && IsVideo(f.LocalPath) ? GetShortcut(placement.Map(f.Path)) : placement.Map(f.Path))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (shared is not null)
        {
            throw new ArgumentException("多个文件会写成媒体库中的同一个 " + shared.Key + (placement.Shortcut ? "；以 .strm 申告的视频以去掉扩展名的 .strm 进入媒体库。" : "。"));
        }

        _logger.LogInformation(
            "添加 {Count} 个文件到媒体库 {Library}（{Location}），原片保存到 {Targets}，申告为 {Declaration}",
            files.Count,
            library.Name,
            location,
            request.Source.Uploads.Count == 0 ? "（不保存）" : string.Join("、", request.Source.Uploads.Select(u => u.Label)),
            Describe(request.Source.Declaration));
        progress?.Report(new AddFilesListed([.. files.Select(f => f.Path)]));

        var context = new AddContext(request.LibraryId, location, request.Source, placement, destination, qualities);
        int added = 0, failed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var (localPath, path) = files[i];
            var file = new FileProgress(progress, i);
            try
            {
                var note = await AddFileAsync(context, localPath, path, file, cancellationToken).ConfigureAwait(false);
                added++;
                file.End(true, note);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("添加 {Path} 失败：{Message}", localPath, ex.Message);
                failed++;
                file.End(false, ex.Message);
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

    // The folder the server names, as it names it: the only one, or the one asked for.
    private static string GetLocation(VirtualFolder library, string? location)
    {
        if (library.Locations.Count == 0)
        {
            throw new ArgumentException("媒体库 " + library.Name + " 没有目录。");
        }

        if (string.IsNullOrEmpty(location))
        {
            return library.Locations.Count == 1
                ? library.Locations[0]
                : throw new ArgumentException("媒体库 " + library.Name + " 有多个目录，需要指定写入哪一个。");
        }

        return library.Locations.FirstOrDefault(l => LibraryFolder.TryGetRelative(l, location, out var relative) && relative.Length == 0)
            ?? throw new ArgumentException(location + " 不是媒体库 " + library.Name + " 的目录。");
    }

    private static string Describe(PlanDeclaration? declaration) => declaration switch
    {
        null => "本机文件（worker-local）",
        { Kind: DeclarationKind.JellyfinLibrary } => "媒体库中的文件",
        { Kind: DeclarationKind.S3 } => declaration.Connection!.GetS3Address(declaration.Path),
        _ => declaration.Connection!.GetServerPath(declaration.Path)
    };

    // A file added from where a directory of this worker keeps it is not copied onto itself.
    private static bool IsInPlace(ConnectionSettings connection, string path, string localPath)
    {
        if (connection.Type != ConnectionType.Worker)
        {
            return false;
        }

        var destination = Path.GetFullPath(Path.Combine(connection.LocalRoot, path.Replace('/', Path.DirectorySeparatorChar)));
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

    private static string InLibrary(string path) => SchemePlanner.JellyfinLibraryLabel + "/" + path;

    // The qualities chosen that the server still pre-encodes the library in.
    private async Task<IReadOnlyList<int>> GetQualitiesAsync(Guid libraryId, string name, IReadOnlyCollection<int> wanted, CancellationToken cancellationToken)
    {
        var library = (await _server.GetLibrariesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(l => l.Id.Equals(libraryId));
        if (library is not { Qualities.Count: > 0 })
        {
            _logger.LogWarning("媒体库 {Library} 没有启用预编码，不随添加编码", name);
            return [];
        }

        var chosen = library.Qualities.Where(wanted.Contains).ToList();
        if (chosen.Count == 0)
        {
            _logger.LogWarning("媒体库 {Library} 所选的档位服务器已不再预编码，不随添加编码", name);
        }

        return chosen;
    }

    // Returns the note of a file added: why it was not encoded at once, or null.
    private Task<string?> AddFileAsync(AddContext context, string localPath, string path, FileProgress file, CancellationToken cancellationToken)
        => IsVideo(localPath) ? AddVideoAsync(context, localPath, path, file, cancellationToken) : AddOtherAsync(context, localPath, path, file, cancellationToken);

    // A file that is no video is written at once, as one upload: it holds nothing and is not resumed.
    private async Task<string?> AddOtherAsync(AddContext context, string localPath, string path, FileProgress file, CancellationToken cancellationToken)
    {
        var placement = context.Placement;
        var id = JobRunner.NewOperationId();
        TryLog(() => _uploadLog.StartOperation(NewOperation(id, context, placement.Map(path), localPath, new FileInfo(localPath).Length, null)));
        try
        {
            file.Step(AddStep.Library);
            await TrackAsync(
                UploadKind.LibraryFile,
                context,
                localPath,
                InLibrary(placement.Map(path)),
                () => _writer.PutFileAsync(context.LibraryId, context.Location, placement.Map(path), localPath, cancellationToken, file.Bytes(new FileInfo(localPath).Length)),
                id).ConfigureAwait(false);
            _logger.LogInformation("已写入 {Path}", placement.Map(path));
            TryLog(() => _uploadLog.EndOperation(id, OperationState.Committed, null));
            return null;
        }
        catch (Exception ex)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            TryLog(() => _uploadLog.EndOperation(id, cancelled ? OperationState.Cancelled : OperationState.Failed, cancelled ? null : ex.Message));
            throw;
        }
    }

    // A video is an operation that holds its file of the library while it runs: it resumes an earlier addition of it by this machine,
    // or removes what that left, uploads, encodes, and puts the file the library sees last. An operation that loses its file stops,
    // removing nothing but what it made here; one that fails or is cancelled keeps what it made, for the next addition to resume.
    private async Task<string?> AddVideoAsync(AddContext context, string localPath, string path, FileProgress file, CancellationToken cancellationToken)
    {
        var placement = context.Placement;
        var original = new FileInfo(localPath);
        var (size, ticks) = (original.Length, original.LastWriteTimeUtc.Ticks);
        file.Step(AddStep.Hash);
        var sha256 = await JobRunner.HashAsync(localPath, cancellationToken, file.Bytes(size)).ConfigureAwait(false);
        var entryPath = placement.Shortcut ? GetShortcut(placement.Map(path)) : placement.Map(path);
        var id = JobRunner.NewOperationId();
        _uploadLog.StartOperation(NewOperation(id, context, entryPath, localPath, size, sha256));

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = operation.Token;
        var ownership = new Ownership();
        Task? watch = null;
        string? directory = null;
        try
        {
            file.Step(AddStep.Prepare);
            var previous = await _server.ClaimWorkingAsync(context.LibraryId, new WorkingClaim(context.Location, entryPath, id), token).ConfigureAwait(false);
            var prior = previous is null ? null : _uploadLog.GetOperation(previous);
            var resume = prior is not null && CanResume(prior, sha256, size, localPath);
            if (resume)
            {
                _uploadLog.Adopt(previous!, id);
                _logger.LogInformation("{Path} 继续上次没有完成的添加", path);
            }

            await CleanAsync(context, entryPath, id, previous, resume, token).ConfigureAwait(false);
            directory = (resume ? prior!.Directory : null) is { } kept && Directory.Exists(kept) ? kept : _area.CreateOperation(id);
            _uploadLog.SetDirectory(id, directory);
            watch = WatchAsync(context.LibraryId, context.Location, entryPath, id, operation, ownership);

            // The entry is made before the file is uploaded: when it cannot be, a video added before is left as it was.
            string? entry = null;
            var (_, shortcut) = Split(entryPath);
            if (placement.Shortcut)
            {
                file.Step(AddStep.Entry);
                var entries = Path.Combine(directory, "entry");
                if (Directory.Exists(entries))
                {
                    Directory.Delete(entries, true);
                }

                entry = await CreateEntryAsync(directory, shortcut, localPath, token).ConfigureAwait(false);
            }

            IReadOnlyCollection<StoredUpload> uploadedBefore = resume ? _uploadLog.GetUncollected(id) : [];
            foreach (var upload in context.Source.Uploads.Where(u => !IsLanding(context, u)))
            {
                await UploadAsync(context, upload, localPath, path, file, id, entryPath, uploadedBefore, token).ConfigureAwait(false);
            }

            string? note = null;
            if (context.Destination is not null && context.Qualities.Count > 0)
            {
                note = await EncodeAsync(localPath, sha256, context, file, id, directory, token).ConfigureAwait(false);
            }

            // The hash is of what was put only when the file did not change meanwhile.
            original.Refresh();
            if (original.Length != size || original.LastWriteTimeUtc.Ticks != ticks)
            {
                throw new InvalidDataException("文件在添加期间被修改，请重新添加。");
            }

            if (!string.Equals(await _server.ReadWorkingAsync(context.LibraryId, new WorkingPath(context.Location, entryPath), token).ConfigureAwait(false), id, StringComparison.Ordinal))
            {
                ownership.Lost = true;
                throw new OperationCanceledException();
            }

            // What the library sees is put last.
            try
            {
                foreach (var upload in context.Source.Uploads.Where(u => IsLanding(context, u)))
                {
                    await UploadAsync(context, upload, localPath, path, file, id, entryPath, uploadedBefore, token).ConfigureAwait(false);
                }

                var (target, copy) = GetTarget(context, localPath, path);
                if (entry is not null)
                {
                    var (folder, _) = Split(entryPath);
                    var entries = Join(Join(folder, TrueCacheDirectory), shortcut);
                    file.Step(AddStep.Shortcut);
                    try
                    {
                        await _writer.PutShortcutAsync(context.LibraryId, context.Location, entryPath, entry, target, token, id).ConfigureAwait(false);
                        LogShortcut(context, entryPath, entry, entries, localPath, target, true, null, id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogShortcut(context, entryPath, entry, entries, localPath, target, false, ex.Message, id);
                        throw;
                    }
                }

                _database.Record(target, size, new LocalCopy(copy, sha256));
                _logger.LogInformation("已添加 {Path}（{Target}）", path, target);
            }
            catch (ServerException ex) when (ex.IsConflict)
            {
                ownership.Lost = true;
                throw;
            }

            TryLog(() => _uploadLog.EndOperation(id, OperationState.Committed, null));
            await TryReleaseAsync(context, entryPath, id).ConfigureAwait(false);
            TryRemoveOperation(directory);
            return note;
        }
        catch (Exception ex)
        {
            if (ownership.Lost)
            {
                _logger.LogWarning("{Path} 被另一个任务接管，本机跳过", path);
                TryLog(() => _uploadLog.EndOperation(id, OperationState.Abandoned, "另一个任务接管了这个文件。"));
                if (directory is not null)
                {
                    TryRemoveOperation(directory);
                }

                throw new InvalidOperationException(TakenOver, ex);
            }

            var cancelled = cancellationToken.IsCancellationRequested;
            TryLog(() => _uploadLog.EndOperation(id, cancelled ? OperationState.Cancelled : OperationState.Failed, cancelled ? null : ex.Message));
            throw;
        }
        finally
        {
            if (!operation.IsCancellationRequested)
            {
                await operation.CancelAsync().ConfigureAwait(false);
            }

            if (watch is not null)
            {
                await watch.ConfigureAwait(false);
            }
        }
    }

    // An earlier addition of the same file by this machine that did not end well, whose leftovers are still there.
    private static bool CanResume(UploadOperation prior, string sha256, long size, string localPath)
        => prior.Kind == OperationKind.Add
            && prior.State is OperationState.Failed or OperationState.Cancelled
            && !prior.Cleaned
            && string.Equals(prior.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
            && prior.Size == size
            && string.Equals(Path.GetFullPath(prior.LocalPath), Path.GetFullPath(localPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // What the library sees of a video: the file the server writes into it, or the one the declared directory of the server holds.
    private static bool IsLanding(AddContext context, PlanTarget upload)
        => upload.Connection is null
            || (context.Source.Declaration is { Kind: DeclarationKind.ServerPath, Connection: { } declared } && upload.Connection.Id.Equals(declared.Id));

    private static UploadOperation NewOperation(string id, AddContext context, string entryPath, string localPath, long size, string? sha256)
        => new(id, OperationKind.Add, DateTime.UtcNow, null, OperationState.Running, null, context.LibraryId, context.Location, entryPath, localPath, size, sha256, null, null, null, false);

    // Checks the file is still held by the operation, which stops when another took it over; a check that fails is tried at the next.
    private async Task WatchAsync(Guid libraryId, string location, string entryPath, string id, CancellationTokenSource operation, Ownership ownership)
    {
        try
        {
            using var timer = new PeriodicTimer(WorkingPollInterval);
            while (await timer.WaitForNextTickAsync(operation.Token).ConfigureAwait(false))
            {
                string? holder;
                try
                {
                    holder = await _server.ReadWorkingAsync(libraryId, new WorkingPath(location, entryPath), operation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ServerException or HttpRequestException)
                {
                    _logger.LogWarning("检查 {Path} 的占用失败，稍后再试：{Message}", entryPath, ex.Message);
                    continue;
                }

                if (!string.Equals(holder, id, StringComparison.Ordinal))
                {
                    ownership.Lost = true;
                    _logger.LogWarning("{Path} 被另一个任务接管，本机跳过", entryPath);
                    await operation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Removes what earlier additions of the file by this machine left and the server did not take: always their renditions, whose
    // directories are theirs alone; the originals, whose paths are those of every addition, only for the addition whose file this
    // one took, unless it resumes it or lost the file, and never one the server took from another addition.
    private async Task CleanAsync(AddContext context, string entryPath, string id, string? previous, bool resumed, CancellationToken cancellationToken)
    {
        foreach (var stale in _uploadLog.GetStale(context.LibraryId, context.Location, entryPath, id))
        {
            var sources = string.Equals(stale.Id, previous, StringComparison.Ordinal) && !resumed && stale.State != OperationState.Abandoned;
            var removed = _uploadLog.GetUncollected(stale.Id).Where(r => r.Kind == UploadKind.Hls || sources).ToList();
            if (removed.Count > 0)
            {
                _logger.LogInformation("删除上次添加 {Path} 留下的 {Count} 个文件", entryPath, removed.Count);
                if (!await RemoveAsync(removed, cancellationToken).ConfigureAwait(false))
                {
                    // Tried again by the next addition of the file.
                    continue;
                }
            }

            if (stale.Directory is { } directory)
            {
                TryRemoveOperation(directory);
            }

            TryLog(() => _uploadLog.SetCleaned(stale.Id));
        }
    }

    // A connection that fails is left for the next addition to try again; a file the server took is only marked. Whether nothing
    // failed: the files of a connection that no longer exists cannot be removed by any addition.
    private async Task<bool> RemoveAsync(IReadOnlyList<StoredUpload> uploads, CancellationToken cancellationToken)
    {
        var removed = new List<long>();
        var complete = true;
        try
        {
            foreach (var group in uploads.GroupBy(u => u.ConnectionId))
            {
                using var local = group.Key.Equals(Guid.Empty)
                    ? new LocalConnection(new ConnectionSettings { Name = HlsDestination.LocalLabel, Type = ConnectionType.Worker, LocalRoot = _settings.LocalHlsDirectory })
                    : null;
                StorageConnection? connection = local;
                if (connection is null && !_connections.TryGetValue(group.Key, out connection))
                {
                    _logger.LogWarning("连接 {Id} 已不存在，跳过删除它上面的 {Count} 个文件", group.Key, group.Count());
                    continue;
                }

                try
                {
                    using var session = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var upload in group)
                    {
                        if (upload.Kind == UploadKind.Hls || !_uploadLog.IsCollected(upload.ConnectionId, upload.StoragePath))
                        {
                            await session.DeleteAsync(upload.StoragePath, cancellationToken).ConfigureAwait(false);
                        }

                        removed.Add(upload.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    complete = false;
                    _logger.LogWarning("删除 {Connection} 上留下的文件失败：{Message}", connection.Settings.Name, ex.Message);
                }
            }
        }
        finally
        {
            TryLog(() => _uploadLog.MarkRemoved(removed));
        }

        return complete;
    }

    // The mark is left when it cannot be removed; it names an operation that ended, which the next addition takes the file from.
    private async Task TryReleaseAsync(AddContext context, string entryPath, string id)
    {
        try
        {
            await _server.ReleaseWorkingAsync(context.LibraryId, new WorkingRelease(context.Location, entryPath, id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerException or HttpRequestException)
        {
            _logger.LogWarning("释放 {Path} 的占用失败：{Message}", entryPath, ex.Message);
        }
    }

    private void TryRemoveOperation(string directory)
    {
        try
        {
            _area.RemoveOperation(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning("操作目录清理失败：{Message}", ex.Message);
        }
    }

    // A session is opened when a directory is written to: one left open while a video is hashed or encoded may be closed by then.
    // A file the operation uploaded before that is still there is not uploaded again.
    private async Task UploadAsync(
        AddContext context,
        PlanTarget upload,
        string localPath,
        string path,
        FileProgress file,
        string operationId,
        string entryPath,
        IReadOnlyCollection<StoredUpload> uploadedBefore,
        CancellationToken cancellationToken)
    {
        var destination = Join(upload.Path, path);
        if (upload.Connection is null)
        {
            // The server puts the file in place only while the operation holds it, which it does by the path the library sees.
            var holder = string.Equals(destination, entryPath, StringComparison.Ordinal) ? operationId : null;
            file.Step(AddStep.Upload, upload.Label);
            await TrackAsync(
                UploadKind.Source,
                context,
                localPath,
                InLibrary(destination),
                () => _writer.PutFileAsync(context.LibraryId, context.Location, destination, localPath, cancellationToken, file.Bytes(new FileInfo(localPath).Length), holder),
                operationId).ConfigureAwait(false);
            return;
        }

        if (IsInPlace(upload.Connection, destination, localPath))
        {
            return;
        }

        file.Step(AddStep.Upload, upload.Label);
        var connection = _connections[upload.Connection.Id];
        if (uploadedBefore.Any(u => u.Kind == UploadKind.Source && u.ConnectionId.Equals(upload.Connection.Id) && string.Equals(u.StoragePath, destination, StringComparison.Ordinal)))
        {
            using var session = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (await session.ExistsAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                var length = new FileInfo(localPath).Length;
                file.Advance(length, length, ProgressUnit.Bytes);
                _logger.LogInformation("{Path} 上次已上传到 {Target}，跳过", path, upload.Label);
                return;
            }
        }

        await TrackAsync(
            UploadKind.Source,
            context,
            localPath,
            upload.Connection.Name + "/" + destination,
            async () =>
            {
                using var session = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await session.PutAsync(destination, localPath, cancellationToken, file.Bytes(new FileInfo(localPath).Length)).ConfigureAwait(false);
            },
            operationId,
            upload.Connection.Id,
            destination).ConfigureAwait(false);
        _logger.LogInformation("已上传 {Path} 到 {Target}", path, upload.Label);
    }

    // What the server is told the video is, and the file of this machine its jobs read.
    private (string Target, string Copy) GetTarget(AddContext context, string localPath, string path)
    {
        var declaration = context.Source.Declaration;
        if (!context.Placement.Shortcut)
        {
            return (StoragePath.JoinServer(context.Location, context.Placement.Map(path)), localPath);
        }

        if (declaration is { Kind: DeclarationKind.S3, Connection: { } bucket })
        {
            return (bucket.GetS3Address(Join(declaration.Path, path)), localPath);
        }

        if (declaration is { Kind: DeclarationKind.WorkerLocal, Connection: { } directory })
        {
            var copy = Path.GetFullPath(Path.Combine(directory.LocalRoot, Join(declaration.Path, path).Replace('/', Path.DirectorySeparatorChar)));
            return (WorkerLocalAddress.Format(_settings.WorkerId, copy), copy);
        }

        return (WorkerLocalAddress.Format(_settings.WorkerId, localPath), localPath);
    }

    private async Task TrackAsync(
        UploadKind kind,
        AddContext context,
        string localPath,
        string destination,
        Func<Task> upload,
        string? operationId = null,
        Guid? connectionId = null,
        string? storagePath = null)
    {
        try
        {
            await upload().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log([new UploadRecord(DateTime.UtcNow, kind, context.LibraryId, localPath, destination, new FileInfo(localPath).Length, false, ex.Message, operationId, connectionId, storagePath)]);
            throw;
        }

        Log([new UploadRecord(DateTime.UtcNow, kind, context.LibraryId, localPath, destination, new FileInfo(localPath).Length, true, null, operationId, connectionId, storagePath)]);
    }

    // The .strm file and every file of its entry.
    private void LogShortcut(AddContext context, string relative, string entry, string entries, string localPath, string target, bool succeeded, string? error, string? operationId)
    {
        var now = DateTime.UtcNow;
        var records = new List<UploadRecord>
        {
            new(now, UploadKind.Shortcut, context.LibraryId, localPath, InLibrary(relative), System.Text.Encoding.UTF8.GetByteCount(target) + 1, succeeded, error, operationId)
        };
        records.AddRange(Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Select(file => new UploadRecord(
            now,
            UploadKind.TrueCache,
            context.LibraryId,
            file,
            InLibrary(Join(entries, Path.GetRelativePath(entry, file).Replace('\\', '/'))),
            new FileInfo(file).Length,
            succeeded,
            error,
            operationId)));
        Log(records);
    }

    // The log is no reason for an addition to fail.
    private void Log(IReadOnlyCollection<UploadRecord> records) => TryLog(() => _uploadLog.Add(records));

    private void TryLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logger.LogWarning("上传记录写入失败：{Message}", ex.Message);
        }
    }

    // The TrueCache entry of a video, made from the local file in the directory of its operation. The .strm is named after the video
    // without its extension, so the subtitles and images named after the video belong to it.
    /// <summary>Gets the hardware acceleration of the TrueCache tool for an encoder of the settings.</summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The value of its --hwaccel.</returns>
    internal static string HardwareOf(string encoder) => encoder switch
    {
        "h264_nvenc" => "nvenc",
        "h264_qsv" => "qsv",
        "h264_amf" => "amf",
        "h264_vaapi" => "vaapi",
        _ => "none"
    };

    // The tool decodes with the hardware of the encoder, and without it when that fails.
    private async Task<string> CreateEntryAsync(string work, string shortcut, string localPath, CancellationToken cancellationToken)
    {
        var output = Path.Combine(work, "entry");
        var hardware = HardwareOf(_settings.Encoder);
        try
        {
            return await RunTrueCacheAsync(output, shortcut, localPath, hardware, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (hardware != "none" && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TrueCache 工具用 {Hardware} 生成 {Name} 失败，改用 CPU 重试", hardware, shortcut);
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }

            return await RunTrueCacheAsync(output, shortcut, localPath, "none", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> RunTrueCacheAsync(string output, string shortcut, string localPath, string hardware, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        List<string> arguments = ["strm", "--ffmpeg", ProgramPaths.Ffmpeg, "--server-url", _settings.Server, "--api-key", _settings.ApiKey, "--name", shortcut, "--output", output, "--hwaccel", hardware];
        if (hardware is "nvenc" or "vaapi" && _settings.HardwareDevice.Length > 0)
        {
            arguments.AddRange(["--hw-device", _settings.HardwareDevice]);
        }

        arguments.Add(localPath);
        await ProcessRunner.RunAsync(ProgramPaths.TrueCacheTool, arguments, cancellationToken).ConfigureAwait(false);
        var entry = Path.Combine(output, shortcut);
        return Directory.Exists(entry) ? entry : throw new InvalidDataException("TrueCache 工具没有写出条目 " + shortcut + "。");
    }

    // A content the server has everything of, or that has a job already, is left to the server; the note says which. A job that
    // fails fails the addition. The job of an earlier addition of the file is resumed with what it encoded and uploaded; when the
    // server gives another, the renditions uploaded for the old one are removed.
    private async Task<string?> EncodeAsync(string localPath, string sha256, AddContext context, FileProgress file, string operationId, string directory, CancellationToken cancellationToken)
    {
        var destination = context.Destination!;
        file.Step(AddStep.Probe);
        var probe = await JobRunner.ProbeAsync(localPath, cancellationToken).ConfigureAwait(false);
        var earlier = _uploadLog.GetOperation(operationId);

        // The slot is taken before the lease, which has no heartbeat until the encoding starts.
        await _encoding.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkLease lease;
        EncodePlan plan;
        try
        {
            var answer = await _server.StartEagerJobAsync(
                    new EagerRequest(_settings.Name, _settings.WorkerId, context.LibraryId, sha256, probe, destination.Base, context.Qualities, earlier?.JobId, earlier?.LeaseToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (answer.Done || answer.Lease is null || answer.Plan is null)
            {
                _logger.LogInformation("{Path} 的这些档位已经存在", localPath);
                _encoding.Release();
                return "档位已存在，无需编码";
            }

            (lease, plan) = (answer.Lease, answer.Plan);
        }
        catch (ServerException ex) when (ex.IsConflict)
        {
            _logger.LogInformation("{Path} 已有任务在处理，不随添加编码", localPath);
            _encoding.Release();
            return "已有任务在处理，未随添加编码";
        }
        catch
        {
            _encoding.Release();
            throw;
        }

        var sameJob = string.Equals(earlier?.LeaseToken, lease.LeaseToken, StringComparison.Ordinal);
        IReadOnlyCollection<StoredUpload> uploaded;
        try
        {
            if (earlier?.LeaseToken is not null && !sameJob)
            {
                await RemoveAsync(_uploadLog.GetUncollected(operationId).Where(u => u.Kind == UploadKind.Hls).ToList(), cancellationToken).ConfigureAwait(false);
            }

            TryLog(() => _uploadLog.SetJob(operationId, lease.JobId, lease.LeaseToken));
            uploaded = sameJob ? _uploadLog.GetUncollected(operationId).Where(u => u.Kind == UploadKind.Hls).ToList() : [];
        }
        catch
        {
            // The runner has not taken the slot yet.
            _encoding.Release();
            throw;
        }

        if (!await _runner.RunEagerAsync(lease, plan, localPath, sha256, destination, context.LibraryId, _encoding, cancellationToken, file.Job, new EagerWork(operationId, directory, uploaded))
                .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("编码任务失败。");
        }

        TryLog(() => _uploadLog.Collect(operationId, UploadKind.Hls));
        return null;
    }

    /// <summary>
    /// Where the files added go in the folder of the library: below a directory of it, the video being the file of the library
    /// itself, or at its root, the video being referred to by a .strm file.
    /// </summary>
    /// <param name="Shortcut">Whether a video is referred to by a .strm file.</param>
    /// <param name="Root">The directory in the folder of the library, with forward slashes; may be empty.</param>
    private sealed record Placement(bool Shortcut, string Root)
    {
        public static Placement Create(PlanDeclaration? declaration, string location)
        {
            switch (declaration?.Kind)
            {
                case DeclarationKind.JellyfinLibrary:
                    return new Placement(false, declaration.Path);
                case DeclarationKind.ServerPath:
                    var root = declaration.Connection!.GetServerPath(declaration.Path);
                    return LibraryFolder.TryGetRelative(location, root, out var relative)
                        ? new Placement(false, relative)
                        : throw new ArgumentException("申告目录 " + root + " 不在媒体库目录 " + location + " 之内；服务器目录只能申告媒体库内的位置。");
                default:
                    return new Placement(true, string.Empty);
            }
        }

        public string Map(string path) => Join(Root, path);
    }

    // The progress of one file, a step at a time; the advances of a step are told four times a second at most, and its end always.
    private sealed class FileProgress(IProgress<AddEvent>? progress, int file)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

        private readonly object _gate = new();

        // When the step was last told how far it is; 0 for not yet.
        private long _last;
        private (JobStage Stage, string Detail)? _stage;

        public Action<JobProgress>? Job => progress is null ? null : OnJob;

        public void Step(AddStep step, string detail = "")
        {
            if (progress is null)
            {
                return;
            }

            lock (_gate)
            {
                _last = 0;
            }

            progress.Report(new AddStepStarted(file, step, detail));
        }

        public void Advance(long done, long total, ProgressUnit unit)
        {
            if (progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (done < total && _last != 0 && Stopwatch.GetElapsedTime(_last, now) < Interval)
                {
                    return;
                }

                _last = now;
            }

            progress.Report(new AddStepAdvanced(file, done, total, unit));
        }

        public Action<long>? Bytes(long total) => progress is null ? null : done => Advance(done, total, ProgressUnit.Bytes);

        public void End(bool added, string? message) => progress?.Report(new AddFileEnded(file, added, message));

        private static AddStep Map(JobStage stage) => stage switch
        {
            JobStage.EncodeVideo => AddStep.EncodeVideo,
            JobStage.EncodeAudio => AddStep.EncodeAudio,
            JobStage.Subtitles => AddStep.Subtitles,
            JobStage.Preview => AddStep.Preview,
            _ => AddStep.UploadHls
        };

        private void OnJob(JobProgress job)
        {
            bool started;
            lock (_gate)
            {
                started = _stage != (job.Stage, job.Detail);
                _stage = (job.Stage, job.Detail);
            }

            if (started)
            {
                Step(Map(job.Stage), job.Detail);
            }

            Advance(job.Done, job.Total, job.Stage == JobStage.UploadHls ? ProgressUnit.Files : ProgressUnit.Milliseconds);
        }
    }

    // Set once another operation took the file over.
    private sealed class Ownership
    {
        public volatile bool Lost;
    }

    private sealed record AddContext(
        Guid LibraryId,
        string Location,
        SourcePlan Source,
        Placement Placement,
        HlsDestination? Destination,
        IReadOnlyList<int> Qualities);
}
