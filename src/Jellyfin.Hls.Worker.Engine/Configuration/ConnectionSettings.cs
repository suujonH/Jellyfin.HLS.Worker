using System.Text.Json;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>How a connection reaches its storage.</summary>
public enum ConnectionType
{
    /// <summary>An S3 compatible bucket.</summary>
    S3 = 0,

    /// <summary>A directory of an SSH server.</summary>
    Sftp = 1,

    /// <summary>A directory of the Jellyfin server, which only the server reaches: it is declared, never written by the worker.</summary>
    Server = 2,

    /// <summary>A directory of this machine, such as a mounted share.</summary>
    Worker = 3
}

/// <summary>
/// A connection to storage. <see cref="Type"/> selects which of the other fields are used.
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>The numbers of parts that can be uploaded at the same time.</summary>
    public static readonly IReadOnlyList<int> PartConcurrencies = [1, 2, 5, 10, 20, 50, 100];

    /// <summary>Gets or sets the id the schemes refer to the connection by.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name the connection is shown by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how the connection reaches its storage.</summary>
    public ConnectionType Type { get; set; }

    /// <summary>Gets or sets the address of the S3 service.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the region the requests are signed for.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Gets or sets the bucket, by the name the server knows it.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory of the bucket that the connection is, with forward slashes; empty for the whole bucket.</summary>
    public string BucketPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the access key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Gets or sets how many parts of a file put in parts are uploaded at the same time, one of <see cref="PartConcurrencies"/>; with 1 they go in order.</summary>
    public int PartConcurrency { get; set; } = 1;

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

    /// <summary>Gets or sets the absolute directory of the Jellyfin server that a connection of the server is.</summary>
    public string ServerPath { get; set; } = string.Empty;

    /// <summary>
    /// Checks the fields the type uses.
    /// </summary>
    /// <exception cref="ArgumentException">The connection is not valid.</exception>
    public void Validate()
    {
        if (Id.Equals(Guid.Empty))
        {
            throw new ArgumentException("连接 " + Name + " 没有 Id。");
        }

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

                var bucketPath = BucketPath.Trim('/');
                if (bucketPath.Length > 0 && !StoragePath.IsValidRelative(bucketPath))
                {
                    throw new ArgumentException(name + "：BucketPath 应为桶中的相对路径，使用 /。");
                }

                if (!PartConcurrencies.Contains(PartConcurrency))
                {
                    throw new ArgumentException(name + "：PartConcurrency 应为 " + string.Join("、", PartConcurrencies) + " 之一。");
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

                break;
            case ConnectionType.Server:
                // The server may run on another system than the worker, so the path is taken in either style.
                if (!StoragePath.IsAbsoluteServerPath(ServerPath))
                {
                    throw new ArgumentException(name + "：服务器上的路径应为绝对路径，例如 /srv/media 或 D:\\Media。");
                }

                break;
            case ConnectionType.Worker:
                if (string.IsNullOrWhiteSpace(LocalRoot) || !Path.IsPathFullyQualified(LocalRoot))
                {
                    throw new ArgumentException(name + "：LocalRoot 应为本机绝对路径。");
                }

                break;
            default:
                throw new ArgumentException(name + "：Type 应为 S3、Sftp、Server 或 Worker。");
        }
    }

    /// <summary>
    /// Gets the key in the bucket of a path of an S3 connection: the path below <see cref="BucketPath"/>.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes; empty for the connection itself.</param>
    /// <returns>The key.</returns>
    public string GetS3Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = BucketPath.Trim('/');
        path = path.Trim('/');
        return root.Length == 0 ? path : path.Length == 0 ? root : root + "/" + path;
    }

    /// <summary>
    /// Gets a path of an S3 connection as the server names it: <c>s3://bucket/key</c>.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes; empty for the connection itself.</param>
    /// <returns>The address.</returns>
    public string GetS3Address(string path)
    {
        var key = GetS3Key(path);
        return "s3://" + Bucket + (key.Length == 0 ? string.Empty : "/" + key);
    }

    /// <summary>
    /// Gets a path of a connection that is not S3 as a path: of the SSH server for SFTP, which is declared as a path of the
    /// Jellyfin server as it is (SFTP reaches the server), of the server, or of this machine.
    /// </summary>
    /// <param name="path">The path in the connection, with forward slashes; empty for the connection itself.</param>
    /// <returns>The path.</returns>
    /// <exception cref="InvalidOperationException">The connection is S3.</exception>
    public string GetServerPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = path.Trim('/');
        return Type switch
        {
            ConnectionType.Sftp => StoragePath.JoinServer(RemoteRoot, path),
            ConnectionType.Server => StoragePath.JoinServer(ServerPath, path),
            ConnectionType.Worker => StoragePath.JoinServer(LocalRoot, path),
            _ => throw new InvalidOperationException("S3 连接没有路径，应使用 s3:// 地址。")
        };
    }

    /// <summary>
    /// Gets whether another connection is of the same place: the same storage and root. Its name, credentials and the like may differ.
    /// </summary>
    /// <param name="other">The other connection.</param>
    /// <returns>Whether files go to the same place in both.</returns>
    public bool IsSamePlace(ConnectionSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Type == other.Type
            && string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal)
            && string.Equals(Bucket, other.Bucket, StringComparison.Ordinal)
            && string.Equals(BucketPath, other.BucketPath, StringComparison.Ordinal)
            && string.Equals(Host, other.Host, StringComparison.Ordinal)
            && Port == other.Port
            && string.Equals(RemoteRoot, other.RemoteRoot, StringComparison.Ordinal)
            && string.Equals(LocalRoot, other.LocalRoot, StringComparison.Ordinal)
            && string.Equals(ServerPath, other.ServerPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes a copy of the connection whose root is a directory below its own.
    /// </summary>
    /// <param name="path">The directory in the connection, with forward slashes.</param>
    /// <param name="id">The id of the copy.</param>
    /// <param name="name">The name of the copy.</param>
    /// <returns>The copy.</returns>
    public ConnectionSettings Below(string path, Guid id, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        var copy = JsonSerializer.Deserialize<ConnectionSettings>(JsonSerializer.Serialize(this, WorkerSettings.Json), WorkerSettings.Json)!;
        copy.Id = id;
        copy.Name = name;
        path = path.Trim('/');
        switch (Type)
        {
            case ConnectionType.S3:
                copy.BucketPath = GetS3Key(path);
                break;
            case ConnectionType.Sftp:
                copy.RemoteRoot = StoragePath.JoinServer(RemoteRoot, path);
                break;
            case ConnectionType.Server:
                copy.ServerPath = StoragePath.JoinServer(ServerPath, path);
                break;
            default:
                copy.LocalRoot = StoragePath.JoinServer(LocalRoot, path);
                break;
        }

        return copy;
    }
}
