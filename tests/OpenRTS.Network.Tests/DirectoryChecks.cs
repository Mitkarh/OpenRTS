using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core;
using OpenRTS.Network;

internal static class DirectoryChecks
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private static void Check(bool value, string message = "NET5 assertion failed") { if (!value) throw new Exception(message); }
    private static void Reject(Action action, string? code = null)
    {
        try { action(); } catch (MatchDirectoryException e) when (code is null || e.Code == code) { return; }
        catch (InvalidDataException) when (code is null) { return; }
        throw new Exception("Expected NET5 rejection: " + code);
    }
    private static AccountSession Login(MatchDirectory service, DirectoryIdentity identity)
    { var challenge = service.Challenge(new("Player", identity.PublicKey)); return service.Login(new(challenge.Id, identity.SignLogin(challenge))); }
    private static MatchAdvertisement Game(Guid? match = null, bool hidden = false) => new(match ?? Guid.NewGuid(), 1, StarSession.ProtocolVersion,
        "net5", "content", "Test match", "test-map", new string('A', 64), MatchPhase.Lobby, [2], 1, 0,
        [new(MatchEndpointKind.Direct, "127.0.0.1", 17771)], hidden);
    private static int Port()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Wait(Func<bool> condition, params StarSession[] sessions)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        { foreach (var session in sessions) if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult(); if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("NET5 TLS scenario timed out"); Thread.Sleep(5); }
    }
    private static long Tick(StarSession session) { using var frame = session.Frames.AcquireLatest(); return frame?.Tick ?? -1; }
    private sealed class Service : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "openrts-net5-" + Guid.NewGuid().ToString("N"));
        internal readonly string Url;
        private Process _process = null!;
        internal Service()
        {
            System.IO.Directory.CreateDirectory(Directory); Url = "http://127.0.0.1:" + Port();
            Start();
        }
        private void Start()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath("tools/OpenRTS.MatchDirectory/bin/Debug/net10.0/OpenRTS.MatchDirectory.dll"));
            start.ArgumentList.Add("--urls=" + Url); start.ArgumentList.Add("--signing-key=" + Path.Combine(Directory, "key.pem"));
            _process = Process.Start(start)!;
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) }; var clock = Stopwatch.StartNew();
            while (true)
            {
                try { if (http.GetAsync(Url + "/v1/info").GetAwaiter().GetResult().IsSuccessStatusCode) break; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                if (_process.HasExited) throw new Exception(_process.StandardError.ReadToEnd());
                if (clock.Elapsed.TotalSeconds > 8) throw new TimeoutException("Directory process did not start"); Thread.Sleep(30);
            }
        }
        internal void Restart() { Stop(); _process.Dispose(); Start(); }
        internal void Stop() { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(5000); } }
        public void Dispose() { Stop(); _process.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("NET5 key-bound session, challenge replay, expiry and lease ownership", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var first = new DirectoryIdentity(); using var other = new DirectoryIdentity();
            var challenge = service.Challenge(new("Player", first.PublicKey));
            Reject(() => service.Login(new(challenge.Id, other.SignLogin(challenge))), "login_rejected");
            Reject(() => service.Login(new(challenge.Id, first.SignLogin(challenge))), "login_rejected");
            var session = Login(service, first); var stranger = Login(service, other); var game = Game(); var lease = service.Register(session.Token, game);
            Reject(() => service.Heartbeat(stranger.Token, game.Match, lease.Token, game), "lease_invalid");
            Reject(() => service.Close(session.Token, game.Match, new string('0', 64)), "lease_invalid");
            service.Heartbeat(session.Token, game.Match, lease.Token, game);
            clock.Advance(31); Check(service.Find("net5", "content").Length == 0);
            Reject(() => service.Join(session.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "match_expired");
            var active = Game(); var activeLease = service.Register(session.Token, active);
            service.Close(session.Token, active.Match, activeLease.Token); Check(service.Find("net5", "content").Length == 0);
            clock.Advance(3600); Reject(() => service.Register(session.Token, Game()), "session_expired");
            Check(!session.ToString().Contains(session.Token) && !lease.ToString().Contains(lease.Token), "Credentials escaped ToString");
        });
        test("NET5 atomic last seat, idempotency, cancellation, private invite and stale heartbeat", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var ownerKey = new DirectoryIdentity(); using var oneKey = new DirectoryIdentity(); using var twoKey = new DirectoryIdentity();
            var owner = Login(service, ownerKey); var one = Login(service, oneKey); var two = Login(service, twoKey);
            var game = Game(); var lease = service.Register(owner.Token, game);
            Guid request = Guid.NewGuid(); int admitted = 0; MatchJoinGrant? grant = null;
            Parallel.ForEach(new[] { one, two }, account => {
                try { var value = service.Join(account.Token, game.Match, new(request, MatchAccessRole.Player)); Interlocked.Increment(ref admitted); grant = value; }
                catch (MatchDirectoryException e) when (e.Code is "slot_reserved" or "request_conflict") { }
            });
            Check(admitted == 1 && grant is not null); var winner = AdmissionTicket.Verify(service.Info.SigningKey, grant!.Ticket, clock.GetUtcNow()).Account == one.Account.Id ? one : two;
            Check(service.Join(winner.Token, game.Match, new(request, MatchAccessRole.Player)).Ticket == grant.Ticket);
            service.Cancel(winner.Token, game.Match, request); Reject(() => service.Join(winner.Token, game.Match, new(request, MatchAccessRole.Player)), "ticket_expired");
            Reject(() => service.Join(winner.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "slot_reserved");
            clock.Advance(25); service.Heartbeat(owner.Token, game.Match, lease.Token, game); clock.Advance(6);
            Check(service.Join(winner.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).Player == 2);
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 2, Phase = MatchPhase.Paused });
            Reject(() => service.Heartbeat(owner.Token, game.Match, lease.Token, game), "stale_revision");
            Check(service.Find("other", "content").Length == 0);
            var hidden = Game(hidden: true); var invitation = service.Register(owner.Token, hidden);
            Check(service.Find("net5", "content").Length == 1);
            Check(service.ResolveInvite(one.Token, invitation.Invite!).Game.Match == hidden.Match);
            Reject(() => service.Join(one.Token, hidden.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "invite_invalid");
            Check(service.Join(one.Token, hidden.Match, new(Guid.NewGuid(), MatchAccessRole.Player, Invite: invitation.Invite)).Player == 2);
        });
        test("NET5 confirmed seats and spectators wait for publication without changing a revision", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var service = new MatchDirectory(key);
            using var hostKey = new DirectoryIdentity(); using var playerKey = new DirectoryIdentity();
            var owner = Login(service, hostKey); var account = Login(service, playerKey); var game = Game(); var lease = service.Register(owner.Token, game);
            var grant = service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            var claims = AdmissionTicket.Verify(service.Info.SigningKey, grant.Ticket, DateTimeOffset.UtcNow);
            service.Confirm(owner.Token, game.Match, lease.Token, new(claims.Nonce, claims.Account));
            service.Confirm(owner.Token, game.Match, lease.Token, new(claims.Nonce, claims.Account));
            service.Heartbeat(owner.Token, game.Match, lease.Token, game);
            Check(service.Find("net5", "content").Single().ReservedPlayers == 1);
            Reject(() => service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)), "slot_reserved");
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 2, AvailablePlayers = [] });
            Check(service.Find("net5", "content").Single().ReservedPlayers == 0);
            for (int i = 0; i < 8; i++)
            {
                var watch = service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator));
                var observation = AdmissionTicket.Verify(service.Info.SigningKey, watch.Ticket, DateTimeOffset.UtcNow);
                service.Confirm(owner.Token, game.Match, lease.Token, new(observation.Nonce, observation.Account));
            }
            Reject(() => service.Join(account.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Spectator)), "spectators_full");
            service.Heartbeat(owner.Token, game.Match, lease.Token, game with { Revision = 3, AvailablePlayers = [], Spectators = 8 });
            Check(service.Find("net5", "content").Single().ReservedSpectators == 0);
        });
        test("NET5 host validates signature, role, match, account proof, expiry and one-time use", () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var clock = new Clock(); using var service = new MatchDirectory(key, clock);
            using var ownerKey = new DirectoryIdentity(); using var playerKey = new DirectoryIdentity(); using var attacker = new DirectoryIdentity();
            var owner = Login(service, ownerKey); var player = Login(service, playerKey); var game = Game(); service.Register(owner.Token, game);
            var grant = service.Join(player.Token, game.Match, new(Guid.NewGuid(), MatchAccessRole.Player));
            var gate = new MatchAdmissionGate(new(service.Info.SigningKey), clock); string nonce = DirectoryIdentity.Secret();
            gate.CheckProof(grant.Ticket, nonce, playerKey.SignAdmission(grant.Ticket, nonce));
            Reject(() => gate.CheckProof(grant.Ticket, nonce, attacker.SignAdmission(grant.Ticket, nonce)));
            Reject(() => gate.CheckProof(grant.Ticket, DirectoryIdentity.Secret(), playerKey.SignAdmission(grant.Ticket, nonce)));
            Reject(() => gate.Redeem(grant.Ticket, Guid.NewGuid(), 2, game.Fingerprint, game.Rules, game.ContentHash));
            Reject(() => gate.Redeem(grant.Ticket, game.Match, 0, game.Fingerprint, game.Rules, game.ContentHash));
            string corrupt = grant.Ticket[..^4] + "AAAA"; Reject(() => gate.Inspect(corrupt));
            Check(gate.Redeem(grant.Ticket, game.Match, 2, game.Fingerprint, game.Rules, game.ContentHash).Account == player.Account.Id);
            Reject(() => gate.Redeem(grant.Ticket, game.Match, 2, game.Fingerprint, game.Rules, game.ContentHash));
            clock.Advance(31); Reject(() => gate.Inspect(grant.Ticket));
        });
        test("NET5 HTTP directory, ticketed TLS fallback and service outage preserve replay", () => {
            using var service = new Service(); using var owner = new MatchDirectoryClient(service.Url, "Host"); using var account = new MatchDirectoryClient(service.Url, "Player");
            owner.ConnectAsync().GetAwaiter().GetResult(); account.ConnectAsync().GetAwaiter().GetResult();
            int port = Port(); var descriptor = new MatchDescriptor("net5", "content", "map", 42, EndTick: 0);
            SimulationWorld World() { var w = new SimulationWorld([new("unit", 100)], 16384, 16384, 42); w.AddInitialUnit(0, new(2048, 2048), 1); w.AddInitialUnit(0, new(4096, 2048), 2); return w; }
            using var host = new StarSession(true, "127.0.0.1", port, null, descriptor, World, Path.Combine(service.Directory, "host.journal"), admissionPolicy: new(owner.Info!.SigningKey));
            Wait(() => host.Status.StartsWith("Лобби", StringComparison.Ordinal), host);
            using var relay = new WanLink(port);
            var game = Game(host.State.Match) with { Fingerprint = host.Fingerprint, Endpoints = [new(MatchEndpointKind.Direct, "127.0.0.1", Port()), new(MatchEndpointKind.Relay, "127.0.0.1", relay.Port)] };
            using var publication = new DirectoryListing(owner, host, game.Title, "127.0.0.1", game.Endpoints[0].Port, () => game.Map, relay: game.Endpoints[1]);
            Wait(() => publication.Status.StartsWith("Игра опубликована", StringComparison.Ordinal), host);
            Check(account.FindAsync("net5", "content").Result.Single().Trust == "peer-hosted");
            var grant = account.JoinAsync(game.Match, new(Guid.NewGuid(), MatchAccessRole.Player)).GetAwaiter().GetResult();
            using (var unsigned = new StarSession(false, "127.0.0.1", port, host.Fingerprint, descriptor, World, Path.Combine(service.Directory, "unsigned.journal")))
            { Wait(() => unsigned.Completion.IsCompleted, host); Check(unsigned.Completion.IsFaulted && !host.State.Slots[1].Connected); }
            using var guest = new StarSession(false, "127.0.0.1", port, host.Fingerprint, descriptor, World, Path.Combine(service.Directory, "guest.journal"), admission: grant, accountIdentity: account.Identity);
            Wait(() => host.State.Slots[1].Connected && guest.State.Revision == host.State.Revision, host, guest);
            host.Ready(host.State.Revision); guest.Ready(guest.State.Revision); Wait(() => host.State.Slots.All(s => s.Ready), host, guest); host.Start();
            Wait(() => Tick(guest) >= 150, host, guest);
            Check(account.FindAsync("net5", "content").Result.Single() is { ReservedPlayers: 0, Game.Phase: MatchPhase.Playing });
            service.Stop(); long before = Tick(guest);
            Wait(() => publication.Status.StartsWith("Каталог недоступен", StringComparison.Ordinal), host, guest);
            Wait(() => Tick(guest) >= before + 16, host, guest); host.Stop(); Wait(() => host.Completion.IsCompleted && guest.Completion.IsCompleted, host, guest);
            Check(host.Completion.Result == guest.Completion.Result && relay.Bytes > 0);
            Check(File.ReadAllBytes(host.JournalPath).SequenceEqual(File.ReadAllBytes(guest.JournalPath)));
            using var log = File.OpenRead(guest.JournalPath); Check(StarJournal.Replay(log, descriptor, World) == host.Completion.Result);
            Check(!File.ReadAllText(host.JournalPath).Contains(grant.Ticket), "Ticket leaked to journal");
        });
        test("NET5 HTTP session renews after a directory restart with the same signing key", () => {
            using var service = new Service(); using var account = new MatchDirectoryClient(service.Url, "Host");
            account.ConnectAsync().GetAwaiter().GetResult(); string identity = account.Identity.AccountId;
            account.RegisterAsync(Game()).GetAwaiter().GetResult(); service.Restart();
            var game = Game(); Check(account.RegisterAsync(game).GetAwaiter().GetResult().Match == game.Match);
            Check(account.Identity.AccountId == identity && account.FindAsync("net5", "content").Result.Single().Game.Match == game.Match);
        });
        test("NET5 HTTP rejects malformed/oversized bodies and rate limits", () => {
            using var service = new Service(); using var http = new HttpClient();
            using var oversized = http.PostAsync(service.Url + "/v1/challenges", new StringContent(new string('x', 20000), Encoding.UTF8, "application/json")).Result;
            Check((int)oversized.StatusCode is 400 or 413);
            using var malformed = http.PostAsync(service.Url + "/v1/challenges", new StringContent("null", Encoding.UTF8, "application/json")).Result; Check(malformed.StatusCode == HttpStatusCode.BadRequest);
            bool limited = false;
            for (int i = 0; i < 125; i++) { using var response = http.GetAsync(service.Url + "/v1/info").Result; if ((int)response.StatusCode == 429) limited = true; }
            Check(limited);
        });
    }
}
