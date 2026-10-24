namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// Reads a stream and tells how much of it was read. The stream stays its owner's: it is not disposed with this one.
/// </summary>
/// <param name="inner">The stream.</param>
/// <param name="read">Told the position reached after each read.</param>
internal sealed class ProgressStream(Stream inner, Action<long> read) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int count)
    {
        if (count > 0)
        {
            read(inner.Position);
        }

        return count;
    }
}
