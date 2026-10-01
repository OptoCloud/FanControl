namespace Vigil.Core.Clients;

/// <summary>
/// A read-only stream that fails once more bytes arrive than the caller has allowed since it last
/// called <see cref="Allow"/>.
/// </summary>
/// <remarks>
/// The bound has to live below the <see cref="StreamReader"/>: <c>ReadLineAsync</c> keeps
/// growing a line until it sees a newline, so counting lines above it bounds nothing when the
/// other end never sends one. An idle timeout does not help either: a peer that keeps sending
/// is never idle (docs/SECURITY.md §4.1).
/// </remarks>
internal sealed class ReadBudgetStream(Stream inner) : Stream
{
    private long _allowed;
    private long _remaining;

    /// <summary>Allows <paramref name="bytes"/> more bytes, replacing whatever was left.</summary>
    public void Allow(long bytes) => _allowed = _remaining = bytes;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            throw new IOException($"the answer is longer than the {_allowed} bytes allowed for it");
        }

        // Never ask for more than the budget, so the budget is exact rather than a buffer's worth over.
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("read asynchronously, as everything above this does");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
