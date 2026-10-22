using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker;

/// <summary>
/// Checks everything a run needs without claiming a job: ffmpeg encodes with the encoder, the work directory is writable, the
/// server speaks the protocol and takes the key, every connection is reached, and the server can use the root of the HLS store.
/// </summary>
public static class Preflight
{
    /// <summary>
    /// Runs the checks, in order; the first that fails throws.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="engine">The engine of the settings.</param>
    /// <param name="logger">Where the steps are reported.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task CheckAsync(WorkerSettings settings, WorkerEngine engine, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation("检查 FFmpeg / FFprobe……");
        var version = await ProcessRunner.RunAsync(settings.Ffmpeg, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
        await ProcessRunner.RunAsync(settings.Ffprobe, ["-hide_banner", "-version"], cancellationToken).ConfigureAwait(false);
        logger.LogInformation("{Version}", version.Split('\n')[0].Trim());
        if (!version.Contains("jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("提示：建议使用 jellyfin-ffmpeg，它带有 HDR 色调映射需要的 zscale 和各硬件编码器");
        }

        await ProcessRunner.RunAsync(settings.Ffmpeg, EncoderTestArguments(settings), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("编码器 {Encoder} 实际编码可用", settings.Encoder);

        Directory.CreateDirectory(settings.WorkDirectory);
        var test = Path.Combine(settings.WorkDirectory, ".check-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(test, "check", cancellationToken).ConfigureAwait(false);
        File.Delete(test);
        logger.LogInformation("工作目录可写：{Directory}", settings.WorkDirectory);

        await engine.Server.CheckProtocolAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await engine.Server.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("服务器协议可用，API Key 有效；预编码的媒体库：{Libraries}", libraries.Count == 0 ? "无" : string.Join("、", libraries.Select(l => l.Name)));

        foreach (var connection in settings.Connections)
        {
            using var storage = StorageConnection.Create(connection, logger);
            using var session = await storage.OpenAsync(cancellationToken).ConfigureAwait(false);
            await session.ExistsAsync(HlsTarget.MarkerName, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("连接 {Name}（{Type}）可用", connection.Name, connection.Type);
        }

        if (await engine.Target.PrepareAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("已在 HLS 根写入标记 {Marker}", HlsTarget.MarkerName);
        }

        var check = await engine.Server.CheckBaseAsync(engine.Target.Base, cancellationToken).ConfigureAwait(false);
        if (!check.Success || !check.HasMarker)
        {
            throw new InvalidOperationException(
                "服务器无法使用 HLS 根 " + engine.Target.Base + "："
                + (check.Message ?? "服务器看不到标记 " + HlsTarget.MarkerName + "，请检查 ServerRoot 或服务器的存储设置。"));
        }

        logger.LogInformation("HLS 根 {Base} 可写，服务器可以读取", engine.Target.Base);
    }

    /// <summary>
    /// Gets the arguments that encode two frames with the encoder of the settings, the way a job uses it.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The arguments.</returns>
    internal static List<string> EncoderTestArguments(WorkerSettings settings)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (settings.Encoder == "h264_vaapi")
        {
            arguments.AddRange(["-vaapi_device", settings.HardwareDevice]);
        }

        arguments.AddRange(
        [
            "-f", "lavfi", "-i", "color=size=256x144:rate=10", "-frames:v", "2",
            "-vf", settings.Encoder == "h264_vaapi" ? "format=nv12,hwupload" : "format=yuv420p",
            "-c:v", settings.Encoder
        ]);
        if (settings.Encoder == "h264_nvenc" && settings.HardwareDevice.Length > 0)
        {
            arguments.AddRange(["-gpu", settings.HardwareDevice]);
        }

        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }
}
