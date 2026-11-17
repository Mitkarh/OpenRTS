using System.Collections.Concurrent;
using System.Diagnostics;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Read-only playback on a dedicated world owner. Seeking reconstructs from zero with a bounded budget.</summary>
public sealed class ReplaySession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _owner;
    private readonly ReplayLimits _limits;
    private readonly ManualResetEventSlim _changed = new();
    private readonly TaskCompletionSource<SimulationReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<WorldEvent> _events = new();
    private ReplayInfo? _info;
    private long _seek = -1, _position, _timestamp, _generation;
    private double _speed = 1;
    private int _paused = 1, _steps, _eventCount, _disposed, _seeking;
    private string _status = "Проверка журнала…";
    public FrameExchange Frames { get; } = new();
    // Completes when the owner exits; reaching the final tick keeps playback available for seeking.
    public Task<SimulationReport> Completion => _completion.Task;
    public ReplayInfo? Info => Volatile.Read(ref _info);
    public long Generation => Interlocked.Read(ref _generation);
    public long Position => Interlocked.Read(ref _position);
    public bool IsPaused => Volatile.Read(ref _paused) != 0;
    public bool IsSeeking => Volatile.Read(ref _seeking) != 0;
    public double PlaybackSpeed => Volatile.Read(ref _speed);
    public string Status => Volatile.Read(ref _status);
    public string Path { get; }

    public ReplaySession(string path, Func<ReplayInfo, SimulationWorld> createWorld, ReplayLimits? limits = null)
    {
        Path = path; _limits = limits ?? new(); _limits.Validate();
        _owner = new Thread(() => Run(createWorld)) { IsBackground = true, Name = "OpenRTS replay owner" }; _owner.Start();
    }
    public void SetPlayback(double speed, bool paused)
    {
        CheckActive();
        if (!double.IsFinite(speed) || speed is < 0.125 or > 8) throw new ArgumentOutOfRangeException(nameof(speed));
        Volatile.Write(ref _speed, speed); Volatile.Write(ref _paused, paused ? 1 : 0); Interlocked.Exchange(ref _steps, 0); _changed.Set();
    }
    public void StepOnce() { CheckActive(); Volatile.Write(ref _paused, 1); Interlocked.Exchange(ref _steps, 1); _changed.Set(); }
    public void Seek(long tick)
    {
        CheckActive(); var info = Info ?? throw new InvalidOperationException("Replay is not ready.");
        if (tick < 0 || tick > info.EndTick) throw new ArgumentOutOfRangeException(nameof(tick));
        Interlocked.Exchange(ref _steps, 0); Interlocked.Exchange(ref _seek, tick); _changed.Set();
    }
    private void CheckActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Completion.IsCompleted) throw new InvalidOperationException("Replay owner has stopped.");
    }
    public bool TryReadEvent(out WorldEvent e)
    {
        if (!_events.TryDequeue(out e)) return false;
        Interlocked.Decrement(ref _eventCount); return true;
    }
    public float GetInterpolationAlpha(long tick) => IsPaused || IsSeeking || tick != Position || Info is not { } info ? 1 :
        (float)Math.Clamp(Stopwatch.GetElapsedTime(Interlocked.Read(ref _timestamp)).TotalSeconds * info.Descriptor.TickRate * PlaybackSpeed, 0, 1);
    private void Run(Func<ReplayInfo, SimulationWorld> createWorld)
    {
        SimulationWorld? world = null; StarJournalReader? reader = null; FileStream? input = null;
        void Publish(bool events)
        {
            // A paused seek must eventually publish its destination, even if readers briefly pin every slot.
            while (!Frames.TryPublish(world!)) { if (_stop.Token.WaitHandle.WaitOne(5)) _stop.Token.ThrowIfCancellationRequested(); }
            Interlocked.Exchange(ref _position, world!.Tick); Interlocked.Exchange(ref _timestamp, Stopwatch.GetTimestamp());
            if (!events) return;
            foreach (var e in world!.Events)
            {
                if (Interlocked.Increment(ref _eventCount) > 65536) throw new InvalidOperationException("Replay event backlog exceeded.");
                _events.Enqueue(e);
            }
        }
        void Reset()
        {
            reader?.Dispose(); input?.Dispose(); world?.Dispose(); world = null;
            input = File.OpenRead(Path); reader = new StarJournalReader(input, limits: _limits, stop: _stop.Token);
            if (Info is { } expected && (reader.Info.Descriptor != expected.Descriptor || reader.Info.InitialHash != expected.InitialHash || reader.Info.Recipe != expected.Recipe || reader.Info.InitialState.Match != expected.InitialState.Match))
                throw new InvalidDataException("Replay changed during playback.");
            world = createWorld(reader.Info); StarJournal.ValidateWorld(world, reader.Info);
        }
        Exception? failure = null;
        try
        {
            using (var scan = File.OpenRead(Path)) Volatile.Write(ref _info, StarJournal.Inspect(scan, _limits, _stop.Token));
            Reset(); Publish(false); Volatile.Write(ref _status, "Пауза");
            var clock = Stopwatch.StartNew(); double due = 0;
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                long seek = Interlocked.Exchange(ref _seek, -1);
                if (seek >= 0)
                {
                    Volatile.Write(ref _seeking, 1); Volatile.Write(ref _status, $"Переход к тику {seek}…");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(_limits.Timeout);
                    try
                    {
                        Reset();
                        while (world!.Tick < seek)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            var turn = reader!.Next() ?? throw new EndOfStreamException(); StarJournal.Apply(world, turn, deadline.Token);
                        }
                        deadline.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { throw new TimeoutException("Replay seeking exceeded its catch-up budget."); }
                    while (_events.TryDequeue(out _)) Interlocked.Decrement(ref _eventCount);
                    Interlocked.Increment(ref _generation); Publish(false); Volatile.Write(ref _seeking, 0);
                    Volatile.Write(ref _status, IsPaused ? "Пауза" : "Воспроизведение"); due = clock.Elapsed.TotalMilliseconds;
                }
                if (world!.Tick >= Info!.EndTick)
                {
                    if (reader!.Next() is not null || reader.Hash != Info.FinalHash) throw new InvalidDataException("Replay changed during playback.");
                    Volatile.Write(ref _paused, 1); Interlocked.Exchange(ref _steps, 0); Volatile.Write(ref _status, "Конец повтора");
                }
                if (IsPaused && Interlocked.Exchange(ref _steps, 0) == 0)
                {
                    _changed.Reset(); if (Interlocked.Read(ref _seek) >= 0 || !IsPaused || Volatile.Read(ref _steps) != 0) continue;
                    _changed.Wait(50, _stop.Token); due = clock.Elapsed.TotalMilliseconds; continue;
                }
                if (world.Tick >= Info.EndTick) continue;
                double period = 1000.0 / Info.Descriptor.TickRate / PlaybackSpeed;
                double wait = due - clock.Elapsed.TotalMilliseconds;
                if (!IsPaused && wait > 0) { _changed.Wait(TimeSpan.FromMilliseconds(Math.Min(wait, 50)), _stop.Token); _changed.Reset(); continue; }
                var next = reader!.Next() ?? throw new EndOfStreamException(); StarJournal.Apply(world, next, _stop.Token); Publish(true);
                Volatile.Write(ref _status, IsPaused ? "Пауза" : "Воспроизведение");
                due = Math.Max(due + period, clock.Elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { failure = e; Volatile.Write(ref _status, e.Message); }
        finally
        {
            world?.Dispose(); reader?.Dispose(); input?.Dispose();
            if (failure is not null) _completion.TrySetException(failure); else _completion.TrySetCanceled(_stop.Token);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _changed.Set();
        if (!_owner.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Replay owner did not stop.");
        _stop.Dispose(); _changed.Dispose();
    }
}
