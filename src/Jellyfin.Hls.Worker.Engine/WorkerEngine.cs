using System.Collections.Concurrent;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker;

/// <summary>
/// The worker: claims jobs of the libraries it is given from the server and runs them, up to
/// <see cref="WorkerSettings.Concurrency"/> encoding and <see cref="WorkerSettings.UploadConcurrency"/> uploading at the same time,
/// writing up to <see cref="WorkerSettings.UploadFileConcurrency"/> files at the same time.
/// </summary>
public sealed class WorkerEngine : IDisposable
{
    /// <summary>The exit code of a run that went well.</summary>
    public const int Success = 0;

    /// <summary>The exit code of a run in which a job failed or the server could not be reached.</summary>
    public const int Failed = 1;

    /// <summary>The exit code of a run that was stopped.</summary>
    public const int Stopped = 130;

    private const int MaxBackoffSeconds = 60;

    private readonly WorkerSettings _settings;
    private readonly SettingsStore _store;
    private readonly ILogger _logger;
    private readonly ServerClient _server;
    private readonly Dictionary<Guid, StorageConnection> _connections = [];
    private readonly ConcurrentDictionary<Guid, byte> _excluded = new();
    private readonly ConcurrentDictionary<string, bool> _checkedBases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _encoding;
    private readonly UploadLimiter _uploading;
    private readonly UploadLimiter _files;
    private readonly Lock _gate = new();
    private Workspace? _workspace;
    private int _adding;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerEngine"/> class.
    /// </summary>
    /// <param name="settings">The settings, validated.</param>
    /// <param name="store">Where the settings are kept, and the data of the worker.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="handler">The HTTP handler for tests.</param>
    public WorkerEngine(WorkerSettings settings, SettingsStore store, ILoggerFactory loggerFactory, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _settings = settings;
        _store = store;
        _logger = loggerFactory.CreateLogger("Jellyfin.Hls.Worker");
        _server = new ServerClient(new Uri(settings.Server), settings.ApiKey, TimeSpan.FromSeconds(settings.ControlTimeoutSeconds), handler);
        foreach (var connection in settings.Connections.Where(c => c.Type != ConnectionType.Server))
        {
            _connections[connection.Id] = StorageConnection.Create(connection, _logger);
        }

        _encoding = new SemaphoreSlim(settings.Concurrency);
        _uploading = new UploadLimiter(settings.UploadConcurrency);
        _files = new UploadLimiter(settings.UploadFileConcurrency);
    }

    /// <summary>Raised when the server gave the worker another id than it had; the id is stored already.</summary>
    public event Action<Guid>? WorkerIdAssigned;

    /// <summary>Gets the server.</summary>
    public ServerClient Server => _server;

    /// <summary>
    /// Gets the id of the worker from the server, which records the one the worker has when it does not know it, and stores it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id.</returns>
    public async Task<Guid> RegisterAsync(CancellationToken cancellationToken)
    {
        var id = await _server.RegisterWorkerAsync(_settings.WorkerId.Equals(Guid.Empty) ? null : _settings.WorkerId, _settings.Name, cancellationToken)
            .ConfigureAwait(false);
        if (!id.Equals(_settings.WorkerId))
        {
            _settings.WorkerId = id;
            _store.SetWorkerId(id);
            _logger.LogInformation("服务器分配了 Worker Id {WorkerId}", id.ToString("N"));
            WorkerIdAssigned?.Invoke(id);
        }

        return id;
    }

    /// <summary>Gets the libraries of the server with their folders.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    public Task<IReadOnlyList<VirtualFolder>> GetLibrariesAsync(CancellationToken cancellationToken) => _server.GetVirtualFoldersAsync(cancellationToken);

    /// <summary>Gets the libraries the server pre-encodes, with their qualities.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    public Task<IReadOnlyList<WorkerLibrary>> GetPreEncodedLibrariesAsync(CancellationToken cancellationToken) => _server.GetLibrariesAsync(cancellationToken);

    /// <summary>
    /// Checks the server and where the renditions of each library go, then claims and runs the jobs of those libraries until it
    /// is stopped. The items whose input this worker does not have are skipped until the next run.
    /// </summary>
    /// <param name="libraries">Where the renditions of each library go; the jobs of the others are left to other workers.</param>
    /// <param name="once">Whether to end once there is no job to claim and the running ones ended.</param>
    /// <param name="cancellationToken">Stops the worker: the running jobs are stopped and reported.</param>
    /// <returns><see cref="Success"/>, <see cref="Failed"/> or <see cref="Stopped"/>.</returns>
    public async Task<int> RunAsync(IReadOnlyDictionary<Guid, HlsPlan> libraries, bool once, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        _excluded.Clear();
        var workspace = GetWorkspace();
        workspace.Cache.Trim();
        var runner = workspace.Runner;
        var active = new List<Task<bool>>();
        var failed = false;
        try
        {
            if (!await ConnectAsync(once, cancellationToken).ConfigureAwait(false))
            {
                return Failed;
            }

            var destinations = new Dictionary<Guid, HlsDestination>();
            var served = await GetServedQualitiesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (libraryId, plan) in libraries)
            {
                // Its source files would be read for nothing. The qualities are those the server has now, which may be more than the
                // settings last saw: a quality the server added is not turned off.
                if (_settings.PreEncodes.Find(p => p.LibraryId.Equals(libraryId)) is { } preEncode
                    && (served.GetValueOrDefault(libraryId) ?? preEncode.Qualities) is { Count: > 0 } qualities
                    && qualities.All(preEncode.DisabledQualities.Contains))
                {
                    _logger.LogWarning("媒体库 {Library} 的档位已全部关闭，不领取它的任务", preEncode.Name);
                    continue;
                }

                HlsDestination destination;
                try
                {
                    destination = HlsDestination.Create(plan, _connections, _settings, _logger);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogError("媒体库 {Library} 的 HLS 方案无法使用，跳过它的任务：{Message}", libraryId.ToString("N"), ex.Message);
                    continue;
                }

                if (await EnsureDestinationAsync(destination, once, cancellationToken).ConfigureAwait(false))
                {
                    destinations[libraryId] = destination;
                }
                else
                {
                    _logger.LogError("媒体库 {Library} 的 HLS 根无法使用，跳过它的任务", libraryId.ToString("N"));
                }
            }

            if (destinations.Count == 0)
            {
                _logger.LogError("没有可以领取任务的媒体库");
                return Failed;
            }

            _logger.LogInformation(
                "Worker {Name} 已启动：编码 {Concurrency} / 上传 {UploadConcurrency} 并发、同时上传 {Files} 个文件，编码器 {Encoder}，领取 {Count} 个媒体库的任务",
                _settings.Name,
                _settings.Concurrency,
                _uploading.Limit,
                _files.Limit,
                _settings.Encoder,
                destinations.Count);
            List<QualityFilter> excludedQualities = [.. destinations.Keys
                .Select(id => new QualityFilter(id, _settings.GetDisabledQualities(id)))
                .Where(f => f.Excluded.Count > 0)];
            var failures = 0;
            while (true)
            {
                failed |= await CollectAsync(active).ConfigureAwait(false);
                if (active.Count >= _settings.Concurrency + _uploading.Limit)
                {
                    await Task.WhenAny(active).WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Media being added comes first: its files are here, and the library waits for them.
                if (Volatile.Read(ref _adding) > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A job is claimed only when it can start encoding at once; the slot goes to the job. Media being added while
                // waiting for it still comes first.
                await _encoding.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _adding) > 0)
                {
                    _encoding.Release();
                    continue;
                }

                WorkAssignment? assignment;
                try
                {
                    assignment = await _server.PollAsync(
                        new WorkerRequest(_settings.Name, _settings.WorkerId, [.. destinations.Keys], [.. _excluded.Keys], excludedQualities.Count == 0 ? null : excludedQualities),
                        cancellationToken).ConfigureAwait(false);
                    failures = 0;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ServerException.IsTransient(ex))
                {
                    _encoding.Release();
                    _logger.LogWarning("暂时无法领取任务：{Message}", ex.Message);
                    if (once)
                    {
                        failed = true;
                        break;
                    }

                    await Task.Delay(Backoff(failures++), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    _encoding.Release();
                    throw;
                }

                if (assignment is null)
                {
                    _encoding.Release();
                    if (once && active.Count == 0)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(active.Count > 0 && once ? 1 : _settings.PollSeconds), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!destinations.TryGetValue(assignment.Source.LibraryId, out var target))
                {
                    // The server filters by library; a job of another one is given back and its item skipped.
                    _encoding.Release();
                    _excluded.TryAdd(assignment.Source.ItemId, 0);
                    await ReturnAsync(assignment.Lease).ConfigureAwait(false);
                    continue;
                }

                active.Add(runner.RunAsync(assignment, target, _encoding, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("正在停止任务……");
        }
        catch (ServerException ex)
        {
            // Refused for good, such as a revoked key: going on would only repeat it.
            _logger.LogError("服务器拒绝了请求，Worker 停止：{Message}", ex.Message);
            failed = true;
        }
        finally
        {
            var results = await Task.WhenAll(active).ConfigureAwait(false);
            failed |= results.Contains(false);
        }

        return cancellationToken.IsCancellationRequested ? Stopped : failed ? Failed : Success;
    }

    /// <summary>
    /// Adds files of this machine to a library, and encodes every quality of the library at once when asked to. While media is
    /// added, no job is claimed.
    /// </summary>
    /// <param name="request">What to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="progress">Told the files, and the steps of each as they go, or <c>null</c>.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentException">The request cannot be carried out.</exception>
    public async Task<AddResult> AddAsync(AddRequest request, CancellationToken cancellationToken, IProgress<AddEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _adding);
        try
        {
            if (!await ConnectAsync(true, cancellationToken).ConfigureAwait(false))
            {
                throw new ArgumentException("无法连接服务器。");
            }

            HlsDestination? destination = null;
            if (request is { PreEncodeQualities.Count: > 0, Hls: { } hls })
            {
                destination = HlsDestination.Create(hls, _connections, _settings, _logger);
                if (!await EnsureDestinationAsync(destination, true, cancellationToken).ConfigureAwait(false))
                {
                    throw new ArgumentException("服务器无法使用 HLS 根，不能随添加编码。");
                }
            }

            return await GetWorkspace().Adder.AddAsync(request, destination, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _adding);
        }
    }

    /// <summary>
    /// Changes how many groups and how many files are uploaded at the same time, for the uploads to come; those running go on.
    /// </summary>
    /// <param name="uploadConcurrency">The groups, as <see cref="WorkerSettings.UploadConcurrency"/>.</param>
    /// <param name="uploadFileConcurrency">The files, as <see cref="WorkerSettings.UploadFileConcurrency"/>.</param>
    public void SetUploadLimits(int uploadConcurrency, int uploadFileConcurrency)
    {
        if (_uploading.Limit == uploadConcurrency && _files.Limit == uploadFileConcurrency)
        {
            return;
        }

        _uploading.Limit = uploadConcurrency;
        _files.Limit = uploadFileConcurrency;
        _settings.UploadConcurrency = uploadConcurrency;
        _settings.UploadFileConcurrency = uploadFileConcurrency;
        _logger.LogInformation("上传并发已改为 {UploadConcurrency}，同时上传 {Files} 个文件", uploadConcurrency, uploadFileConcurrency);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _workspace?.Area.Dispose();
        _server.Dispose();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _encoding.Dispose();
    }

    /// <summary>
    /// Checks the protocol and gets the id of the worker. A server that cannot be reached is waited for, except with <paramref name="once"/>.
    /// </summary>
    /// <param name="once">Whether to give up when the server cannot be reached.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the server was reached.</returns>
    internal async Task<bool> ConnectAsync(bool once, CancellationToken cancellationToken)
    {
        for (var failures = 0; ; failures++)
        {
            try
            {
                await _server.CheckProtocolAsync(cancellationToken).ConfigureAwait(false);
                await RegisterAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ServerException.IsTransient(ex))
            {
                _logger.LogWarning("暂时无法连接服务器{Retry}：{Message}", once ? string.Empty : "，将重试", ex.Message);
                if (once)
                {
                    return false;
                }

                await Task.Delay(Backoff(failures), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Checks where the renditions go: gives the server an access to the bucket of the root, writes the marker to every directory
    /// where it is missing, and asks the server whether it can read the root. A server or a storage that cannot be reached is
    /// waited for, except with <paramref name="once"/>; a root the server refuses is not. A root found good is not checked again.
    /// </summary>
    /// <param name="destination">Where the renditions go.</param>
    /// <param name="once">Whether to give up when the server or a storage cannot be reached.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the renditions can go there.</returns>
    internal async Task<bool> EnsureDestinationAsync(HlsDestination destination, bool once, CancellationToken cancellationToken)
    {
        if (_checkedBases.ContainsKey(destination.Base))
        {
            return true;
        }

        StoreTestResult check;
        for (var failures = 0; ; failures++)
        {
            try
            {
                if (destination.DeclaredS3 is { } bucket && await MediaAdder.EnsureAccessAsync(_server, bucket, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("已在服务器上为桶 {Bucket} 创建网络存储访问权限", bucket.Bucket);
                }

                if (await destination.PrepareAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("已在 HLS 目录写入标记 {Marker}", HlsTarget.MarkerName);
                }

                check = await _server.CheckBaseAsync(destination.Base, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (!once && !cancellationToken.IsCancellationRequested
                && (ServerException.IsTransient(ex) || (ex is not ServerException && StorageConnection.IsTransient(ex))))
            {
                _logger.LogWarning("暂时无法连接服务器或存储，将重试：{Message}", ex.Message);
                await Task.Delay(Backoff(failures), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!check.Success || !check.HasMarker)
        {
            _logger.LogError(
                "服务器无法使用 HLS 根 {Base}：{Message}",
                destination.Base,
                check.Message ?? (check.Success ? "服务器看不到标记 " + HlsTarget.MarkerName + "，请检查方案的申告目录或存储设置" : "未知原因"));
            return false;
        }

        _checkedBases[destination.Base] = true;
        return true;
    }

    // The work directory is locked on first use, not when the engine is made, so a check can run beside a running worker.
    private Workspace GetWorkspace()
    {
        lock (_gate)
        {
            if (_workspace is null)
            {
                var area = new WorkArea(_settings.TempDirectory.Length > 0 ? _settings.TempDirectory : _store.Paths.DefaultTempDirectory, _settings.KeepFailedJobs);
                var cache = new InputCache(area.Inputs, (long)(_settings.InputCacheGiB * 1024 * 1024 * 1024));
                var database = new MediaDatabase(_store.Paths.Database);
                var uploadLog = new UploadLog(_store.Paths.Database);

                // The work directory is locked: the operations still running in the database were of a worker that stopped.
                var interrupted = uploadLog.EndInterrupted();
                if (interrupted > 0)
                {
                    _logger.LogInformation("{Count} 个上次没有完成的上传操作已标记为取消", interrupted);
                }

                var runner = new JobRunner(_settings, _server, cache, area, _uploading, _files, _logger, uploadLog, id => _excluded.TryAdd(id, 0), database);
                var writer = new LibraryWriter(_server, () => _settings.WorkerId, _logger);
                var adder = new MediaAdder(_settings, _server, _connections, runner, database, area, _encoding, writer, uploadLog, _logger);
                _workspace = new Workspace(area, cache, runner, adder);
            }

            return _workspace;
        }
    }

    // The qualities of each library the server pre-encodes; none when it cannot tell, the settings being used instead.
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetServedQualitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _server.GetLibrariesAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(l => l.Id, l => l.Qualities);
        }
        catch (Exception ex) when (ex is ServerException or HttpRequestException or IOException)
        {
            _logger.LogWarning("未能取得服务器的档位，使用设置中记下的档位：{Message}", ex.Message);
            return new Dictionary<Guid, IReadOnlyList<int>>();
        }
    }

    private async Task ReturnAsync(WorkLease lease)
    {
        try
        {
            await _server.ReturnAsync(lease.JobId, lease.LeaseToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerException or HttpRequestException or IOException)
        {
            _logger.LogWarning("未能交还任务 {JobId}，租约过期后服务器会回收它：{Message}", lease.JobId, ex.Message);
        }
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxBackoffSeconds, 5 * Math.Pow(2, Math.Min(failures, 4))));

    // Whether a job that ended failed.
    private static async Task<bool> CollectAsync(List<Task<bool>> active)
    {
        var failed = false;
        foreach (var done in active.Where(t => t.IsCompleted).ToList())
        {
            failed |= !await done.ConfigureAwait(false);
            active.Remove(done);
        }

        return failed;
    }

    private sealed record Workspace(WorkArea Area, InputCache Cache, JobRunner Runner, MediaAdder Adder);
}
