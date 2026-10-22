using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Tests;

internal static class TestPlans
{
    public const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public const string MediaKey = "0123456789abcdef0123456789abcdef";

    public const string Attempt = "fedcba9876543210fedcba9876543210";

    // A plan as the server makes it for a 25 fps clip with one stereo track and one subtitle: 240p, the track, the subtitle, the preview.
    public static EncodePlan Plan(int expectedSegments, double duration = 13.5, string sha256 = Sha256, int audioIndex = 1, int subtitleIndex = 2)
        => new(
            sha256,
            MediaKey,
            "h264-aac-sdr-v3",
            new Probe(
                640,
                360,
                25,
                duration,
                "h264",
                "yuv420p",
                "bt709",
                "progressive",
                0,
                [new AudioTrack(audioIndex, "aac", 2, 48000, "eng", "Main", true)],
                [new SubtitleTrack(subtitleIndex, "subrip", "jpn", string.Empty, false)]),
            6,
            2,
            48000,
            expectedSegments,
            [
                new PlannedRendition("v240", RenditionKind.Video, MediaKey + "/video/240/" + Attempt, new VideoQuality(240, 240, 426, 25), -1, 0),
                new PlannedRendition("v144", RenditionKind.Video, MediaKey + "/video/144/" + Attempt, new VideoQuality(144, 144, 256, 25), -1, 0),
                new PlannedRendition("a" + audioIndex, RenditionKind.Audio, MediaKey + "/audio/" + audioIndex + "/" + Attempt, null, audioIndex, 2),
                new PlannedRendition("s" + subtitleIndex, RenditionKind.Subtitle, MediaKey + "/subtitles/" + subtitleIndex + "/" + Attempt, null, subtitleIndex, 0),
                new PlannedRendition("preview", RenditionKind.Preview, MediaKey + "/preview/" + Attempt, null, -1, 0)
            ]);
}
