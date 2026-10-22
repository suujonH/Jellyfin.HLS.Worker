using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// The root of the HLS store the worker writes to: a directory of a connection, and the name the server knows it by.
/// </summary>
public sealed class HlsTarget
{
    /// <summary>The marker in the root that tells the server the store is an HLS store.</summary>
    public const string MarkerName = ".jellyfin-hls";

    /// <summary>How many files of a batch are written at the same time.</summary>
    public const int Lanes = 4;

    private const int Attempts = 3;

    private readonly string _prefix;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsTarget"/> class.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="settings">The directory in the connection.</param>
    /// <param name="logger">The logger.</param>
    public HlsTarget(StorageConnection connection, HlsTargetSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);
        Connection = connection;
        var path = settings.Path.Trim('/');
        _prefix = path.Length == 0 ? string.Empty : path + "/";
        Base = settings.GetBase(connection.Settings);
        _logger = logger;
    }

    /// <summary>Gets the connection.</summary>
    public StorageConnection Connection { get; }

    /// <summary>Gets the root as the server names it.</summary>
    public string Base { get; }

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
    /// Writes files below the root, <see cref="Lanes"/> at a time, each in a session of its own; a transient failure is tried again.
    /// </summary>
    /// <param name="files">The files: the path below the root, with forward slashes, and the file to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task UploadAsync(IReadOnlyList<(string Path, string LocalFile)> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var next = -1;
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lanes = Enumerable.Range(0, Math.Min(Lanes, files.Count)).Select(_ => Task.Run(
            async () =>
            {
                using var lane = new Lane();
                try
                {
                    int index;
                    while ((index = Interlocked.Increment(ref next)) < files.Count)
                    {
                        var (path, localFile) = files[index];
                        await PutAsync(lane, _prefix + path, localFile, failed.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // The other lanes stop too; the first error is the one reported.
                    await failed.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            },
            failed.Token)).ToList();
        try
        {
            await Task.WhenAll(lanes).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            throw lanes.Select(l => l.Exception?.InnerException).FirstOrDefault(e => e is not null and not OperationCanceledException)
                ?? new OperationCanceledException();
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
