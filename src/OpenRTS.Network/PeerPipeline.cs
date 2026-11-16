using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace OpenRTS.Network;

/// <summary>One stream reader/writer, bounded queues. Never receives a world or ECS reference.</summary>
internal sealed class PeerPipeline : IDisposable
{
    internal const int Capacity = 32;
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(Capacity);
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(Capacity);
    private readonly CancellationTokenSource _stop;
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly Task _reader, _writer;
    private Exception? _fault;
    private readonly ManualResetEventSlim _written = new();
    private long _enqueued, _sent;
    internal int SendHighWater { get; private set; }
    internal int ReceiveHighWater { get; private set; }

    internal PeerPipeline(Stream stream, TimeSpan timeout, CancellationToken cancellation)
    {
        _stream = stream; _timeout = timeout;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _reader = Task.Run(async () => {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var frame = await Wire.Receive(stream, _stop.Token);
                    await _incoming.Writer.WriteAsync(frame, _stop.Token);
                    ReceiveHighWater = Math.Max(ReceiveHighWater, _incoming.Reader.Count);
                }
            }
            catch (Exception e) { Fail(e); }
            finally { _incoming.Writer.TryComplete(_fault); }
        });
        _writer = Task.Run(async () => {
            try
            {
                await foreach (var frame in _outgoing.Reader.ReadAllAsync(_stop.Token))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(timeout);
                    try { await Wire.Send(stream, frame, deadline.Token); Interlocked.Increment(ref _sent); _written.Set(); }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    { throw new TimeoutException("Peer stopped reading: send deadline exceeded."); }
                }
            }
            catch (Exception e) { Fail(e); }
        });
    }
    private void Fail(Exception e)
    {
        if (!_stop.IsCancellationRequested) Interlocked.CompareExchange(ref _fault, e, null);
        _incoming.Writer.TryComplete(_fault); _outgoing.Writer.TryComplete(_fault); _written.Set();
    }
    internal void Check()
    {
        if (Volatile.Read(ref _fault) is { } fault) ExceptionDispatchInfo.Capture(fault).Throw();
        _stop.Token.ThrowIfCancellationRequested();
    }
    internal void Send(byte[] frame)
    {
        Check();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(_timeout);
        try { _outgoing.Writer.WriteAsync(frame, deadline.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        { throw new TimeoutException("Bounded send queue deadline exceeded."); }
        catch (ChannelClosedException) { Check(); throw new EndOfStreamException(); }
        _enqueued++;
        SendHighWater = Math.Max(SendHighWater, _outgoing.Reader.Count);
    }
    internal bool TryRead(out byte[] frame) => _incoming.Reader.TryRead(out frame!);
    internal byte[] Read(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Peer progress deadline exceeded.");
        // Buffered final messages precede EOF, but never bypass an expired progress deadline.
        if (TryRead(out var frame)) return frame;
        Check();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(remaining);
        try { return _incoming.Reader.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        { throw new TimeoutException("Peer progress deadline exceeded."); }
        catch (ChannelClosedException) { Check(); throw new EndOfStreamException(); }
    }
    internal void Flush()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref _sent) < _enqueued)
        {
            _written.Reset();
            if (Interlocked.Read(ref _sent) >= _enqueued) return;
            // The peer may close after receiving our final ACK, before our writer continuation records it.
            // Read-side EOF must not cancel an in-flight successful flush; final ACK validation still follows.
            if (Volatile.Read(ref _fault) is not EndOfStreamException || _writer.IsCompleted) Check();
            else _stop.Token.ThrowIfCancellationRequested();
            var remaining = _timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !_written.Wait(remaining, _stop.Token))
                throw new TimeoutException("Send flush deadline exceeded.");
        }
    }
    public void Dispose()
    {
        _stop.Cancel(); _stream.Dispose();
        _outgoing.Writer.TryComplete();
        try { if (!Task.WhenAll(_reader, _writer).Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Pipeline did not stop."); }
        finally { _written.Dispose(); _stop.Dispose(); }
    }
}
