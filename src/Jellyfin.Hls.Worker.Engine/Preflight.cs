using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker;

/// <summary>
/// Checks a remote directory without writing to it.
/// </summary>
public static class Preflight
{
    /// <summary>
    /// Checks that a connection is reached and its credentials are taken; nothing is written.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="logger">Where the result is reported.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="NotSupportedException">The connection is a directory of the server, which only the server reaches.</exception>
    public static async Task CheckConnectionAsync(ConnectionSettings connection, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        if (connection.Type == ConnectionType.Server)
        {
            throw new NotSupportedException("服务器目录由服务器访问，Worker 无法测试。");
        }

        using var storage = StorageConnection.Create(connection, logger);
        await storage.CheckAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("连接 {Name}（{Type}）可用", connection.Name, connection.Type);
    }
}
