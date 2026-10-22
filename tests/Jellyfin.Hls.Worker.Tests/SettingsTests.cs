using Jellyfin.Hls.Worker.Configuration;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Load_ResolvesRelativePathsAgainstTheFile()
    {
        var path = Write("""
            {
              "Server": "http://jellyfin:8096/jf",
              "ApiKey": "key",
              "WorkDirectory": "work",
              "Ffmpeg": "bin/ffmpeg",
              "Connections": [ { "Name": "hls", "Type": "Local", "LocalRoot": "hls" } ],
              "Hls": { "Connection": "hls", "Path": "media" }
            }
            """);

        var settings = WorkerSettings.Load(path);

        Assert.Equal(Path.Combine(_directory, "work"), settings.WorkDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "bin", "ffmpeg")), settings.Ffmpeg);
        Assert.Equal("ffprobe", settings.Ffprobe);
        Assert.Equal(Path.Combine(_directory, "hls"), settings.Connections[0].LocalRoot);
    }

    [Theory]
    [InlineData("""{ "Server": "ftp://x", "ApiKey": "k" }""", "Server")]
    [InlineData("""{ "ApiKey": "k", "Concurrency": 0 }""", "Concurrency")]
    [InlineData("""{ "ApiKey": "k", "Encoder": "libx265" }""", "Encoder")]
    [InlineData("""{ "ApiKey": "k", "Encoder": "h264_vaapi" }""", "HardwareDevice")]
    [InlineData("""{ "ApiKey": "k", "Unknown": 1 }""", "配置文件无效")]
    [InlineData("""{ "ApiKey": "k", "Hls": { "Connection": "missing" } }""", "missing")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "AccessKey": "a", "SecretKey": "s" }, { "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "AccessKey": "a", "SecretKey": "s" } ] }""", "重复")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Name": "a", "Type": "Sftp", "Host": "h", "User": "u", "Password": "p", "RemoteRoot": "/srv" } ] }""", "ServerRoot")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b/c", "AccessKey": "a", "SecretKey": "s" } ] }""", "Bucket")]
    public void Load_RefusesInvalidSettings(string json, string expected)
    {
        var path = Write(json);

        var error = Assert.Throws<ArgumentException>(() => WorkerSettings.Load(path));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // Through Validate, as Load takes the key from JELLYFIN_HLS_API_KEY when it is set.
    [Fact]
    public void Read_KeepsTheFileAsItIs()
    {
        var path = Write("""
            { "WorkDirectory": "work", "Ffmpeg": "bin/ffmpeg", "Connections": [ { "Name": "x", "Type": "S3" } ] }
            """);

        var settings = WorkerSettings.Read(path);

        Assert.Equal("work", settings.WorkDirectory);
        Assert.Equal("bin/ffmpeg", settings.Ffmpeg);
        Assert.Empty(settings.ApiKey);
        Assert.Throws<ArgumentException>(() => WorkerSettings.Load(path));
    }

    [Fact]
    public void Read_TakesNullListsAsEmpty_AndNullTextsAsTheDefaults()
    {
        var path = Write("""
            { "ApiKey": "k", "Ffmpeg": null, "Connections": null, "Libraries": [ null ], "Hls": { "Connection": null } }
            """);

        var settings = WorkerSettings.Read(path);

        Assert.Empty(settings.Connections);
        Assert.Empty(settings.Libraries);
        Assert.Equal("ffmpeg", settings.Ffmpeg);
        Assert.Equal(string.Empty, settings.Hls!.Connection);

        path = Write("""
            { "ApiKey": "k", "Connections": [ { "Name": "s", "Type": "S3", "Region": null } ] }
            """);
        Assert.Equal("us-east-1", WorkerSettings.Read(path).Connections[0].Region);
    }

    [Fact]
    public void Validate_RequiresAnApiKey()
    {
        var error = Assert.Throws<ArgumentException>(() => new WorkerSettings().Validate());
        Assert.Contains("ApiKey", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RefusesAnHlsPathTheServerRefuses()
    {
        var path = Write("""
            { "ApiKey": "k", "Connections": [ { "Name": "a", "Type": "Local", "LocalRoot": "x" } ], "Hls": { "Connection": "a", "Path": "media/../x" } }
            """);

        Assert.Throws<ArgumentException>(() => WorkerSettings.Load(path));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var path = Path.Combine(_directory, "worker.json");
        new WorkerSettings
        {
            ApiKey = "key",
            Connections = [new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = "http://minio:9000", Bucket = "hls", AccessKey = "a", SecretKey = "s" }],
            Hls = new HlsTargetSettings { Connection = "s3", Path = "jellyfin" }
        }.Save(path);

        var settings = WorkerSettings.Load(path);

        Assert.Equal(ConnectionType.S3, settings.Connections[0].Type);
        Assert.Equal("s3://hls/jellyfin", settings.Hls!.GetBase(settings.GetConnection("s3")));
    }

    [Theory]
    [InlineData(ConnectionType.S3, "", "", "", "s3://bucket")]
    [InlineData(ConnectionType.S3, "", "", "/a/b/", "s3://bucket/a/b")]
    [InlineData(ConnectionType.Sftp, "", "/mnt/hls/", "a/b", "/mnt/hls/a/b")]
    [InlineData(ConnectionType.Sftp, "", "/", "a", "/a")]
    [InlineData(ConnectionType.Sftp, "", @"D:\HLS", "a/b", @"D:\HLS\a\b")]
    [InlineData(ConnectionType.Sftp, "", "D:\\", "a", @"D:\a")]
    [InlineData(ConnectionType.Sftp, "", @"\\nas\hls", "", @"\\nas\hls")]
    [InlineData(ConnectionType.Local, "/data/hls", "", "x", "/data/hls/x")]
    [InlineData(ConnectionType.Local, "/data/hls", "/srv/hls", "x", "/srv/hls/x")]
    public void GetBase_NamesTheRootAsTheServerSeesIt(ConnectionType type, string localRoot, string serverRoot, string path, string expected)
    {
        var connection = new ConnectionSettings { Name = "c", Type = type, Bucket = "bucket", LocalRoot = localRoot, ServerRoot = serverRoot };

        Assert.Equal(expected, new HlsTargetSettings { Connection = "c", Path = path }.GetBase(connection));
    }

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("a//b", false)]
    [InlineData("a/./b", false)]
    [InlineData("a/b.", false)]
    [InlineData("a/b ", false)]
    [InlineData(@"a\b", false)]
    [InlineData("c:/b", false)]
    public void IsValidRelative_FollowsTheServer(string path, bool expected)
        => Assert.Equal(expected, StoragePath.IsValidRelative(path));

    private string Write(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
