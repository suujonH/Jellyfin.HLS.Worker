using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// A directory of this machine. The directory has to exist: creating it would hide a share that is not mounted.
/// </summary>
internal sealed class LocalConnection : StorageConnection
{
    public LocalConnection(ConnectionSettings settings)
        : base(settings)
    {
    }

    public override Task<IStorageSession> OpenAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Settings.LocalRoot))
        {
            throw new DirectoryNotFoundException("连接 " + Settings.Name + " 的 LocalRoot 不存在：" + Settings.LocalRoot);
        }

        return Task.FromResult<IStorageSession>(new Session(Settings.LocalRoot));
    }

    private sealed class Session(string root) : IStorageSession
    {
        public async Task PutAsync(string path, string localFile, CancellationToken cancellationToken)
        {
            var destination = Resolve(path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            var temporary = GetTemporaryName(destination);
            try
            {
                var from = File.OpenRead(localFile);
                await using (from.ConfigureAwait(false))
                {
                    var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                    await using (to.ConfigureAwait(false))
                    {
                        await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
                        await to.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                File.Move(temporary, destination, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(File.Exists(Resolve(path)));

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var file = Resolve(path);
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        // The path was checked against the rules of the server, so it stays below the root.
        private string Resolve(string path) => System.IO.Path.Combine(root, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
