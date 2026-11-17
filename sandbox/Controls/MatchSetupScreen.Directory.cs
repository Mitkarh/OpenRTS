using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Network;

public sealed partial class MatchSetupScreen
{
    private readonly LineEdit _directoryUrl = new() { Name = "DirectoryUrl", Text = "http://127.0.0.1:17773", PlaceholderText = "HTTPS адрес каталога", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly LineEdit _directoryName = new() { Name = "DirectoryName", Text = "Игрок", CustomMinimumSize = new(140, 0), MaxLength = 40, PlaceholderText = "Имя в каталоге" };
    private readonly CheckButton _publishDirectory = new() { Name = "PublishDirectory", Text = "Опубликовать в каталоге" };
    private readonly CheckButton _privateDirectory = new() { Name = "PrivateDirectory", Text = "Только по приглашению" };
    private readonly LineEdit _publicEndpoint = new() { Name = "PublicEndpoint", Text = "127.0.0.1", PlaceholderText = "Адрес хоста для других игроков" };
    private readonly LineEdit _relayEndpoint = new() { Name = "RelayEndpoint", PlaceholderText = "Relay host:port · если настроен" };
    private readonly LineEdit _inviteCode = new() { Name = "InviteCode", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "Приглашение в приватную игру", MaxLength = 97, Secret = true };
    private readonly LineEdit _ownInvite = new() { Name = "OwnInvite", Editable = false, PlaceholderText = "Приглашение появится после публикации", Secret = true };
    private readonly Label _directoryStatus = new() { Text = "Каталог необязателен · прямые и LAN игры доступны отдельно", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private Control? _directoryInvitePanel;
    private readonly Label _publicationStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private MatchDirectoryClient? _directoryClient;
    private string _directoryConfiguration = "", _directoryContent = "";
    private DirectoryMatch[] _directoryGames = [];
    private MatchJoinGrant? _directoryGrant;
    private bool _directoryShowingInvite;
    private Task<Action>? _directoryWork;
    private CancellationTokenSource? _directoryCancellation;
    private double _directoryRefresh;
    public MatchDirectoryClient? DirectoryClient => _directoryClient;
    public MatchJoinGrant? DirectoryGrant => _directoryGrant;
    public bool PublishDirectory => NetworkMode == 1 && _publishDirectory.ButtonPressed;
    public bool PrivateDirectory => _privateDirectory.ButtonPressed;
    public string PublicEndpoint => _publicEndpoint.Text.Trim();
    public MatchEndpoint? RelayEndpoint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_relayEndpoint.Text)) return null;
            if (!Uri.TryCreate("tcp://" + _relayEndpoint.Text.Trim(), UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/")
                throw new ArgumentException("Relay указывается как host:port.");
            return new(MatchEndpointKind.Relay, uri.Host, uri.Port);
        }
    }
    public void ShowDirectoryPublication(string status, string? invite)
    { _directoryStatus.Text = status; _publicationStatus.Text = status; _ownInvite.Text = invite ?? ""; }
    private MatchDirectoryClient GetDirectoryClient()
    {
        string configuration = _directoryUrl.Text.Trim() + "\n" + _directoryName.Text.Trim();
        if (_directoryClient is null || configuration != _directoryConfiguration)
        {
            _directoryClient?.Dispose(); _directoryClient = new(_directoryUrl.Text.Trim(), _directoryName.Text.Trim());
            _directoryConfiguration = configuration; _directoryGames = [];
        }
        return _directoryClient;
    }
    private void BuildDirectory(VBoxContainer box)
    {
        var account = new HBoxContainer(); box.AddChild(account); account.AddChild(_directoryUrl); account.AddChild(_directoryName);
        account.AddChild(Button("Каталог", BrowseDirectory, "BrowseDirectory"));
        box.AddChild(_directoryStatus);
        var access = new HBoxContainer(); box.AddChild(access); access.AddChild(_inviteCode);
        access.AddChild(Button("По приглашению", ResolveDirectoryInvite, "ResolveInvite"));
        var publishing = new HBoxContainer(); box.AddChild(publishing); publishing.AddChild(_publishDirectory); publishing.AddChild(_privateDirectory);
        var endpoints = new HBoxContainer { Visible = false }; box.AddChild(endpoints);
        _publicEndpoint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _relayEndpoint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        endpoints.AddChild(_publicEndpoint); endpoints.AddChild(_relayEndpoint);
        _publishDirectory.Toggled += enabled => endpoints.Visible = enabled;
    }
    private void QueueDirectory(Func<CancellationToken, Task<Action>> operation)
    {
        if (_directoryWork is not null) return;
        _directoryCancellation?.Dispose(); _directoryCancellation = new();
        _directoryStatus.Text = "Запрос к каталогу…";
        try { _directoryWork = operation(_directoryCancellation.Token); }
        catch (Exception e) { _directoryStatus.Text = e.Message; }
    }
    private void BrowseDirectory()
    {
        if (_directoryWork is not null) return;
        try
        {
            _directoryShowingInvite = false;
            var client = GetDirectoryClient(); string content = _directoryContent;
            QueueDirectory(async cancellation => {
                var games = await client.FindAsync("openrts-lobby-v3", content, cancellation);
                return () => { _directoryGames = games; _directoryStatus.Text = $"Каталог · найдено {games.Length} · прямые игры с хостом-игроком"; RefreshGames(); };
            });
        }
        catch (Exception e) { _directoryStatus.Text = e.Message; }
    }
    private void PrepareDirectoryHost()
    {
        try
        {
            var client = GetDirectoryClient(); _ = RelayEndpoint;
            QueueDirectory(async cancellation => { await client.ConnectAsync(cancellation); return () => BeginNetworkLobby(true); });
        }
        catch (Exception e) { _directoryStatus.Text = e.Message; }
    }
    private void ResolveDirectoryInvite()
    {
        try
        {
            var client = GetDirectoryClient(); string invite = _inviteCode.Text.Trim();
            QueueDirectory(async cancellation => {
                var game = await client.ResolveInviteAsync(invite, cancellation);
                return () => { _directoryShowingInvite = true; _directoryGames = [game]; _directoryStatus.Text = "Приватный матч найден. Выберите роль."; RefreshGames(); };
            });
        }
        catch (Exception e) { _directoryStatus.Text = e.Message; }
    }
    private void JoinDirectory(DirectoryMatch match, MatchAccessRole role)
    {
        if (_directoryWork is not null) return;
        var client = _directoryClient!; string? invite = match.Game.Private ? _inviteCode.Text.Trim() : null;
        Guid request = Guid.NewGuid();
        QueueDirectory(async cancellation => {
            try
            {
                var grant = await client.JoinAsync(match.Game.Match, new(request, role, Invite: invite), cancellation);
                return () => {
                    _directoryGrant = grant; var endpoint = grant.Endpoints.OrderBy(e => e.Kind).First();
                    _address.Text = endpoint.Address; _port.Value = endpoint.Port; _pin.Text = grant.Fingerprint;
                    _joinRole.Select(role == MatchAccessRole.Player ? 0 : 1); _networkPlayer.Visible = role == MatchAccessRole.Player;
                    if (role == MatchAccessRole.Player) _networkPlayer.Value = grant.Player;
                    BeginNetworkLobby(false);
                };
            }
            catch
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await client.CancelAsync(match.Game.Match, request, deadline.Token); } catch { }
                throw;
            }
        });
    }
    private void AddDirectoryGames()
    {
        foreach (var match in _directoryGames.Where(m => m.Expires > DateTimeOffset.UtcNow && (m.Game.Title + " " + m.Game.Map).Contains(_gameFilter.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var game = match.Game; var row = new HBoxContainer(); _gameRows.AddChild(row);
            string map = _mapChoices.FirstOrDefault(m => m.Map.Id == game.Map).Title ?? game.Map;
            string phase = game.Phase switch { MatchPhase.Lobby => "Ожидание", MatchPhase.Paused => "Пауза", _ => "Игра" };
            row.AddChild(new Label { Text = $"[Каталог] {game.Title} · {map} · {game.Players} игр. · {phase}\nСвободно: {Math.Max(0, game.AvailablePlayers.Length - match.ReservedPlayers)} · Хост игрока", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var join = Button("Войти", () => JoinDirectory(match, MatchAccessRole.Player), "DirectoryJoinMatch"); join.Disabled = game.Phase != MatchPhase.Lobby || game.AvailablePlayers.Length <= match.ReservedPlayers; row.AddChild(join);
            var watch = Button("Смотреть", () => JoinDirectory(match, MatchAccessRole.Spectator)); watch.Disabled = game.Phase is not (MatchPhase.Lobby or MatchPhase.Paused); row.AddChild(watch);
        }
    }
    public void CancelDirectoryJoin()
    {
        var grant = _directoryGrant; var client = _directoryClient; _directoryGrant = null;
        if (grant is not null && client is not null) _ = ReleaseDirectoryJoin(client, grant);
    }
    private static async Task ReleaseDirectoryJoin(MatchDirectoryClient client, MatchJoinGrant grant)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await client.CancelAsync(grant.Match, grant.Request, deadline.Token); } catch { }
    }
    private void UpdateDirectory(double delta)
    {
        if (_directoryWork is { IsCompleted: true } work)
        {
            _directoryWork = null;
            try { var apply = work.GetAwaiter().GetResult(); _directoryCancellation?.Token.ThrowIfCancellationRequested(); apply(); }
            catch (OperationCanceledException) { _directoryStatus.Text = "Запрос каталога отменён."; }
            catch (Exception e) { _directoryStatus.Text = "Каталог: " + e.Message; }
        }
        if (_page == "browser" && !_directoryShowingInvite && _directoryClient?.Info is not null && (_directoryRefresh -= delta) <= 0)
        { _directoryRefresh = 5; BrowseDirectory(); }
    }
}
