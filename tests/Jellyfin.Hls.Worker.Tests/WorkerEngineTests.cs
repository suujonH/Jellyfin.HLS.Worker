using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

/// <summary>A test that needs ffmpeg and ffprobe on the PATH; it is skipped without them.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!IsOnPath("ffmpeg") || !IsOnPath("ffprobe"))
        {
            Skip = "ffmpeg 和 ffprobe 不在 PATH 中。";
        }
    }

    private static bool IsOnPath(string program)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, program)) || File.Exists(Path.Combine(directory, program + ".exe")));
}

public sealed class WorkerEngineTests : IDisposable
{
    private static readonly Guid _jobId = Guid.NewGuid();
    private static readonly Guid _library = Guid.NewGuid();
    private static readonly Guid _worker = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-engine-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionSettings _local;
    private readonly SettingsStore _store;

    public WorkerEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "root"));
        _local = new ConnectionSettings { Id = Guid.NewGuid(), Name = "local", Type = ConnectionType.Worker, LocalRoot = Path.Combine(_directory, "root") };
        _store = new SettingsStore(new WorkerPaths(Path.Combine(_directory, "data")));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [FfmpegFact]
    public async Task Run_EncodesUploadsAndReportsAJob()
    {
        var clip = await CreateClipAsync(_directory);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(clip)));
        var plan = TestPlans.Plan(expectedSegments: 3, sha256: sha);
        var polled = 0;
        Completion? completion = null;
        Identification? identification = null;
        var server = Serve(async (path, body) =>
        {
            if (path.EndsWith("/Poll", StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref polled) == 1
                    ? FakeServer.Json(new WorkAssignment(
                        new WorkLease(_jobId, TestPlans.Attempt, 300, 1),
                        new SourceFile(Guid.NewGuid(), _library, "/media/clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
                        "HlsWorker/Jobs/" + _jobId.ToString("N") + "/Input?leaseToken=" + TestPlans.Attempt))
                    : FakeServer.Status(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/Input", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(clip)) };
            }

            if (path.EndsWith("/Identify", StringComparison.Ordinal))
            {
                identification = JsonSerializer.Deserialize<Identification>(body, ServerClient.Json);
                return FakeServer.Json(new IdentifyResponse(false, plan));
            }

            if (path.EndsWith("/Complete", StringComparison.Ordinal))
            {
                completion = JsonSerializer.Deserialize<Completion>(body, ServerClient.Json);
                return FakeServer.Status(HttpStatusCode.Accepted);
            }

            return null;
        });
        var settings = Settings();
        settings.InputCacheGiB = 0;
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = _library, Qualities = [1080, 720], DisabledQualities = [720] });
        var hls = Hls(settings, "hls");
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        var result = await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = hls }, true, CancellationToken.None);

        Assert.Equal(WorkerEngine.Success, result);
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Failed", StringComparison.Ordinal));
        Assert.Equal(sha, identification!.Sha256);
        Assert.Equal(hls.Base, identification.Base);
        Assert.Contains("\"streams\"", identification.ProbeJson, StringComparison.Ordinal);
        Assert.Equal([720], identification.ExcludedQualities!);

        Assert.NotNull(completion);
        Assert.Equal(TestPlans.Attempt, completion.LeaseToken);
        Assert.Equal(["v240", "v144", "a1", "s2", "preview"], completion.Renditions.Select(r => r.Key));
        var video = completion.Renditions[0];
        Assert.InRange(video.SegmentDurations.Count, 2, 4);
        Assert.StartsWith("avc1.", video.Summary.Codec, StringComparison.Ordinal);
        Assert.True(video.Summary.AverageBandwidth > 0);
        Assert.Equal(144, completion.Renditions[1].Summary.Quality);
        Assert.Equal(1, completion.Renditions[2].Summary.SourceIndex);

        // The audio ends after 8 seconds and is padded to the 13.5 of the media.
        Assert.InRange(completion.Renditions[2].SegmentDurations.Sum(), 13.4, 13.6);

        var root = Path.Combine(_directory, "root", "hls");
        Assert.True(File.Exists(Path.Combine(root, ".jellyfin-hls")));
        foreach (var (rendition, output) in plan.Renditions.Zip(completion.Renditions))
        {
            foreach (var file in output.Files)
            {
                var uploaded = new FileInfo(Path.Combine(root, rendition.RelativeDirectory, file.Path));
                Assert.True(uploaded.Exists, uploaded.FullName);
                Assert.Equal(file.Length, uploaded.Length);
            }

            Assert.False(File.Exists(Path.Combine(root, rendition.RelativeDirectory, HlsEncoder.PlaylistFile)));
        }

        var vtt = await File.ReadAllTextAsync(Path.Combine(root, plan.Renditions[3].RelativeDirectory, "subtitle.vtt"));
        Assert.StartsWith("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000", vtt, StringComparison.Ordinal);
        Assert.Contains("Hello", vtt, StringComparison.Ordinal);

        // Every file uploaded is recorded.
        var uploads = new Jobs.UploadLog(_store.Paths.Database).Recent(5000);
        Assert.Equal(completion.Renditions.Sum(r => r.Files.Count), uploads.Count);
        Assert.All(uploads, u => Assert.Equal((Jobs.UploadKind.Hls, (Guid?)_library, true), (u.Kind, u.LibraryId, u.Succeeded)));

        // The job directory is gone, and with a cache of 0 GiB the input too.
        Assert.Empty(Directory.GetDirectories(Path.Combine(_directory, "work", "jobs")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "work", "inputs")));
    }

    [FfmpegFact]
    public async Task Run_ReportsAPlanForAnotherFile_AsFailed()
    {
        var clip = await CreateClipAsync(_directory);
        string? failure = null;
        var polled = 0;
        var server = Serve(async (path, body) => path switch
        {
            _ when path.EndsWith("/Poll", StringComparison.Ordinal) => Interlocked.Increment(ref polled) == 1
                ? FakeServer.Json(new WorkAssignment(
                    new WorkLease(_jobId, TestPlans.Attempt, 300, 1),
                    new SourceFile(Guid.NewGuid(), _library, "/media/clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
                    "HlsWorker/Jobs/x/Input"))
                : FakeServer.Status(HttpStatusCode.NoContent),
            _ when path.EndsWith("/Input", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(clip)) },
            _ when path.EndsWith("/Identify", StringComparison.Ordinal) => FakeServer.Json(new IdentifyResponse(false, TestPlans.Plan(3))),
            _ when path.EndsWith("/Failed", StringComparison.Ordinal) => Record(body),
            _ => null
        });
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Failed, await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, string.Empty) }, true, CancellationToken.None));
        Assert.Contains("SHA-256", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Complete", StringComparison.Ordinal));

        // The cached input is not what the server read, so it is not kept.
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "work", "inputs")));

        HttpResponseMessage Record(string body)
        {
            failure = JsonSerializer.Deserialize<Failure>(body, ServerClient.Json)!.Error;
            return FakeServer.Status(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Run_RegistersAndClaimsTheJobsOfItsLibrariesOnly()
    {
        var other = Guid.NewGuid();
        var assigned = Guid.Empty;
        var server = Serve((path, _) => Task.FromResult(path.EndsWith("/Poll", StringComparison.Ordinal) ? FakeServer.Status(HttpStatusCode.NoContent) : null));
        var settings = Settings();
        var off = Guid.NewGuid();
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = _library, Qualities = [1080, 720], DisabledQualities = [720] });
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = off, Name = "Off", Qualities = [480], DisabledQualities = [480] });
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);
        engine.WorkerIdAssigned += id => assigned = id;

        Assert.Equal(
            WorkerEngine.Success,
            await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, "a"), [other] = Hls(settings, "b"), [off] = Hls(settings, "c") }, true, CancellationToken.None));

        Assert.Equal(_worker, assigned);
        Assert.Equal(_worker, _store.Read().WorkerId);
        var registration = JsonSerializer.Deserialize<WorkerRegistration>(Body(server, "/Workers"), ServerClient.Json)!;
        Assert.Equal(new WorkerRegistration(null, "test"), registration);
        var poll = JsonSerializer.Deserialize<WorkerRequest>(Body(server, "/Poll"), ServerClient.Json)!;
        Assert.Equal(("test", _worker), (poll.Worker, poll.WorkerId));
        Assert.Equal(new[] { _library, other }.Order(), poll.Libraries!.Order());
        Assert.Empty(poll.ExcludedItems!);

        // The qualities turned off; a library with every quality turned off is not asked for.
        var filter = Assert.Single(poll.ExcludedQualities!);
        Assert.Equal(_library, filter.LibraryId);
        Assert.Equal([720], filter.Excluded);
    }

    // No HLS directory declared: the renditions are kept in the work directory as well, which the server is told.
    [Fact]
    public async Task Run_WithNoHlsDirectoryDeclared_DeclaresTheWorkDirectory()
    {
        var server = Serve((path, _) => Task.FromResult(path.EndsWith("/Poll", StringComparison.Ordinal) ? FakeServer.Status(HttpStatusCode.NoContent) : null));
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Success, await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, "a", declare: false) }, true, CancellationToken.None));

        var local = Path.Combine(_directory, "work", "hls", "a");
        Assert.Equal(WorkerLocalAddress.Format(_worker, local), JsonSerializer.Deserialize<BaseRequest>(Body(server, "/Bases/Check"), ServerClient.Json)!.Base);
        Assert.True(File.Exists(Path.Combine(local, HlsTarget.MarkerName)));
        Assert.True(File.Exists(Path.Combine(_local.LocalRoot, "a", HlsTarget.MarkerName)));
    }

    [Fact]
    public async Task Run_GivesBackAJobWhoseFileIsNotOnThisWorker_AndSkipsItsItemUntilTheNextRun()
    {
        var item = Guid.NewGuid();
        var polled = 0;
        var missing = Path.Combine(_directory, "missing.mkv");
        var server = Serve((path, _) => Task.FromResult(path switch
        {
            _ when path.EndsWith("/Poll", StringComparison.Ordinal) => Interlocked.Increment(ref polled) == 1
                ? FakeServer.Json(new WorkAssignment(
                    new WorkLease(_jobId, TestPlans.Attempt, 300, 1),
                    new SourceFile(item, _library, WorkerLocalAddress.Format(_worker, missing), 10, DateTime.UtcNow),
                    "HlsWorker/Jobs/x/Input"))
                : FakeServer.Status(HttpStatusCode.NoContent),
            _ => null
        }));
        var settings = Settings();
        settings.WorkerId = _worker;
        var plans = new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, string.Empty) };
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Success, await engine.RunAsync(plans, true, CancellationToken.None));

        Assert.Contains(server.Paths, p => p.EndsWith($"/Jobs/{_jobId:N}/Return", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Failed", StringComparison.Ordinal) || p.EndsWith("/Input", StringComparison.Ordinal));
        var polls = Bodies(server, "/Poll").Select(b => JsonSerializer.Deserialize<WorkerRequest>(b, ServerClient.Json)!).ToList();
        Assert.Empty(polls[0].ExcludedItems!);
        Assert.Equal([item], polls[^1].ExcludedItems);

        // A new run tries it again.
        await engine.RunAsync(plans, true, CancellationToken.None);
        polls = Bodies(server, "/Poll").Select(b => JsonSerializer.Deserialize<WorkerRequest>(b, ServerClient.Json)!).ToList();
        Assert.Empty(polls[^1].ExcludedItems!);
    }

    [Fact]
    public async Task Run_StopsWhenTheServerCannotUseTheRoot()
    {
        var server = Serve((path, _) => Task.FromResult(path.EndsWith("/Bases/Check", StringComparison.Ordinal)
            ? FakeServer.Json(new StoreTestResult(true, false, null))
            : null));
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Failed, await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, string.Empty) }, true, CancellationToken.None));
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Poll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RefusesAServerOfAnotherProtocol()
    {
        var server = Serve((path, _) => Task.FromResult(path.EndsWith("/Protocol", StringComparison.Ordinal) ? FakeServer.Json(new ProtocolVersion(1)) : null));
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Failed, await engine.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = Hls(settings, string.Empty) }, true, CancellationToken.None));
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Workers", StringComparison.Ordinal) || p.EndsWith("/Poll", StringComparison.Ordinal));
    }

    [FfmpegFact]
    public async Task Run_TellingTheLines_ReturnsWhatWasWritten()
    {
        var lines = new List<string>();

        var output = await ProcessRunner.RunAsync("ffmpeg", ["-hide_banner", "-version"], CancellationToken.None, null, lines.Add);

        Assert.NotEmpty(lines);
        Assert.Equal(output, string.Concat(lines.Select(l => l + "\n")));
    }

    // 13.5 seconds of 640x360 at 25 fps with a stereo track that ends after 8 seconds and a text subtitle: streams 0, 1 and 2.
    internal static async Task<string> CreateClipAsync(string directory, string name = "clip.mkv")
    {
        var srt = Path.Combine(directory, "clip.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:03,000\nHello\n\n2\n00:00:07,000 --> 00:00:09,000\nWorld\n");
        var clip = Path.Combine(directory, name);
        await ProcessRunner.RunAsync(
            "ffmpeg",
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8",
                "-i", srt,
                "-t", "13.5", "-map", "0:v", "-map", "1:a", "-map", "2:s", "-ac", "2",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "srt",
                clip
            ],
            CancellationToken.None);
        return clip;
    }

    private static string Body(FakeServer server, string suffix) => Bodies(server, suffix).First();

    private static IEnumerable<string> Bodies(FakeServer server, string suffix)
        => server.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)).Select(r => r.Body);

    // A server that speaks the protocol, gives the worker its id and accepts the root; route answers a path first, or leaves it with null.
    private static FakeServer Serve(Func<string, string, Task<HttpResponseMessage?>> route)
        => new(async (request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return await route(path, body) ?? path switch
            {
                _ when path.EndsWith("/Protocol", StringComparison.Ordinal) => FakeServer.Json(new ProtocolVersion(3)),
                _ when path.EndsWith("/Workers", StringComparison.Ordinal) => FakeServer.Json(new WorkerRegistered(_worker)),
                _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
                _ => FakeServer.Status(HttpStatusCode.NoContent)
            };
        });

    private HlsPlan Hls(WorkerSettings settings, string path, bool declare = true)
        => SchemePlanner.ResolveHls(
            settings,
            new SchemeSettings { Id = Guid.NewGuid(), Name = "h", Hls = [new SchemeEntry { ConnectionId = _local.Id, Declare = declare }] },
            new LibrarySettings { Id = _library, Prefix = path })!;

    private WorkerSettings Settings() => new()
    {
        ApiKey = "k",
        Name = "test",
        TempDirectory = Path.Combine(_directory, "work"),
        Connections = [_local]
    };
}
