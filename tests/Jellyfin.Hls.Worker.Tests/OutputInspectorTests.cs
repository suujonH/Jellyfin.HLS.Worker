using System.Globalization;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class OutputInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-inspect-" + Guid.NewGuid().ToString("N"));

    public OutputInspectorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Inspect_Video_ReportsFilesDurationsAndRates()
    {
        WriteRendition([6, 6, 1.5], [6000, 3000, 750]);
        var plan = TestPlans.Plan(expectedSegments: 3);

        var output = OutputInspector.Inspect(plan, plan.Renditions[0], _directory);

        Assert.Equal(["init.mp4", "segment-000000.m4s", "segment-000001.m4s", "segment-000002.m4s"], output.Files.Select(f => f.Path));
        Assert.Equal([6, 6, 1.5], output.SegmentDurations);
        Assert.Equal("avc1.64001F", output.Summary.Codec);
        Assert.Equal(8000, output.Summary.Bandwidth);
        Assert.Equal((int)Math.Ceiling(9750 * 8 / 13.5), output.Summary.AverageBandwidth);
        Assert.Equal(240, output.Summary.Quality);
        Assert.Equal(426, output.Summary.Width);
    }

    [Theory]
    [InlineData(new[] { 6.0, 5.5, 1 })]
    [InlineData(new[] { 6.0, 6, 6.2 })]
    [InlineData(new[] { 6.0, 6, 0 })]
    public void Inspect_RefusesDurationsTheServerRefuses(double[] durations)
    {
        WriteRendition(durations, [1000, 1000, 1000]);
        var plan = TestPlans.Plan(expectedSegments: 3);

        Assert.Throws<InvalidDataException>(() => OutputInspector.Inspect(plan, plan.Renditions[0], _directory));
    }

    [Fact]
    public void Inspect_RefusesTooFewSegments()
    {
        WriteRendition([6], [1000]);
        var plan = TestPlans.Plan(expectedSegments: 3);

        Assert.Throws<InvalidDataException>(() => OutputInspector.Inspect(plan, plan.Renditions[0], _directory));
    }

    [Fact]
    public void Inspect_AcceptsTheLastSegmentWithinAFrame()
    {
        WriteRendition([6.04, 5.98, 6.04], [1000, 1000, 1000]);
        var plan = TestPlans.Plan(expectedSegments: 3);

        var output = OutputInspector.Inspect(plan, plan.Renditions[0], _directory);

        Assert.Equal(3, output.SegmentDurations.Count);
    }

    [Fact]
    public void ReadAvcCodec_RefusesASegmentWithoutAvcC()
        => Assert.Throws<InvalidDataException>(() => OutputInspector.ReadAvcCodec("ftypisom"u8));

    private void WriteRendition(double[] durations, int[] sizes)
    {
        // An avcC box: size, type, version 1, then profile 0x64, constraints 0x00, level 0x1F.
        File.WriteAllBytes(Path.Combine(_directory, "init.mp4"), [0, 0, 0, 20, .. "ftypisom"u8, 0, 0, 0, 16, .. "avcC"u8, 1, 0x64, 0x00, 0x1F, 0xFF]);
        var playlist = new List<string> { "#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-TARGETDURATION:6", "#EXT-X-MAP:URI=\"init.mp4\"" };
        for (var i = 0; i < durations.Length; i++)
        {
            var name = "segment-" + i.ToString("D6", CultureInfo.InvariantCulture) + ".m4s";
            File.WriteAllBytes(Path.Combine(_directory, name), new byte[sizes[i]]);
            playlist.Add("#EXTINF:" + durations[i].ToString("0.000000", CultureInfo.InvariantCulture) + ",");
            playlist.Add(name);
        }

        playlist.Add("#EXT-X-ENDLIST");
        File.WriteAllLines(Path.Combine(_directory, HlsEncoder.PlaylistFile), playlist);
    }
}
