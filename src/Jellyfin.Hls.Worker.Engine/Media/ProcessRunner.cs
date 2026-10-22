using System.Diagnostics;
using System.Text;

namespace Jellyfin.Hls.Worker.Media;

/// <summary>
/// Runs ffmpeg and ffprobe.
/// </summary>
public static class ProcessRunner
{
    private const int ErrorTail = 4000;

    /// <summary>
    /// Runs a program to its end. A cancellation kills the program and everything it started.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="workingDirectory">The working directory, or <c>null</c> for the current one.</param>
    /// <returns>What the program wrote to its standard output.</returns>
    /// <exception cref="InvalidOperationException">The program failed; the message ends with what it wrote to its standard error.</exception>
    public static async Task<string> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("无法启动 " + program + "：" + ex.Message, ex);
        }

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // It ended in the meantime.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        var result = await output.ConfigureAwait(false);
        var message = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(program)} 失败（退出码 {process.ExitCode}）：{(message.Length > ErrorTail ? message[^ErrorTail..] : message)}");
        }

        return result;
    }
}
