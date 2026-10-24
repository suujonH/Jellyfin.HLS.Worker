using System.Collections.Concurrent;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// A directory of a connection the worker writes renditions to.
/// </summary>
public sealed class HlsTarget
{
    /// <summary>The marker in the root that tells the server the store is an HLS store.</summary>
    public const string MarkerName = ".jellyfin-hls";

    private const int Attempts = 3;

    private readonly string _prefix;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsTarget"/> class.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="path">The directory in the connection, with forward slashes; empty for the connection itself.</param>
    /// <param name="label">How the directory is shown in the log.</param>
    /// <param name="logger">The logger.</param>
    public HlsTarget(StorageConnection connection, string path, string label, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(path);
        Connection = connection;
        path = path.Trim('/');
        _prefix = path.Length == 0 ? string.Empty : path + "/";
        Label = label;
        _logger = logger;
    }

    /// <summary>Gets the connection.</summary>
    public StorageConnection Connection { get; }

    /// <summary>Gets how the directory is shown in the log.</summary>
    public string Label { get; }

    /// <summary>
    /// Gets the path in the connection of a file below the root.
    /// </summary>
    /// <param name="path">The path below the root, with forward slashes.</param>
    /// <returns>The path in the connection.</returns>
    public string GetPath(string path) => _prefix + path;

    /// <summary>
    /// Writes a test file to the root and deletes it again, then writes the marker if it is missing.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the marker was written.</returns>
    public async Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        var probe = Path.Combine(Path.GetTempPath(), "jellyfin-hls-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(probe, "jellyfin-hls", cancellationToken).ConfigureAwait(false);
        try
        {
            using var session = await Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var test = _prefix + ".jellyfin-hls-check-" + Guid.NewGuid().ToString("N");
            await session.PutAsync(test, probe, cancellationToken).ConfigureAwait(false);
            await session.DeleteAsync(test, cancellationToken).ConfigureAwait(false);
            if (await session.ExistsAsync(_prefix + MarkerName, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await session.PutAsync(_prefix + MarkerName, probe, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <summary>
    /// Writes files below the root, in order, as many at a time as the limiter lets, over the sessions it opened; a transient
    /// failure is tried again.
    /// </summary>
    /// <param name="files">The files: the path below the root, with forward slashes, and the file to write.</param>
    /// <param name="limiter">The places of the files written at the same time, shared by every upload of the worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="uploaded">Told of each file once it is written or failed: its path, the file, whether it was written, and why not.</param>
    /// <param name="done">The paths in the connection written before; one that is still there is not written again.</param>
    /// <param name="skipped">Told of each file not written again.</param>
    /// <returns>A task.</returns>
    public async Task UploadAsync(
        IReadOnlyList<(string Path, string LocalFile)> files,
        UploadLimiter limiter,
        CancellationToken cancellationToken,
        Action<string, string, bool, string?>? uploaded = null,
        IReadOnlySet<string>? done = null,
        Action? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(limiter);
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idle = new ConcurrentBag<Lane>();
        var running = new List<Task>();
        try
        {
            foreach (var (path, localFile) in files)
            {
                try
                {
                    await limiter.WaitAsync(failed.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A file failed, or the upload was stopped: no other file starts.
                    break;
                }

                running.Add(Task.Run(() => UploadOneAsync(path, localFile, limiter, idle, failed, uploaded, done, skipped), CancellationToken.None));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            throw running.Select(t => t.Exception?.InnerException).FirstOrDefault(e => e is not null and not OperationCanceledException)
                ?? new OperationCanceledException();
        }
        finally
        {
            while (idle.TryTake(out var lane))
            {
                lane.Dispose();
            }
        }
    }

    // A file in a place of the limiter, over an idle session or a new one, which goes back to the idle ones when it is done with.
    private async Task UploadOneAsync(
        string path,
        string localFile,
        UploadLimiter limiter,
        ConcurrentBag<Lane> idle,
        CancellationTokenSource failed,
        Action<string, string, bool, string?>? uploaded,
        IReadOnlySet<string>? done,
        Action? skipped)
    {
        var lane = idle.TryTake(out var free) ? free : new Lane();
        try
        {
            if (done is not null && done.Contains(_prefix + path) && await IsThereAsync(lane, _prefix + path, failed.Token).ConfigureAwait(false))
            {
                skipped?.Invoke();
            }
            else
            {
                try
                {
                    await PutAsync(lane, _prefix + path, localFile, failed.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    uploaded?.Invoke(path, localFile, false, ex.Message);
                    throw;
                }

                uploaded?.Invoke(path, localFile, true, null);
            }

            idle.Add(lane);
        }
        catch
        {
            // The other files stop too; the first error is the one reported.
            lane.Dispose();
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            limiter.Release();
        }
    }

    // A file written before that cannot be checked is written again.
    private async Task<bool> IsThereAsync(Lane lane, string path, CancellationToken cancellationToken)
    {
        try
        {
            lane.Session ??= await Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await lane.Session.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && StorageConnection.IsTransient(ex))
        {
            lane.Dispose();
            return false;
        }
    }

    // A session that failed is dropped; the next attempt opens a new one.
    private async Task PutAsync(Lane lane, string path, string localFile, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                lane.Session ??= await Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await lane.Session.PutAsync(path, localFile, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && !cancellationToken.IsCancellationRequested && StorageConnection.IsTransient(ex))
            {
                _logger.LogWarning("上传 {Path} 暂时失败，第 {Attempt} 次重试：{Message}", path, attempt, ex.Message);
                lane.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class Lane : IDisposable
    {
        public IStorageSession? Session { get; set; }

        public void Dispose()
        {
            Session?.Dispose();
            Session = null;
        }
    }
}
