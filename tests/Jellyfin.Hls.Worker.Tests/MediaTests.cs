using System.Net;
using System.Text.Json;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-media-" + Guid.NewGuid().ToString("N"));

    public MediaTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Database_FindsAnUnchangedCopy_AndForgetsAChangedOne()
    {
        var database = new MediaDatabase(Path.Combine(_directory, "worker.db"));
        var file = Path.Combine(_directory, "film.mkv");
        File.WriteAllBytes(file, new byte[10]);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        database.Record("s3://bucket/film.mkv", 10, new LocalCopy(file, TestPlans.Sha256));

        Assert.Equal(TestPlans.Sha256, database.Find("s3://bucket/film.mkv", 10, modified)!.Sha256);
        Assert.NotNull(database.Find("s3://bucket/film.mkv", 10, modified));
        Assert.Null(database.Find("s3://bucket/film.mkv", 11, modified));
        Assert.Null(database.Find("s3://bucket/other.mkv", 10, modified));

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(database.Find("s3://bucket/film.mkv", 10, modified));

        // Forgotten, so it stays gone even when the file is as it was.
        Assert.Null(database.Find("s3://bucket/film.mkv", 10, modified));
    }

    [Fact]
    public void Database_ForgetsACopyOfAFileTheServerHasAnotherOneOf()
    {
        var database = new MediaDatabase(Path.Combine(_directory, "worker.db"));
        var file = Path.Combine(_directory, "film.mkv");
        File.WriteAllBytes(file, new byte[10]);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        database.Record("/srv/film.mkv", 10, new LocalCopy(file, TestPlans.Sha256));

        // The first job tells the time; one of the same size replaced since has another.
        Assert.NotNull(database.Find("/srv/film.mkv", 10, modified));
        Assert.Null(database.Find("/srv/film.mkv", 10, modified.AddSeconds(1)));
        Assert.Null(database.Find("/srv/film.mkv", 10, modified));

        // Adding it again learns the time again.
        database.Record("/srv/film.mkv", 10, new LocalCopy(file, TestPlans.Sha256));
        Assert.NotNull(database.Find("/srv/film.mkv", 10, modified.AddSeconds(1)));
    }

    [Theory]
    [InlineData("/srv/media", "/srv/media/movies", true, "movies")]
    [InlineData("/srv/media/", "/srv/media", true, "")]
    [InlineData("/", "/movies/a", true, "movies/a")]
    [InlineData("/srv/media", "/srv/mediathek", false, "")]
    [InlineData("/srv/Media", "/srv/media/movies", false, "")]
    [InlineData(@"D:\Media", @"d:\media\Movies", true, "Movies")]
    [InlineData(@"D:\", @"D:\Movies", true, "Movies")]
    [InlineData(@"\\nas\share", @"\\NAS\share\tv\a", true, "tv/a")]
    [InlineData("/srv/media", @"D:\media", false, "")]
    public void TryGetRelative_MapsAFolderOfTheServer(string root, string path, bool expected, string relative)
    {
        Assert.Equal(expected, LibraryFolder.TryGetRelative(root, path, out var actual));
        Assert.Equal(relative, actual);
    }

    [Fact]
    public void Expand_KeepsTheFolderName_PutsOtherFilesFirst_AndSkipsHiddenAndEntries()
    {
        var film = Path.Combine(_directory, "Film (2020)");
        Directory.CreateDirectory(Path.Combine(film, "__true_cache__", "Film.strm"));
        Directory.CreateDirectory(Path.Combine(film, ".thumbs"));
        File.WriteAllText(Path.Combine(film, "Film.mkv"), "v");
        File.WriteAllText(Path.Combine(film, "Film.en.srt"), "s");
        File.WriteAllText(Path.Combine(film, ".DS_Store"), "x");
        File.WriteAllText(Path.Combine(film, ".thumbs", "a.jpg"), "x");
        File.WriteAllText(Path.Combine(film, "__true_cache__", "Film.strm", "probe.json"), "{}");
        var single = Path.Combine(_directory, "Clip.mp4");
        File.WriteAllText(single, "v");

        var files = MediaAdder.Expand([film, single], "/Movies/");

        Assert.Equal(["Movies/Film (2020)/Film.en.srt", "Movies/Film (2020)/Film.mkv", "Movies/Clip.mp4"], files.Select(f => f.Path));
    }

    [Theory]
    [InlineData("Movies ")]
    [InlineData("a/./b")]
    [InlineData("a:b")]
    [InlineData("a//b")]
    public void Expand_RefusesAFolderTheServerRefuses(string into)
    {
        var file = Path.Combine(_directory, "a.mkv");
        File.WriteAllText(file, "v");

        Assert.Throws<ArgumentException>(() => MediaAdder.Expand([file], into));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureAccess_CreatesOneOnlyWhenTheBucketHasNone(bool exists)
    {
        var server = new FakeServer((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? FakeServer.Json(exists ? new[] { new NetworkStorageAccess(Guid.NewGuid(), "s3", "x", "http://s3", "media", null, true, "a", null, true) } : [])
            : FakeServer.Json(new { })));
        using var client = new ServerClient(new Uri("http://jellyfin"), "k", TimeSpan.FromSeconds(5), server);
        var connection = new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = "http://s3", Bucket = "media", AccessKey = "a", SecretKey = "s" };

        Assert.Equal(!exists, await MediaAdder.EnsureAccessAsync(client, connection, CancellationToken.None));

        var posted = server.Requests.Where(r => r.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal(exists ? 0 : 1, posted.Count);
        if (!exists)
        {
            var access = JsonSerializer.Deserialize<NetworkStorageAccess>(posted[0].Body, ServerClient.Json)!;
            Assert.False(access.IsArchive);
            Assert.Equal("s", access.SecretKey);
            Assert.True(access.UsePathStyle);
        }
    }

    [Fact]
    public async Task EnsureAccess_TakesTheOneAnotherWorkerCreatedMeanwhile()
    {
        var created = false;
        var server = new FakeServer((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                created = true;
                return Task.FromResult(FakeServer.Status(HttpStatusCode.BadRequest));
            }

            return Task.FromResult(FakeServer.Json(created
                ? new[] { new NetworkStorageAccess(Guid.NewGuid(), "s3", "x", "http://s3", "media", null, true, "a", null, false) }
                : []));
        });
        using var client = new ServerClient(new Uri("http://jellyfin"), "k", TimeSpan.FromSeconds(5), server);
        var connection = new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = "http://s3", Bucket = "media", AccessKey = "a", SecretKey = "s" };

        Assert.False(await MediaAdder.EnsureAccessAsync(client, connection, CancellationToken.None));
    }

    [Fact]
    public void Libraries_RefuseAConnectionThatIsNotS3()
    {
        var settings = new WorkerSettings
        {
            ApiKey = "k",
            Connections = [new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = _directory }],
            Libraries = [new LibrarySettings { Id = Guid.NewGuid(), Connection = "local" }]
        };

        Assert.Throws<ArgumentException>(settings.Validate);
    }
}
