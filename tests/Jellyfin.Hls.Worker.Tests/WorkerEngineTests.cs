using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
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
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-engine-" + Guid.NewGuid().ToString("N"));

    public WorkerEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "root"));
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
        var server = new FakeServer(async (request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Protocol", StringComparison.Ordinal))
            {
                return FakeServer.Json(new ProtocolVersion(1));
            }

            if (path.EndsWith("/Bases/Check", StringComparison.Ordinal))
            {
                return FakeServer.Json(new StoreTestResult(true, true, null));
            }

            if (path.EndsWith("/Poll", StringComparison.Ordinal))
            {
                return Interlocked.Increment(ref polled) == 1
                    ? FakeServer.Json(new WorkAssignment(
                        new WorkLease(_jobId, TestPlans.Attempt, 300, 1),
                        new SourceFile(Guid.NewGuid(), Guid.NewGuid(), "/media/clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
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

            return FakeServer.Status(path.EndsWith("/Heartbeat", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        });
        var settings = new WorkerSettings
        {
            ApiKey = "k",
            Name = "test",
            WorkDirectory = Path.Combine(_directory, "work"),
            InputCacheGiB = 0,
            Connections = [new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "root") }],
            Hls = new HlsTargetSettings { Connection = "local", Path = "hls" }
        };
        using var engine = new WorkerEngine(settings, NullLoggerFactory.Instance, server);

        var result = await engine.RunAsync(true, CancellationToken.None);

        Assert.Equal(WorkerEngine.Success, result);
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Failed", StringComparison.Ordinal));
        Assert.Equal(sha, identification!.Sha256);
        Assert.Equal(engine.Target.Base, identification.Base);
        Assert.Contains("\"streams\"", identification.ProbeJson, StringComparison.Ordinal);

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

        var hls = Path.Combine(_directory, "root", "hls");
        Assert.True(File.Exists(Path.Combine(hls, ".jellyfin-hls")));
        foreach (var (rendition, output) in plan.Renditions.Zip(completion.Renditions))
        {
            foreach (var file in output.Files)
            {
                var uploaded = new FileInfo(Path.Combine(hls, rendition.RelativeDirectory, file.Path));
                Assert.True(uploaded.Exists, uploaded.FullName);
                Assert.Equal(file.Length, uploaded.Length);
            }

            Assert.False(File.Exists(Path.Combine(hls, rendition.RelativeDirectory, HlsEncoder.PlaylistFile)));
        }

        var vtt = await File.ReadAllTextAsync(Path.Combine(hls, plan.Renditions[3].RelativeDirectory, "subtitle.vtt"));
        Assert.StartsWith("WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000", vtt, StringComparison.Ordinal);
        Assert.Contains("Hello", vtt, StringComparison.Ordinal);

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
        var server = new FakeServer(async (request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                _ when path.EndsWith("/Protocol", StringComparison.Ordinal) => FakeServer.Json(new ProtocolVersion(1)),
                _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
                _ when path.EndsWith("/Poll", StringComparison.Ordinal) => Interlocked.Increment(ref polled) == 1
                    ? FakeServer.Json(new WorkAssignment(
                        new WorkLease(_jobId, TestPlans.Attempt, 300, 1),
                        new SourceFile(Guid.NewGuid(), Guid.NewGuid(), "/media/clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
                        "HlsWorker/Jobs/x/Input"))
                    : FakeServer.Status(HttpStatusCode.NoContent),
                _ when path.EndsWith("/Input", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(clip)) },
                _ when path.EndsWith("/Identify", StringComparison.Ordinal) => FakeServer.Json(new IdentifyResponse(false, TestPlans.Plan(3))),
                _ when path.EndsWith("/Failed", StringComparison.Ordinal) => Record(body),
                _ => FakeServer.Status(HttpStatusCode.NoContent)
            };
        });
        var settings = new WorkerSettings
        {
            ApiKey = "k",
            WorkDirectory = Path.Combine(_directory, "work"),
            Connections = [new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "root") }],
            Hls = new HlsTargetSettings { Connection = "local" }
        };
        using var engine = new WorkerEngine(settings, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Failed, await engine.RunAsync(true, CancellationToken.None));
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
    public async Task Run_StopsWhenTheServerCannotUseTheRoot()
    {
        var server = new FakeServer((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/Protocol", StringComparison.Ordinal)
            ? FakeServer.Json(new ProtocolVersion(1))
            : FakeServer.Json(new StoreTestResult(true, false, null))));
        var settings = new WorkerSettings
        {
            ApiKey = "k",
            WorkDirectory = Path.Combine(_directory, "work"),
            Connections = [new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "root") }],
            Hls = new HlsTargetSettings { Connection = "local" }
        };
        using var engine = new WorkerEngine(settings, NullLoggerFactory.Instance, server);

        Assert.Equal(WorkerEngine.Failed, await engine.RunAsync(true, CancellationToken.None));
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Poll", StringComparison.Ordinal));
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
}
