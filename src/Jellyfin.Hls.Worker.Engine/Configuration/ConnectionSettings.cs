namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>How a connection reaches its storage.</summary>
public enum ConnectionType
{
    /// <summary>An S3 compatible bucket.</summary>
    S3 = 0,

    /// <summary>A directory of an SSH server.</summary>
    Sftp = 1,

    /// <summary>A directory of this machine, such as a mounted share.</summary>
    Local = 2
}

/// <summary>
/// A connection to storage. <see cref="Type"/> selects which of the other fields are used.
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>Gets or sets the name the connection is referred to by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how the connection reaches its storage.</summary>
    public ConnectionType Type { get; set; }

    /// <summary>Gets or sets the address of the S3 service.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the region the requests are signed for.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Gets or sets the bucket, by the name the server knows it.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the access key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the SSH host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the SSH port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// Gets or sets the SHA-256 fingerprint of the host key, as <c>ssh-keygen -lf</c> prints it; empty accepts any key and logs it.
    /// </summary>
    public string HostKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the SSH user.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets the password of the SSH user, when no key is used.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the private key of the SSH user, in OpenSSH or PEM format.</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the passphrase of the private key, if it has one.</summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute directory of the SSH server that the connection is.</summary>
    public string RemoteRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory of this machine that the connection is.</summary>
    public string LocalRoot { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path at which the Jellyfin server sees <see cref="RemoteRoot"/> or <see cref="LocalRoot"/>;
    /// empty for a local connection on the machine of the server.
    /// </summary>
    public string ServerRoot { get; set; } = string.Empty;

    /// <summary>
    /// Checks the fields the type uses.
    /// </summary>
    /// <exception cref="ArgumentException">The connection is not valid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
        {
            throw new ArgumentException("连接的 Name 长度应为 1–64。");
        }

        var name = "连接 " + Name;
        switch (Type)
        {
            case ConnectionType.S3:
                if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                {
                    throw new ArgumentException(name + "：Endpoint 应为 http(s) 地址。");
                }

                if (!StoragePath.IsValidBucket(Bucket) || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
                {
                    throw new ArgumentException(name + "：需要有效的 Bucket、AccessKey 和 SecretKey。");
                }

                break;
            case ConnectionType.Sftp:
                if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(User))
                {
                    throw new ArgumentException(name + "：需要 Host、User，Port 应为 1–65535。");
                }

                if (Password.Length == 0 && PrivateKeyPath.Length == 0)
                {
                    throw new ArgumentException(name + "：需要 Password 或 PrivateKeyPath。");
                }

                if (!RemoteRoot.StartsWith('/'))
                {
                    throw new ArgumentException(name + "：RemoteRoot 应为以 / 开头的绝对路径。");
                }

                RequireServerRoot(name, required: true);
                break;
            case ConnectionType.Local:
                if (string.IsNullOrWhiteSpace(LocalRoot) || !Path.IsPathFullyQualified(LocalRoot))
                {
                    throw new ArgumentException(name + "：LocalRoot 应为本机绝对路径。");
                }

                RequireServerRoot(name, required: false);
                break;
            default:
                throw new ArgumentException(name + "：Type 应为 S3、Sftp 或 Local。");
        }
    }

    // The server may run on another system than the worker, so its path is taken in either style.
    private void RequireServerRoot(string name, bool required)
    {
        if (ServerRoot.Length == 0 && !required)
        {
            return;
        }

        if (!StoragePath.IsAbsoluteServerPath(ServerRoot))
        {
            throw new ArgumentException(name + "：ServerRoot 应为 Jellyfin 服务器上的绝对路径，例如 /mnt/hls 或 D:\\HLS。");
        }
    }
}

/// <summary>
/// Where the renditions are written: a directory of a connection.
/// </summary>
public sealed class HlsTargetSettings
{
    /// <summary>Gets or sets the name of the connection.</summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory in the connection, with forward slashes; empty for the connection itself.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Checks the directory.
    /// </summary>
    /// <param name="connection">The connection it names.</param>
    /// <exception cref="ArgumentException">The directory is not valid.</exception>
    public void Validate(ConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var path = Path.Trim('/');
        if (path.Length > 0 && !StoragePath.IsValidRelative(path))
        {
            throw new ArgumentException("Hls.Path 应为相对路径，使用 /，各段不能为空、不能以 . 或空格结尾。");
        }
    }

    /// <summary>
    /// Gets the root of the HLS store as the server names it: <c>s3://bucket/path</c> or a path of the server.
    /// </summary>
    /// <param name="connection">The connection it names.</param>
    /// <returns>The root.</returns>
    public string GetBase(ConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var path = Path.Trim('/');
        return connection.Type switch
        {
            ConnectionType.S3 => "s3://" + connection.Bucket + (path.Length == 0 ? string.Empty : "/" + path),
            ConnectionType.Sftp => StoragePath.JoinServer(connection.ServerRoot, path),
            _ => StoragePath.JoinServer(connection.ServerRoot.Length == 0 ? connection.LocalRoot : connection.ServerRoot, path)
        };
    }
}
