using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// A session of a connection: one lane of an upload. A session is used by one caller at a time.
/// </summary>
public interface IStorageSession : IDisposable
{
    /// <summary>
    /// Writes a file; it appears complete or not at all, and replaces a file of the same name.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes.</param>
    /// <param name="localFile">The file to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task PutAsync(string path, string localFile, CancellationToken cancellationToken);

    /// <summary>
    /// Gets whether a file exists.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it exists.</returns>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a file; one that does not exist is no error.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// A connection to storage, shared by all jobs.
/// </summary>
public abstract class StorageConnection : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StorageConnection"/> class.
    /// </summary>
    /// <param name="settings">The settings of the connection.</param>
    protected StorageConnection(ConnectionSettings settings)
    {
        Settings = settings;
    }

    /// <summary>Gets the settings of the connection.</summary>
    public ConnectionSettings Settings { get; }

    /// <summary>
    /// Creates the connection the settings describe.
    /// </summary>
    /// <param name="settings">The settings, validated.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The connection.</returns>
    public static StorageConnection Create(ConnectionSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Type switch
        {
            ConnectionType.S3 => new S3Connection(settings),
            ConnectionType.Sftp => new SftpConnection(settings, logger),
            ConnectionType.Local => new LocalConnection(settings),
            _ => throw new ArgumentException("不支持的连接类型：" + settings.Type)
        };
    }

    /// <summary>
    /// Gets whether a storage error is worth trying again: the network, a timeout, or an error of the service.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns>Whether it may pass.</returns>
    public static bool IsTransient(Exception exception) => exception switch
    {
        AmazonServiceException service => service.StatusCode is 0 or HttpStatusCode.TooManyRequests || (int)service.StatusCode >= 500,
        AmazonClientException or HttpRequestException or SocketException or TimeoutException => true,
        SshConnectionException or SshOperationTimeoutException or ProxyException => true,
        SshAuthenticationException or SftpPermissionDeniedException or SftpPathNotFoundException => false,
        UnauthorizedAccessException or DirectoryNotFoundException => false,
        IOException => true,
        _ => false
    };

    /// <summary>
    /// Opens a session.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The session.</returns>
    public abstract Task<IStorageSession> OpenAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources of the connection.
    /// </summary>
    /// <param name="disposing">Whether it is called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// Gets the media type of a file of a rendition.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The media type.</returns>
    protected static string GetContentType(string path) => System.IO.Path.GetExtension(path) switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".mp4" => "video/mp4",
        ".m4s" => "video/iso.segment",
        ".vtt" => "text/vtt",
        ".jpg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Gets a name next to a file for writing it first, unique to the writer.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The temporary name.</returns>
    protected static string GetTemporaryName(string path) => path + ".part-" + Guid.NewGuid().ToString("N")[..12];
}
