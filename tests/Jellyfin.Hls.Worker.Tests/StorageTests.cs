using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-storage-" + Guid.NewGuid().ToString("N"));

    public StorageTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "root"));
        Directory.CreateDirectory(Path.Combine(_directory, "files"));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task Prepare_WritesTheMarkerOnce_AndLeavesNoTestFile()
    {
        using var connection = Local();
        var target = new HlsTarget(connection, "media/hls", "local:media/hls", NullLogger.Instance);

        Assert.True(await target.PrepareAsync(CancellationToken.None));
        Assert.False(await target.PrepareAsync(CancellationToken.None));

        var hls = Path.Combine(_directory, "root", "media", "hls");
        Assert.Equal([HlsTarget.MarkerName], Directory.GetFiles(hls).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Upload_WritesEveryFile_BelowThePath()
    {
        using var connection = Local();
        var target = new HlsTarget(connection, "hls", "local:hls", NullLogger.Instance);
        var files = Enumerable.Range(0, 11).Select(i =>
        {
            var local = Path.Combine(_directory, "files", i + ".bin");
            File.WriteAllBytes(local, new byte[i * 100]);
            return ($"m/video/240/a/segment-{i:D6}.m4s", local);
        }).ToList();

        await target.UploadAsync(files, new UploadLimiter(4), CancellationToken.None);

        var written = Directory.GetFiles(Path.Combine(_directory, "root", "hls", "m", "video", "240", "a")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(11, written.Count);
        Assert.Equal(1000, new FileInfo(written[10]).Length);
    }

    [Fact]
    public async Task Upload_OverTwoTargets_WritesNoMoreFilesAtATimeThanTheLimiter()
    {
        using var connection = Local();
        var a = new HlsTarget(connection, "a", "local:a", NullLogger.Instance);
        var b = new HlsTarget(connection, "b", "local:b", NullLogger.Instance);
        var files = Enumerable.Range(0, 20).Select(i =>
        {
            var local = Path.Combine(_directory, "files", i + ".bin");
            File.WriteAllBytes(local, new byte[10_000]);
            return ($"m/{i}", local);
        }).ToList();
        var limiter = new UploadLimiter(3);
        var most = 0;
        void Uploaded(string path, string localFile, bool succeeded, string? error)
        {
            var running = limiter.Running;
            int seen;
            while (running > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, running, seen) != seen)
            {
            }
        }

        await Task.WhenAll(
            a.UploadAsync(files, limiter, CancellationToken.None, Uploaded),
            b.UploadAsync(files, limiter, CancellationToken.None, Uploaded));

        Assert.InRange(most, 1, 3);
        Assert.Equal(0, limiter.Running);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(_directory, "root", "a", "m")).Length);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(_directory, "root", "b", "m")).Length);
    }

    [Fact]
    public async Task Destination_UploadsToEveryDirectoryInOrder_AndRecordsEachFile()
    {
        using var connection = Local();
        var destination = new HlsDestination(
            [new HlsTarget(connection, "a", "local:a", NullLogger.Instance), new HlsTarget(connection, "b", "local:b", NullLogger.Instance)],
            "/srv/a",
            null);
        var local = Path.Combine(_directory, "files", "x");
        File.WriteAllBytes(local, new byte[7]);
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var library = Guid.NewGuid();

        await destination.UploadAsync([("m/x", local), ("m/y", local)], new UploadLimiter(4), log, library, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_directory, "root", "a", "m", "y")));
        Assert.True(File.Exists(Path.Combine(_directory, "root", "b", "m", "x")));
        var records = log.Recent(10).Reverse().ToList();
        Assert.Equal(["local:a/m/x", "local:a/m/y"], records.Take(2).Select(r => r.Destination).Order(StringComparer.Ordinal));
        Assert.Equal(["local:b/m/x", "local:b/m/y"], records.Skip(2).Select(r => r.Destination).Order(StringComparer.Ordinal));
        Assert.All(records, r => Assert.Equal((UploadKind.Hls, (Guid?)library, 7L, true), (r.Kind, r.LibraryId, r.Size, r.Succeeded)));
    }

    [Fact]
    public async Task Put_ToALocalDirectory_TellsTheBytesSent()
    {
        using var connection = Local();
        using var session = await connection.OpenAsync(CancellationToken.None);
        var local = Path.Combine(_directory, "files", "big");
        File.WriteAllBytes(local, new byte[300_000]);
        var sent = new List<long>();

        await session.PutAsync("a/big", local, CancellationToken.None, sent.Add);

        Assert.NotEmpty(sent);
        Assert.Equal(300_000, sent[^1]);
        Assert.Equal(sent.Order(), sent);
    }

    [Fact]
    public async Task MoveDirectory_TellsWhetherThereWasOne()
    {
        using var connection = Local();
        using var session = await connection.OpenAsync(CancellationToken.None);
        var local = Path.Combine(_directory, "files", "x");
        File.WriteAllText(local, "x");
        await session.PutAsync("a/b/x", local, CancellationToken.None);

        Assert.False(await session.MoveDirectoryAsync("a/missing", "a/c", CancellationToken.None));
        Assert.True(await session.MoveDirectoryAsync("a/b", "d/e", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_directory, "root", "d", "e", "x")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "root", "a", "b")));
    }

    [Fact]
    public async Task Upload_ToAMissingRoot_Fails()
    {
        using var connection = StorageConnection.Create(
            new ConnectionSettings { Name = "local", Type = ConnectionType.Worker, LocalRoot = Path.Combine(_directory, "missing") },
            NullLogger.Instance);
        var target = new HlsTarget(connection, string.Empty, "local", NullLogger.Instance);
        var local = Path.Combine(_directory, "files", "x");
        File.WriteAllText(local, "x");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => target.UploadAsync([("a/b", local)], new UploadLimiter(4), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_directory, "missing")));
    }

    private StorageConnection Local() => StorageConnection.Create(
        new ConnectionSettings { Name = "local", Type = ConnectionType.Worker, LocalRoot = Path.Combine(_directory, "root") },
        NullLogger.Instance);
}
