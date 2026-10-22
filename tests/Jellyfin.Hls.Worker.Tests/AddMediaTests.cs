using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class AddMediaTests : IDisposable
{
    private static readonly Guid _library = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-add-" + Guid.NewGuid().ToString("N"));

    public AddMediaTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "nas", "movies"));
        Directory.CreateDirectory(Path.Combine(_directory, "hls"));
        Directory.CreateDirectory(Path.Combine(_directory, "source"));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    // The NAS is /srv/nas on the server; the library is its folder movies.
    [FfmpegFact]
    public async Task Add_Direct_CopiesEncodesAtOnceAndScans_ThenAJobReadsTheLocalFile()
    {
        var clip = await WorkerEngineTests.CreateClipAsync(Path.Combine(_directory, "source"), "Clip.mkv");
        File.Delete(Path.Combine(_directory, "source", "clip.srt"));
        var subtitle = Path.Combine(_directory, "source", "Clip.en.srt");
        File.WriteAllText(subtitle,"1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(clip)));
        var plan = TestPlans.Plan(expectedSegments: 3, sha256: sha);
        EagerRequest? eager = null;
        var completed = 0;
        var server = new FakeServer((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                _ when path.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
                    => FakeServer.Json(new[] { new VirtualFolder("Movies", _library.ToString(), ["/srv/nas/movies"]) }),
                _ when path.EndsWith("/HlsWorker/Libraries", StringComparison.Ordinal)
                    => FakeServer.Json(new[] { new WorkerLibrary(_library, "Movies", [1080, 240, 144]) }),
                _ when path.EndsWith("/Protocol", StringComparison.Ordinal) => FakeServer.Json(new ProtocolVersion(1)),
                _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
                _ when path.EndsWith("/Jobs/Eager", StringComparison.Ordinal) => Eager(body),
                _ when path.EndsWith("/Complete", StringComparison.Ordinal) => Complete(),
                _ when path.EndsWith("/Refresh", StringComparison.Ordinal) => FakeServer.Status(HttpStatusCode.NoContent),
                _ => FakeServer.Status(HttpStatusCode.NoContent)
            });
        });
        using var engine = new WorkerEngine(Settings(), NullLoggerFactory.Instance, server);

        var result = await engine.AddAsync(new AddRequest(_library, [clip, subtitle], "Clips", Qualities: [240, 144]), CancellationToken.None);

        Assert.Equal(new AddResult(2, 0), result);
        var library = Path.Combine(_directory, "nas", "movies", "Clips");
        Assert.True(File.Exists(Path.Combine(library, "Clip.en.srt")));
        Assert.Equal(new FileInfo(clip).Length, new FileInfo(Path.Combine(library, "Clip.mkv")).Length);
        Assert.Equal([240, 144], eager!.Qualities);
        Assert.Equal(sha, eager.Sha256);
        Assert.Equal(1, completed);
        Assert.Contains(server.Paths, p => p.EndsWith($"/Items/{_library:N}/Refresh", StringComparison.Ordinal));
        engine.Dispose(); // Releases the work directory for the next worker.

        // A job of the file, as the server names it after the scan, reads the local file instead of downloading it.
        var identified = false;
        var polled = 0;
        var jobs = new FakeServer((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                _ when path.EndsWith("/Protocol", StringComparison.Ordinal) => FakeServer.Json(new ProtocolVersion(1)),
                _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
                _ when path.EndsWith("/Poll", StringComparison.Ordinal) => Interlocked.Increment(ref polled) == 1
                    ? FakeServer.Json(new WorkAssignment(
                        new WorkLease(Guid.NewGuid(), TestPlans.Attempt, 300, 1),
                        new SourceFile(Guid.NewGuid(), _library, "/srv/nas/movies/Clips/Clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
                        "HlsWorker/Jobs/x/Input"))
                    : FakeServer.Status(HttpStatusCode.NoContent),
                _ when path.EndsWith("/Identify", StringComparison.Ordinal) => Identify(body),
                _ => FakeServer.Status(HttpStatusCode.NoContent)
            });
        });
        using var worker = new WorkerEngine(Settings(), NullLoggerFactory.Instance, jobs);

        Assert.Equal(WorkerEngine.Success, await worker.RunAsync(true, CancellationToken.None));
        Assert.True(identified);
        Assert.DoesNotContain(jobs.Paths, p => p.EndsWith("/Input", StringComparison.Ordinal));

        HttpResponseMessage Eager(string body)
        {
            eager = JsonSerializer.Deserialize<EagerRequest>(body, ServerClient.Json);
            return FakeServer.Json(new EagerResponse(false, new WorkLease(Guid.NewGuid(), TestPlans.Attempt, 300, 1), plan));
        }

        HttpResponseMessage Complete()
        {
            Interlocked.Increment(ref completed);
            return FakeServer.Status(HttpStatusCode.Accepted);
        }

        HttpResponseMessage Identify(string body)
        {
            identified = JsonSerializer.Deserialize<Identification>(body, ServerClient.Json)!.Sha256 == sha;
            return FakeServer.Json(new IdentifyResponse(true, null));
        }
    }

    [Fact]
    public async Task Add_ToALibraryNoConnectionReaches_Fails()
    {
        File.WriteAllText(Path.Combine(_directory, "source", "a.mkv"), "v");
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new[] { new VirtualFolder("TV", _library.ToString(), ["/elsewhere/tv"]) })));
        using var engine = new WorkerEngine(Settings(), NullLoggerFactory.Instance, server);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.AddAsync(new AddRequest(_library, [Path.Combine(_directory, "source", "a.mkv")]), CancellationToken.None));
        Assert.Contains("ServerRoot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_ToS3_RefusesVideosThatShareAShortcut()
    {
        File.WriteAllText(Path.Combine(_directory, "source", "Film.mkv"), "v");
        File.WriteAllText(Path.Combine(_directory, "source", "Film.mp4"), "v");
        var server = new FakeServer((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
                => FakeServer.Json(new[] { new VirtualFolder("Movies", _library.ToString(), ["/srv/nas/movies"]) }),
            _ => FakeServer.Json(new[] { new NetworkStorageAccess(Guid.NewGuid(), "s3", "x", "http://s3", "media", null, true, "a", null, false) })
        }));
        var settings = Settings();
        settings.Connections.Add(new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = "http://127.0.0.1:9", Bucket = "media", AccessKey = "a", SecretKey = "s" });
        using var engine = new WorkerEngine(settings, NullLoggerFactory.Instance, server);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.AddAsync(new AddRequest(_library, [Path.Combine(_directory, "source")], Connection: "s3"), CancellationToken.None));
        Assert.Contains("source/Film.strm", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "nas", "movies")));
    }

    [Fact]
    public async Task Add_Direct_RefusesFilesOfTheSameName()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "source", "a"));
        Directory.CreateDirectory(Path.Combine(_directory, "source", "b"));
        File.WriteAllText(Path.Combine(_directory, "source", "a", "Film.mkv"), "a");
        File.WriteAllText(Path.Combine(_directory, "source", "b", "Film.mkv"), "b");
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new[] { new VirtualFolder("Movies", _library.ToString(), ["/srv/nas/movies"]) })));
        using var engine = new WorkerEngine(Settings(), NullLoggerFactory.Instance, server);

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAsync(
            new AddRequest(_library, [Path.Combine(_directory, "source", "a", "Film.mkv"), Path.Combine(_directory, "source", "b", "Film.mkv")]),
            CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "nas", "movies")));
    }

    [Fact]
    public async Task Add_Direct_LeavesAFileInPlace_AndDropsTheEntryOfAVideoItReplaces()
    {
        var movies = Path.Combine(_directory, "nas", "movies");
        var nfo = Path.Combine(movies, "Film.nfo");
        File.WriteAllText(nfo, "n");
        var modified = File.GetLastWriteTimeUtc(nfo).AddMinutes(-1);
        File.SetLastWriteTimeUtc(nfo, modified);
        var inPlace = Path.Combine(movies, "Clip.mkv");
        File.WriteAllText(inPlace, "v");
        Directory.CreateDirectory(Path.Combine(movies, "__true_cache__", "Film.mkv"));
        File.WriteAllText(Path.Combine(movies, "Film.mkv"), "old");
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "new");
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new[] { new VirtualFolder("Movies", _library.ToString(), ["/srv/nas/movies"]) })));
        using var engine = new WorkerEngine(Settings(), NullLoggerFactory.Instance, server);

        Assert.Equal(new AddResult(3, 0), await engine.AddAsync(new AddRequest(_library, [nfo, inPlace, film]), CancellationToken.None));

        Assert.Equal(modified, File.GetLastWriteTimeUtc(nfo));
        Assert.Equal("v", File.ReadAllText(inPlace));
        Assert.Equal("new", File.ReadAllText(Path.Combine(movies, "Film.mkv")));
        Assert.False(Directory.Exists(Path.Combine(movies, "__true_cache__", "Film.mkv")));
    }

    private WorkerSettings Settings() => new()
    {
        ApiKey = "k",
        Name = "test",
        WorkDirectory = Path.Combine(_directory, "work"),
        Connections =
        [
            new ConnectionSettings { Name = "nas", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "nas"), ServerRoot = "/srv/nas" },
            new ConnectionSettings { Name = "hls", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "hls"), ServerRoot = "/srv/hls" }
        ],
        Hls = new HlsTargetSettings { Connection = "hls" }
    };
}
