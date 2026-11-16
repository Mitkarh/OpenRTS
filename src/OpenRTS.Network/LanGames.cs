using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenRTS.Network;

public sealed record LanGame(string Address, int Port, string Title, string Map, int Players, int OpenSlots, int Spectators, string Fingerprint, uint[]? AvailablePlayers = null);

/// <summary>Optional LAN directory only. Discovery never creates a simulation or joins its tick barrier.</summary>
public sealed class LanGames : IDisposable
{
    public const int DiscoveryPort = 17772;
    private static readonly byte[] Query = Encoding.ASCII.GetBytes("OpenRTS-LAN-1");
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receive, _query;
    private readonly ConcurrentDictionary<string, (LanGame Game, long Seen)> _games = new();
    public IReadOnlyList<LanGame> Games => _games.Values.Where(g => Stopwatch.GetElapsedTime(g.Seen).TotalSeconds < 6)
        .OrderBy(g => g.Game.Title, StringComparer.Ordinal).Select(g => g.Game).ToArray();
    public LanGames(Func<LanGame?>? advertise = null, IPAddress? destination = null)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.EnableBroadcast = true;
        if (advertise is not null) _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, advertise is null ? 0 : DiscoveryPort));
        _receive = Task.Run(async () => {
            long rateAt = 0; int replies = 0;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    if (advertise is not null)
                    {
                        if (!packet.Buffer.AsSpan().SequenceEqual(Query)) continue;
                        if (Stopwatch.GetElapsedTime(rateAt).TotalSeconds >= 1) { rateAt = Stopwatch.GetTimestamp(); replies = 0; }
                        if (++replies > 4) continue;
                        var game = advertise(); if (game is null) continue;
                        byte[] data = JsonSerializer.SerializeToUtf8Bytes(game);
                        if (data.Length <= 2048) await _udp.SendAsync(data, packet.RemoteEndPoint, _stop.Token);
                    }
                    else if (packet.Buffer.Length <= 2048)
                    {
                        LanGame? game;
                        try { game = JsonSerializer.Deserialize<LanGame>(packet.Buffer); } catch (JsonException) { continue; }
                        if (game is null || game.Port is < 1 or > 65535 || game.Title is null || game.Map is null ||
                            game.Title.Length > 96 || game.Map.Length > 96 || game.Players is < 1 or > 8 || game.OpenSlots is < 0 or > 7 ||
                            game.Spectators is < 0 or > 8 || game.Fingerprint?.Length != 64 || !game.Fingerprint.All(Uri.IsHexDigit) || game.AvailablePlayers is { } available && (available.Length > 7 || available.Any(p => p is < 2 or > 8))) continue;
                        game = game with { Address = packet.RemoteEndPoint.Address.ToString() };
                        foreach (var old in _games.Where(g => Stopwatch.GetElapsedTime(g.Value.Seen).TotalSeconds >= 6).Select(g => g.Key)) _games.TryRemove(old, out _);
                        string key = game.Address + ":" + game.Port;
                        if (_games.Count < 64 || _games.ContainsKey(key)) _games[key] = (game, Stopwatch.GetTimestamp());
                    }
                }
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
        _query = advertise is not null ? Task.CompletedTask : Task.Run(async () => {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _udp.SendAsync(Query, new IPEndPoint(destination ?? IPAddress.Broadcast, DiscoveryPort), _stop.Token);
                    await Task.Delay(1500, _stop.Token);
                }
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
    }
    public void Dispose()
    {
        _stop.Cancel(); _udp.Dispose();
        try { Task.WhenAll(_receive, _query).Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
