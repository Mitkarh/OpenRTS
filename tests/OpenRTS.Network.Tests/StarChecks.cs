using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Network;

internal static class StarChecks
{
    private static void Check(bool value, string message = "NET3 assertion failed") { if (!value) throw new Exception(message); }
    private static void Wait(Func<bool> ready, params StarSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            foreach (var session in sessions) if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult();
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("NET3 scenario timed out: " + string.Join(" | ", sessions.Select(s => s.Status)));
            Thread.Sleep(5);
        }
    }
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static long Tick(StarSession session) { using var frame = session.Frames.AcquireLatest(); return frame?.Tick ?? -1; }
    private static SimulationWorld World(int workers = 1, int players = 4)
    {
        var world = new SimulationWorld([new UnitDefinition("unit", 100)], 65536, 65536, 42, navigationWorkers: workers);
        for (uint p = 1; p <= players; p++) world.AddInitialUnit(0, new((int)p * 4096, 2048), p);
        return world;
    }
    private static readonly MatchDescriptor Descriptor = new("net3-test", "unit", "four-players", 42, EndTick: 0, InputDelay: 3, HashInterval: 4);
    private sealed class Match : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "openrts-net3-" + Guid.NewGuid().ToString("N"));
        internal readonly int Port = StarChecks.Port();
        internal readonly List<StarSession> Sessions = [];
        internal readonly StarSession Host;
        internal Match(Func<SimulationWorld, uint, IReadOnlyList<WorldCommand>>? ai = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Host = new(true, "127.0.0.1", Port, null, Descriptor, () => World(), Path.Combine(Directory, "1.journal"),
                playerCount: 4, aiPlayers: [4], planAi: ai ?? ((_, _) => []));
            Sessions.Add(Host);
            // The listener is started before the owner's lobby publication.
            Wait(() => Host.Status.StartsWith("Лобби", StringComparison.Ordinal), Host);
        }
        internal StarSession Join(uint player, string? pin = null, int? relayPort = null)
        {
            var session = new StarSession(false, "127.0.0.1", relayPort ?? Port, pin ?? Host.Fingerprint, Descriptor, () => World(2),
                Path.Combine(Directory, player + ".journal"), playerCount: 4, player: player,
                planAi: (_, _) => throw new Exception("A client attempted to run AI"));
            Sessions.Add(session); return session;
        }
        internal void Start(StarSession two, StarSession three)
        {
            StarSession[] active = [Host, two, three];
            Wait(() => Host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == Host.State.Revision && three.State.Revision == Host.State.Revision, active);
            foreach (var s in active) s.Ready(s.State.Revision);
            Wait(() => Host.State.Slots.Take(3).All(s => s.Ready), active); Host.Start();
            Wait(() => active.All(s => Tick(s) >= 1), active);
        }
        internal void Replay(StarSession session)
        {
            using var input = File.OpenRead(session.JournalPath);
            Check(StarJournal.Replay(input, Descriptor, () => World()) == session.Completion.GetAwaiter().GetResult(), "NET3 offline replay differs");
        }
        public void Dispose() { foreach (var session in Sessions.AsEnumerable().Reverse()) session.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("NET3 stale Ready, config reset, three TLS players and shared pause", () => {
            using var match = new Match(); var host = match.Host;
            var two = match.Join(2); var three = match.Join(3);
            Wait(() => host.State.Slots.Take(3).All(s => s.Connected) && two.State.Revision == host.State.Revision && three.State.Revision == host.State.Revision, match.Sessions.ToArray());
            long stale = host.State.Revision;
            host.SetAi(4, false); Wait(() => host.State.Revision > stale, match.Sessions.ToArray());
            Check(host.State.Slots.All(s => !s.Ready));
            host.Ready(stale); two.Ready(stale); three.Ready(stale); host.Start(); Thread.Sleep(100);
            Check(host.State.Phase == MatchPhase.Lobby && host.State.Slots.All(s => !s.Ready));
            host.SetAi(4, true); Wait(() => host.State.Slots[3].Controller == SlotController.Ai, match.Sessions.ToArray());
            match.Start(two, three);
            for (uint p = 1; p <= 3; p++) match.Sessions[(int)p - 1].Submit(new(0, 0, 0, CommandKind.Move, new(p), new(20000, (int)p * 4096)));
            two.SendChat("Привет, RTS");
            Wait(() => Tick(host) >= 8, match.Sessions.ToArray()); two.Pause(true);
            Wait(() => match.Sessions.All(s => s.State.Phase == MatchPhase.Paused), match.Sessions.ToArray());
            long tick = Tick(host); Check(match.Sessions.All(s => Tick(s) == tick)); Thread.Sleep(150); Check(match.Sessions.All(s => Tick(s) == tick));
            Check(match.Sessions.All(s => s.TryReadChat(out var chat) && chat == new MatchChat(2, "Привет, RTS")));
            three.Pause(false); Wait(() => Tick(host) >= tick + 6, match.Sessions.ToArray()); host.Stop();
            Wait(() => match.Sessions.All(s => s.Completion.IsCompleted), match.Sessions.ToArray());
            Check(match.Sessions.All(s => s.Completion.Result == host.Completion.Result));
            foreach (var session in match.Sessions) { match.Replay(session); Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)), "Control/command histories differ"); }
            using var incomplete = new MemoryStream(File.ReadAllBytes(host.JournalPath)[..^5]);
            try { StarJournal.Replay(incomplete, Descriptor, () => World()); throw new Exception("Truncated replay was accepted"); } catch (EndOfStreamException) { }
        });
        test("NET3 agreed leave, host AI takeover and survivor histories", () => {
            var planned = new Dictionary<uint, List<long>>();
            using var match = new Match((world, p) => {
                if (!planned.TryGetValue(p, out var ticks)) planned[p] = ticks = [];
                Check(!ticks.Contains(world.Tick), "AI planned twice on one world tick"); ticks.Add(world.Tick);
                return [new(0, 0, 0, CommandKind.Move, new(p), new(30000, (int)p * 4096))];
            });
            var host = match.Host; var two = match.Join(2); var three = match.Join(3); match.Start(two, three);
            Wait(() => Tick(host) >= 8, match.Sessions.ToArray()); three.Leave();
            Wait(() => three.Completion.IsCompleted && host.State.Phase == MatchPhase.Paused && two.State.Revision == host.State.Revision, match.Sessions.ToArray());
            Check(host.State.Slots[2].Controller == SlotController.None && Tick(host) == three.Completion.Result.Tick);
            match.Replay(three); host.SetAi(3, true);
            Wait(() => host.State.Slots[2].Controller == SlotController.Ai && two.State.Revision == host.State.Revision, host, two);
            host.Pause(false); Wait(() => Tick(host) >= three.Completion.Result.Tick + 10, host, two); host.Stop();
            Wait(() => host.Completion.IsCompleted && two.Completion.IsCompleted, host, two);
            Check(host.Completion.Result == two.Completion.Result && planned[3].Count >= 7);
            match.Replay(host); match.Replay(two);
            Check(File.ReadAllBytes(host.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(two.JournalPath)));
            byte[] departed = File.ReadAllBytes(three.JournalPath), survived = File.ReadAllBytes(host.JournalPath);
            // The departing history has its own terminal checkpoint/footer after the common membership prefix.
            int footer = 4 + 1 + 8 + 2 + 64 + 5;
            Check(departed.AsSpan(0, departed.Length - footer).SequenceEqual(survived.AsSpan(0, departed.Length - footer)));
        });
        test("NET3 spectator loss keeps playing; guest loss pauses; host loss aborts clients", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2); var three = match.Join(3);
            var observer = match.Join(0); Wait(() => host.State.Spectators == 1, host, two, three, observer); match.Start(two, three);
            long beforeObserverLeave = Tick(host); observer.Dispose();
            Wait(() => host.State.Spectators == 0 && Tick(host) >= beforeObserverLeave + 3, host, two, three);
            Check(host.State.Phase == MatchPhase.Playing, "A departed spectator paused the match");
            Wait(() => Tick(host) >= 5, match.Sessions.ToArray()); three.Dispose();
            Wait(() => host.State.Phase == MatchPhase.Paused && two.State.Phase == MatchPhase.Paused, host, two);
            long tick = Tick(two); Thread.Sleep(150); Check(Tick(host) == tick && Tick(two) == tick);
            host.Pause(false); Wait(() => Tick(two) >= tick + 4, host, two);
            host.Dispose(); Wait(() => two.Completion.IsCompleted); Check(!two.Completion.IsCompletedSuccessfully);
            long ended = Tick(two); Thread.Sleep(100); Check(Tick(two) == ended);
        });
        test("NET3 rejects slot collision and bad pin without killing lobby", () => {
            using var match = new Match(); var host = match.Host; var two = match.Join(2);
            Wait(() => host.State.Slots[1].Connected, host, two);
            var collision = match.Join(2); Wait(() => collision.Completion.IsCompleted); Check(collision.Completion.IsFaulted);
            var badPin = match.Join(3, new string('A', 64)); Wait(() => badPin.Completion.IsCompleted); Check(badPin.Completion.IsFaulted);
            Check(!host.Completion.IsCompleted && host.State.Phase == MatchPhase.Lobby);
            var three = match.Join(3); match.Start(two, three); host.Stop();
            Wait(() => host.Completion.IsCompleted && two.Completion.IsCompleted && three.Completion.IsCompleted, host, two, three);
            Check(host.Completion.Result == two.Completion.Result && host.Completion.Result == three.Completion.Result);
        });
        test("NET3 control revision, frame limits and command ownership envelope", () => {
            var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing,
                [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true), new(3, SlotController.Ai, false, false)], "start");
            var copy = StarProtocol.ReadState(StarProtocol.WriteState(state)); Check(copy.Slots.Count == 3);
            void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; } throw new Exception("Expected rejection"); }
            Reject(() => StarJournal.ValidateTransition(state, new(state.Match, 3, 0, MatchPhase.Paused, state.Slots, "gap"), 0));
            Reject(() => StarProtocol.ChatFrame(2, new string('я', 129)));
            Reject(() => Wire.Batch(Guid.NewGuid(), 9, 1, []));
            var batch = Wire.Batch(state.Match, 3, 1, [new(1, 3, 1, CommandKind.Stop, new(3))]);
            Check(StarJournal.ReadCommands(batch, state, 3, 1, new long[3]).Single().Issuer == 3);
            Reject(() => StarJournal.ReadCommands(batch, state, 2, 1, new long[3]));
        });
        test("NET3 asymmetric WAN delay/loss retains bounded verification and identical journals", () => {
            using var match = new Match(); var host = match.Host;
            using var fast = new WanLink(match.Port, rtt: 50, jitter: 20);
            using var slow = new WanLink(match.Port, rtt: 150, jitter: 20, lossPercent: 5);
            var two = match.Join(2, relayPort: fast.Port); var three = match.Join(3, relayPort: slow.Port); match.Start(two, three);
            two.Submit(new(0, 0, 0, CommandKind.Move, new(2), new(25000, 4096)));
            three.Submit(new(0, 0, 0, CommandKind.Move, new(3), new(25000, 6144)));
            Wait(() => Tick(host) >= 35, host, two, three);
            Check(host.Statistics is { } sample && sample.Tick - sample.VerifiedTick <= 2 * Descriptor.InputDelay + Descriptor.HashInterval);
            Check(slow.Retransmissions > 0 && host.Statistics!.SendQueueHighWater <= PeerPipeline.Capacity);
            host.Stop(); Wait(() => match.Sessions.All(s => s.Completion.IsCompleted), match.Sessions.ToArray());
            foreach (var session in match.Sessions) { Check(session.Completion.Result == host.Completion.Result); match.Replay(session); }
            Check(File.ReadAllBytes(host.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(three.JournalPath)));
        });
        test("Lobby host configuration replaces guest defaults; spectators do not issue input", () => {
            string directory = Path.Combine(Path.GetTempPath(), "openrts-lobby-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            int port = Port();
            (MatchDescriptor Descriptor, SimulationWorld World) Prepare(string data)
            {
                var world = World();
                if (data == "host-revised") world.SetInitialTeams(new Dictionary<uint, uint> { [1] = 1, [2] = 1 });
                return (Descriptor with { ScenarioHash = data }, world);
            }
            try
            {
                using var host = new StarSession(true, "127.0.0.1", port, null, Descriptor with { ScenarioHash = "host" }, () => World(), Path.Combine(directory, "host"),
                    playerCount: 4, aiPlayers: [3], closedPlayers: [4], planAi: (_, _) => [], lobbySettings: "host", prepareSettings: Prepare);
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, Descriptor with { ScenarioHash = "different-local-map" }, () => World(), Path.Combine(directory, "guest"),
                    playerCount: 4, player: 2, lobbySettings: "different-local-map", prepareSettings: Prepare);
                using var spectator = new StarSession(false, "127.0.0.1", port, host.Fingerprint, Descriptor, () => World(), Path.Combine(directory, "spectator"),
                    playerCount: 4, player: 0, lobbySettings: "default", prepareSettings: Prepare);
                Wait(() => guest.LobbySettings == "host" && spectator.LobbySettings == "host" && host.State.Spectators == 1, host, guest, spectator);
                host.ConfigureLobby("host-revised", [SlotController.Human, SlotController.Human, SlotController.Ai, SlotController.None]);
                Wait(() => guest.LobbySettings == "host-revised" && spectator.LobbySettings == "host-revised" && guest.State.Revision == host.State.Revision, host, guest, spectator);
                void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected role rejection"); }
                Reject(() => guest.ConfigureLobby("foreign", [SlotController.Human, SlotController.Human, SlotController.Ai, SlotController.None]));
                Reject(() => spectator.Ready(spectator.State.Revision)); Reject(() => spectator.Pause(true));
                Reject(() => spectator.Submit(new(0, 0, 0, CommandKind.Stop, new(1))));
                host.Ready(host.State.Revision); guest.Ready(guest.State.Revision);
                Wait(() => host.State.Slots.Take(2).All(p => p.Ready), host, guest, spectator); host.Start();
                Wait(() => Tick(spectator) >= 12, host, guest, spectator); host.Stop();
                Wait(() => host.Completion.IsCompleted && guest.Completion.IsCompleted && spectator.Completion.IsCompleted, host, guest, spectator);
                foreach (var session in new[] { host, guest, spectator })
                {
                    Check(session.Completion.Result == host.Completion.Result);
                    using var log = File.OpenRead(session.JournalPath); Check(StarJournal.Replay(log, session.Descriptor, () => Prepare("host-revised").World) == host.Completion.Result);
                    Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)));
                }
            }
            finally { Directory.Delete(directory, true); }
        });
        test("LAN directory discovers a real lobby and its TLS fingerprint", () => {
            var entry = new LanGame("", 17771, "LAN test", "crossroads", 1, 2, 0, new string('A', 64), [2, 3]);
            using var advertise = new LanGames(() => entry);
            using var browser = new LanGames(destination: IPAddress.Loopback);
            Wait(() => browser.Games.Count != 0);
            Check(browser.Games.Any(g => g.Title == entry.Title && g.Address == "127.0.0.1" && g.Fingerprint == entry.Fingerprint && g.AvailablePlayers!.SequenceEqual(new uint[] { 2, 3 })));
        });
        test("NET3 eight human slots at minimum input delay", () => {
            int port = Port(); var sessions = new List<StarSession>();
            string directory = Path.Combine(Path.GetTempPath(), "openrts-net3-eight-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var descriptor = Descriptor with { EndTick = 16, InputDelay = 1 };
            try
            {
                var host = new StarSession(true, "127.0.0.1", port, null, descriptor, () => World(players: 8), Path.Combine(directory, "1"), playerCount: 8); sessions.Add(host);
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                for (uint p = 2; p <= 8; p++) sessions.Add(new(false, "127.0.0.1", port, host.Fingerprint, descriptor,
                    () => World(2, 8), Path.Combine(directory, p.ToString()), playerCount: 8, player: p));
                Wait(() => host.State.Slots.All(s => s.Connected) && sessions.All(s => s.State.Revision == host.State.Revision), sessions.ToArray());
                foreach (var session in sessions) session.Ready(session.State.Revision);
                Wait(() => host.State.Slots.All(s => s.Ready), sessions.ToArray()); host.Start();
                Wait(() => sessions.All(s => Tick(s) >= 1), sessions.ToArray());
                foreach (var session in sessions) session.Submit(new(0, 0, 0, CommandKind.Move, new(session.Player), new((int)session.Player * 4096, 10000)));
                Wait(() => sessions.All(s => s.Completion.IsCompleted), sessions.ToArray());
                foreach (var session in sessions)
                {
                    Check(session.Completion.Result == host.Completion.Result);
                    using var input = File.OpenRead(session.JournalPath); Check(StarJournal.Replay(input, descriptor, () => World(players: 8)) == host.Completion.Result);
                    Check(File.ReadAllBytes(session.JournalPath).AsSpan().SequenceEqual(File.ReadAllBytes(host.JournalPath)));
                }
            }
            finally { foreach (var session in sessions) session.Dispose(); Directory.Delete(directory, true); }
        });
        test("NET3 heartbeat cannot extend missing-command deadline", () => {
            string directory = Path.Combine(Path.GetTempPath(), "openrts-net3-deadline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            int port = Port(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                using var host = new StarSession(true, "127.0.0.1", port, null, Descriptor, () => World(), Path.Combine(directory, "host"),
                    playerCount: 4, aiPlayers: [3, 4], planAi: (_, _) => [], timeout: TimeSpan.FromMilliseconds(300));
                Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
                using var socket = new TcpClient(); socket.Connect(IPAddress.Loopback, port);
                using var tls = new System.Net.Security.SslStream(socket.GetStream(), false, (_, _, _, _) => true);
                tls.AuthenticateAsClient("openrts.local");
                using var initial = World(); initial.RestrictCommandsToPlayers();
                Wire.Send(tls, Wire.Pack(w => { w.Write(StarProtocol.Hello); w.Write(StarProtocol.Version); w.Write(2u); w.Write(4);
                    Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, initial.Report().StateHash); Wire.Text(w, Descriptor.Rules); Wire.Text(w, Descriptor.ContentHash); }), cancel.Token).GetAwaiter().GetResult();
                long revision = 0;
                while (revision == 0)
                {
                    var bytes = Wire.Receive(tls, cancel.Token).GetAwaiter().GetResult();
                    if (bytes[0] == StarProtocol.State) revision = StarProtocol.ReadState(bytes).Revision;
                }
                Wire.Send(tls, StarProtocol.RequestFrame(StarProtocol.Operation.Ready, revision), cancel.Token).GetAwaiter().GetResult();
                host.Ready(revision); Wait(() => host.State.Slots.Take(2).All(s => s.Ready), host); host.Start();
                int pings = 0;
                var reader = Task.Run(async () => {
                    try
                    {
                        while (true)
                        {
                            var frame = await Wire.Receive(tls, cancel.Token);
                            if (frame.AsSpan().SequenceEqual(new byte[] { StarProtocol.Ping })) { Interlocked.Increment(ref pings); await Wire.Send(tls, frame, cancel.Token); }
                            if (frame[0] == StarProtocol.State && StarProtocol.ReadState(frame).Phase == MatchPhase.Paused) return;
                        }
                    }
                    catch (EndOfStreamException) { }
                });
                Wait(() => host.State.Phase == MatchPhase.Paused, host); reader.GetAwaiter().GetResult();
                Check(Tick(host) == Descriptor.InputDelay - 1 && Volatile.Read(ref pings) >= 2);
                Check(host.State.Slots[1].Controller == SlotController.None);
                host.Stop(); Wait(() => host.Completion.IsCompleted, host);
                using var log = File.OpenRead(host.JournalPath); Check(StarJournal.Replay(log, Descriptor, () => World()) == host.Completion.Result);
            }
            finally { Directory.Delete(directory, true); }
        });
    }
}
