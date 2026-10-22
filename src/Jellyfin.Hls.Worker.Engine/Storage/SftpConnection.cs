using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// A directory of an SSH server. Every session is an SSH connection of its own, so the lanes of an upload do not wait on each other.
/// </summary>
internal sealed class SftpConnection : StorageConnection
{
    private readonly ILogger _logger;
    private int _loggedHostKey;

    public SftpConnection(ConnectionSettings settings, ILogger logger)
        : base(settings)
    {
        _logger = logger;
    }

    public override async Task<IStorageSession> OpenAsync(CancellationToken cancellationToken)
    {
        AuthenticationMethod method;
        if (Settings.PrivateKeyPath.Length > 0)
        {
            var key = Settings.PrivateKeyPassphrase.Length > 0
                ? new PrivateKeyFile(Settings.PrivateKeyPath, Settings.PrivateKeyPassphrase)
                : new PrivateKeyFile(Settings.PrivateKeyPath);
            method = new PrivateKeyAuthenticationMethod(Settings.User, key);
        }
        else
        {
            method = new PasswordAuthenticationMethod(Settings.User, Settings.Password);
        }

        // A server that stops answering fails the request instead of holding the upload, and the cleanup after a cancellation.
        var client = new SftpClient(new ConnectionInfo(Settings.Host, Settings.Port, Settings.User, method) { Timeout = TimeSpan.FromSeconds(30) })
        {
            OperationTimeout = TimeSpan.FromSeconds(60)
        };
        client.HostKeyReceived += (_, e) => e.CanTrust = TrustHostKey(e.FingerPrintSHA256);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var root = Settings.RemoteRoot.Length > 1 ? Settings.RemoteRoot.TrimEnd('/') : Settings.RemoteRoot;
            if (!await client.ExistsAsync(root, cancellationToken).ConfigureAwait(false))
            {
                throw new DirectoryNotFoundException("连接 " + Settings.Name + " 的 RemoteRoot 不存在：" + root);
            }

            return new Session(client, root == "/" ? string.Empty : root);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // The fingerprint as ssh-keygen prints it, "SHA256:" and base64 without padding.
    private bool TrustHostKey(string fingerprint)
    {
        var expected = Settings.HostKey.Trim();
        if (expected.Length == 0)
        {
            if (Interlocked.Exchange(ref _loggedHostKey, 1) == 0)
            {
                _logger.LogWarning("连接 {Name} 未配置 HostKey，接受主机密钥 SHA256:{Fingerprint}；建议将其填入 HostKey", Settings.Name, fingerprint);
            }

            return true;
        }

        if (expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            expected = expected[7..];
        }

        if (string.Equals(expected.TrimEnd('='), fingerprint.TrimEnd('='), StringComparison.Ordinal))
        {
            return true;
        }

        _logger.LogError("连接 {Name} 的主机密钥为 SHA256:{Fingerprint}，与 HostKey 不符，拒绝连接", Settings.Name, fingerprint);
        return false;
    }

    private sealed class Session(SftpClient client, string root) : IStorageSession
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

        public async Task PutAsync(string path, string localFile, CancellationToken cancellationToken)
        {
            var destination = root + "/" + path;
            await CreateDirectoryAsync(destination[..destination.LastIndexOf('/')], cancellationToken).ConfigureAwait(false);
            var temporary = GetTemporaryName(destination);
            try
            {
                var from = File.OpenRead(localFile);
                await using (from.ConfigureAwait(false))
                {
                    await client.UploadFileAsync(from, temporary, cancellationToken).ConfigureAwait(false);
                }

                // SFTP version 3 does not rename onto an existing file; a file left by an earlier attempt goes first.
                if (await client.ExistsAsync(destination, cancellationToken).ConfigureAwait(false))
                {
                    await client.DeleteFileAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                await client.RenameFileAsync(temporary, destination, cancellationToken).ConfigureAwait(false);
                temporary = null;
            }
            finally
            {
                if (temporary is not null && client.IsConnected)
                {
                    try
                    {
                        await client.DeleteFileAsync(temporary, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (SshException)
                    {
                    }
                }
            }
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
            => client.ExistsAsync(root + "/" + path, cancellationToken);

        public async Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await client.DeleteFileAsync(root + "/" + path, cancellationToken).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
            }
        }

        public void Dispose() => client.Dispose();

        // mkdir -p below the root; the root itself has to exist, like the directory of a local connection.
        private async Task CreateDirectoryAsync(string directory, CancellationToken cancellationToken)
        {
            if (directory.Length <= root.Length || _directories.Contains(directory))
            {
                return;
            }

            await CreateDirectoryAsync(directory[..directory.LastIndexOf('/')], cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await client.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
                }
                catch (SshException)
                {
                    // Another lane may have created it in the meantime.
                    if (!await client.ExistsAsync(directory, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }
                }
            }

            _directories.Add(directory);
        }
    }
}
