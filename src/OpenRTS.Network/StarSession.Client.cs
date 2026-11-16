using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    private SimulationReport RunClient(string address, int port, SimulationWorld world)
    {
        using var socket = new TcpClient { NoDelay = true };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            socket.ConnectAsync(address, port, connect.Token).AsTask().GetAwaiter().GetResult();
        }
        using var tls = new SslStream(socket.GetStream(), false, (_, remote, _, _) => remote is not null &&
            Convert.ToHexString(SHA256.HashData(remote.GetRawCertData())) == Fingerprint);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            // A guest may wait behind other bounded lobby handshakes in the accept queue.
            connect.CancelAfter(TimeSpan.FromSeconds(30)); Authenticate(tls, null, connect.Token).GetAwaiter().GetResult();
        }
        using var pipe = new PeerPipeline(tls, _timeout, _stop.Token);
        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.Hello); w.Write(StarProtocol.Version); w.Write(Player); w.Write(State.Slots.Count);
            Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, world.Report().StateHash); Wire.Text(w, Descriptor.Rules); Wire.Text(w, Descriptor.ContentHash); }));
        var clock = Stopwatch.StartNew(); double lastSeen = 0, lastTurn = 0, finishAt = 0; long sentSequence = 0, sealedTick = -1;
        long[] sequences = new long[State.Slots.Count];
        StarJournal? log = null; FileStream? output = null; bool finishing = false;
        void Seal()
        {
            long target = world.Tick + Descriptor.InputDelay;
            if (Player == 0 || sealedTick == target || State.Slots[(int)Player - 1].Controller != SlotController.Human) return;
            var commands = Collect().Select(c => c.WithStamp(target, Player, checked(++sentSequence))).ToArray();
            pipe.Send(Wire.Batch(State.Match, Player, target, commands)); sealedTick = target;
        }
        void PumpRequests()
        {
            int count = Volatile.Read(ref _requestCount);
            for (int i = 0; i < count && _requests.TryDequeue(out var request); i++)
            { Interlocked.Decrement(ref _requestCount); pipe.Send(request); }
        }
        // Only the coordinator emits turns. A disconnected client can never confirm a local branch.
        byte[] Read()
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if ((log is not null && State.Phase == MatchPhase.Playing && clock.Elapsed.TotalMilliseconds - lastTurn > _timeout.TotalMilliseconds) ||
                    finishing && clock.Elapsed.TotalMilliseconds - finishAt > _timeout.TotalMilliseconds)
                    throw new TimeoutException("Coordinator turn/completion deadline exceeded.");
                if (pipe.TryRead(out var frame))
                {
                    lastSeen = clock.Elapsed.TotalMilliseconds;
                    if (frame.AsSpan().SequenceEqual(new byte[] { StarProtocol.Ping }))
                    {
                        try { pipe.Send(frame); } catch (Exception e) when (LinkFailure(e)) { /* Consume any buffered final messages before EOF. */ }
                        continue;
                    }
                    if (frame[0] == StarProtocol.Chat) { var message = StarProtocol.ReadChat(frame); if (message.Player > State.Slots.Count) throw new InvalidDataException("Invalid chat player."); Chat(message); continue; }
                    return frame;
                }
                if (!finishing && State.Phase != MatchPhase.Finished && (Player == 0 || State.Slots[(int)Player - 1].Controller == SlotController.Human)) PumpRequests();
                pipe.Check();
                if (clock.Elapsed.TotalMilliseconds - lastSeen > _timeout.TotalMilliseconds) throw new TimeoutException("Player-host progress deadline exceeded.");
                Idle();
            }
        }
        try
        {
            while (true)
            {
                byte[] frame = Read();
                switch (frame[0])
                {
                    case StarProtocol.Settings:
                        if (_prepare is null || log is not null) throw new InvalidDataException("Unexpected lobby configuration.");
                        var configuration = ReadSettings(frame);
                        world = PrepareSettings(configuration.Data);
                        if (Wire.Hash(DirectMatch.Descriptor(Descriptor)) != configuration.Digest || _initialHash != configuration.Hash)
                            throw new InvalidDataException("Lobby configuration hash mismatch.");
                        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.SettingsAck); Wire.Text(w, SettingsDigest); Wire.Text(w, _initialHash); }));
                        break;
                    case StarProtocol.State:
                        var next = StarProtocol.ReadState(frame); var previous = State;
                        if (previous.Match != Guid.Empty && next.Match != previous.Match || next.Slots.Count != previous.Slots.Count ||
                            Player != 0 && next.Slots[(int)Player - 1].Controller == SlotController.Human && !next.Slots[(int)Player - 1].Connected)
                            throw new InvalidDataException("Lobby identity mismatch.");
                        if (log is null)
                        {
                            if (next.Tick != 0 || next.Revision < previous.Revision || next.Phase is not (MatchPhase.Lobby or MatchPhase.Playing))
                                throw new InvalidDataException("Invalid lobby/start transition.");
                            if (next.Phase == MatchPhase.Playing)
                            {
                                if (previous.Match == Guid.Empty || next.Slots.Any(p => p.Controller == SlotController.Human && (!p.Connected || !p.Ready)))
                                    throw new InvalidDataException("Start without agreed Ready.");
                                output = File.Create(JournalPath); log = new StarJournal(output, Descriptor, world.Report().StateHash, frame);
                            }
                        }
                        else { StarJournal.ValidateTransition(previous, next, world.Tick); log.Write(frame); }
                        SetState(next);
                        lastTurn = clock.Elapsed.TotalMilliseconds;
                        if (log is not null && world.Tick == 0) { Seal(); Publish(world); }
                        break;
                    case StarProtocol.Turn:
                        if (log is null || finishing) throw new InvalidDataException("Unexpected turn.");
                        long tick = StarProtocol.ReadTurn(frame, world.Tick + 1, State);
                        if (Descriptor.EndTick != 0 && tick > Descriptor.EndTick) throw new InvalidDataException("End tick exceeded.");
                        var records = new List<byte[]> { frame }; var commands = new List<WorldCommand>();
                        foreach (var slot in State.Slots)
                        {
                            byte[] batch = Read();
                            commands.AddRange(StarJournal.ReadCommands(batch, State, slot.Player, tick, sequences)); records.Add(batch);
                        }
                        byte[] checkFrame = Read(); var check = StarProtocol.ReadCheck(checkFrame, StarProtocol.Hash); records.Add(checkFrame);
                        if (check.Tick != tick) throw new InvalidDataException("Turn hash order mismatch.");
                        world.Step(commands.ToArray(), _stop.Token);
                        string hash = world.Report().StateHash;
                        if (hash != check.Hash) throw new DesyncException(tick, hash, check.Hash);
                        foreach (var record in records) log.Write(record);
                        if (Player != 0 && tick % Descriptor.HashInterval == 0) pipe.Send(StarProtocol.Check(StarProtocol.Hash, tick, hash));
                        Publish(world); Seal(); lastTurn = clock.Elapsed.TotalMilliseconds;
                        Volatile.Write(ref _statistics, new(tick, tick, 0, null, pipe.SendHighWater, pipe.ReceiveHighWater, clock.Elapsed.TotalMilliseconds));
                        break;
                    case StarProtocol.Finish:
                        if (log is null || finishing || State.Phase != MatchPhase.Finished && Player != 0 && State.Slots[(int)Player - 1].Controller == SlotController.Human)
                            throw new InvalidDataException("Unexpected final boundary.");
                        var final = StarProtocol.ReadCheck(frame, StarProtocol.Finish);
                        if (final.Tick != world.Tick || final.Hash != world.Report().StateHash) throw new DesyncException(world.Tick, world.Report().StateHash, final.Hash);
                        log.Write(frame); finishing = true; finishAt = clock.Elapsed.TotalMilliseconds;
                        if (Player != 0) { pipe.Send(StarProtocol.Check(StarProtocol.Ack, world.Tick, final.Hash)); pipe.Flush(); } break;
                    case StarProtocol.Complete:
                        if (frame.Length != 1 || !finishing) throw new InvalidDataException("Premature completion.");
                        log!.Complete(); return world.Report();
                    default: throw new InvalidDataException("Unknown coordinator message.");
                }
            }
        }
        finally { log?.Dispose(); output?.Dispose(); }
    }
}
