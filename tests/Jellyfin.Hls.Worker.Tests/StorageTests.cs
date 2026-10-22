using Jellyfin.Hls.Worker.Configuration;
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
        var target = new HlsTarget(connection, new HlsTargetSettings { Connection = "local", Path = "media/hls" }, NullLogger.Instance);

        Assert.True(await target.PrepareAsync(CancellationToken.None));
        Assert.False(await target.PrepareAsync(CancellationToken.None));

        var hls = Path.Combine(_directory, "root", "media", "hls");
        Assert.Equal([HlsTarget.MarkerName], Directory.GetFiles(hls).Select(Path.GetFileName));
        Assert.Equal(StoragePath.JoinServer(Path.Combine(_directory, "root"), "media/hls"), target.Base);
    }

    [Fact]
    public async Task Upload_WritesEveryFile_BelowThePath()
    {
        using var connection = Local();
        var target = new HlsTarget(connection, new HlsTargetSettings { Connection = "local", Path = "hls" }, NullLogger.Instance);
        var files = Enumerable.Range(0, 11).Select(i =>
        {
            var local = Path.Combine(_directory, "files", i + ".bin");
            File.WriteAllBytes(local, new byte[i * 100]);
            return ($"m/video/240/a/segment-{i:D6}.m4s", local);
        }).ToList();

        await target.UploadAsync(files, CancellationToken.None);

        var written = Directory.GetFiles(Path.Combine(_directory, "root", "hls", "m", "video", "240", "a")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(11, written.Count);
        Assert.Equal(1000, new FileInfo(written[10]).Length);
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
            new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "missing") },
            NullLogger.Instance);
        var target = new HlsTarget(connection, new HlsTargetSettings { Connection = "local" }, NullLogger.Instance);
        var local = Path.Combine(_directory, "files", "x");
        File.WriteAllText(local, "x");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => target.UploadAsync([("a/b", local)], CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_directory, "missing")));
    }

    private StorageConnection Local() => StorageConnection.Create(
        new ConnectionSettings { Name = "local", Type = ConnectionType.Local, LocalRoot = Path.Combine(_directory, "root") },
        NullLogger.Instance);
}
