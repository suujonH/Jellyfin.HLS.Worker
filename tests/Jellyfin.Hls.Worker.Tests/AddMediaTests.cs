using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class AddMediaTests : IDisposable
{
    private static readonly Guid _library = Guid.NewGuid();
    private static readonly Guid _worker = Guid.NewGuid();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-add-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionSettings _local;
    private readonly ConnectionSettings _s3 = new() { Id = Guid.NewGuid(), Name = "s3", Type = ConnectionType.S3, Endpoint = "http://127.0.0.1:9", Bucket = "media", AccessKey = "a", SecretKey = "s" };
    private readonly ConnectionSettings _srv = new() { Id = Guid.NewGuid(), Name = "srv", Type = ConnectionType.Server, ServerPath = "/srv/nas" };
    private readonly SettingsStore _store;
    private readonly FakeLibrary _files = new();

    public AddMediaTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "local"));
        Directory.CreateDirectory(Path.Combine(_directory, "source"));
        _local = new ConnectionSettings { Id = Guid.NewGuid(), Name = "local", Type = ConnectionType.Worker, LocalRoot = Path.Combine(_directory, "local") };
        _store = new SettingsStore(new WorkerPaths(Path.Combine(_directory, "data")));
        ProgramPaths.TrueCacheToolOverride = CreateTrueCacheTool(Path.Combine(_directory, "tool"));
    }

    public void Dispose()
    {
        ProgramPaths.TrueCacheToolOverride = null;
        Directory.Delete(_directory, true);
    }

    // The library is /srv/nas/movies on the server; the clip goes into it, and its renditions to a directory of this machine.
    [FfmpegFact]
    public async Task Add_IntoTheLibrary_EncodesEveryQualityAtOnce_ThenAJobReadsTheLocalFile()
    {
        var clip = await WorkerEngineTests.CreateClipAsync(Path.Combine(_directory, "source"), "Clip.mkv");
        File.Delete(Path.Combine(_directory, "source", "clip.srt"));
        var subtitle = Path.Combine(_directory, "source", "Clip.en.srt");
        File.WriteAllText(subtitle, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(clip)));
        var plan = TestPlans.Plan(expectedSegments: 3, sha256: sha);
        EagerRequest? eager = null;
        var completed = 0;
        var server = Serve((path, body) => path switch
        {
            _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
            _ when path.EndsWith("/Jobs/Eager", StringComparison.Ordinal) => Eager(body),
            _ when path.EndsWith("/Complete", StringComparison.Ordinal) && !path.Contains("/Uploads/", StringComparison.Ordinal) => Complete(),
            _ => null
        });
        var settings = Settings();
        var hls = Hls(settings);
        var recorder = new Recorder();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server))
        {
            var result = await engine.AddAsync(new AddRequest(_library, null, [clip, subtitle], IntoLibrary(settings), hls, PreEncodeQualities: [240, 2160]), CancellationToken.None, recorder);

            Assert.Equal(new AddResult(2, 0), result);
        }

        // The subtitle is written first, then the clip is encoded a group at a time, each to its end.
        var events = recorder.Events.ToList();
        Assert.Equal(new AddFileEnded(0, true, null), events.OfType<AddFileEnded>().First());
        var video = events.FindIndex(e => e is AddStepStarted { File: 1, Step: AddStep.EncodeVideo } started && started.Detail.Contains("240p", StringComparison.Ordinal));
        Assert.True(video >= 0);
        Assert.Contains(events.Skip(video), e => e is AddStepAdvanced { File: 1, Unit: ProgressUnit.Milliseconds } advanced && advanced.Done == advanced.Total && advanced.Total > 0);
        Assert.Contains(events, e => e is AddStepStarted { File: 1, Step: AddStep.Preview });
        Assert.Equal(new AddFileEnded(1, true, null), events[^1]);

        Assert.Equal(["Clip.en.srt", "Clip.mkv"], _files.Files.Select(f => f.Path));
        Assert.Equal(new FileInfo(clip).Length, _files.Files.Last().Content.Length);
        Assert.Equal([240], eager!.Qualities);
        Assert.Equal(sha, eager.Sha256);
        Assert.Equal(_worker, eager.WorkerId);
        Assert.Equal(hls.Base, eager.Base);
        Assert.Equal(1, completed);
        Assert.True(File.Exists(Path.Combine(_directory, "local", "hls", Storage.HlsTarget.MarkerName)));
        Assert.Contains(new UploadLog(_store.Paths.Database).Recent(5000), r => r.Kind == UploadKind.Hls && r.Succeeded);

        // A job of the file, as the server names it after the scan, reads the local file instead of downloading it.
        var identified = false;
        var polled = 0;
        var jobs = Serve((path, body) => path switch
        {
            _ when path.EndsWith("/Bases/Check", StringComparison.Ordinal) => FakeServer.Json(new StoreTestResult(true, true, null)),
            _ when path.EndsWith("/Poll", StringComparison.Ordinal) => Interlocked.Increment(ref polled) == 1
                ? FakeServer.Json(new WorkAssignment(
                    new WorkLease(Guid.NewGuid(), TestPlans.Attempt, 300, 1),
                    new SourceFile(Guid.NewGuid(), _library, "/srv/nas/movies/Clip.mkv", new FileInfo(clip).Length, DateTime.UtcNow),
                    "HlsWorker/Jobs/x/Input"))
                : FakeServer.Status(HttpStatusCode.NoContent),
            _ when path.EndsWith("/Identify", StringComparison.Ordinal) => Identify(body),
            _ => null
        });
        using var worker = new WorkerEngine(Settings(), _store, NullLoggerFactory.Instance, jobs);

        Assert.Equal(WorkerEngine.Success, await worker.RunAsync(new Dictionary<Guid, HlsPlan> { [_library] = hls }, true, CancellationToken.None));
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
    public async Task Add_IntoTheLibrary_WritesEveryFileThroughTheServer_AndRecordsEach()
    {
        var film = Path.Combine(_directory, "source", "Film");
        Directory.CreateDirectory(film);
        File.WriteAllText(Path.Combine(film, "Film.mkv"), "video");
        File.WriteAllText(Path.Combine(film, "Film.nfo"), "n");
        var server = Serve();
        var settings = Settings();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server))
        {
            Assert.Equal(new AddResult(2, 0), await engine.AddAsync(new AddRequest(_library, null, [film], IntoLibrary(settings)), CancellationToken.None));
        }

        Assert.Equal(
            [new("Film/Film.nfo", null, "n"), new("Film/Film.mkv", null, "video")],
            _files.Files.Select(f => new Written(f.Path, f.ShortcutTarget, Encoding.UTF8.GetString(f.Content))));
        Assert.All(_files.Files, f => Assert.Equal((_library, "/srv/nas/movies", _worker), (f.LibraryId, f.Location, f.WorkerId)));
        Assert.Equal(_worker, _store.Read().WorkerId);
        Assert.Equal(Path.Combine(film, "Film.mkv"), Find("/srv/nas/movies/Film/Film.mkv", "video")?.LocalPath);
        Assert.Equal(
            [(UploadKind.Source, "Jellyfin 媒体库/Film/Film.mkv"), (UploadKind.LibraryFile, "Jellyfin 媒体库/Film/Film.nfo")],
            new UploadLog(_store.Paths.Database).Recent(10).Select(r => (r.Kind, r.Destination)));
        Assert.Contains(server.Paths, p => p.EndsWith($"/Items/{_library:N}/Refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_WithNvenc_GivesTheGpuToTheTrueCacheTool_AndRetriesWithoutIt()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        settings.Encoder = "h264_nvenc";
        settings.HardwareDevice = "1";
        var tool = Path.Combine(_directory, "tool-gpu");
        ProgramPaths.TrueCacheToolOverride = CreateTrueCacheTool(tool, failOnHardware: true);
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        var runs = File.ReadAllLines(Path.Combine(tool, "args.txt"));
        Assert.Equal(2, runs.Length);
        Assert.Contains("--hwaccel nvenc --hw-device 1", runs[0], StringComparison.Ordinal);
        Assert.Contains("--hwaccel none", runs[1], StringComparison.Ordinal);
        Assert.DoesNotContain("--hw-device", runs[1], StringComparison.Ordinal);
        Assert.Equal("Film.strm", Assert.Single(_files.Files).Path);
    }

    [Fact]
    public async Task Add_ToADirectoryOfThisMachine_RefersToTheCopy_ByAShortcut()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        var recorder = new Recorder();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None, recorder));
        }

        // Every step of the video, in order, the bytes it hashed and uploaded to their end.
        var events = recorder.Events.ToList();
        Assert.Equal(["Film.mkv"], Assert.IsType<AddFilesListed>(events[0]).Files);
        Assert.Equal(
            [(AddStep.Hash, ""), (AddStep.Prepare, ""), (AddStep.Entry, ""), (AddStep.Upload, source.Uploads[0].Label), (AddStep.Shortcut, "")],
            events.OfType<AddStepStarted>().Select(e => (e.Step, e.Detail)));
        var upload = events.FindIndex(e => e is AddStepStarted { Step: AddStep.Upload });
        Assert.Equal(new AddStepAdvanced(0, 5, 5, ProgressUnit.Bytes), events.Take(upload).OfType<AddStepAdvanced>().Last());
        Assert.Equal(new AddStepAdvanced(0, 5, 5, ProgressUnit.Bytes), events.Skip(upload).OfType<AddStepAdvanced>().Last());
        Assert.Equal(new AddFileEnded(0, true, null), events[^1]);

        var copy = Path.Combine(_directory, "local", "movies", "Film.mkv");
        Assert.Equal("video", File.ReadAllText(copy));
        var shortcut = Assert.Single(_files.Files);
        Assert.Equal("Film.strm", shortcut.Path);
        Assert.Equal(WorkerLocalAddress.Format(_worker, copy), shortcut.ShortcutTarget);
        using (var zip = new ZipArchive(new MemoryStream(shortcut.Content)))
        {
            Assert.Equal("probe.json", Assert.Single(zip.Entries).FullName);
        }

        Assert.Equal(copy, Find(shortcut.ShortcutTarget!, "video")?.LocalPath);
        Assert.Equal(
            [UploadKind.TrueCache, UploadKind.Shortcut, UploadKind.Source],
            new UploadLog(_store.Paths.Database).Recent(10).Select(r => r.Kind));
    }

    [Fact]
    public async Task Add_WithoutAScheme_RefersToTheFileAddedFrom()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        using (var engine = new WorkerEngine(Settings(), _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], new SourcePlan([], null)), CancellationToken.None));
        }

        Assert.Equal(WorkerLocalAddress.Format(_worker, film), Assert.Single(_files.Files).ShortcutTarget);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "local")));
    }

    [Fact]
    public async Task Add_DeclaredToS3_RefersToTheObject()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = new SourcePlan([], new PlanDeclaration(DeclarationKind.S3, _s3, "movies"));
        var server = Serve((path, _) => path.EndsWith("/NetworkStorage/Accesses", StringComparison.Ordinal)
            ? FakeServer.Json(new[] { new NetworkStorageAccess(Guid.NewGuid(), "s3", "x", "http://s3", "media", null, true, "a", null, false) })
            : null);
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        Assert.Equal("s3://media/movies/Film.mkv", Assert.Single(_files.Files).ShortcutTarget);
        Assert.Equal(film, Find("s3://media/movies/Film.mkv", "video")?.LocalPath);
    }

    // The user put the files in the library; the other files are written next to the video, which stays as it is.
    [Fact]
    public async Task Add_DeclaredToAServerDirectoryOfTheLibrary_LeavesTheVideoToTheUser()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var nfo = Path.Combine(_directory, "source", "Film.nfo");
        File.WriteAllText(nfo, "n");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _srv.Id, Declare = true }, "movies/x");
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(2, 0), await engine.AddAsync(new AddRequest(_library, null, [film, nfo], source), CancellationToken.None));
        }

        Assert.Equal("x/Film.nfo", Assert.Single(_files.Files).Path);
        Assert.Equal(film, Find("/srv/nas/movies/x/Film.mkv", "video")?.LocalPath);
    }

    [Fact]
    public async Task Add_DeclaredToAServerDirectoryOutsideTheLibrary_IsRefused()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _srv.Id, Declare = true }, "other");
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));

        Assert.Contains("/srv/nas/other", error.Message, StringComparison.Ordinal);
        Assert.Empty(_files.Files);
    }

    [Fact]
    public async Task Add_ToALibraryOfSeveralFolders_WritesToTheOneNamed()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve(locations: ["/srv/nas/movies", "/srv/nas/films"]));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAsync(new AddRequest(_library, null, [film], IntoLibrary(settings)), CancellationToken.None));
        Assert.Contains("多个目录", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAsync(new AddRequest(_library, "/srv/nas/tv", [film], IntoLibrary(settings)), CancellationToken.None));

        Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, "/srv/nas/films/", [film], IntoLibrary(settings)), CancellationToken.None));
        Assert.Equal("/srv/nas/films", Assert.Single(_files.Files).Location);
        Assert.NotNull(Find("/srv/nas/films/Film.mkv", "video"));
    }

    [Fact]
    public async Task Add_ByShortcut_RefusesVideosThatShareOne()
    {
        File.WriteAllText(Path.Combine(_directory, "source", "Film.mkv"), "v");
        File.WriteAllText(Path.Combine(_directory, "source", "Film.mp4"), "v");
        using var engine = new WorkerEngine(Settings(), _store, NullLoggerFactory.Instance, Serve());

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.AddAsync(new AddRequest(_library, null, [Path.Combine(_directory, "source")], new SourcePlan([], null)), CancellationToken.None));
        Assert.Contains("source/Film.strm", error.Message, StringComparison.Ordinal);
        Assert.Empty(_files.Files);
    }

    [Fact]
    public async Task Add_IntoTheLibrary_RefusesFilesOfTheSameName()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "source", "a"));
        Directory.CreateDirectory(Path.Combine(_directory, "source", "b"));
        File.WriteAllText(Path.Combine(_directory, "source", "a", "Film.mkv"), "a");
        File.WriteAllText(Path.Combine(_directory, "source", "b", "Film.mkv"), "b");
        var settings = Settings();
        using var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve());

        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAsync(
            new AddRequest(_library, null, [Path.Combine(_directory, "source", "a", "Film.mkv"), Path.Combine(_directory, "source", "b", "Film.mkv")], IntoLibrary(settings)),
            CancellationToken.None));
        Assert.Empty(_files.Files);
    }

    // The entry is made first: a video it cannot be made for is not uploaded anywhere.
    [Fact]
    public async Task Add_ByShortcut_WhenTheEntryCannotBeMade_UploadsNothing()
    {
        ProgramPaths.TrueCacheToolOverride = Path.Combine(_directory, "missing-tool");
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        var server = Serve();
        var recorder = new Recorder();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server))
        {
            Assert.Equal(new AddResult(0, 1), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None, recorder));
        }

        var ended = Assert.IsType<AddFileEnded>(recorder.Events.Last());
        Assert.Equal(0, ended.File);
        Assert.False(ended.Added);
        Assert.StartsWith("无法启动", ended.Message, StringComparison.Ordinal);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "local")));
        Assert.Empty(_files.Files);
        Assert.DoesNotContain(server.Paths, p => p.EndsWith("/Refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Add_OfAVideo_ClaimsItsFile_AndReleasesIt_OnceAdded()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], IntoLibrary(settings)), CancellationToken.None));
        }

        Assert.Equal([(string?)null], _files.Claimed);
        Assert.Empty(_files.Marks);
        var log = new UploadLog(_store.Paths.Database);
        var operation = Assert.Single(log.RecentOperations(10)).Operation;
        Assert.Equal((OperationKind.Add, OperationState.Committed, "Film.mkv"), (operation.Kind, operation.State, operation.EntryPath));
        Assert.All(log.ForOperation(operation.Id), r => Assert.Equal(UploadState.Collected, r.State));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "work", "operations")));
    }

    [Fact]
    public async Task Add_ThatFailedBefore_ResumesIt_AndSkipsWhatWasUploaded()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        var copy = Path.Combine(_directory, "local", "movies", "Film.mkv");
        _files.Refuse = path => path == "Film.strm" ? FakeServer.Status(HttpStatusCode.BadRequest, "refused") : null;
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(0, 1), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        var log = new UploadLog(_store.Paths.Database);
        var failed = Assert.Single(log.RecentOperations(10)).Operation;
        Assert.Equal(OperationState.Failed, failed.State);
        Assert.Equal(failed.Id, _files.Marks["/srv/nas/movies|Film.strm"]);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(copy, written);

        _files.Refuse = null;
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(1, 0), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        Assert.Equal([null, failed.Id], _files.Claimed);
        Assert.Equal(written, File.GetLastWriteTimeUtc(copy));
        var operation = Assert.Single(log.RecentOperations(10)).Operation;
        Assert.Equal(OperationState.Committed, operation.State);
        Assert.Contains(log.ForOperation(operation.Id), r => r.Kind == UploadKind.Source && r.Succeeded && r.State == UploadState.Collected);
        Assert.Single(_files.Files);
        Assert.Empty(_files.Marks);
    }

    [Fact]
    public async Task Add_OfAFileTakenOver_SkipsIt_AndDeletesNothing()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        var other = Guid.NewGuid().ToString("N");

        // Another worker claims the file while this one adds it.
        var server = Serve((path, _) =>
        {
            if (path.EndsWith("/Working/Read", StringComparison.Ordinal))
            {
                _files.Take("/srv/nas/movies", "Film.strm", other);
            }

            return null;
        });
        var recorder = new Recorder();
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, server))
        {
            Assert.Equal(new AddResult(0, 1), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None, recorder));
        }

        Assert.Equal(new AddFileEnded(0, false, "另一个任务接管了这个文件，本机已跳过。"), recorder.Events.Last());
        Assert.Equal("video", File.ReadAllText(Path.Combine(_directory, "local", "movies", "Film.mkv")));
        Assert.Empty(_files.Files);
        Assert.Equal(other, _files.Marks["/srv/nas/movies|Film.strm"]);
        var operation = Assert.Single(new UploadLog(_store.Paths.Database).RecentOperations(10)).Operation;
        Assert.Equal(OperationState.Abandoned, operation.State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_directory, "work", "operations")));
    }

    [Fact]
    public async Task Add_AfterAFailureNotResumed_RemovesWhatItLeft()
    {
        var film = Path.Combine(_directory, "source", "Film.mkv");
        File.WriteAllText(film, "video");
        var settings = Settings();
        var source = Source(settings, new SchemeEntry { ConnectionId = _local.Id, Declare = true }, "movies");
        var copy = Path.Combine(_directory, "local", "movies", "Film.mkv");
        _files.Refuse = path => path == "Film.strm" ? FakeServer.Status(HttpStatusCode.BadRequest, "refused") : null;
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(0, 1), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        Assert.True(File.Exists(copy));
        var log = new UploadLog(_store.Paths.Database);
        var first = Assert.Single(log.RecentOperations(10)).Operation;

        // Another file of the same name cannot resume it; it removes what the first left, then fails before it uploads anything.
        File.WriteAllText(film, "video, edited");
        ProgramPaths.TrueCacheToolOverride = Path.Combine(_directory, "missing-tool");
        using (var engine = new WorkerEngine(settings, _store, NullLoggerFactory.Instance, Serve()))
        {
            Assert.Equal(new AddResult(0, 1), await engine.AddAsync(new AddRequest(_library, null, [film], source), CancellationToken.None));
        }

        Assert.False(File.Exists(copy));
        Assert.True(log.GetOperation(first.Id)!.Cleaned);
        Assert.Equal(UploadState.Removed, Assert.Single(log.ForOperation(first.Id), r => r.Kind == UploadKind.Source).State);
        Assert.False(Directory.Exists(first.Directory));
    }

    // Writes the entry the real tool would, from its command line: strm --ffmpeg f --server-url u --api-key k --name n --output o
    // --hwaccel h [--hw-device d] file. Each command line is added to args.txt next to it; one asked for nvenc fails when told to.
    private static string CreateTrueCacheTool(string directory, bool failOnHardware = false)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, "truecache.cmd");
            File.WriteAllText(
                batch,
                "@echo off\r\n>> \"%~dp0args.txt\" echo %*\r\n"
                + (failOnHardware ? "echo %* | findstr /C:\"--hwaccel nvenc\" >nul && exit /b 1\r\n" : string.Empty)
                + "set \"name=%~9\"\r\nshift\r\nshift\r\nmkdir \"%~9\\%name%\"\r\necho {}> \"%~9\\%name%\\probe.json\"\r\n");
            return batch;
        }

        var script = Path.Combine(directory, "truecache");
        File.WriteAllText(
            script,
            "#!/bin/sh\necho \"$@\" >> \"$(dirname \"$0\")/args.txt\"\n"
            + (failOnHardware ? "case \"$*\" in *\"--hwaccel nvenc\"*) exit 1;; esac\n" : string.Empty)
            + "mkdir -p \"${11}/$9\" && echo '{}' > \"${11}/$9/probe.json\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static SourcePlan IntoLibrary(WorkerSettings settings)
        => Source(settings, new SchemeEntry { ConnectionId = SchemeEntry.JellyfinLibrary, Declare = true });

    private static SourcePlan Source(WorkerSettings settings, SchemeEntry entry, string prefix = "")
        => SchemePlanner.ResolveSource(settings, new SchemeSettings { Id = Guid.NewGuid(), Name = "s", Sources = [entry] }, new LibrarySettings { Id = _library, Prefix = prefix });

    private HlsPlan Hls(WorkerSettings settings)
        => SchemePlanner.ResolveHls(
            settings,
            new SchemeSettings { Id = Guid.NewGuid(), Name = "h", Hls = [new SchemeEntry { ConnectionId = _local.Id, Declare = true }] },
            new LibrarySettings { Id = _library, Prefix = "hls" })!;

    private LocalCopy? Find(string target, string content)
        => new MediaDatabase(_store.Paths.Database).Find(target, Encoding.UTF8.GetByteCount(content), DateTime.UtcNow);

    // The server of the library, which writes its folders; extra answers a path before the rest, or leaves it with null.
    private FakeServer Serve(Func<string, string, HttpResponseMessage?>? extra = null, string[]? locations = null)
        => new(async (request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (extra?.Invoke(path, body) is { } answer)
            {
                return answer;
            }

            if (await _files.TryServeAsync(request, body) is { } upload)
            {
                return upload;
            }

            return path switch
            {
                _ when path.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
                    => FakeServer.Json(new[] { new VirtualFolder("Movies", _library.ToString(), locations ?? ["/srv/nas/movies"]) }),
                _ when path.EndsWith("/HlsWorker/Libraries", StringComparison.Ordinal)
                    => FakeServer.Json(new[] { new WorkerLibrary(_library, "Movies", [1080, 240, 144]) }),
                _ when path.EndsWith("/Protocol", StringComparison.Ordinal) => FakeServer.Json(new ProtocolVersion(3)),
                _ when path.EndsWith("/Workers", StringComparison.Ordinal) => FakeServer.Json(new WorkerRegistered(_worker)),
                _ => FakeServer.Status(HttpStatusCode.NoContent)
            };
        });

    private WorkerSettings Settings() => new()
    {
        ApiKey = "k",
        Name = "test",
        TempDirectory = Path.Combine(_directory, "work"),
        Connections = [_local, _s3, _srv]
    };

    private sealed record Written(string Path, string? ShortcutTarget, string Content);

    // Keeps what an addition told, as it told it.
    private sealed class Recorder : IProgress<AddEvent>
    {
        public ConcurrentQueue<AddEvent> Events { get; } = new();

        public void Report(AddEvent value) => Events.Enqueue(value);
    }
}
