using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

/// <summary>TCP byte relay with a bounded, seeded link model. Loss delays delivery until modeled retransmission;
/// application bytes are never deleted. This is not a kernel TCP congestion-control benchmark.</summary>
internal sealed class WanLink : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private TcpClient? _downstream, _upstream;
    private long _segments, _retransmissions, _bytes;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal long Segments => Interlocked.Read(ref _segments);
    internal long Retransmissions => Interlocked.Read(ref _retransmissions);
    internal long Bytes => Interlocked.Read(ref _bytes);
    internal WanLink(int serverPort, int rtt = 0, int jitter = 0, int lossPercent = 0, int bytesPerSecond = 1000000)
    {
        _listener.Start();
        _run = Task.Run(async () => {
            try
            {
                _downstream = await _listener.AcceptTcpClientAsync(_stop.Token);
                _upstream = new TcpClient(); await _upstream.ConnectAsync(IPAddress.Loopback, serverPort, _stop.Token);
                _downstream.NoDelay = _upstream.NoDelay = true;
                await Task.WhenAll(Forward(_downstream.GetStream(), _upstream.GetStream(), 17),
                    Forward(_upstream.GetStream(), _downstream.GetStream(), 29));
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        });
        async Task Forward(Stream source, Stream target, int seed)
        {
            var delivery = Channel.CreateBounded<(byte[] Data, double Due)>(256);
            var clock = Stopwatch.StartNew(); var random = new Random(seed);
            var read = Task.Run(async () => {
                double transmit = 0, previousDue = 0; int packet = 0;
                try
                {
                    var buffer = new byte[1200];
                    while (!_stop.IsCancellationRequested)
                    {
                        int count = await source.ReadAsync(buffer, _stop.Token);
                        if (count == 0) break;
                        packet++; Interlocked.Increment(ref _segments); Interlocked.Add(ref _bytes, count);
                        transmit = Math.Max(transmit, clock.Elapsed.TotalMilliseconds) + 1000.0 * count / bytesPerSecond;
                        double due = transmit + rtt / 2.0 + (jitter == 0 ? 0 : random.Next(jitter + 1));
                        // Periodic loss targets the configured segment fraction; seeded jitter varies recovery overlap.
                        if (lossPercent != 0 && packet % (100 / lossPercent) == 0)
                        { due += 150; Interlocked.Increment(ref _retransmissions); }
                        due = Math.Max(due, previousDue); previousDue = due; // reliable-stream head-of-line ordering
                        await delivery.Writer.WriteAsync((buffer.AsSpan(0, count).ToArray(), due), _stop.Token);
                    }
                    delivery.Writer.TryComplete();
                }
                catch (Exception e) { delivery.Writer.TryComplete(e); }
            });
            await foreach (var segment in delivery.Reader.ReadAllAsync(_stop.Token))
            {
                double wait = segment.Due - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), _stop.Token);
                await target.WriteAsync(segment.Data, _stop.Token);
            }
            await read;
        }
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop(); _downstream?.Dispose(); _upstream?.Dispose();
        try { _run.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* Peer shutdown closes both relay directions. */ }
        _stop.Dispose();
    }
}
