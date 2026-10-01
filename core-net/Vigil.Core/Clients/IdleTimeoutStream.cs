namespace Vigil.Core.Clients;

/// <summary>
/// A read-only stream that fails if no byte arrives within <paramref name="idleTimeout"/>.
/// </summary>
/// <remarks>
/// <para>
/// vigild sends a keepalive comment when it has had nothing to say, so silence on its stream
/// means the connection is dead even when the socket has not noticed — a half-open TCP
/// connection, or a container whose peer vanished, looks exactly like an idle one.
/// </para>
/// <para>
/// The timeout has to live here, below the SSE parser, because the parser deliberately discards
/// comments: a keepalive never surfaces as an event. Timing out on events instead would make a
/// hung vigild — which stops publishing but keeps sending keepalives — look disconnected, and
/// churn a reconnect every timeout instead of reporting the unhealthy snapshot vigild is still
/// serving.
/// </para>
/// </remarks>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idleTimeout) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);

        try
        {
            return await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ours, not the caller's: the stream went quiet rather than being shut down.
            throw new TimeoutException($"no data for {idleTimeout.TotalSeconds:0}s");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("read asynchronously: the timeout is enforced per await");

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
