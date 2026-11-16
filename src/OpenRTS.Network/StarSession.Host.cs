using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    private sealed class Peer(TcpClient socket, PeerPipeline pipeline, TickInbox inbox) : IDisposable
    {
        internal readonly TcpClient Socket = socket;
        internal readonly PeerPipeline Pipe = pipeline;
        internal TickInbox Inbox = inbox;
        internal string? SettingsDigest;
        internal long Ack, LastSeen = Stopwatch.GetTimestamp(), RateAt = Stopwatch.GetTimestamp();
        internal int Requests, Chats;
        internal void Limit(bool chat)
        {
            if (Stopwatch.GetElapsedTime(RateAt).TotalSeconds >= 1) { RateAt = Stopwatch.GetTimestamp(); Requests = Chats = 0; }
            if (chat ? ++Chats > 4 : ++Requests > 16) throw new InvalidDataException("Peer control/chat rate exceeded.");
        }
        public void Dispose() { Pipe.Dispose(); Socket.Dispose(); }
    }
    private SimulationReport RunHost(string address, int port, X509Certificate2 certificate, SimulationWorld world)
    {
        var listener = new TcpListener(IPAddress.Parse(address), port); listener.Start();
        var peers = new SortedDictionary<uint, Peer>();
        var lost = new HashSet<uint>();
        int count = State.Slots.Count, window = 2 * Descriptor.InputDelay + Descriptor.HashInterval;
        var slots = State.Slots.ToArray(); var phase = MatchPhase.Lobby;
        var pending = Enumerable.Range(0, count).Select(_ => new Dictionary<long, WorldCommand[]>()).ToArray();
        long[] sequences = new long[count];
        var hashes = new Dictionary<long, string>();
        StarJournal? log = null; FileStream? output = null;
        var clock = Stopwatch.StartNew(); long prepared = -1; double nextDue = 0, pingAt = 0, blockedAt = 0;
        long revision = State.Revision;
        MatchState Snapshot(string reason) => new(State.Match, revision, world.Tick, phase, slots, reason, peers.Keys.Count(p => p > 8));
        void Send(uint player, Peer peer, byte[] frame)
        {
            try { if (player > 8) { if (!peer.Pipe.TrySend(frame)) lost.Add(player); } else peer.Pipe.Send(frame); }
            catch (Exception e) when (LinkFailure(e)) { lost.Add(player); }
        }
        void Broadcast(byte[] frame) { foreach (var pair in peers) Send(pair.Key, pair.Value, frame); }
        void Change(string reason, bool resetReady = false, bool increment = true)
        {
            if (increment) revision++;
            if (resetReady) for (int i = 0; i < count; i++) slots[i] = slots[i] with { Ready = false };
            var state = Snapshot(reason); SetState(state);
            var frame = StarProtocol.WriteState(state); log?.Write(frame); Broadcast(frame);
        }
        void WaitFinal(Peer peer)
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                byte[] reply = peer.Pipe.Read(_timeout - deadline.Elapsed);
                if (reply[0] == StarProtocol.Ack)
                {
                    var check = StarProtocol.ReadCheck(reply, StarProtocol.Ack);
                    if (check.Tick != world.Tick || check.Hash != world.Report().StateHash)
                        throw new DesyncException(world.Tick, world.Report().StateHash, check.Hash);
                    break;
                }
                if (reply[0] is not (3 or StarProtocol.Hash or StarProtocol.Ping or StarProtocol.Request or StarProtocol.Chat))
                    throw new InvalidDataException("Unexpected departing peer message.");
            }
        }
        void FinishPeer(uint player, Peer peer)
        {
            peer.Pipe.Send(StarProtocol.Check(StarProtocol.Finish, world.Tick, world.Report().StateHash));
            if (player <= 8) WaitFinal(peer); peer.Pipe.Send([StarProtocol.Complete]); peer.Pipe.Flush(player > 8 ? TimeSpan.FromMilliseconds(100) : null);
        }
        void Drop(uint player, string reason, bool graceful)
        {
            if (!peers.TryGetValue(player, out var peer)) return;
            if (player > 8)
            {
                peers.Remove(player); lost.Remove(player); Change("Зритель отключился");
                try { if (graceful && log is not null) FinishPeer(player, peer); }
                catch (Exception e) when (LinkFailure(e)) { }
                finally { peer.Dispose(); }
                return;
            }
            slots[player - 1] = slots[player - 1] with { Connected = false, Ready = false,
                Controller = phase == MatchPhase.Lobby ? SlotController.Human : SlotController.None };
            if (phase != MatchPhase.Lobby) phase = MatchPhase.Paused;
            Change($"Игрок {player}: {reason}", resetReady: phase == MatchPhase.Lobby);
            peers.Remove(player); lost.Remove(player); pending[player - 1].Clear();
            try { if (graceful && log is not null) FinishPeer(player, peer); }
            catch (Exception e) when (LinkFailure(e)) { /* The surviving peers retain the same committed prefix. */ }
            finally { peer.Dispose(); }
        }
        void Process(uint player, byte[] bytes)
        {
            if (bytes[0] == StarProtocol.Chat)
            {
                if (player != 1) peers[player].Limit(true);
                var message = StarProtocol.ReadChat(bytes);
                if (message.Player != (player > 8 ? 0 : player)) throw new InvalidDataException("Chat sender spoof.");
                Chat(message); Broadcast(bytes); return;
            }
            if (bytes[0] == 3 && player is > 1 and <= 8 && phase != MatchPhase.Lobby)
            {
                peers[player].Inbox.Add(bytes, world.Tick); return;
            }
            if (bytes[0] == StarProtocol.Hash && player is > 1 and <= 8 && phase != MatchPhase.Lobby)
            {
                var check = StarProtocol.ReadCheck(bytes, StarProtocol.Hash); var peer = peers[player];
                if (check.Tick != peer.Ack + Descriptor.HashInterval || !hashes.TryGetValue(check.Tick, out var expected))
                    throw new InvalidDataException("Peer checkpoint cadence/window mismatch.");
                if (check.Hash != expected) throw new DesyncException(check.Tick, expected, check.Hash);
                peer.Ack = check.Tick; return;
            }
            if (bytes.AsSpan().SequenceEqual(new byte[] { StarProtocol.Ping }) && player != 1) return;
            if (bytes[0] == StarProtocol.SettingsAck && player != 1 && phase == MatchPhase.Lobby && _prepare is not null)
            {
                var acknowledgement = Wire.Unpack(bytes, r => { r.ReadByte(); return (Digest: Wire.Text(r), Hash: Wire.Text(r)); });
                if (acknowledgement.Digest != SettingsDigest) return;
                if (acknowledgement.Hash != _initialHash) throw new InvalidDataException("Lobby world acknowledgement mismatch.");
                peers[player].SettingsDigest = SettingsDigest; return;
            }
            var request = StarProtocol.ReadRequest(bytes);
            if (player != 1) peers[player].Limit(false);
            if (request.Op == StarProtocol.Operation.Leave)
            {
                if (player == 1) { if (phase == MatchPhase.Lobby) throw new OperationCanceledException(_stop.Token); phase = MatchPhase.Finished; Change("Хост завершил матч"); }
                else Drop(player, "покинул матч", true);
                return;
            }
            if (player > 8) throw new InvalidDataException("Spectator control request.");
            if (request.Op is StarProtocol.Operation.Start or StarProtocol.Operation.Ai or StarProtocol.Operation.Stop && player != 1)
                throw new InvalidDataException("Coordinator operation from a guest.");
            if (request.Revision != revision) return; // A stale Ready/config/pause cannot authorize the current revision.
            switch (request.Op)
            {
                case StarProtocol.Operation.Ready when phase == MatchPhase.Lobby:
                    if (_prepare is not null && player != 1 && peers[player].SettingsDigest != SettingsDigest) break;
                    slots[player - 1] = slots[player - 1] with { Ready = true }; Change("Готовность", increment: false); break;
                case StarProtocol.Operation.Start when phase == MatchPhase.Lobby:
                    if (slots.Any(p => p.Controller == SlotController.Human && (!p.Connected || !p.Ready))) break;
                    phase = MatchPhase.Playing; revision++;
                    var initial = Snapshot("Матч начат"); SetState(initial);
                    output = File.Create(JournalPath); log = new StarJournal(output, Descriptor, world.Report().StateHash, StarProtocol.WriteState(initial));
                    Broadcast(StarProtocol.WriteState(initial)); Publish(world); nextDue = clock.Elapsed.TotalMilliseconds + 1000.0 / Descriptor.TickRate;
                    break;
                case StarProtocol.Operation.Pause when phase is MatchPhase.Playing or MatchPhase.Paused:
                    var desired = request.Enabled ? MatchPhase.Paused : MatchPhase.Playing;
                    if (phase != desired) { phase = desired; Change($"Игрок {player}"); nextDue = clock.Elapsed.TotalMilliseconds + 1000.0 / Descriptor.TickRate; }
                    break;
                case StarProtocol.Operation.Ai:
                    uint target = request.Player;
                    if (target < 2 || target > count || request.Enabled && _planAi is null) throw new InvalidDataException("Invalid AI assignment.");
                    if (slots[target - 1].Connected || phase is MatchPhase.Playing or MatchPhase.Finished) break;
                    slots[target - 1] = slots[target - 1] with { Controller = request.Enabled ? SlotController.Ai : phase == MatchPhase.Lobby ? SlotController.Human : SlotController.None };
                    pending[target - 1].Clear(); prepared = -1;
                    Change($"Управление игроком {target}", resetReady: phase == MatchPhase.Lobby); break;
                case StarProtocol.Operation.Stop when phase != MatchPhase.Lobby:
                    phase = MatchPhase.Finished; Change("Хост завершил матч"); break;
            }
        }
        void Poll()
        {
            if (clock.Elapsed.TotalMilliseconds >= pingAt)
            { Broadcast([StarProtocol.Ping]); pingAt = clock.Elapsed.TotalMilliseconds + Math.Min(1000, _timeout.TotalMilliseconds / 3); }
            foreach (var pair in peers.ToArray())
            {
                try
                {
                    for (int i = 0; i < PeerPipeline.Capacity && pair.Value.Pipe.TryRead(out var frame); i++)
                    {
                        pair.Value.LastSeen = Stopwatch.GetTimestamp(); Process(pair.Key, frame);
                        if (!peers.ContainsKey(pair.Key)) break;
                    }
                    if (peers.ContainsKey(pair.Key))
                    {
                        pair.Value.Pipe.Check();
                        if (Stopwatch.GetElapsedTime(pair.Value.LastSeen) > _timeout) lost.Add(pair.Key);
                    }
                }
                catch (Exception e) when (LinkFailure(e)) { lost.Add(pair.Key); }
            }
            foreach (uint player in lost.ToArray()) { lost.Remove(player); Drop(player, "соединение потеряно", false); }
            while (_configuration.TryDequeue(out var configuration))
            {
                if (phase != MatchPhase.Lobby) continue;
                if (configuration.Slots.Where((controller, i) => slots[i].Connected && controller != SlotController.Human).Any())
                { Volatile.Write(ref _status, "Сначала освободите занятый слот."); continue; }
                world = PrepareSettings(configuration.Data);
                window = 2 * Descriptor.InputDelay + Descriptor.HashInterval;
                for (int i = 0; i < count; i++) slots[i] = slots[i] with { Controller = configuration.Slots[i] };
                foreach (var pair in peers)
                {
                    pair.Value.SettingsDigest = null;
                    pair.Value.Inbox = new TickInbox(State.Match, pair.Key, Descriptor.InputDelay, window);
                }
                Broadcast(SettingsFrame()); Change("Хост изменил настройки", resetReady: true);
            }
            int requests = Interlocked.CompareExchange(ref _requestCount, 0, 0);
            for (int i = 0; i < requests && _requests.TryDequeue(out var frame); i++)
            { Interlocked.Decrement(ref _requestCount); Process(1, frame); }
        }
        void Accept()
        {
            if (!listener.Pending()) return;
            TcpClient socket = listener.AcceptTcpClient(); PeerPipeline? pipe = null; SslStream? tls = null;
            try
            {
                socket.NoDelay = true;
                tls = new SslStream(socket.GetStream());
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                Authenticate(tls, certificate, deadline.Token).GetAwaiter().GetResult();
                pipe = new PeerPipeline(tls, _timeout, _stop.Token);
                uint player = Wire.Unpack(pipe.Read(TimeSpan.FromSeconds(5)), r => {
                    if (r.ReadByte() != StarProtocol.Hello || r.ReadInt32() != StarProtocol.Version) throw new InvalidDataException("NET3 protocol mismatch.");
                    uint p = r.ReadUInt32(); int n = r.ReadInt32();
                    string digest = Wire.Text(r), hash = Wire.Text(r), rules = Wire.Text(r), content = Wire.Text(r);
                    if (n != count || p != 0 && (p < 2 || p > count || slots[p - 1].Controller != SlotController.Human || slots[p - 1].Connected) ||
                        p == 0 && peers.Keys.Count(k => k > 8) >= 8 || rules != Descriptor.Rules || content != Descriptor.ContentHash ||
                        _prepare is null && (digest != Wire.Hash(DirectMatch.Descriptor(Descriptor)) || hash != _initialHash))
                        throw new InvalidDataException("Lobby slot, descriptor or initial world mismatch.");
                    return p == 0 ? Enumerable.Range(9, 8).Select(i => (uint)i).First(i => !peers.ContainsKey(i)) : p;
                });
                peers.Add(player, new Peer(socket, pipe, new TickInbox(State.Match, player, Descriptor.InputDelay, window)));
                if (player <= 8) slots[player - 1] = slots[player - 1] with { Connected = true };
                if (_prepare is not null) Send(player, peers[player], SettingsFrame());
                Change($"Подключён игрок {player}", resetReady: true);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or TimeoutException or ArgumentException)
            { pipe?.Dispose(); tls?.Dispose(); socket.Dispose(); if (_stop.IsCancellationRequested) throw; Volatile.Write(ref _status, "Подключение отклонено: " + e.Message); }
        }
        try
        {
            SetState(Snapshot("Лобби"));
            while (phase == MatchPhase.Lobby) { _stop.Token.ThrowIfCancellationRequested(); Accept(); Poll(); Idle(); }
            listener.Stop();
            while (phase != MatchPhase.Finished)
            {
                _stop.Token.ThrowIfCancellationRequested(); Poll();
                if (phase == MatchPhase.Finished) break;
                if (phase == MatchPhase.Paused) { blockedAt = clock.Elapsed.TotalMilliseconds; Idle(); continue; }
                if (prepared != world.Tick)
                {
                    long target = world.Tick + Descriptor.InputDelay;
                    for (uint p = 1; p <= count; p++)
                    {
                        if (slots[p - 1].Controller == SlotController.Ai && !pending[p - 1].ContainsKey(target))
                            pending[p - 1][target] = _planAi!(world, p).ToArray();
                        else if (p == 1 && !pending[0].ContainsKey(target)) pending[0][target] = Collect();
                    }
                    prepared = world.Tick; blockedAt = clock.Elapsed.TotalMilliseconds;
                }
                long tick = world.Tick + 1;
                var blockers = peers.Where(pair => pair.Key <= 8 && ( world.Tick - pair.Value.Ack >= window ||
                    tick >= Descriptor.InputDelay && !pair.Value.Inbox.Contains(tick))).Select(pair => pair.Key).ToArray();
                if (blockers.Length != 0)
                {
                    if (clock.Elapsed.TotalMilliseconds - blockedAt >= _timeout.TotalMilliseconds)
                        foreach (uint p in blockers) Drop(p, "истёк срок команд/проверки", false);
                    Idle(); continue;
                }
                if (clock.Elapsed.TotalMilliseconds < nextDue) { Idle(); continue; }
                double waiting = clock.Elapsed.TotalMilliseconds - blockedAt;
                var frames = new List<byte[]> { StarProtocol.TurnFrame(tick, revision) };
                var commands = new List<WorldCommand>();
                for (uint p = 1; p <= count; p++)
                {
                    WorldCommand[] input = [];
                    if (tick >= Descriptor.InputDelay)
                    {
                        if (p != 1 && slots[p - 1].Controller == SlotController.Human) input = peers[p].Inbox.Take(tick);
                        else if (slots[p - 1].Controller != SlotController.None) pending[p - 1].Remove(tick, out input!);
                    }
                    var stamped = (input ?? []).Select(c => c.WithStamp(tick, p, checked(++sequences[p - 1]))).ToArray();
                    frames.Add(Wire.Batch(State.Match, p, tick, stamped)); commands.AddRange(stamped);
                }
                world.Step(commands.ToArray(), _stop.Token);
                string hash = world.Report().StateHash; hashes[tick] = hash;
                frames.Add(StarProtocol.Check(StarProtocol.Hash, tick, hash));
                foreach (var frame in frames) { log!.Write(frame); Broadcast(frame); }
                Publish(world);
                long verified = peers.Where(p => p.Key <= 8).Select(p => p.Value.Ack).DefaultIfEmpty(tick).Min();
                foreach (long old in hashes.Keys.Where(t => t <= verified).ToArray()) hashes.Remove(old);
                Volatile.Write(ref _statistics, new(tick, verified, waiting, null,
                    peers.Values.Select(p => p.Pipe.SendHighWater).DefaultIfEmpty().Max(), peers.Values.Select(p => p.Pipe.ReceiveHighWater).DefaultIfEmpty().Max(), clock.Elapsed.TotalMilliseconds));
                double period = 1000.0 / Descriptor.TickRate;
                nextDue = Math.Max(nextDue + period, clock.Elapsed.TotalMilliseconds);
                if (Descriptor.EndTick != 0 && tick >= Descriptor.EndTick) { phase = MatchPhase.Finished; Change("Достигнут конечный тик"); }
            }
            byte[] final = StarProtocol.Check(StarProtocol.Finish, world.Tick, world.Report().StateHash); log!.Write(final);
            // No successful footer until every retained peer confirms the terminal hash.
            foreach (var pair in peers) Send(pair.Key, pair.Value, final);
            foreach (var pair in peers.Where(p => p.Key <= 8)) WaitFinal(pair.Value);
            foreach (var pair in peers)
            {
                Send(pair.Key, pair.Value, [StarProtocol.Complete]);
                try { pair.Value.Pipe.Flush(pair.Key > 8 ? TimeSpan.FromMilliseconds(100) : null); } catch (Exception e) when (pair.Key > 8 && LinkFailure(e)) { }
            }
            log.Complete(); return world.Report();
        }
        finally
        {
            listener.Stop();
            foreach (var peer in peers.Values) peer.Dispose();
            log?.Dispose(); output?.Dispose();
        }
    }
}
