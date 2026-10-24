using System.IO.Compression;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// Writes files into the folder of a library through the server, in chunks: the worker never writes the folder itself.
/// </summary>
public sealed class LibraryWriter
{
    /// <summary>The size of a chunk, below the limits of the server and of the usual reverse proxies.</summary>
    public const int ChunkSize = 16 << 20;

    private const int ChunkAttempts = 3;

    private readonly ServerClient _server;
    private readonly Func<Guid> _workerId;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWriter"/> class.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="workerId">Gets the id of the worker, which the server records with each file.</param>
    /// <param name="logger">The logger.</param>
    public LibraryWriter(ServerClient server, Func<Guid> workerId, ILogger logger)
    {
        _server = server;
        _workerId = workerId;
        _logger = logger;
    }

    /// <summary>
    /// Writes a file into a folder of a library, replacing the one there.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="location">The folder of the library; <c>null</c> for the only one.</param>
    /// <param name="relative">The path in the folder, with forward slashes.</param>
    /// <param name="localFile">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="sent">Told the bytes sent so far, a chunk at a time.</param>
    /// <param name="operationId">The operation that holds the file; it is put in place only while it does.</param>
    /// <param name="content">The hash and size of the media content the file is; the server links the library item to it.</param>
    /// <returns>A task.</returns>
    public Task PutFileAsync(
        Guid libraryId,
        string? location,
        string relative,
        string localFile,
        CancellationToken cancellationToken,
        Action<long>? sent = null,
        string? operationId = null,
        (string Sha256, long Length)? content = null)
        => UploadAsync(libraryId, location, relative, localFile, null, cancellationToken, sent, operationId, content);

    /// <summary>
    /// Writes a .strm file into a folder of a library with its TrueCache entry: the server puts the entry in place, then the .strm.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="location">The folder of the library; <c>null</c> for the only one.</param>
    /// <param name="relativeShortcut">The path of the .strm file in the folder, with forward slashes.</param>
    /// <param name="entryDirectory">The entry, made by the TrueCache tool.</param>
    /// <param name="target">What the .strm file refers to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="operationId">The operation that holds the .strm file; it is put in place only while it does.</param>
    /// <param name="content">The hash and size of the media content the target is; the server links the library item to it.</param>
    /// <returns>A task.</returns>
    public async Task PutShortcutAsync(
        Guid libraryId,
        string? location,
        string relativeShortcut,
        string entryDirectory,
        string target,
        CancellationToken cancellationToken,
        string? operationId = null,
        (string Sha256, long Length)? content = null)
    {
        var zip = Path.TrimEndingDirectorySeparator(entryDirectory) + ".zip";
        try
        {
            ZipFile.CreateFromDirectory(entryDirectory, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            await UploadAsync(libraryId, location, relativeShortcut, zip, target, cancellationToken, operationId: operationId, content: content).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    private async Task UploadAsync(
        Guid libraryId,
        string? location,
        string relative,
        string localFile,
        string? shortcutTarget,
        CancellationToken cancellationToken,
        Action<long>? sent = null,
        string? operationId = null,
        (string Sha256, long Length)? content = null)
    {
        var length = new FileInfo(localFile).Length;
        var id = await _server.StartUploadAsync(libraryId, new LibraryUploadStart(location, relative, length, _workerId()), cancellationToken).ConfigureAwait(false);
        try
        {
            var file = File.OpenRead(localFile);
            await using (file.ConfigureAwait(false))
            {
                var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(length, 1))];
                for (long offset = 0; offset < length;)
                {
                    file.Position = offset;
                    var count = 0;
                    int read;
                    while (count < buffer.Length && (read = await file.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        count += read;
                    }

                    if (count == 0)
                    {
                        throw new InvalidDataException("文件在上传期间变短了：" + localFile);
                    }

                    await PutChunkAsync(id, offset, buffer.AsMemory(0, count), relative, cancellationToken).ConfigureAwait(false);
                    offset += count;
                    sent?.Invoke(offset);
                }
            }

            await _server.CompleteUploadAsync(id, shortcutTarget, cancellationToken, operationId, content).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _server.AbortUploadAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerException or HttpRequestException or IOException)
            {
                // The server drops an upload left idle.
            }

            throw;
        }
    }

    // A chunk sent again replaces what was written of it.
    private async Task PutChunkAsync(string id, long offset, ReadOnlyMemory<byte> chunk, string relative, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _server.PutChunkAsync(id, offset, chunk, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < ChunkAttempts && !cancellationToken.IsCancellationRequested && (ServerException.IsTransient(ex) || ex is HttpRequestException))
            {
                _logger.LogWarning("写入媒体库文件 {Path} 暂时失败，第 {Attempt} 次重试：{Message}", relative, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
