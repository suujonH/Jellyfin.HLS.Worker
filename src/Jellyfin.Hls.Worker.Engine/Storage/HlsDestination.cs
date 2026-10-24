using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// Where the renditions of a library go: every directory of its scheme, in order, and the root the server is told.
/// </summary>
public sealed class HlsDestination
{
    /// <summary>The label of the directory of this machine the renditions are declared in when the scheme declares none.</summary>
    public const string LocalLabel = "本机工作目录";

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsDestination"/> class.
    /// </summary>
    /// <param name="targets">The directories, in order.</param>
    /// <param name="base">The root the server is told.</param>
    /// <param name="declaredS3">The S3 connection of the root; <c>null</c> for a path.</param>
    public HlsDestination(IReadOnlyList<HlsTarget> targets, string @base, ConnectionSettings? declaredS3)
    {
        Targets = targets;
        Base = @base;
        DeclaredS3 = declaredS3;
    }

    /// <summary>Gets the directories, in order.</summary>
    public IReadOnlyList<HlsTarget> Targets { get; }

    /// <summary>Gets the root the server is told, as it names it.</summary>
    public string Base { get; }

    /// <summary>Gets the S3 connection of the root, for the access of the server; <c>null</c> for a path.</summary>
    public ConnectionSettings? DeclaredS3 { get; }

    /// <summary>
    /// Makes the destination of a plan.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="connections">The connections by id.</param>
    /// <param name="settings">The settings: where this machine keeps the renditions it declares, and the id of the worker.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The destination.</returns>
    /// <exception cref="ArgumentException">A directory is of a connection the worker cannot write, or the renditions are declared on
    /// this machine before the server gave the worker an id.</exception>
    public static HlsDestination Create(HlsPlan plan, IReadOnlyDictionary<Guid, StorageConnection> connections, WorkerSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(settings);
        var targets = plan.Uploads.Select(u => u.Connection is not null && connections.TryGetValue(u.Connection.Id, out var connection)
                ? new HlsTarget(connection, u.Path, u.Label, logger)
                : throw new ArgumentException("HLS 不能上传到 " + u.Label + "。"))
            .ToList();
        if (plan.Base is { } @base)
        {
            return new HlsDestination(targets, @base, plan.DeclaredS3);
        }

        // No directory declared: the renditions are kept in the work directory too, and declared there.
        if (settings.WorkerId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Worker 还没有登记，不能申告本机目录。");
        }

        var root = settings.LocalHlsDirectory;
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("临时文件目录不是绝对路径：" + settings.TempDirectory);
        }

        Directory.CreateDirectory(root);
        var local = new LocalConnection(new ConnectionSettings { Name = LocalLabel, Type = ConnectionType.Worker, LocalRoot = root });
        targets.Add(new HlsTarget(local, plan.LocalPath, plan.LocalPath.Length == 0 ? LocalLabel : LocalLabel + ":" + plan.LocalPath, logger));
        return new HlsDestination(targets, WorkerLocalAddress.Format(settings.WorkerId, StoragePath.JoinServer(root, plan.LocalPath)), null);
    }

    /// <summary>
    /// Checks that every directory can be written, and writes the marker where it is missing.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a marker was written.</returns>
    public async Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        var written = false;
        foreach (var target in Targets)
        {
            written |= await target.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    /// Writes files to every directory, one directory after the other, and records each file. A directory that fails stops the upload.
    /// </summary>
    /// <param name="files">The files: the path below the root, with forward slashes, and the file to write.</param>
    /// <param name="limiter">The places of the files written at the same time.</param>
    /// <param name="log">The log of the uploads.</param>
    /// <param name="libraryId">The library the files are of, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="uploaded">Told of each file written to a directory, or that was there already.</param>
    /// <param name="operationId">The operation the files are uploaded for.</param>
    /// <param name="done">The files the operation uploaded before; those still there are not written again, nor recorded.</param>
    /// <returns>A task.</returns>
    public async Task UploadAsync(
        IReadOnlyList<(string Path, string LocalFile)> files,
        UploadLimiter limiter,
        UploadLog log,
        Guid? libraryId,
        CancellationToken cancellationToken,
        Action? uploaded = null,
        string? operationId = null,
        IReadOnlyCollection<StoredUpload>? done = null)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var target in Targets)
        {
            var connectionId = target.Connection.Settings.Id;
            var skip = done?.Where(d => d.ConnectionId.Equals(connectionId)).Select(d => d.StoragePath).ToHashSet(StringComparer.Ordinal);
            var records = new List<UploadRecord>();
            try
            {
                await target.UploadAsync(
                    files,
                    limiter,
                    cancellationToken,
                    (path, localFile, succeeded, error) =>
                    {
                        var record = new UploadRecord(
                            DateTime.UtcNow,
                            UploadKind.Hls,
                            libraryId,
                            localFile,
                            target.Label + "/" + path,
                            new FileInfo(localFile).Length,
                            succeeded,
                            error,
                            operationId,
                            connectionId,
                            target.GetPath(path));
                        lock (records)
                        {
                            records.Add(record);
                        }

                        if (succeeded)
                        {
                            uploaded?.Invoke();
                        }
                    },
                    skip,
                    () => uploaded?.Invoke()).ConfigureAwait(false);
            }
            finally
            {
                log.Add(records);
            }
        }
    }
}
