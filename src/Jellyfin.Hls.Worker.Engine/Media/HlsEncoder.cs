using System.Globalization;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// The renditions of a plan that one ffmpeg run writes: all video qualities, all audio tracks, all subtitles, or the preview.
/// </summary>
/// <param name="Kind">What the renditions hold.</param>
/// <param name="Renditions">The renditions, each with the local directory it is written to.</param>
public sealed record RenditionGroup(RenditionKind Kind, IReadOnlyList<(PlannedRendition Rendition, string Directory)> Renditions);

/// <summary>
/// Encodes the renditions of a plan with ffmpeg, a group at a time: the input is decoded once for all the video qualities.
/// </summary>
public sealed class HlsEncoder
{
    /// <summary>The playlist ffmpeg writes next to the segments; it is read here and never uploaded.</summary>
    public const string PlaylistFile = "index.m3u8";

    private readonly WorkerSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsEncoder"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    public HlsEncoder(WorkerSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Divides the renditions of a plan into the groups that are encoded together, in this order: video, audio, subtitles, preview.
    /// Each rendition gets a directory below the root named by its place in the plan, not by its key, which comes from the server.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="root">The local directory of the outputs.</param>
    /// <returns>The groups that have renditions.</returns>
    public static IReadOnlyList<RenditionGroup> GetGroups(EncodePlan plan, string root)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var renditions = plan.Renditions.Select((r, i) => (Rendition: r, Directory: Path.Combine(root, i.ToString("D3", CultureInfo.InvariantCulture)))).ToList();
        return new[] { RenditionKind.Video, RenditionKind.Audio, RenditionKind.Subtitle, RenditionKind.Preview }
            .Select(kind => new RenditionGroup(kind, renditions.Where(r => r.Rendition.Kind == kind).ToList()))
            .Where(g => g.Renditions.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Encodes a group.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="group">The group.</param>
    /// <param name="input">The input file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EncodeAsync(EncodePlan plan, RenditionGroup group, string input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);
        foreach (var (_, directory) in group.Renditions)
        {
            Directory.CreateDirectory(directory);
        }

        var arguments = group.Kind switch
        {
            RenditionKind.Video => VideoArguments(plan, group, input),
            RenditionKind.Audio => AudioArguments(plan, group, input),
            RenditionKind.Subtitle => SubtitleArguments(plan, group, input),
            RenditionKind.Preview => PreviewArguments(plan, group, input),
            _ => throw new InvalidDataException("不支持的档位类型：" + group.Kind)
        };
        await ProcessRunner.RunAsync(_settings.Ffmpeg, arguments, cancellationToken, group.Renditions[0].Directory).ConfigureAwait(false);
        if (group.Kind == RenditionKind.Subtitle)
        {
            foreach (var (_, directory) in group.Renditions)
            {
                await AddTimestampMapAsync(Path.Combine(directory, "subtitle.vtt"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Gets the arguments of the video qualities: one decode, deinterlaced and tone mapped once, then split and scaled for each quality.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="group">The video renditions.</param>
    /// <param name="input">The input file.</param>
    /// <returns>The arguments.</returns>
    internal List<string> VideoArguments(EncodePlan plan, RenditionGroup group, string input)
    {
        var vaapi = _settings.Encoder == "h264_vaapi";
        var arguments = new List<string>();
        if (vaapi)
        {
            arguments.AddRange(["-vaapi_device", _settings.HardwareDevice]);
        }

        arguments.AddRange(InputArguments(input));
        var count = group.Renditions.Count;
        var graph = new List<string>
        {
            // A video that ends before the media is held on its last frame, and every output is cut at the duration of the media,
            // so the video has the segments the plan expects, like the audio.
            $"[0:{Number(plan.Probe.VideoIndex)}]{CommonFilters(plan.Probe)},tpad=stop=-1:stop_mode=clone,split={Number(count)}{string.Concat(Enumerable.Range(0, count).Select(i => $"[s{Number(i)}]"))}"
        };
        for (var i = 0; i < count; i++)
        {
            var quality = Quality(group.Renditions[i].Rendition);
            // The output is tagged BT.709, so the pixels of a BT.601 source are converted to it; an HDR source is BT.709 already.
            graph.Add($"[s{Number(i)}]scale={Number(quality.Width)}:{Number(quality.Height)}:flags=lanczos:out_color_matrix=bt709:out_range=tv,setsar=1,fps={Number(quality.FramesPerSecond)},{(vaapi ? "format=nv12,hwupload" : "format=yuv420p")}[v{Number(i)}]");
        }

        arguments.AddRange(["-filter_complex", string.Join(';', graph)]);
        for (var i = 0; i < count; i++)
        {
            var quality = Quality(group.Renditions[i].Rendition);
            arguments.AddRange(["-map", $"[v{Number(i)}]", "-an", "-sn", "-dn", "-c:v", _settings.Encoder]);
            arguments.AddRange(
            [
                "-g", Number(Math.Max(1, Math.Round(quality.FramesPerSecond * plan.KeyframeSeconds))),
                "-force_key_frames", $"expr:gte(t,n_forced*{Number(plan.KeyframeSeconds)})",
                "-flags", "+cgop",
                "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
                "-t", Number(plan.Probe.DurationSeconds)
            ]);
            arguments.AddRange(EncoderArguments());
            arguments.AddRange(OutputArguments());
            arguments.AddRange(HlsArguments(plan, group.Renditions[i].Directory));
        }

        return arguments;
    }

    /// <summary>
    /// Gets the arguments of the audio tracks: AAC, stereo or mono as the plan says, at the sample rate of the plan.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="group">The audio renditions.</param>
    /// <param name="input">The input file.</param>
    /// <returns>The arguments.</returns>
    internal List<string> AudioArguments(EncodePlan plan, RenditionGroup group, string input)
    {
        var arguments = InputArguments(input);
        var rate = Number(plan.AudioSampleRate);
        foreach (var (rendition, directory) in group.Renditions)
        {
            if (plan.Probe.Audio.All(a => a.Index != rendition.SourceIndex))
            {
                throw new InvalidDataException("任务引用了不存在的音轨 " + rendition.SourceIndex + "。");
            }

            if (rendition.Channels is < 1 or > 2)
            {
                throw new InvalidDataException("音轨 " + rendition.SourceIndex + " 的声道数应为 1–2，任务为 " + rendition.Channels + "。");
            }

            arguments.AddRange(
            [
                "-map", "0:" + Number(rendition.SourceIndex), "-vn", "-sn", "-dn",
                "-c:a", "aac", "-b:a", rendition.Channels == 1 ? "96k" : "160k", "-ac", Number(rendition.Channels), "-ar", rate,
                // A track that ends before the media is padded with silence and one that runs longer is cut, so every track
                // has the segments the plan expects.
                "-af", $"aresample={rate}:async=1:first_pts=0,apad=whole_dur={Number(plan.Probe.DurationSeconds)}",
                "-t", Number(plan.Probe.DurationSeconds)
            ]);
            arguments.AddRange(OutputArguments());
            arguments.AddRange(HlsArguments(plan, directory));
        }

        return arguments;
    }

    /// <summary>
    /// Gets the arguments of the subtitles: text subtitles converted to WebVTT.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="group">The subtitle renditions.</param>
    /// <param name="input">The input file.</param>
    /// <returns>The arguments.</returns>
    internal List<string> SubtitleArguments(EncodePlan plan, RenditionGroup group, string input)
    {
        var arguments = InputArguments(input);
        foreach (var (rendition, directory) in group.Renditions)
        {
            if (plan.Probe.Subtitles.All(s => s.Index != rendition.SourceIndex))
            {
                throw new InvalidDataException("任务引用了不存在的字幕 " + rendition.SourceIndex + "。");
            }

            arguments.AddRange(["-map", "0:" + Number(rendition.SourceIndex), "-c:s", "webvtt"]);
            arguments.AddRange(OutputArguments());
            arguments.Add(Path.Combine(directory, "subtitle.vtt"));
        }

        return arguments;
    }

    /// <summary>
    /// Gets the arguments of the preview: a frame from the first five seconds, 480 pixels wide.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="group">The preview rendition.</param>
    /// <param name="input">The input file.</param>
    /// <returns>The arguments.</returns>
    internal List<string> PreviewArguments(EncodePlan plan, RenditionGroup group, string input)
    {
        var arguments = InputArguments(input);
        arguments.AddRange(
        [
            "-ss", Number(Math.Min(5, plan.Probe.DurationSeconds / 2)),
            "-map", "0:" + Number(plan.Probe.VideoIndex),
            "-frames:v", "1",
            // A video that ends before the seek point is held on its last frame, so there is always a frame to take. The size
            // follows the display aspect ratio with square pixels, as a JPEG is shown without its pixel aspect.
            "-vf", CommonFilters(plan.Probe) + ",tpad=stop=-1:stop_mode=clone,scale=480:trunc(480/dar/2)*2,setsar=1",
            "-update", "1"
        ]);
        arguments.AddRange(OutputArguments());
        arguments.Add(Path.Combine(group.Renditions[0].Directory, "preview.jpg"));
        return arguments;
    }

    // HLS players expect the cue times of WebVTT to start at the start of the media.
    private static async Task AddTimestampMapAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        var header = end < 0 ? text : text[..end];
        if (!header.TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            throw new InvalidDataException("ffmpeg 写出的字幕不是 WebVTT。");
        }

        var rest = end < 0 ? "\n" : text[end..];
        await File.WriteAllTextAsync(path, header.TrimEnd('\r') + "\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000" + rest, cancellationToken).ConfigureAwait(false);
    }

    private static VideoQuality Quality(PlannedRendition rendition)
        => rendition.Video ?? throw new InvalidDataException("视频档位 " + rendition.Key + " 缺少质量信息。");

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    // Deinterlacing only touches interlaced frames; PQ and HLG are tone mapped to SDR, which is what the profile publishes.
    private static string CommonFilters(Probe probe)
    {
        var filters = new List<string> { "bwdif=mode=send_frame:parity=auto:deint=interlaced" };
        if (probe.ColorTransfer is "smpte2084" or "arib-std-b67")
        {
            filters.AddRange(["zscale=t=linear:npl=100", "format=gbrpf32le", "zscale=p=bt709", "tonemap=tonemap=mobius:desat=0", "zscale=t=bt709:m=bt709:r=tv"]);
        }

        return string.Join(',', filters);
    }

    // ffmpeg places the init segment next to the playlist by the last forward slash of its path, so on Windows the paths must use
    // forward slashes: with backslashes every output of a run would write its init segment into the working directory.
    private static string[] HlsArguments(EncodePlan plan, string directory) =>
    [
        "-f", "hls",
        "-hls_time", Number(plan.SegmentSeconds),
        "-hls_list_size", "0",
        "-hls_playlist_type", "vod",
        "-hls_segment_type", "fmp4",
        "-hls_flags", "independent_segments",

        // Timestamps as decoded, as the transcoding of the server writes fMP4: the video keeps its first picture at 0 instead of
        // being shifted by its B-frame delay, so all renditions share one timeline; frag_discont writes the decode time into tfdt.
        "-avoid_negative_ts", "disabled",
        "-hls_segment_options", "movflags=+frag_discont+skip_sidx",
        "-hls_fmp4_init_filename", "init.mp4",
        "-hls_segment_filename", Path.Combine(directory, "segment-%06d.m4s").Replace('\\', '/'),
        Path.Combine(directory, PlaylistFile).Replace('\\', '/')
    ];

    private List<string> InputArguments(string input)
        => ["-hide_banner", "-loglevel", "warning", "-xerror", "-nostdin", "-y", "-threads", Number(_settings.Threads), "-i", input];

    // Output options apply to the output that follows them, so every output gets them.
    private string[] OutputArguments() => ["-threads", Number(_settings.Threads), "-map_metadata", "-1", "-map_chapters", "-1"];

    private List<string> EncoderArguments()
    {
        List<string> arguments = _settings.Encoder switch
        {
            "libx264" => ["-preset", "veryfast", "-crf", "23", "-bf", "16", "-b_strategy", "1", "-sc_threshold", "0"],
            "h264_nvenc" => ["-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-bf", "3", "-forced-idr", "1"],
            // The forced keyframes must be IDR frames, or the muxer cannot cut a segment at them; QSV and AMF force plain I frames
            // by default.
            "h264_qsv" => ["-preset", "veryfast", "-global_quality", "23", "-bf", "3", "-forced_idr", "1"],
            "h264_amf" => ["-quality", "speed", "-rc", "cqp", "-qp_i", "23", "-qp_p", "23", "-forced_idr", "1"],
            "h264_vaapi" => ["-qp", "23"],
            _ => throw new ArgumentException("不支持的编码器：" + _settings.Encoder)
        };
        if (_settings.Encoder == "h264_nvenc" && _settings.HardwareDevice.Length > 0)
        {
            arguments.AddRange(["-gpu", _settings.HardwareDevice]);
        }

        return arguments;
    }
}
