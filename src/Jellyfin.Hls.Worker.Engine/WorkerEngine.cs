using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker;

/// <summary>
/// The worker: claims jobs from the server and runs them, up to <see cref="WorkerSettings.Concurrency"/> encoding and
/// <see cref="WorkerSettings.UploadConcurrency"/> uploading at the same time.
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
    private readonly ILogger _logger;
    private readonly ServerClient _server;
    private readonly Dictionary<string, StorageConnection> _connections = new(StringComparer.Ordinal);
    private readonly HlsTarget _target;
    private readonly SemaphoreSlim _encoding;
    private readonly SemaphoreSlim _uploading;
    private readonly Lock _gate = new();
    private Workspace? _workspace;
    private int _adding;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerEngine"/> class.
    /// </summary>
    /// <param name="settings">The settings, validated, with <see cref="WorkerSettings.Hls"/>.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="handler">The HTTP handler for tests.</param>
    public WorkerEngine(WorkerSettings settings, ILoggerFactory loggerFactory, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var hls = settings.Hls ?? throw new ArgumentException("需要配置 Hls：写入 HLS 的连接和目录。");
        _settings = settings;
        _logger = loggerFactory.CreateLogger("Jellyfin.Hls.Worker");
        _server = new ServerClient(new Uri(settings.Server), settings.ApiKey, TimeSpan.FromSeconds(settings.ControlTimeoutSeconds), handler);
        foreach (var connection in settings.Connections)
        {
            _connections[connection.Name] = StorageConnection.Create(connection, _logger);
        }

        _target = new HlsTarget(_connections[hls.Connection], hls, _logger);
        _encoding = new SemaphoreSlim(settings.Concurrency);
        _uploading = new SemaphoreSlim(settings.UploadConcurrency);
    }

    /// <summary>Gets the server.</summary>
    public ServerClient Server => _server;

    /// <summary>Gets the root of the HLS store.</summary>
    public HlsTarget Target => _target;

    /// <summary>
    /// Checks the server and the root of the HLS store, then claims and runs jobs until it is stopped.
    /// </summary>
    /// <param name="once">Whether to end once there is no job to claim and the running ones ended.</param>
    /// <param name="cancellationToken">Stops the worker: the running jobs are stopped and reported.</param>
    /// <returns><see cref="Success"/>, <see cref="Failed"/> or <see cref="Stopped"/>.</returns>
    public async Task<int> RunAsync(bool once, CancellationToken cancellationToken)
    {
        var workspace = GetWorkspace();
        workspace.Cache.Trim();
        var runner = workspace.Runner;
        var active = new List<Task<bool>>();
        var failed = false;
        try
        {
            if (!await StartAsync(once, cancellationToken).ConfigureAwait(false))
            {
                return Failed;
            }

            _logger.LogInformation(
                "Worker {Name} 已启动：编码 {Concurrency} / 上传 {UploadConcurrency} 并发，编码器 {Encoder}，HLS 根 {Base}",
                _settings.Name,
                _settings.Concurrency,
                _settings.UploadConcurrency,
                _settings.Encoder,
                _target.Base);
            var failures = 0;
            while (true)
            {
                failed |= await CollectAsync(active).ConfigureAwait(false);
                if (active.Count >= _settings.Concurrency + _settings.UploadConcurrency)
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
                    assignment = await _server.PollAsync(_settings.Name, cancellationToken).ConfigureAwait(false);
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

                active.Add(runner.RunAsync(assignment, _encoding, cancellationToken));
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
    /// Adds files of this machine to a library, and encodes the qualities asked for at once. While media is added, no job is claimed.
    /// </summary>
    /// <param name="request">What to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentException">The request cannot be carried out.</exception>
    public async Task<AddResult> AddAsync(AddRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _adding);
        try
        {
            if (request.Qualities is { Count: > 0 } && !await StartAsync(true, cancellationToken).ConfigureAwait(false))
            {
                throw new ArgumentException("服务器无法使用 HLS 根，不能随添加编码。");
            }

            return await GetWorkspace().Adder.AddAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _adding);
        }
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
        _uploading.Dispose();
    }

    /// <summary>
    /// Checks the protocol and the root of the HLS store: writes the marker if it is missing, and asks the server whether it can
    /// read the root. A server or a storage that cannot be reached is waited for, except with <paramref name="once"/>; a root the
    /// server refuses is not.
    /// </summary>
    /// <param name="once">Whether to give up when the server cannot be reached.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the worker can start.</returns>
    internal async Task<bool> StartAsync(bool once, CancellationToken cancellationToken)
    {
        StoreTestResult check;
        for (var failures = 0; ; failures++)
        {
            try
            {
                await _server.CheckProtocolAsync(cancellationToken).ConfigureAwait(false);
                if (_target.Connection.Settings.Type == ConnectionType.S3
                    && await MediaAdder.EnsureAccessAsync(_server, _target.Connection.Settings, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("已在服务器上为桶 {Bucket} 创建网络存储访问权限", _target.Connection.Settings.Bucket);
                }

                if (await _target.PrepareAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("已在 HLS 根写入标记 {Marker}", HlsTarget.MarkerName);
                }

                check = await _server.CheckBaseAsync(_target.Base, cancellationToken).ConfigureAwait(false);
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
                _target.Base,
                check.Message ?? (check.Success ? "服务器看不到标记 " + HlsTarget.MarkerName + "，请检查 ServerRoot 或存储设置" : "未知原因"));
            return false;
        }

        return true;
    }

    // The work directory is locked on first use, not when the engine is made, so a check can run beside a running worker.
    private Workspace GetWorkspace()
    {
        lock (_gate)
        {
            if (_workspace is null)
            {
                var area = new WorkArea(_settings.WorkDirectory, _settings.KeepFailedJobs);
                var cache = new InputCache(area.Inputs, (long)(_settings.InputCacheGiB * 1024 * 1024 * 1024));
                var database = new MediaDatabase(_settings.DatabasePath);
                var runner = new JobRunner(_settings, _server, _target, cache, area, _uploading, _logger, database);
                var adder = new MediaAdder(_settings, _server, _connections, _target, runner, database, area, _encoding, _logger);
                _workspace = new Workspace(area, cache, runner, adder);
            }

            return _workspace;
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
