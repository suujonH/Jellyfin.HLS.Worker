using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>What a test of a remote directory found.</summary>
internal enum TestStatus
{
    /// <summary>Not tested since it was changed.</summary>
    None = 0,

    /// <summary>Being tested.</summary>
    Testing = 1,

    /// <summary>The worker can write it.</summary>
    Ok = 2,

    /// <summary>The worker cannot write it.</summary>
    Failed = 3,

    /// <summary>A directory of the server, which the worker cannot test.</summary>
    NotTestable = 4
}

/// <summary>
/// A remote directory being edited. The schemes refer to it by its id, so a rename changes nothing else; a change of any field
/// drops what its last test found. Its type is chosen when it is made: another type is another directory.
/// </summary>
internal sealed partial class ConnectionViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionViewModel"/> class.
    /// </summary>
    /// <param name="model">The settings of the connection.</param>
    public ConnectionViewModel(ConnectionSettings model)
    {
        Model = model;
    }

    public ConnectionSettings Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsFailed), nameof(IsTesting), nameof(IsNotTestable), nameof(StatusText))]
    public partial TestStatus Status { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool IsOk => Status == TestStatus.Ok;

    public bool IsFailed => Status == TestStatus.Failed;

    public bool IsTesting => Status == TestStatus.Testing;

    public bool IsNotTestable => Status == TestStatus.NotTestable;

    public string StatusText => Status switch
    {
        TestStatus.Ok => "✓",
        TestStatus.Failed => "✗",
        TestStatus.Testing => "⟳",
        TestStatus.NotTestable => "—",
        _ => string.Empty
    };

    public string Name
    {
        get => Model.Name;
        set => Edit(Model.Name, value, v => Model.Name = v);
    }

    public ConnectionType Type => Model.Type;

    public string TypeLabel => Type switch
    {
        ConnectionType.S3 => "S3",
        ConnectionType.Sftp => "SFTP",
        ConnectionType.Server => "服务器目录",
        ConnectionType.Worker => "Worker目录",
        _ => Type.ToString()
    };

    public bool IsS3 => Type == ConnectionType.S3;

    public bool IsSftp => Type == ConnectionType.Sftp;

    public bool IsServer => Type == ConnectionType.Server;

    public bool IsWorker => Type == ConnectionType.Worker;

    public string Endpoint { get => Model.Endpoint; set => Edit(Model.Endpoint, value, v => Model.Endpoint = v); }

    public string Region { get => Model.Region; set => Edit(Model.Region, value, v => Model.Region = v); }

    public string Bucket { get => Model.Bucket; set => Edit(Model.Bucket, value, v => Model.Bucket = v); }

    public string BucketPath { get => Model.BucketPath; set => Edit(Model.BucketPath, value, v => Model.BucketPath = v); }

    public string AccessKey { get => Model.AccessKey; set => Edit(Model.AccessKey, value, v => Model.AccessKey = v); }

    public string SecretKey { get => Model.SecretKey; set => Edit(Model.SecretKey, value, v => Model.SecretKey = v); }

    public int PartConcurrency { get => Model.PartConcurrency; set => Edit(Model.PartConcurrency, value, v => Model.PartConcurrency = v); }

    public string Host { get => Model.Host; set => Edit(Model.Host, value, v => Model.Host = v); }

    public int Port { get => Model.Port; set => Edit(Model.Port, value, v => Model.Port = v); }

    public string HostKey { get => Model.HostKey; set => Edit(Model.HostKey, value, v => Model.HostKey = v); }

    public string User { get => Model.User; set => Edit(Model.User, value, v => Model.User = v); }

    public string Password { get => Model.Password; set => Edit(Model.Password, value, v => Model.Password = v); }

    public string PrivateKeyPath { get => Model.PrivateKeyPath; set => Edit(Model.PrivateKeyPath, value, v => Model.PrivateKeyPath = v); }

    public string PrivateKeyPassphrase { get => Model.PrivateKeyPassphrase; set => Edit(Model.PrivateKeyPassphrase, value, v => Model.PrivateKeyPassphrase = v); }

    public string RemoteRoot { get => Model.RemoteRoot; set => Edit(Model.RemoteRoot, value, v => Model.RemoteRoot = v); }

    public string LocalRoot { get => Model.LocalRoot; set => Edit(Model.LocalRoot, value, v => Model.LocalRoot = v); }

    public string ServerPath { get => Model.ServerPath; set => Edit(Model.ServerPath, value, v => Model.ServerPath = v); }

    // A field changed: what the last test found no longer holds.
    private bool Edit<T>(T current, T value, Action<T> apply, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(current, value, apply, propertyName!))
        {
            return false;
        }

        if (Status != TestStatus.Testing)
        {
            Status = TestStatus.None;
            StatusMessage = null;
        }

        return true;
    }
}
