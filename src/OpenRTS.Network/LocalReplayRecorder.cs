using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Records a single local controller using the StarJournal format, without a transport.</summary>
public sealed class LocalReplayRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path, _partial, _recipe;
    private readonly MatchDescriptor _descriptor;
    private readonly MatchState _initial;
    private StarJournal? _journal;
    private FileStream? _stream;
    private byte[][]? _turn;
    private SimulationReport? _last;
    private long _sequence;
    private bool _closed, _ownsPartial;

    public LocalReplayRecorder(string path, MatchDescriptor descriptor, string recipe, IReadOnlyList<SlotController> controllers)
    {
        if (controllers.Count is < 2 or > 8 || controllers[0] != SlotController.Human ||
            controllers.Any(slot => slot is not (SlotController.Human or SlotController.None)))
            throw new ArgumentException("A recorded local match needs a local player and valid seats.");
        _path = path;
        _partial = path + ".partial";
        _descriptor = descriptor;
        _recipe = recipe;
        _initial = new(Guid.NewGuid(), 1, 0, MatchPhase.Playing, controllers.Select((controller, index) =>
            new MatchSlot((uint)index + 1, controller, controller == SlotController.Human, controller == SlotController.Human)), "Local playtest");
    }

    public static void ValidateInput(WorldCommand command) =>
        _ = Wire.Batch(Guid.Empty, 1, 1, [command.WithStamp(1, 1, 1)]);

    public void ValidateTurn(SimulationWorld world, IReadOnlyList<WorldCommand> commands)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                long tick = world.Tick + 1;
                if (_last?.Tick != world.Tick || tick > 1000000)
                    throw new InvalidDataException("Local replay tick limit or order.");
                long sequence = _sequence;
                foreach (var command in commands)
                    if (command.Sequence != ++sequence) throw new InvalidDataException("Local replay command sequence gap.");
                var turn = new byte[_initial.Slots.Count + 1][];
                turn[0] = StarProtocol.TurnFrame(tick, _initial.Revision);
                turn[1] = Wire.Batch(_initial.Match, 1, tick, commands);
                for (uint player = 2; player <= _initial.Slots.Count; player++) turn[player] = Wire.Batch(_initial.Match, player, tick, []);
                _turn = turn;
                _sequence = sequence;
            }
            catch { Close(); throw; }
        }
    }

    public void ObserveTick(SimulationWorld world)
    {
        lock (_gate)
        {
            if (_closed || _last?.Tick == world.Tick) return;
            try
            {
                var report = world.Report();
                if (_last is null)
                {
                    if (world.Tick != 0 || world.TickRate != _descriptor.TickRate || !world.PlayerCommandsRestricted)
                        throw new InvalidOperationException("Local replay requires its restricted tick-zero world.");
                    _stream = new(_partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _ownsPartial = true;
                    _journal = new(_stream, _descriptor, report.StateHash, StarProtocol.WriteState(_initial), _recipe);
                }
                else
                {
                    if (world.Tick != _last.Tick + 1 || _turn is null) throw new InvalidOperationException("Missing local replay turn.");
                    foreach (byte[] frame in _turn) _journal!.Write(frame);
                    _journal!.Write(StarProtocol.Check(StarProtocol.Hash, world.Tick, report.StateHash));
                    _turn = null;
                }
                _last = report;
            }
            catch { Close(); throw; }
        }
    }

    public SimulationReport Complete()
    {
        lock (_gate)
        {
            if (_closed || _last is null || _journal is null) throw new InvalidOperationException("No local replay is ready to save.");
            try
            {
                _journal.Write(StarProtocol.Check(StarProtocol.Finish, _last.Tick, _last.StateHash));
                _journal.Complete();
                CloseStream();
                File.Move(_partial, _path);
                _ownsPartial = false;
                return _last;
            }
            finally { Close(); }
        }
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        try { CloseStream(); }
        finally { if (_ownsPartial) File.Delete(_partial); }
    }

    private void CloseStream()
    {
        var journal = _journal; _journal = null;
        var stream = _stream; _stream = null;
        try { journal?.Dispose(); }
        finally { stream?.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }
}
