using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class HlsEncoderTests
{
    private static readonly HlsEncoder _nvenc = new(new WorkerSettings { Encoder = "h264_nvenc", HardwareDevice = "1" });

    [Theory]
    [InlineData("h264_nvenc", "bt709", 360, "bt709", 0, "yuv420p", true)]
    [InlineData("h264_nvenc", "", 1080, "bt709", 0, "yuv420p", true)]
    [InlineData("h264_nvenc", "unknown", 720, "", 0, "yuv420p10le", true)]
    [InlineData("h264_nvenc", "", 576, "bt709", 0, "yuv420p", false)]
    [InlineData("h264_nvenc", "bt470bg", 1080, "bt709", 0, "yuv420p", false)]
    [InlineData("h264_nvenc", "bt2020nc", 2160, "smpte2084", 0, "yuv420p10le", false)]
    [InlineData("h264_nvenc", "bt709", 1080, "arib-std-b67", 0, "yuv420p10le", false)]
    [InlineData("h264_nvenc", "bt709", 1080, "bt709", 90, "yuv420p", false)]
    [InlineData("h264_nvenc", "bt709", 1080, "bt709", 0, "yuvj420p", false)]
    [InlineData("libx264", "bt709", 1080, "bt709", 0, "yuv420p", false)]
    [InlineData("h264_qsv", "bt709", 1080, "bt709", 0, "yuv420p", false)]
    public void UsesGpu_OnlyForNvenc_AndBt709Sdr(string encoder, string colorSpace, int height, string transfer, int rotation, string pixelFormat, bool expected)
    {
        var probe = TestPlans.Plan(3).Probe with { ColorSpace = colorSpace, Height = height, ColorTransfer = transfer, Rotation = rotation, PixelFormat = pixelFormat };

        Assert.Equal(expected, new HlsEncoder(new WorkerSettings { Encoder = encoder }).UsesGpu(probe));
    }

    [Fact]
    public void VideoArguments_OnTheGpu_DecodeAndScaleThere()
    {
        var plan = TestPlans.Plan(3);

        var arguments = _nvenc.VideoArguments(plan, Video(plan), "in.mkv", true);

        Assert.Equal(["-hwaccel", "cuda", "-hwaccel_device", "1", "-hwaccel_output_format", "cuda"], arguments.Take(6));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("bwdif_cuda=", graph, StringComparison.Ordinal);
        Assert.Contains("scale_cuda=426:240:interp_algo=lanczos:format=yuv420p", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("]scale=", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("format=yuv420p[", graph, StringComparison.Ordinal);
        Assert.Equal(2, arguments.Select((a, i) => (a, i)).Count(x => x.a == "-gpu" && arguments[x.i + 1] == "1"));
    }

    [Fact]
    public void VideoArguments_OffTheGpu_AreAsBefore()
    {
        var plan = TestPlans.Plan(3);

        var arguments = _nvenc.VideoArguments(plan, Video(plan), "in.mkv", false);

        Assert.DoesNotContain("-hwaccel", arguments);
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("bwdif=mode", graph, StringComparison.Ordinal);
        Assert.Contains("out_color_matrix=bt709", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("cuda", graph, StringComparison.Ordinal);
    }

    private static RenditionGroup Video(EncodePlan plan)
        => HlsEncoder.GetGroups(plan, Path.Combine(Path.GetTempPath(), "hls-encoder-test")).Single(g => g.Kind == RenditionKind.Video);
}
