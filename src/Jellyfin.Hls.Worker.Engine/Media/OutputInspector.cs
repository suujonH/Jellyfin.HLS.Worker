using System.Globalization;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// Reads what ffmpeg wrote for a rendition and checks it the way the server will, so a rendition the server would refuse
/// fails here, before its upload.
/// </summary>
public static class OutputInspector
{
    // The slack of the server: ffmpeg writes durations with six decimals, and the frame rate is rounded.
    private const double DurationSlackSeconds = 0.001;

    private const int AudioFrameSamples = 1024;

    /// <summary>
    /// Gets the files to upload and the report of a rendition.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="rendition">The rendition.</param>
    /// <param name="directory">Where ffmpeg wrote it.</param>
    /// <returns>The report.</returns>
    /// <exception cref="InvalidDataException">The rendition is not what the server accepts; the message says why.</exception>
    public static RenditionOutput Inspect(EncodePlan plan, PlannedRendition rendition, string directory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rendition);
        return rendition.Kind switch
        {
            RenditionKind.Video => InspectVideo(plan, rendition, directory),
            RenditionKind.Audio => InspectAudio(plan, rendition, directory),
            RenditionKind.Subtitle => InspectSubtitle(plan, rendition, directory),
            RenditionKind.Preview => new RenditionOutput(rendition.Key, new RenditionSummary(), [File(directory, "preview.jpg")], []),
            _ => throw new InvalidDataException("不支持的档位类型：" + rendition.Kind)
        };
    }

    /// <summary>
    /// Reads the codec string of an H.264 init segment: <c>avc1.</c> and the profile, constraints and level of its avcC box.
    /// </summary>
    /// <param name="init">The init segment.</param>
    /// <returns>The codec string.</returns>
    /// <exception cref="InvalidDataException">The segment has no avcC box.</exception>
    public static string ReadAvcCodec(ReadOnlySpan<byte> init)
    {
        var box = init.IndexOf("avcC"u8);
        if (box < 0 || box + 8 > init.Length)
        {
            throw new InvalidDataException("H.264 初始化分片缺少 avcC。");
        }

        // avcC: the box type, the version, then profile, constraints and level.
        return "avc1." + Convert.ToHexString(init.Slice(box + 5, 3));
    }

    /// <summary>
    /// Reads the segments of a playlist: the file of each and its duration.
    /// </summary>
    /// <param name="playlist">The playlist.</param>
    /// <returns>The segments.</returns>
    /// <exception cref="InvalidDataException">The playlist is not one ffmpeg writes.</exception>
    public static IReadOnlyList<(string File, double Duration)> ReadPlaylist(string playlist)
    {
        var segments = new List<(string File, double Duration)>();
        double? duration = null;
        foreach (var raw in System.IO.File.ReadLines(playlist))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line[8..];
                var comma = value.IndexOf(',', StringComparison.Ordinal);
                duration = double.Parse(comma < 0 ? value : value[..comma], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segments.Add((line, duration ?? throw new InvalidDataException("播放列表中的分片 " + line + " 没有 EXTINF。")));
                duration = null;
            }
        }

        return segments;
    }

    private static RenditionOutput InspectVideo(EncodePlan plan, PlannedRendition rendition, string directory)
    {
        var quality = rendition.Video ?? throw new InvalidDataException("视频档位 " + rendition.Key + " 缺少质量信息。");
        var (files, durations, peak, average) = InspectSegments(plan, rendition, directory, 1 / quality.FramesPerSecond);
        var codec = ReadAvcCodec(System.IO.File.ReadAllBytes(Path.Combine(directory, "init.mp4")));
        return new RenditionOutput(
            rendition.Key,
            new RenditionSummary(
                Quality: quality.Name,
                Width: quality.Width,
                Height: quality.Height,
                FramesPerSecond: quality.FramesPerSecond,
                Bandwidth: peak,
                AverageBandwidth: average,
                Codec: codec),
            files,
            durations);
    }

    private static RenditionOutput InspectAudio(EncodePlan plan, PlannedRendition rendition, string directory)
    {
        var track = plan.Probe.Audio.FirstOrDefault(a => a.Index == rendition.SourceIndex)
            ?? throw new InvalidDataException("任务引用了不存在的音轨 " + rendition.SourceIndex + "。");
        var (files, durations, peak, _) = InspectSegments(plan, rendition, directory, (double)AudioFrameSamples / plan.AudioSampleRate);
        return new RenditionOutput(
            rendition.Key,
            new RenditionSummary(
                Bandwidth: peak,
                SourceIndex: track.Index,
                Language: track.Language,
                Title: track.Title,
                Channels: rendition.Channels,
                Default: track.Default),
            files,
            durations);
    }

    private static RenditionOutput InspectSubtitle(EncodePlan plan, PlannedRendition rendition, string directory)
    {
        var track = plan.Probe.Subtitles.FirstOrDefault(s => s.Index == rendition.SourceIndex)
            ?? throw new InvalidDataException("任务引用了不存在的字幕 " + rendition.SourceIndex + "。");
        return new RenditionOutput(
            rendition.Key,
            new RenditionSummary(SourceIndex: track.Index, Language: track.Language, Title: track.Title, Default: track.Default),
            [File(directory, "subtitle.vtt")],
            []);
    }

    // The init segment and the segments, numbered from 0 without gaps, as many as the plan expects give or take one, each but the
    // last as long as a segment of the plan within a frame, and the bit rates the master playlist announces.
    private static (List<OutputFile> Files, List<double> Durations, int Peak, int Average) InspectSegments(
        EncodePlan plan,
        PlannedRendition rendition,
        string directory,
        double frame)
    {
        var segments = ReadPlaylist(Path.Combine(directory, HlsEncoder.PlaylistFile));
        if (segments.Count == 0 || Math.Abs(segments.Count - plan.ExpectedSegments) > 1)
        {
            throw new InvalidDataException($"档位 {rendition.Key} 有 {segments.Count} 个分片，任务预期 {plan.ExpectedSegments} 个。");
        }

        var files = new List<OutputFile> { File(directory, "init.mp4") };
        var durations = new List<double>();
        var tolerance = DurationSlackSeconds + frame;
        long bytes = 0;
        double total = 0, peak = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var (name, duration) = segments[i];
            var expected = "segment-" + i.ToString("D6", CultureInfo.InvariantCulture) + ".m4s";
            if (!string.Equals(name, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"档位 {rendition.Key} 的第 {i} 个分片名为 {name}，应为 {expected}。");
            }

            var last = i == segments.Count - 1;
            if (!double.IsFinite(duration) || duration <= 0
                || (last ? duration > plan.SegmentSeconds + tolerance : Math.Abs(duration - plan.SegmentSeconds) > tolerance))
            {
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"档位 {rendition.Key} 的第 {i} 个分片长 {duration} 秒，分片应为 {plan.SegmentSeconds} 秒；源文件的关键帧或时间戳可能有问题。"));
            }

            var file = File(directory, name);
            files.Add(file);
            durations.Add(duration);
            bytes += file.Length;
            total += duration;
            peak = Math.Max(peak, file.Length * 8 / duration);
        }

        var peakRate = (int)Math.Min(int.MaxValue, Math.Ceiling(peak));
        var averageRate = (int)Math.Min(int.MaxValue, Math.Ceiling(bytes * 8 / total));
        if (peakRate <= 0 || averageRate <= 0)
        {
            throw new InvalidDataException("档位 " + rendition.Key + " 的分片为空。");
        }

        return (files, durations, peakRate, averageRate);
    }

    private static OutputFile File(string directory, string name)
    {
        var info = new FileInfo(Path.Combine(directory, name));
        if (!info.Exists)
        {
            throw new InvalidDataException("ffmpeg 没有写出 " + name + "。");
        }

        return new OutputFile(name, info.Length);
    }
}
