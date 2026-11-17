using System.Text;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

public sealed record ReplayLimits(long MaximumBytes = 64 * 1024 * 1024, long MaximumTicks = 100000,
    TimeSpan? CatchUpTimeout = null)
{
    internal TimeSpan Timeout => CatchUpTimeout ?? TimeSpan.FromSeconds(15);
    internal void Validate()
    {
        if (MaximumBytes is < 1 or > 64 * 1024 * 1024 || MaximumTicks is < 1 or > 1000000 ||
            Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(ReplayLimits));
    }
}

public sealed record ReplayInfo(MatchDescriptor Descriptor, string Recipe, string InitialHash, MatchState InitialState,
    long EndTick = -1, string? FinalHash = null);

internal sealed record RecordedTurn(long Tick, WorldCommand[] Commands, string Hash);

// Structural validation shared by inspection, playback and checkpoint capture. Caller owns the stream.
internal sealed class StarJournalReader : IDisposable
{
    private readonly Stream _input;
    private readonly BinaryReader _reader;
    private readonly ReplayLimits _limits;
    private readonly CancellationToken _stop;
    private readonly Action<byte[]>? _record;
    private long _bytes;
    private bool _final, _complete;
    private readonly long[] _sequences;
    internal ReplayInfo Info { get; }
    internal MatchState State { get; private set; }
    internal long Tick { get; private set; }
    internal string Hash { get; private set; }
    internal long Bytes => _bytes;

    internal StarJournalReader(Stream input, MatchDescriptor? expected = null, ReplayLimits? limits = null,
        CancellationToken stop = default, Action<byte[]>? record = null)
    {
        _input = input; _reader = new(input, Encoding.UTF8, true); _limits = limits ?? new(); _limits.Validate(); _stop = stop; _record = record;
        var header = Wire.Unpack(Read(), r => {
            if (r.ReadByte() != 40) throw new InvalidDataException("Not a StarJournal.");
            int version = r.ReadInt32(); MatchDescriptor descriptor; string recipe = "", initial;
            if (version == 2)
            {
                descriptor = expected ?? throw new InvalidDataException("Journal v2 requires its original descriptor and world factory.");
                if (Wire.Text(r) != Wire.Hash(DirectMatch.Descriptor(descriptor))) throw new InvalidDataException("Journal descriptor mismatch.");
                initial = Wire.Text(r);
            }
            else if (version == 3)
            {
                int size = r.ReadInt32(); if (size is < 1 or > 4096) throw new InvalidDataException("Descriptor limit.");
                descriptor = DirectMatch.ReadDescriptor(r.ReadBytes(size));
                initial = Wire.Text(r); int count = r.ReadUInt16();
                if (count > 16384) throw new InvalidDataException("Recipe limit.");
                byte[] data = r.ReadBytes(count); if (data.Length != count) throw new EndOfStreamException();
                recipe = new UTF8Encoding(false, true).GetString(data);
                if (expected is not null && descriptor != expected) throw new InvalidDataException("Journal descriptor mismatch.");
            }
            else throw new InvalidDataException("Unsupported journal version.");
            if (initial.Length != 64 || !initial.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid initial hash.");
            return (descriptor, recipe, initial);
        });
        State = StarProtocol.ReadState(Read());
        if (State.Phase != MatchPhase.Playing || State.Tick != 0 || State.Slots.Any(s => s.Controller == SlotController.Human && (!s.Connected || !s.Ready)))
            throw new InvalidDataException("Missing agreed match start.");
        Info = new(header.descriptor, header.recipe, header.initial, State); Hash = header.initial;
        _sequences = new long[State.Slots.Count];
    }
    private byte[] Read()
    {
        _stop.ThrowIfCancellationRequested();
        int n = _reader.ReadInt32();
        if (n is < 1 or > Wire.MaximumFrame || (_bytes += n + 4) > _limits.MaximumBytes) throw new InvalidDataException("Journal limits.");
        byte[] frame = _reader.ReadBytes(n); if (frame.Length != n) throw new EndOfStreamException();
        _record?.Invoke(frame); return frame;
    }
    internal RecordedTurn? Next()
    {
        if (_complete) return null;
        while (true)
        {
            byte[] frame = Read();
            if (frame[0] == StarProtocol.State && !_final)
                State = StarJournal.ValidateTransition(State, StarProtocol.ReadState(frame), Tick);
            else if (frame[0] == StarProtocol.Turn && !_final)
            {
                long tick = StarProtocol.ReadTurn(frame, Tick + 1, State);
                if (tick > _limits.MaximumTicks || Info.Descriptor.EndTick != 0 && tick > Info.Descriptor.EndTick)
                    throw new InvalidDataException("Journal tick limit.");
                var commands = new List<WorldCommand>();
                foreach (var slot in State.Slots) commands.AddRange(StarJournal.ReadCommands(Read(), State, slot.Player, tick, _sequences));
                var check = StarProtocol.ReadCheck(Read(), StarProtocol.Hash);
                if (check.Tick != tick) throw new InvalidDataException("Journal checkpoint order.");
                Tick = tick; Hash = check.Hash; return new(tick, commands.ToArray(), Hash);
            }
            else if (frame[0] == StarProtocol.Finish && !_final)
            {
                var check = StarProtocol.ReadCheck(frame, StarProtocol.Finish);
                if (check.Tick != Tick || check.Hash != Hash) throw new InvalidDataException("Journal final hash mismatch.");
                _final = true;
            }
            else if (frame.AsSpan().SequenceEqual(new byte[] { StarProtocol.Complete }) && _final)
            {
                if (_input.ReadByte() != -1) throw new InvalidDataException("Trailing journal data.");
                _complete = true; return null;
            }
            else throw new InvalidDataException("Incomplete or invalid journal history.");
        }
    }
    internal ReplayInfo Inspect()
    {
        while (Next() is not null) { }
        return Info with { EndTick = Tick, FinalHash = Hash };
    }
    public void Dispose() => _reader.Dispose();
}
