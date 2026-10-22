using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// The downloaded inputs, kept up to a size so a job tried again or the next job of the same file does not download it again.
/// An input is named by the item, its length and its modified time, so a changed file is a new name; its hash is kept next to it.
/// </summary>
public sealed partial class InputCache
{
    private const string Extension = ".media";
    private const string HashExtension = ".sha256";

    private readonly string _directory;
    private readonly long _byteLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="InputCache"/> class.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="byteLimit">The size the inputs not in use are trimmed to.</param>
    public InputCache(string directory, long byteLimit)
    {
        _directory = directory;
        _byteLimit = byteLimit;
    }

    /// <summary>
    /// Gets the input of a source, downloading it unless it is cached. Jobs of the same source wait for one download.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="download">Downloads to the given file and returns the SHA-256 of what it wrote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The input; it stays while the lease is held.</returns>
    /// <exception cref="InvalidDataException">The download is not as long as the source.</exception>
    public async Task<InputLease> AcquireAsync(SourceFile source, Func<string, CancellationToken, Task<string>> download, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(download);
        var path = Path.Combine(_directory, $"{source.ItemId:N}-{source.Length}-{source.DateModified.ToUniversalTime().Ticks}{Extension}");
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out entry!))
            {
                _entries[path] = entry = new Entry();
            }

            entry.Users++;
        }

        try
        {
            string sha256;
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                sha256 = await GetCachedAsync(path, source.Length, cancellationToken).ConfigureAwait(false)
                    ?? await DownloadAsync(path, source.Length, download, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
            }

            return new InputLease(path, sha256, () => Release(path, entry), () => Invalidate(entry));
        }
        catch
        {
            Release(path, entry);
            throw;
        }
    }

    /// <summary>
    /// Deletes what was left of interrupted downloads and the inputs not in use beyond the size, the least recently used first.
    /// </summary>
    public void Trim()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var directory = new DirectoryInfo(_directory);
            foreach (var partial in directory.GetFiles("*" + Extension + ".part"))
            {
                if (OwnedName().IsMatch(partial.Name[..^5]) && !_entries.ContainsKey(partial.FullName[..^5]))
                {
                    TryDelete(partial.FullName);
                }
            }

            var files = directory.GetFiles("*" + Extension).Where(f => OwnedName().IsMatch(f.Name)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var size = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (size <= _byteLimit)
                {
                    break;
                }

                if (_entries.ContainsKey(file.FullName))
                {
                    continue;
                }

                if (TryDelete(file.FullName))
                {
                    size -= file.Length;
                }
            }
        }
    }

    // The cache is kept on a best-effort basis: a file another program holds is left for the next trim, and never fails a job.
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + HashExtension);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The hash of a complete input; one without its hash is hashed again.
    private static async Task<string?> GetCachedAsync(string path, long length, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != length)
        {
            return null;
        }

        var hashFile = path + HashExtension;
        if (File.Exists(hashFile) && (await File.ReadAllTextAsync(hashFile, cancellationToken).ConfigureAwait(false)).Trim() is { Length: 64 } cached
            && cached.All(char.IsAsciiHexDigitLower))
        {
            return cached;
        }

        string sha256;
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        await File.WriteAllTextAsync(hashFile, sha256, cancellationToken).ConfigureAwait(false);
        return sha256;
    }

    private async Task<string> DownloadAsync(string path, long length, Func<string, CancellationToken, Task<string>> download, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporary = path + ".part";
        try
        {
            var sha256 = await download(temporary, cancellationToken).ConfigureAwait(false);
            var written = new FileInfo(temporary).Length;
            if (written != length)
            {
                throw new InvalidDataException($"下载的输入长 {written} 字节，任务为 {length} 字节；源文件可能在此期间变化。");
            }

            File.Delete(path + HashExtension);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(path + HashExtension, sha256, cancellationToken).ConfigureAwait(false);
            return sha256;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void Invalidate(Entry entry)
    {
        lock (_gate)
        {
            entry.Invalid = true;
        }
    }

    private void Release(string path, Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            try
            {
                if (File.Exists(path))
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It is only the order of the trim.
            }

            if (entry.Users == 0)
            {
                if (entry.Invalid)
                {
                    TryDelete(path);
                }

                _entries.Remove(path);
            }

            Trim();
        }
    }

    [GeneratedRegex(@"^[0-9a-f]{32}-[0-9]+-[0-9]+\.media\z")]
    private static partial Regex OwnedName();

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1);

        public int Users { get; set; }

        public bool Invalid { get; set; }
    }
}

/// <summary>
/// The use of an input; the input may be trimmed once every lease of it is released.
/// </summary>
public sealed class InputLease : IDisposable
{
    private readonly Action _invalidate;
    private Action? _release;

    /// <summary>
    /// Initializes a new instance of the <see cref="InputLease"/> class.
    /// </summary>
    /// <param name="path">The input file.</param>
    /// <param name="sha256">Its SHA-256, in lower-case hexadecimal.</param>
    /// <param name="release">Releases the lease.</param>
    /// <param name="invalidate">Marks the input for deletion.</param>
    public InputLease(string path, string sha256, Action release, Action invalidate)
    {
        Path = path;
        Sha256 = sha256;
        _release = release;
        _invalidate = invalidate;
    }

    /// <summary>Gets the input file.</summary>
    public string Path { get; }

    /// <summary>Gets the SHA-256 of the input, in lower-case hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>
    /// Deletes the input once it is no longer in use, because it is not what the server says it is.
    /// </summary>
    public void Invalidate() => _invalidate();

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
