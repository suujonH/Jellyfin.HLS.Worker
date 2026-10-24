using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Data.Sqlite;
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
/// The operation a job of a file being added runs for: its directory, which keeps what was encoded, and the files of it already
/// uploaded.
/// </summary>
/// <param name="OperationId">The operation.</param>
/// <param name="Directory">The directory of the operation; the caller removes it.</param>
/// <param name="Uploaded">The renditions files the operation uploaded before, not written again while they are there.</param>
public sealed record EagerWork(string OperationId, string Directory, IReadOnlyCollection<StoredUpload> Uploaded);

/// <summary>A stage of a job: a group being encoded, or the renditions still being uploaded once all are encoded.</summary>
public enum JobStage
{
    /// <summary>The video qualities are encoded.</summary>
    EncodeVideo,

    /// <summary>The audio tracks are encoded.</summary>
    EncodeAudio,

    /// <summary>The subtitles are extracted.</summary>
    Subtitles,

    /// <summary>The preview is made.</summary>
    Preview,

    /// <summary>The renditions are still being uploaded.</summary>
    UploadHls
}

/// <summary>How far a job is.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Detail">What the stage works on, such as the qualities; may be empty.</param>
/// <param name="Done">The milliseconds of the media encoded, or the files uploaded.</param>
/// <param name="Total">The milliseconds of the media, or the files to upload.</param>
public sealed record JobProgress(JobStage Stage, string Detail, long Done, long Total);

/// <summary>
/// Runs a job from its lease to its completion: the input, the identification, the encoding of each group, the upload of each
/// group as soon as it is encoded, and the report. The encode slot is held from the start until the encoding ends, an upload
/// slot for the upload of each group, and a place of the worker for each file it writes.
/// </summary>
public sealed partial class JobRunner
{
    private const int CompleteAttempts = 5;

    private readonly WorkerSettings _settings;
    private readonly ServerClient _server;
    private readonly InputCache _cache;
    private readonly WorkArea _area;
    private readonly MediaDatabase? _database;
    private readonly UploadLog _uploadLog;
    private readonly Action<Guid> _exclude;
    private readonly UploadLimiter _uploading;
    private readonly UploadLimiter _files;
    private readonly HlsEncoder _encoder;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobRunner"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="server">The server.</param>
    /// <param name="cache">The cache of the inputs.</param>
    /// <param name="area">The work directory.</param>
    /// <param name="uploading">The places of the groups uploaded at the same time.</param>
    /// <param name="files">The places of the files written at the same time.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="uploadLog">The log of the uploads.</param>
    /// <param name="exclude">Keeps an item from being claimed again in this run: its input is not on this worker.</param>
    /// <param name="database">The local copies of library files, or <c>null</c>.</param>
    public JobRunner(
        WorkerSettings settings,
        ServerClient server,
        InputCache cache,
        WorkArea area,
        UploadLimiter uploading,
        UploadLimiter files,
        ILogger logger,
        UploadLog uploadLog,
        Action<Guid> exclude,
        MediaDatabase? database = null)
    {
        _database = database;
        _settings = settings;
        _server = server;
        _cache = cache;
        _area = area;
        _uploading = uploading;
        _files = files;
        _uploadLog = uploadLog;
        _exclude = exclude;
        _encoder = new HlsEncoder(settings, logger);
        _logger = logger;
    }

    /// <summary>
    /// Runs a job the server handed out: the input is downloaded, or read where it is on this worker, probed and identified.
    /// </summary>
    /// <param name="assignment">The job.</param>
    /// <param name="destination">Where the renditions of its library go.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <returns>Whether the job succeeded.</returns>
    public async Task<bool> RunAsync(WorkAssignment assignment, HlsDestination destination, SemaphoreSlim encodeSlot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // The operation only groups the uploads of the job in the log: the server keeps what the job leaves.
        var source = assignment.Source;
        var id = NewOperationId();
        TryLog(() => _uploadLog.StartOperation(new UploadOperation(
            id,
            OperationKind.Job,
            DateTime.UtcNow,
            null,
            OperationState.Running,
            null,
            source.LibraryId,
            null,
            source.Path,
            source.Path,
            source.Length,
            null,
            assignment.Lease.JobId,
            assignment.Lease.LeaseToken,
            null,
            false)));
        var succeeded = false;
        try
        {
            succeeded = await RunCoreAsync(
                assignment.Lease,
                token => PrepareAsync(assignment, destination, token),
                destination,
                source.LibraryId,
                encodeSlot,
                cancellationToken,
                operationId: id).ConfigureAwait(false);
            return succeeded;
        }
        finally
        {
            var state = succeeded ? OperationState.Committed : cancellationToken.IsCancellationRequested ? OperationState.Cancelled : OperationState.Failed;
            TryLog(() => _uploadLog.EndOperation(id, state, state == OperationState.Failed ? "详见日志" : null));
        }
    }

    /// <summary>
    /// Runs a job the server created for a file this worker adds: the plan came with the lease, and the file is here.
    /// </summary>
    /// <param name="lease">The lease.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="inputPath">The file.</param>
    /// <param name="sha256">Its SHA-256.</param>
    /// <param name="destination">Where the renditions go.</param>
    /// <param name="libraryId">The library the file is added to.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <param name="progress">Told how far the job is, or <c>null</c>.</param>
    /// <param name="work">The operation the job runs for, whose directory keeps what was encoded; <c>null</c> for a directory of its own.</param>
    /// <returns>Whether the job succeeded.</returns>
    public Task<bool> RunEagerAsync(
        WorkLease lease,
        EncodePlan plan,
        string inputPath,
        string sha256,
        HlsDestination destination,
        Guid libraryId,
        SemaphoreSlim encodeSlot,
        CancellationToken cancellationToken,
        Action<JobProgress>? progress = null,
        EagerWork? work = null)
        => RunCoreAsync(lease, _ => Task.FromResult<PreparedJob?>(new PreparedJob(plan, inputPath, sha256, null)), destination, libraryId, encodeSlot, cancellationToken, progress, work);

    /// <summary>
    /// Runs a job from a lease: prepares it, then encodes, uploads and reports it. A job whose input is a file of a worker that
    /// this worker does not have is given back.
    /// </summary>
    /// <param name="lease">The lease.</param>
    /// <param name="prepare">Prepares the job; <c>null</c> when nothing is left to do.</param>
    /// <param name="destination">Where the renditions go.</param>
    /// <param name="libraryId">The library of the job, for the log of the uploads.</param>
    /// <param name="encodeSlot">The encode slot the caller took; it is released when the encoding ends.</param>
    /// <param name="cancellationToken">The cancellation token of the worker.</param>
    /// <param name="progress">Told how far the job is, or <c>null</c>.</param>
    /// <param name="work">The operation of a file being added the job runs for, whose directory it uses and keeps; <c>null</c> for a directory of its own.</param>
    /// <param name="operationId">The operation the uploads are recorded for; by default the one of <paramref name="work"/>.</param>
    /// <returns>Whether the job succeeded; a job given back did.</returns>
    public async Task<bool> RunCoreAsync(
        WorkLease lease,
        Func<CancellationToken, Task<PreparedJob?>> prepare,
        HlsDestination destination,
        Guid? libraryId,
        SemaphoreSlim encodeSlot,
        CancellationToken cancellationToken,
        Action<JobProgress>? progress = null,
        EagerWork? work = null,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(destination);
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
            directory = work?.Directory ?? _area.CreateJob();
            prepared = await prepare(job.Token).ConfigureAwait(false);
            if (prepared is null)
            {
                _logger.LogInformation("任务 {JobId}：服务器已有这些档位，无需编码", lease.JobId);
                succeeded = true;
                return true;
            }

            ValidatePlan(prepared.Plan, prepared.Sha256);
            var outputs = await EncodeAndUploadAsync(prepared, Path.Combine(directory, "output"), destination, libraryId, slot, job, progress, work, operationId ?? work?.OperationId)
                .ConfigureAwait(false);
            await CompleteAsync(lease, outputs, job.Token).ConfigureAwait(false);
            succeeded = true;
            _logger.LogInformation("完成任务 {JobId}：{Count} 个档位，等待服务器校验后发布", lease.JobId, outputs.Count);
            return true;
        }
        catch (InputUnreachableException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _exclude(ex.ItemId);
            _logger.LogWarning("任务 {JobId} 的源文件 {Path} 在本机找不到，本次运行不再领取它", lease.JobId, ex.LocalPath);
            try
            {
                await _server.ReturnAsync(lease.JobId, lease.LeaseToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception returnError) when (returnError is ServerException or HttpRequestException or IOException)
            {
                _logger.LogWarning("未能交还任务 {JobId}，租约过期后服务器会回收它：{Message}", lease.JobId, returnError.Message);
            }

            succeeded = true;
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
            if (directory is not null && work is null)
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

    // A file this worker added is read where it is, with the hash recorded when it was added; a file of this worker the server
    // cannot read is hashed where it is; anything else is downloaded.
    private async Task<PreparedJob?> PrepareAsync(WorkAssignment assignment, HlsDestination destination, CancellationToken cancellationToken)
    {
        var lease = assignment.Lease;
        var source = assignment.Source;
        if (_database?.Find(source.Path, source.Length, source.DateModified) is { } copy)
        {
            _logger.LogInformation("任务 {JobId}：使用本地文件 {Path}", lease.JobId, copy.LocalPath);
            var plan = await IdentifyAsync(lease, source.LibraryId, copy.LocalPath, copy.Sha256, destination, cancellationToken).ConfigureAwait(false);
            return plan is null ? null : new PreparedJob(plan, copy.LocalPath, copy.Sha256, null);
        }

        if (WorkerLocalAddress.TryParse(source.Path, out var owner, out var localPath))
        {
            var file = new FileInfo(localPath);
            if (!owner.Equals(_settings.WorkerId) || !file.Exists || file.Length != source.Length)
            {
                throw new InputUnreachableException(source.ItemId, localPath);
            }

            _logger.LogInformation("任务 {JobId}：读取本机文件 {Path}", lease.JobId, localPath);
            var sha256 = await HashAsync(localPath, cancellationToken).ConfigureAwait(false);
            var plan = await IdentifyAsync(lease, source.LibraryId, localPath, sha256, destination, cancellationToken).ConfigureAwait(false);
            return plan is null ? null : new PreparedJob(plan, localPath, sha256, null);
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
            var plan = await IdentifyAsync(lease, source.LibraryId, input.Path, input.Sha256, destination, cancellationToken).ConfigureAwait(false);
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

    // The plan, or null when the server has nothing left to do; the qualities turned off for the library are left to other workers.
    private async Task<EncodePlan?> IdentifyAsync(WorkLease lease, Guid libraryId, string path, string sha256, HlsDestination destination, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var excluded = _settings.GetDisabledQualities(libraryId);
        var answer = await _server.IdentifyAsync(lease.JobId, new Identification(lease.LeaseToken, sha256, probe, destination.Base, excluded.Count == 0 ? null : excluded), cancellationToken)
            .ConfigureAwait(false);
        return answer.Done ? null : answer.Plan ?? throw new InvalidDataException("服务器没有返回编码计划。");
    }

    /// <summary>
    /// Makes the id of a new operation.
    /// </summary>
    /// <returns>32 lower-case hexadecimal characters.</returns>
    internal static string NewOperationId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs ffprobe as the server expects its output.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The JSON of ffprobe.</returns>
    public static Task<string> ProbeAsync(string path, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(ProgramPaths.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], cancellationToken);

    /// <summary>
    /// Gets the SHA-256 of a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="read">Told the bytes read so far, or <c>null</c>.</param>
    /// <returns>The hash, in lower-case hexadecimal.</returns>
    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken, Action<long>? read = null)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            Stream counted = read is null ? stream : new ProgressStream(stream, read);
            await using (counted.ConfigureAwait(false))
            {
                return Convert.ToHexStringLower(await SHA256.HashDataAsync(counted, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    // A group is uploaded while the next is encoded. An upload that fails stops the job, and its error is the one reported, not the
    // cancellation of the encoding it caused. The uploads are told of only once every group is encoded. A job of a file being added
    // does not encode again the groups its operation encoded before.
    private async Task<IReadOnlyList<RenditionOutput>> EncodeAndUploadAsync(
        PreparedJob prepared,
        string root,
        HlsDestination destination,
        Guid? libraryId,
        Slot slot,
        CancellationTokenSource job,
        Action<JobProgress>? progress,
        EagerWork? work,
        string? operationId)
    {
        var plan = prepared.Plan;
        HashSet<string> kept = work is null ? [] : KeptOutputs.Load(work.Directory, plan);
        var outputs = new List<RenditionOutput>();
        var uploads = new List<Task>();
        Exception? uploadError = null;
        var duration = (long)Math.Round(plan.Probe.DurationSeconds * 1000);
        var labels = string.Join("、", destination.Targets.Select(t => t.Label));
        var count = new UploadCount();
        Action? uploaded = progress is null ? null : () =>
        {
            var sent = Interlocked.Increment(ref count.Sent);
            if (Volatile.Read(ref count.Encoded) == 1)
            {
                progress(new JobProgress(JobStage.UploadHls, labels, sent, Interlocked.Read(ref count.Files)));
            }
        };
        try
        {
            foreach (var group in HlsEncoder.GetGroups(plan, root))
            {
                job.Token.ThrowIfCancellationRequested();
                var (stage, detail) = Describe(group);
                progress?.Invoke(new JobProgress(stage, detail, 0, duration));
                var inspected = work is not null && group.Renditions.All(r => kept.Contains(r.Rendition.Key)) ? InspectKept(plan, group) : null;
                if (inspected is not null)
                {
                    _logger.LogInformation("{MediaKey}：{Kind} × {Count} 已在上次编码，不再编码", plan.MediaKey, group.Kind, group.Renditions.Count);
                    progress?.Invoke(new JobProgress(stage, detail, duration, duration));
                }
                else
                {
                    _logger.LogInformation(
                        "编码 {MediaKey}：{Kind} × {Count}{Detail}",
                        plan.MediaKey,
                        group.Kind,
                        group.Renditions.Count,
                        group.Kind == RenditionKind.Video ? "（" + string.Join("/", group.Renditions.Select(r => r.Rendition.Video!.Name + "p")) + "，" + _settings.Encoder + "）" : string.Empty);
                    await _encoder.EncodeAsync(
                        plan,
                        group,
                        prepared.InputPath,
                        job.Token,
                        progress is null ? null : part => progress(new JobProgress(stage, detail, (long)(part * duration), duration))).ConfigureAwait(false);
                    inspected = group.Renditions.Select(r => (r.Rendition, r.Directory, Output: OutputInspector.Inspect(plan, r.Rendition, r.Directory))).ToList();
                    if (work is not null)
                    {
                        KeptOutputs.MarkEncoded(work.Directory, group.Renditions.Select(r => r.Rendition.Key));
                    }
                }

                outputs.AddRange(inspected.Select(i => i.Output));
                Interlocked.Add(ref count.Files, (long)inspected.Sum(i => i.Output.Files.Count) * destination.Targets.Count);
                uploads.Add(UploadAsync(inspected, destination, libraryId, error => uploadError ??= error, job, uploaded, operationId, work?.Uploaded));
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

        Volatile.Write(ref count.Encoded, 1);
        if (progress is not null && Interlocked.Read(ref count.Sent) < Interlocked.Read(ref count.Files))
        {
            progress(new JobProgress(JobStage.UploadHls, labels, Interlocked.Read(ref count.Sent), Interlocked.Read(ref count.Files)));
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

    private async Task UploadAsync(
        List<(PlannedRendition Rendition, string Directory, RenditionOutput Output)> renditions,
        HlsDestination destination,
        Guid? libraryId,
        Action<Exception> fail,
        CancellationTokenSource job,
        Action? uploaded,
        string? operationId,
        IReadOnlyCollection<StoredUpload>? done)
    {
        try
        {
            await _uploading.WaitAsync(job.Token).ConfigureAwait(false);
            try
            {
                var files = renditions
                    .SelectMany(r => r.Output.Files.Select(f => (r.Rendition.RelativeDirectory + "/" + f.Path, Path.Combine(r.Directory, f.Path))))
                    .ToList();
                _logger.LogInformation(
                    "上传 {Count} 个文件（{Size:F1} MiB）到 {Targets}",
                    files.Count,
                    renditions.Sum(r => r.Output.Files.Sum(f => f.Length)) / 1048576.0,
                    string.Join("、", destination.Targets.Select(t => t.Label)));
                await destination.UploadAsync(files, _files, _uploadLog, libraryId, job.Token, uploaded, operationId, done).ConfigureAwait(false);
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

    // The outputs of a group encoded before; null when one of them is not whole, whose directories are then deleted to encode it again.
    private List<(PlannedRendition Rendition, string Directory, RenditionOutput Output)>? InspectKept(EncodePlan plan, RenditionGroup group)
    {
        try
        {
            return group.Renditions.Select(r => (r.Rendition, r.Directory, Output: OutputInspector.Inspect(plan, r.Rendition, r.Directory))).ToList();
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning("{MediaKey}：上次编码的 {Kind} 不完整，重新编码：{Message}", plan.MediaKey, group.Kind, ex.Message);
            foreach (var (_, directory) in group.Renditions)
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }

            return null;
        }
    }

    private void TryLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogWarning("上传记录写入失败：{Message}", ex.Message);
        }
    }

    // The layout of the server; ASCII digits only and \z, as there.
    [GeneratedRegex(@"^[0-9a-f]{32}/(video/[0-9]{3,4}|audio/[0-9]{1,3}|subtitles/[0-9]{1,3}|preview)/[0-9a-f]{32}\z")]
    private static partial Regex ValidDirectory();

    private static (JobStage Stage, string Detail) Describe(RenditionGroup group) => group.Kind switch
    {
        RenditionKind.Video => (JobStage.EncodeVideo, string.Join("/", group.Renditions.Select(r => r.Rendition.Video!.Name + "p"))),
        RenditionKind.Audio => (JobStage.EncodeAudio, group.Renditions.Count + " 条"),
        RenditionKind.Subtitle => (JobStage.Subtitles, group.Renditions.Count + " 条"),
        _ => (JobStage.Preview, string.Empty)
    };

    // The files of the renditions to upload to every directory, those uploaded, and whether every group is encoded.
    private sealed class UploadCount
    {
        public long Files;
        public long Sent;
        public int Encoded;
    }

    // Released once, whichever comes first: the end of the encoding or the end of the job.
    private sealed class Slot(SemaphoreSlim semaphore)
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Release() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
