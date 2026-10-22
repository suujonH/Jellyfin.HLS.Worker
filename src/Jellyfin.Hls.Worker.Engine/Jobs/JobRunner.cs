using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// What a job encodes once it is prepared: the plan and the input.
/// </summary>
/// <param name="Plan">The plan.</param>
/// <param name="InputPath">The input file.</param>
/// <param name="Sha256">The SHA-256 of the input.</param>
/// <param name="Input">What keeps the input, released once the encoding ends; <c>null</c> for a file the caller keeps.</param>
public sealed record PreparedJob(EncodePlan Plan, string InputPath, string Sha256, InputLease? Input);

/// <summary>
/// Runs a job from its lease to its completion: the input, the identification, the encoding of each group, the upload of each
/// group as soon as it is encoded, and the report. The encode slot is held from the start until the encoding ends, an upload
/// slot for the upload of each group.
/// </summary>
public sealed partial class JobRunner
{
    private const int CompleteAttempts = 5;

    private readonly WorkerSettings _settings;
    private readonly ServerClient _server;
    private readonly HlsTarget _target;
    private readonly InputCache _cache;
    private readonly WorkArea _area;
    private readonly MediaDatabase? _database;
    private readonly SemaphoreSlim _uploading;
    private readonly HlsEncoder _encoder;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobRunner"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="server">The server.</param>
    /// <param name="target">The root of the HLS store.</param>
    /// <param name="cache">The cache of the inputs.</param>
    /// <param name="area">The work directory.</param>
    /// <param name="uploading">The upload slots.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="database">The local copies of library files, or <c>null</c>.</param>
    public JobRunner(
        WorkerSettings settings,
        ServerClient server,
        HlsTarget target,
        InputCache cache,
        WorkArea area,
        SemaphoreSlim uploading,
        ILogger logger,
        MediaDatabase? database = null)
    {
        _database = database;
        _settings = settings;
        _server = server;
        _target = target;
        _cache = cache;
        _area = area;
        _uploading = uploading;
        _encoder = new HlsEncoder(settings);
        _logger = logger;
    }

    /// <summary>
    /// Runs a job the server handed out: the input is downloaded, probed and identified.
    /// </summary>
    /// <param name="assignment">The job.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <returns>Whether the job succeeded.</returns>
    public Task<bool> RunAsync(WorkAssignment assignment, SemaphoreSlim encodeSlot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return RunCoreAsync(assignment.Lease, token => PrepareAsync(assignment, token), encodeSlot, cancellationToken);
    }

    /// <summary>
    /// Runs a job the server created for a file this worker adds: the plan came with the lease, and the file is here.
    /// </summary>
    /// <param name="lease">The lease.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="inputPath">The file.</param>
    /// <param name="sha256">Its SHA-256.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <returns>Whether the job succeeded.</returns>
    public Task<bool> RunEagerAsync(WorkLease lease, EncodePlan plan, string inputPath, string sha256, SemaphoreSlim encodeSlot, CancellationToken cancellationToken)
        => RunCoreAsync(lease, _ => Task.FromResult<PreparedJob?>(new PreparedJob(plan, inputPath, sha256, null)), encodeSlot, cancellationToken);

    /// <summary>
    /// Runs a job from a lease: prepares it, then encodes, uploads and reports it.
    /// </summary>
    /// <param name="lease">The lease.</param>
    /// <param name="prepare">Prepares the job; <c>null</c> when nothing is left to do.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <returns>Whether the job succeeded.</returns>
    public async Task<bool> RunCoreAsync(WorkLease lease, Func<CancellationToken, Task<PreparedJob?>> prepare, SemaphoreSlim encodeSlot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(encodeSlot);
        _logger.LogInformation("领取任务 {JobId}（第 {Attempt} 次）", lease.JobId, lease.Attempt);
        using var job = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = new Heartbeat(_server, lease, _logger);
        var beating = heartbeat.RunAsync(job);
        var slot = new Slot(encodeSlot);
        PreparedJob? prepared = null;
        string? directory = null;
        var succeeded = false;
        try
        {
            directory = _area.CreateJob();
            prepared = await prepare(job.Token).ConfigureAwait(false);
            if (prepared is null)
            {
                _logger.LogInformation("任务 {JobId}：服务器已有这些档位，无需编码", lease.JobId);
                succeeded = true;
                return true;
            }

            ValidatePlan(prepared.Plan, prepared.Sha256);
            var outputs = await EncodeAndUploadAsync(prepared, Path.Combine(directory, "output"), slot, job).ConfigureAwait(false);
            await CompleteAsync(lease, outputs, job.Token).ConfigureAwait(false);
            succeeded = true;
            _logger.LogInformation("完成任务 {JobId}：{Count} 个档位，等待服务器校验后发布", lease.JobId, outputs.Count);
            return true;
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(lease, ex, heartbeat.Lost, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            return false;
        }
        finally
        {
            slot.Release();
            prepared?.Input?.Dispose();
            await job.CancelAsync().ConfigureAwait(false);
            await beating.ConfigureAwait(false);
            if (directory is not null)
            {
                if (!succeeded && _settings.KeepFailedJobs)
                {
                    _logger.LogWarning("保留失败任务的目录：{Directory}", directory);
                }
                else
                {
                    try
                    {
                        _area.RemoveJob(directory);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning("任务目录清理失败：{Message}", ex.Message);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks a plan before anything is encoded for it: it is for this input, and every directory is one of the layout of the server.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="sha256">The SHA-256 of the input.</param>
    /// <exception cref="InvalidDataException">The plan cannot be carried out.</exception>
    internal static void ValidatePlan(EncodePlan plan, string sha256)
    {
        if (!string.Equals(plan.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("编码计划的 SHA-256 与输入不一致。");
        }

        if (plan.Renditions is not { Count: > 0 } || plan.Probe is null)
        {
            throw new InvalidDataException("编码计划没有档位。");
        }

        if (!(plan.SegmentSeconds > 0) || !(plan.KeyframeSeconds > 0) || plan.AudioSampleRate <= 0 || plan.ExpectedSegments < 1)
        {
            throw new InvalidDataException("编码计划的分片参数无效。");
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rendition in plan.Renditions)
        {
            if (!ValidDirectory().IsMatch(rendition.RelativeDirectory ?? string.Empty) || !directories.Add(rendition.RelativeDirectory!))
            {
                throw new InvalidDataException("编码计划的目录无效或重复：" + rendition.RelativeDirectory);
            }

            if (rendition.Kind == RenditionKind.Video
                && rendition.Video is not { Width: > 0, Height: > 0, FramesPerSecond: > 0 and <= 1000 })
            {
                throw new InvalidDataException("视频档位 " + rendition.Key + " 的尺寸或帧率无效。");
            }
        }
    }

    // A file this worker added is read where it is, with the hash recorded when it was added; anything else is downloaded.
    private async Task<PreparedJob?> PrepareAsync(WorkAssignment assignment, CancellationToken cancellationToken)
    {
        var lease = assignment.Lease;
        if (_database?.Find(assignment.Source.Path, assignment.Source.Length, assignment.Source.DateModified) is { } copy)
        {
            _logger.LogInformation("任务 {JobId}：使用本地文件 {Path}", lease.JobId, copy.LocalPath);
            var plan = await IdentifyAsync(lease, copy.LocalPath, copy.Sha256, cancellationToken).ConfigureAwait(false);
            return plan is null ? null : new PreparedJob(plan, copy.LocalPath, copy.Sha256, null);
        }

        var input = await _cache.AcquireAsync(
            assignment.Source,
            (path, token) =>
            {
                _logger.LogInformation("任务 {JobId}：下载 {Path}（{Size:F1} MiB）", lease.JobId, assignment.Source.Path, assignment.Source.Length / 1048576.0);
                return _server.DownloadAsync(assignment.InputUrl, path, token);
            },
            cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await IdentifyAsync(lease, input.Path, input.Sha256, cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                input.Dispose();
                return null;
            }

            if (!string.Equals(plan.Sha256, input.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The cached file is not what the server read; the next attempt downloads it again.
                input.Invalidate();
            }

            return new PreparedJob(plan, input.Path, input.Sha256, input);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    // The plan, or null when the server has nothing left to do.
    private async Task<EncodePlan?> IdentifyAsync(WorkLease lease, string path, string sha256, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var answer = await _server.IdentifyAsync(lease.JobId, new Identification(lease.LeaseToken, sha256, probe, _target.Base), cancellationToken)
            .ConfigureAwait(false);
        return answer.Done ? null : answer.Plan ?? throw new InvalidDataException("服务器没有返回编码计划。");
    }

    /// <summary>
    /// Runs ffprobe as the server expects its output.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The JSON of ffprobe.</returns>
    public Task<string> ProbeAsync(string path, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(_settings.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellationToken);

    // A group is uploaded while the next is encoded. An upload that fails stops the job, and its error is the one reported, not the
    // cancellation of the encoding it caused.
    private async Task<IReadOnlyList<RenditionOutput>> EncodeAndUploadAsync(PreparedJob prepared, string root, Slot slot, CancellationTokenSource job)
    {
        var plan = prepared.Plan;
        var outputs = new List<RenditionOutput>();
        var uploads = new List<Task>();
        Exception? uploadError = null;
        try
        {
            foreach (var group in HlsEncoder.GetGroups(plan, root))
            {
                job.Token.ThrowIfCancellationRequested();
                _logger.LogInformation(
                    "编码 {MediaKey}：{Kind} × {Count}{Detail}",
                    plan.MediaKey,
                    group.Kind,
                    group.Renditions.Count,
                    group.Kind == RenditionKind.Video ? "（" + string.Join("/", group.Renditions.Select(r => r.Rendition.Video!.Name + "p")) + "，" + _settings.Encoder + "）" : string.Empty);
                await _encoder.EncodeAsync(plan, group, prepared.InputPath, job.Token).ConfigureAwait(false);
                var inspected = group.Renditions.Select(r => (r.Rendition, r.Directory, Output: OutputInspector.Inspect(plan, r.Rendition, r.Directory))).ToList();
                outputs.AddRange(inspected.Select(i => i.Output));
                uploads.Add(UploadAsync(inspected, error => uploadError ??= error, job));
            }
        }
        catch (Exception ex)
        {
            // Every upload ends before the job does, as they use its directory and its slots. A failed upload stopped the encoding,
            // and its error is the one reported; otherwise the error of the encoding is.
            await job.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(uploads.Select(u => u.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(ex is OperationCanceledException && Volatile.Read(ref uploadError) is { } error ? error : ex);
        }
        finally
        {
            slot.Release();
            prepared.Input?.Dispose();
        }

        try
        {
            await Task.WhenAll(uploads).ConfigureAwait(false);
        }
        catch
        {
            await Task.WhenAll(uploads.Select(u => u.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
            throw;
        }

        return outputs;
    }

    private async Task UploadAsync(List<(PlannedRendition Rendition, string Directory, RenditionOutput Output)> renditions, Action<Exception> fail, CancellationTokenSource job)
    {
        try
        {
            await _uploading.WaitAsync(job.Token).ConfigureAwait(false);
            try
            {
                var files = renditions
                    .SelectMany(r => r.Output.Files.Select(f => (r.Rendition.RelativeDirectory + "/" + f.Path, Path.Combine(r.Directory, f.Path))))
                    .ToList();
                _logger.LogInformation("上传 {Count} 个文件（{Size:F1} MiB）到 {Base}", files.Count, renditions.Sum(r => r.Output.Files.Sum(f => f.Length)) / 1048576.0, _target.Base);
                await _target.UploadAsync(files, job.Token).ConfigureAwait(false);
            }
            finally
            {
                _uploading.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !job.IsCancellationRequested)
        {
            fail(ex);
            await job.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The server answers 202 once it recorded the outputs; it verifies and publishes them afterwards.
    private async Task CompleteAsync(WorkLease lease, IReadOnlyList<RenditionOutput> outputs, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _server.CompleteAsync(lease.JobId, new Completion(lease.LeaseToken, outputs), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < CompleteAttempts && !cancellationToken.IsCancellationRequested && ServerException.IsTransient(ex))
            {
                _logger.LogWarning("任务 {JobId} 的完成报告暂时失败，第 {Attempt} 次重试：{Message}", lease.JobId, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A lost lease is not reported: the server took the job back already.
    private async Task ReportFailureAsync(WorkLease lease, Exception exception, bool leaseLost, bool stopping)
    {
        if (leaseLost || exception is ServerException { IsConflict: true })
        {
            _logger.LogWarning("任务 {JobId} 的租约已失效，服务器会重新分配", lease.JobId);
            return;
        }

        var message = stopping ? "Worker 已停止。" : exception.Message;
        if (stopping)
        {
            _logger.LogWarning("任务 {JobId} 随 Worker 停止", lease.JobId);
        }
        else
        {
            _logger.LogError(exception is InvalidDataException or InvalidOperationException or ServerException ? null : exception, "任务 {JobId} 失败：{Message}", lease.JobId, message);
        }

        try
        {
            await _server.FailAsync(lease.JobId, new Failure(lease.LeaseToken, message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerException or HttpRequestException or IOException)
        {
            _logger.LogWarning("未能向服务器报告任务 {JobId} 失败，租约过期后服务器会回收它：{Message}", lease.JobId, ex.Message);
        }
    }

    // The layout of the server; ASCII digits only and \z, as there.
    [GeneratedRegex(@"^[0-9a-f]{32}/(video/[0-9]{3,4}|audio/[0-9]{1,3}|subtitles/[0-9]{1,3}|preview)/[0-9a-f]{32}\z")]
    private static partial Regex ValidDirectory();

    // Released once, whichever comes first: the end of the encoding or the end of the job.
    private sealed class Slot(SemaphoreSlim semaphore)
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Release() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
