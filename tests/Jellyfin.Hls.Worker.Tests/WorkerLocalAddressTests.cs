using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class WorkerLocalAddressTests
{
    private static readonly Guid _worker = Guid.Parse("3227ce1e-0697-54c5-94af-25ea66d69fc7");

    [Theory]
    [InlineData(@"worker-local://3227ce1e069754c594af25ea66d69fc7/D:\Movies\a.mkv", @"D:\Movies\a.mkv")]
    [InlineData("worker-local://3227ce1e069754c594af25ea66d69fc7//srv/a.mkv", "/srv/a.mkv")]
    [InlineData("WORKER-LOCAL://3227CE1E069754C594AF25EA66D69FC7//srv/电影 a.mkv", "/srv/电影 a.mkv")]
    public void TryParse_ReadsTheWorkerAndThePath(string value, string path)
    {
        Assert.True(WorkerLocalAddress.TryParse(value, out var worker, out var localPath));
        Assert.Equal(_worker, worker);
        Assert.Equal(path, localPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("s3://bucket/a.mkv")]
    [InlineData("worker-local://3227ce1e069754c594af25ea66d69fc7")]
    [InlineData("worker-local://3227ce1e069754c594af25ea66d69fc7/")]
    [InlineData("worker-local://3227ce1e069754c594af25ea66d69fc7:/srv/a")]
    [InlineData("worker-local://3227ce1e-0697-54c5-94af-25ea66d69fc7//srv/a")]
    [InlineData("worker-local://00000000000000000000000000000000//srv/a")]
    [InlineData("worker-local://3227ce1e069754c594af25ea66d69fc7//srv/a\nb")]
    public void TryParse_RefusesWhatIsNoAddress(string? value)
    {
        Assert.False(WorkerLocalAddress.TryParse(value, out var worker, out var path));
        Assert.Equal(Guid.Empty, worker);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void Format_IsReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "a b", "电影.mkv");

        var address = WorkerLocalAddress.Format(_worker, path);

        Assert.Equal("worker-local://3227ce1e069754c594af25ea66d69fc7/" + path, address);
        Assert.True(WorkerLocalAddress.TryParse(address, out var worker, out var localPath));
        Assert.Equal((_worker, path), (worker, localPath));
    }

    [Fact]
    public void Format_RefusesAWorkerWithoutIdAndARelativePath()
    {
        Assert.Throws<ArgumentException>(() => WorkerLocalAddress.Format(Guid.Empty, Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => WorkerLocalAddress.Format(_worker, "movies/a.mkv"));
        Assert.Throws<ArgumentException>(() => WorkerLocalAddress.Format(_worker, string.Empty));
    }
}
