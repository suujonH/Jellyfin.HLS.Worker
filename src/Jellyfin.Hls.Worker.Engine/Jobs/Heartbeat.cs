using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// Keeps the lease of a job while it runs, and stops the job when the lease is lost.
/// </summary>
internal sealed class Heartbeat
{
    private readonly ServerClient _server;
    private readonly WorkLease _lease;
    private readonly ILogger _logger;
    private volatile bool _lost;

    public Heartbeat(ServerClient server, WorkLease lease, ILogger logger)
    {
        _server = server;
        _lease = lease;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the lease was lost, so the job was stopped.</summary>
    public bool Lost => _lost;

    /// <summary>
    /// Sends heartbeats until the job ends. A failed heartbeat does not end the job by itself: the lease is good until its
    /// time is up. A 409 says it is not, and so does the clock: the job stops one interval before the lease would end, when the last
    /// good heartbeat is a lease less one interval ago. Half an interval absorbs the jitter of the timer.
    /// </summary>
    /// <param name="job">The job; it is cancelled when the lease is lost.</param>
    /// <returns>A task that ends with the job.</returns>
    public async Task RunAsync(CancellationTokenSource job)
    {
        var leaseSpan = TimeSpan.FromSeconds(Math.Max(1, _lease.LeaseSeconds));
        var interval = TimeSpan.FromSeconds(Math.Clamp(leaseSpan.TotalSeconds / 3, 1, 30));
        var lastOk = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(job.Token).ConfigureAwait(false))
            {
                // A heartbeat that hangs must not outlive the lease: it is cut off where the job would stop anyway.
                var remaining = lastOk + leaseSpan - interval - DateTimeOffset.UtcNow;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
                request.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                // The server renews the lease when it receives the request, at the latest when it was sent, not when it answers.
                var sent = DateTimeOffset.UtcNow;
                try
                {
                    await _server.HeartbeatAsync(_lease.JobId, _lease.LeaseToken, request.Token).ConfigureAwait(false);
                    lastOk = sent;
                }
                catch (OperationCanceledException) when (!job.IsCancellationRequested)
                {
                    _logger.LogWarning("任务 {JobId} 的心跳没有应答，租约即将过期，停止编码和上传", _lease.JobId);
                    await StopAsync(job).ConfigureAwait(false);
                    return;
                }
                catch (ServerException ex) when (ex.IsConflict)
                {
                    _logger.LogWarning("任务 {JobId} 的租约已失效，停止编码和上传", _lease.JobId);
                    await StopAsync(job).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!job.IsCancellationRequested && ServerException.IsTransient(ex))
                {
                    if (DateTimeOffset.UtcNow + (interval / 2) >= lastOk + leaseSpan - interval)
                    {
                        _logger.LogWarning("任务 {JobId} 的心跳持续失败，租约即将过期，停止编码和上传", _lease.JobId);
                        await StopAsync(job).ConfigureAwait(false);
                        return;
                    }

                    _logger.LogWarning("任务 {JobId} 的心跳暂时失败，将重试：{Message}", _lease.JobId, ex.Message);
                }
                catch (ServerException ex) when (!job.IsCancellationRequested)
                {
                    // Refused for another reason, such as a revoked key: the lease cannot be kept.
                    _logger.LogError("任务 {JobId} 的心跳被拒绝，停止编码和上传：{Message}", _lease.JobId, ex.Message);
                    await StopAsync(job).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
            // The job ended.
        }
    }

    private async Task StopAsync(CancellationTokenSource job)
    {
        _lost = true;
        await job.CancelAsync().ConfigureAwait(false);
    }
}
