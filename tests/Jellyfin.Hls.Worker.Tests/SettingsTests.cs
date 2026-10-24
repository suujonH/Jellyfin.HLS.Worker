using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class SettingsTests : IDisposable
{
    private const string S3 = """{ "Id": "11111111-1111-1111-1111-111111111111", "Name": "s3", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "AccessKey": "a", "SecretKey": "s" }""";
    private const string Server = """{ "Id": "22222222-2222-2222-2222-222222222222", "Name": "srv", "Type": "Server", "ServerPath": "/srv/media" }""";
    private const string Library = "00000000-0000-0000-0000-000000000001";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-settings-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public SettingsTests()
    {
        _store = new SettingsStore(new WorkerPaths(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Read_WithoutSettings_GivesTheDefaults_AndWritesNothing()
    {
        var settings = _store.Read(out var imported);

        Assert.False(imported);
        Assert.Empty(settings.Connections);
        Assert.Equal(string.Empty, settings.TempDirectory);
        Assert.Null(Stored());
    }

    [Fact]
    public void WriteAndLoad_RoundTrip_ResolvingRelativePathsAgainstTheDataDirectory()
    {
        var id = Guid.NewGuid();
        var scheme = Guid.NewGuid();
        _store.Write(new WorkerSettings
        {
            ApiKey = "key",
            WorkerId = id,
            Connections = [new ConnectionSettings { Id = id, Name = "local", Type = ConnectionType.Worker, LocalRoot = "hls" }],
            Schemes = [new SchemeSettings { Id = scheme, Name = "a", Hls = [new SchemeEntry { ConnectionId = id, Declare = true }] }],
            Libraries = [new LibrarySettings { Id = Guid.NewGuid(), SchemeId = scheme, Prefix = "movies" }]
        });

        var read = _store.Read();
        var loaded = _store.Load();

        Assert.Equal("hls", read.Connections[0].LocalRoot);
        Assert.Equal(Path.Combine(_directory, "hls"), loaded.Connections[0].LocalRoot);
        Assert.Equal(Path.Combine(_directory, "work"), loaded.TempDirectory);
        Assert.Equal(id, loaded.WorkerId);
        Assert.Equal(id, loaded.GetScheme(scheme)!.Hls[0].ConnectionId);
        Assert.True(loaded.GetScheme(scheme)!.Hls[0].Declare);
        Assert.Equal(scheme, loaded.Libraries[0].SchemeId);
    }

    [Fact]
    public void SetWorkerId_KeepsTheRestAsStored()
    {
        _store.Write(new WorkerSettings { ApiKey = "key", TempDirectory = "temp" });
        var id = Guid.NewGuid();

        _store.SetWorkerId(id);

        var settings = _store.Read();
        Assert.Equal(id, settings.WorkerId);
        Assert.Equal("temp", settings.TempDirectory);
    }

    [Theory]
    [InlineData("""{ "Server": "ftp://x", "ApiKey": "k" }""", "Server")]
    [InlineData("""{ "ApiKey": "k", "Concurrency": 0 }""", "Concurrency")]
    [InlineData("""{ "ApiKey": "k", "UploadFileConcurrency": 0 }""", "UploadFileConcurrency")]
    [InlineData("""{ "ApiKey": "k", "Encoder": "libx265" }""", "Encoder")]
    [InlineData("""{ "ApiKey": "k", "Encoder": "h264_vaapi" }""", "HardwareDevice")]
    [InlineData("""{ "ApiKey": "k", "Unknown": 1 }""", "配置无效")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + S3 + ", " + S3 + """ ] }""", "重复")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "AccessKey": "a", "SecretKey": "s" } ] }""", "Id")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Id": "11111111-1111-1111-1111-111111111111", "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b/c", "AccessKey": "a", "SecretKey": "s" } ] }""", "Bucket")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Id": "11111111-1111-1111-1111-111111111111", "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "BucketPath": "a//b", "AccessKey": "a", "SecretKey": "s" } ] }""", "BucketPath")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Id": "11111111-1111-1111-1111-111111111111", "Name": "a", "Type": "S3", "Endpoint": "http://s3", "Bucket": "b", "AccessKey": "a", "SecretKey": "s", "PartConcurrency": 3 } ] }""", "PartConcurrency")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Id": "11111111-1111-1111-1111-111111111111", "Name": "a", "Type": "Server", "ServerPath": "media" } ] }""", "绝对路径")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ { "Id": "11111111-1111-1111-1111-111111111111", "Name": "a", "Type": "Sftp", "Host": "h", "User": "u", "Password": "p", "RemoteRoot": "srv" } ] }""", "RemoteRoot")]
    [InlineData("""{ "ApiKey": "k", "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": [ { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Declare": true } ] } ] }""", "不存在")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + S3 + """ ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": [ { } ] } ] }""", "没有选择目录")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + S3 + """ ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": [ { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Declare": true }, { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Declare": true } ] } ] }""", "HLS 只能申告一个目录")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + S3 + """ ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Sources": [ { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Declare": true }, { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Declare": true } ] } ] }""", "源文件只能申告一个目录")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + Server + """ ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Sources": [ { "ConnectionId": "22222222-2222-2222-2222-222222222222" } ] } ] }""", "只能用于申告")]
    [InlineData("""{ "ApiKey": "k", "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": [ { "ConnectionId": """ + "\"" + Library + "\"" + """, "Declare": true } ] } ] }""", "不能保存到 Jellyfin 媒体库")]
    [InlineData("""{ "ApiKey": "k", "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Sources": [ { "ConnectionId": """ + "\"" + Library + "\"" + """ } ] } ] }""", "必须申告")]
    [InlineData("""{ "ApiKey": "k", "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Sources": [ { "ConnectionId": """ + "\"" + Library + "\"" + """, "Declare": true }, { "ConnectionId": """ + "\"" + Library + "\"" + """, "Declare": true } ] } ] }""", "只能有一行")]
    [InlineData("""{ "ApiKey": "k", "Connections": [ """ + Server + """ ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": [ { "ConnectionId": "22222222-2222-2222-2222-222222222222", "Declare": true } ] } ] }""", "还需要一个上传 HLS 的目录")]
    [InlineData("""{ "ApiKey": "k", "Libraries": [ { "Id": "55555555-5555-5555-5555-555555555555", "Prefix": "a//b" } ] }""", "前缀")]
    public void Load_RefusesInvalidSettings(string json, string expected)
    {
        Store(json);

        var error = Assert.Throws<ArgumentException>(_store.Load);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // The lines of former versions: a directory below a connection becomes a connection of its own, and only the first line
    // declared stays declared; a directory of the server declared elsewhere becomes a line of its own.
    [Fact]
    public void Read_UpgradesTheLinesOfTheSchemes()
    {
        const string Sftp = """{ "Id": "44444444-4444-4444-4444-444444444444", "Name": "ssh", "Type": "Sftp", "Host": "h", "User": "u", "Password": "p", "RemoteRoot": "/srv" }""";
        Store($$"""
            {
              "ApiKey": "k",
              "Connections": [ {{S3}}, {{Server}}, {{Sftp}} ],
              "Schemes": [ {
                "Id": "33333333-3333-3333-3333-333333333333", "Name": "x",
                "Hls": [
                  { "Upload": { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Path": "hls/" } },
                  { "Upload": { "ConnectionId": "44444444-4444-4444-4444-444444444444", "Path": "hls" }, "DeclareSameAsUpload": true },
                  { "DeclareSameAsUpload": false, "Declared": { "ConnectionId": "22222222-2222-2222-2222-222222222222" } }
                ],
                "Sources": [
                  { "Upload": { "ConnectionId": "44444444-4444-4444-4444-444444444444", "Path": "media" }, "DeclareSameAsUpload": false, "Declared": { "ConnectionId": "22222222-2222-2222-2222-222222222222", "Path": "media" } },
                  { "Upload": { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Path": "hls" } },
                  { "Upload": { "ConnectionId": "11111111-1111-1111-1111-111111111111" }, "DeclareSameAsUpload": false, "Declared": { "ConnectionId": "11111111-1111-1111-1111-111111111111", "Path": "x" } }
                ]
              } ]
            }
            """);

        var settings = _store.Load();

        Assert.Equal(["s3", "srv", "ssh", "s3/hls", "ssh/hls", "ssh/media", "srv/media"], settings.Connections.Select(c => c.Name));
        Assert.Equal("hls", settings.Connections[3].BucketPath);
        Assert.Equal("/srv/hls", settings.Connections[4].RemoteRoot);
        Assert.Equal("/srv/media", settings.Connections[5].RemoteRoot);
        Assert.Equal("/srv/media/media", settings.Connections[6].ServerPath);
        Assert.Equal("s", settings.Connections[3].SecretKey);
        var scheme = Assert.Single(settings.Schemes);
        Assert.Equal([(settings.Connections[3].Id, true), (settings.Connections[4].Id, false)], scheme.Hls.Select(e => (e.ConnectionId!.Value, e.Declare)));
        Assert.Equal(
            [(settings.Connections[5].Id, false), (settings.Connections[6].Id, true), (settings.Connections[3].Id, false), (settings.Connections[0].Id, false)],
            scheme.Sources.Select(e => (e.ConnectionId!.Value, e.Declare)));

        // Written in the form of now, it reads the same.
        _store.Write(settings);
        Assert.DoesNotContain("DeclareSameAsUpload", Stored(), StringComparison.Ordinal);
        Assert.Equal(7, _store.Load().Connections.Count);
    }

    [Fact]
    public void Read_DropsADefaultSchemeThatIsGone()
    {
        Store("""{ "ApiKey": "k", "Libraries": [ { "Id": "55555555-5555-5555-5555-555555555555", "SchemeId": "33333333-3333-3333-3333-333333333333" } ] }""");

        Assert.Null(_store.Read().Libraries.Single().SchemeId);
        _store.Load();
    }

    [Fact]
    public void Read_UpgradesWhetherALibraryWasEncodedAtOnce_ToItsQualities()
    {
        Store("""
            {
              "ApiKey": "k",
              "Libraries": [ { "Id": "55555555-5555-5555-5555-555555555555", "PreEncode": true }, { "Id": "66666666-6666-6666-6666-666666666666", "PreEncode": false } ],
              "PreEncodes": [ { "LibraryId": "55555555-5555-5555-5555-555555555555", "Qualities": [ 1080, 720 ] } ]
            }
            """);

        var settings = _store.Read();

        Assert.Equal([1080, 720], settings.Libraries[0].PreEncodeQualities);
        Assert.Empty(settings.Libraries[1].PreEncodeQualities);
        settings.PreEncodes[0].DisabledQualities = [720];
        _store.Write(settings);
        var read = _store.Read();
        Assert.Equal([720], read.PreEncodes[0].DisabledQualities);
        Assert.Equal([1080], read.PreEncodes[0].EnabledQualities);
        Assert.Equal([720], read.GetDisabledQualities(read.PreEncodes[0].LibraryId));
        Assert.Empty(read.GetDisabledQualities(Guid.NewGuid()));
        Assert.DoesNotContain("EnabledQualities", Stored(), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TakesNullListsAsEmpty_AndNullTextsAsTheDefaults()
    {
        Store("""{ "ApiKey": "k", "Name": null, "Connections": null, "Libraries": [ null ], "Schemes": [ { "Id": "33333333-3333-3333-3333-333333333333", "Name": "x", "Hls": null, "Sources": [ null ] } ] }""");

        var settings = _store.Read();

        Assert.Empty(settings.Connections);
        Assert.Empty(settings.Libraries);
        Assert.Empty(settings.Schemes[0].Hls);
        Assert.Empty(settings.Schemes[0].Sources);
        Assert.Equal(Environment.MachineName, settings.Name);

        Store("""{ "ApiKey": "k", "Connections": [ { "Name": "s", "Type": "S3", "Region": null } ] }""");
        Assert.Equal("us-east-1", _store.Read().Connections[0].Region);
    }

    [Fact]
    public void Read_ImportsTheSettingsFileOfFormerVersions_Once()
    {
        File.WriteAllText(Path.Combine(_directory, "worker.json"), """
            {
              "Server": "http://jellyfin:8096/jf",
              "ApiKey": "key",
              "WorkDirectory": "work",
              "Ffmpeg": "bin/ffmpeg",
              "Concurrency": 2,
              "Connections": [
                { "Name": "nas", "Type": "Local", "LocalRoot": "nas", "ServerRoot": "/srv/nas", "DeclareOnly": true },
                { "Name": "b2", "Type": "S3", "Endpoint": "https://s3.example.com", "Bucket": "media", "AccessKey": "a", "SecretKey": "s", "PartConcurrency": 5 },
                { "Name": "ssh", "Type": "Sftp", "Host": "h", "User": "u", "Password": "p", "PrivateKeyPath": "id_ed25519", "RemoteRoot": "/srv", "ServerRoot": "/srv" }
              ],
              "Hls": { "Connection": "b2", "Path": "hls" },
              "Libraries": [ { "Id": "55555555-5555-5555-5555-555555555555", "Connection": "b2" } ]
            }
            """);
        var film = Path.Combine(_directory, "film.mkv");
        File.WriteAllBytes(film, new byte[10]);
        Directory.CreateDirectory(Path.Combine(_directory, "work"));
        new MediaDatabase(Path.Combine(_directory, "work", "worker.db")).Record("s3://media/film.mkv", 10, new LocalCopy(film, TestPlans.Sha256));

        var settings = _store.Read(out var imported);

        Assert.True(imported);
        Assert.Equal("http://jellyfin:8096/jf", settings.Server);
        Assert.Equal(2, settings.Concurrency);
        Assert.Equal(string.Empty, settings.TempDirectory);
        Assert.Equal([ConnectionType.Worker, ConnectionType.S3, ConnectionType.Sftp, ConnectionType.S3], settings.Connections.Select(c => c.Type));
        Assert.Equal(Path.Combine(_directory, "nas"), settings.Connections[0].LocalRoot);
        Assert.Equal(5, settings.Connections[1].PartConcurrency);
        Assert.Equal(Path.Combine(_directory, "id_ed25519"), settings.Connections[2].PrivateKeyPath);
        Assert.All(settings.Connections, c => Assert.NotEqual(Guid.Empty, c.Id));
        var scheme = Assert.Single(settings.Schemes);
        var line = Assert.Single(scheme.Hls);
        // The directory below the connection is a connection of its own.
        Assert.Equal("b2/hls", settings.Connections[3].Name);
        Assert.Equal("hls", settings.Connections[3].BucketPath);
        Assert.Equal(settings.Connections[3].Id, line.ConnectionId);
        Assert.True(line.Declare);
        Assert.Empty(settings.Libraries);
        Assert.NotNull(new MediaDatabase(_store.Paths.Database).Find("s3://media/film.mkv", 10, DateTime.UtcNow));
        _store.Load();

        File.WriteAllText(Path.Combine(_directory, "worker.json"), """{ "ApiKey": "other" }""");
        Assert.Equal("key", _store.Read(out imported).ApiKey);
        Assert.False(imported);
    }

    [Fact]
    public void Validate_RequiresAnApiKey()
    {
        var error = Assert.Throws<ArgumentException>(() => new WorkerSettings().Validate());
        Assert.Contains("ApiKey", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionType.Sftp, "/mnt/hls/", "a/b", "/mnt/hls/a/b")]
    [InlineData(ConnectionType.Sftp, "/", "a", "/a")]
    [InlineData(ConnectionType.Sftp, @"D:\HLS", "a/b", @"D:\HLS\a\b")]
    [InlineData(ConnectionType.Server, "D:\\", "a", @"D:\a")]
    [InlineData(ConnectionType.Server, @"\\nas\hls", "", @"\\nas\hls")]
    [InlineData(ConnectionType.Server, "/srv/hls", "/x/", "/srv/hls/x")]
    [InlineData(ConnectionType.Worker, "/data/hls", "x", "/data/hls/x")]
    public void GetServerPath_NamesTheDirectoryAsTheServerSeesIt(ConnectionType type, string root, string path, string expected)
    {
        var connection = new ConnectionSettings { Name = "c", Type = type, RemoteRoot = root, ServerPath = root, LocalRoot = root };

        Assert.Equal(expected, connection.GetServerPath(path));
    }

    [Fact]
    public void GetServerPath_OfS3_Throws()
        => Assert.Throws<InvalidOperationException>(() => new ConnectionSettings { Type = ConnectionType.S3 }.GetServerPath("a"));

    [Theory]
    [InlineData("", "", "", "s3://bucket")]
    [InlineData("/root/", "", "root", "s3://bucket/root")]
    [InlineData("root", "/a/b/", "root/a/b", "s3://bucket/root/a/b")]
    [InlineData("root/sub", "a", "root/sub/a", "s3://bucket/root/sub/a")]
    public void GetS3Address_PutsThePathBelowTheBucketPath(string bucketPath, string path, string key, string expected)
    {
        var connection = new ConnectionSettings { Name = "c", Type = ConnectionType.S3, Bucket = "bucket", BucketPath = bucketPath };

        Assert.Equal(key, connection.GetS3Key(path));
        Assert.Equal(expected, connection.GetS3Address(path));
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

    // As another version, or a hand, may have written it.
    private void Store(string json)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.Paths.Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings (Id, Json) VALUES (1, $json) ON CONFLICT (Id) DO UPDATE SET Json = excluded.Json;";
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
    }

    private string? Stored()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.Paths.Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM Settings;";
        return command.ExecuteScalar() as string;
    }
}
