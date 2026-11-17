namespace OpenRTS.Network;

/// <summary>Independent catalogue heartbeat. Failure cannot enter the simulation's barrier.</summary>
public sealed class DirectoryListing : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private string _status = "Публикация в каталоге…";
    private DirectoryLease? _lease;
    public string Status => Volatile.Read(ref _status);
    public string? Invite => Volatile.Read(ref _lease)?.Invite;
    public DirectoryListing(MatchDirectoryClient client, StarSession host, string title, string publicAddress, int publicPort,
        Func<string> map, bool privateGame = false, MatchEndpoint? relay = null)
    {
        if (!host.IsHost || client.Info is null) throw new ArgumentException("A connected directory account and player-host are required.");
        DirectoryValidation.Text(title, 64, "название");
        MatchEndpoint[] endpoints = relay is null ? [new(MatchEndpointKind.Direct, publicAddress, publicPort)] : [new(MatchEndpointKind.Direct, publicAddress, publicPort), relay];
        foreach (var endpoint in endpoints) DirectoryValidation.Endpoint(endpoint);
        _worker = Task.Run(async () => {
            var receipts = new Queue<MatchAdmissionReceipt>();
            try
            {
                while (!_stop.IsCancellationRequested && !host.Completion.IsCompleted && host.State.Phase != MatchPhase.Finished)
                {
                    try
                    {
                        var state = host.State;
                        var game = new MatchAdvertisement(state.Match, state.Revision, StarSession.ProtocolVersion, host.Descriptor.Rules, host.Descriptor.ContentHash,
                            title, map(), host.Fingerprint, state.Phase, state.Slots.Where(s => s.Controller == SlotController.Human && !s.Connected).Select(s => s.Player).ToArray(),
                            state.Slots.Count(s => s.Connected), state.Spectators, endpoints, privateGame);
                        if (_lease is null) Volatile.Write(ref _lease, await client.RegisterAsync(game, _stop.Token));
                        else await client.HeartbeatAsync(_lease, game, _stop.Token);
                        while (host.TryReadAdmission(out var receipt)) receipts.Enqueue(receipt);
                        while (receipts.TryPeek(out var receipt)) { await client.ConfirmAsync(_lease!, receipt, _stop.Token); receipts.Dequeue(); }
                        Volatile.Write(ref _status, privateGame ? "Приватная игра опубликована · доступ по приглашению" : "Игра опубликована в каталоге");
                    }
                    catch (MatchDirectoryException e) when (e.Code is "lease_invalid" or "match_expired") { Volatile.Write(ref _lease, null); Volatile.Write(ref _status, "Регистрация истекла; повторная публикация…"); }
                    catch (Exception e) when (e is HttpRequestException or MatchDirectoryException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
                    { if (_stop.IsCancellationRequested) break; Volatile.Write(ref _status, "Каталог недоступен; прямой матч продолжается. " + e.Message); }
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                if (_lease is { } lease)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await client.CloseAsync(lease, deadline.Token); } catch (Exception e) when (e is HttpRequestException or MatchDirectoryException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        });
    }
    public void Dispose()
    {
        _stop.Cancel();
        if (!_worker.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Catalogue publisher did not stop.");
        _stop.Dispose();
    }
}
