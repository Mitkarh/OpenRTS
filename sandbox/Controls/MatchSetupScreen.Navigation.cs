using System;
using Godot;
using OpenRTS.Network;

public sealed partial class MatchSetupScreen
{
    private readonly OptionButton _joinRole = new() { Name = "JoinRole" };
    private readonly LineEdit _gameName = new() { Text = "Игра OpenRTS", MaxLength = 64, PlaceholderText = "Название игры", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public string NetworkGameName => _gameName.Text.Trim().Length == 0 ? "Игра OpenRTS" : _gameName.Text.Trim();
    private LanGames? _browser;
    private VBoxContainer _gameRows = null!;
    private Label _gameStatus = null!;
    private double _refreshAt;
    private readonly LineEdit _gameFilter = new() { PlaceholderText = "Название или карта…" };
    private readonly LineEdit _mapFilter = new() { Name = "MapFilter", PlaceholderText = "Найти карту…" };
    private GridContainer _mapTiles = null!;

    private VBoxContainer Page(string key, string heading)
    {
        var scroll = new ScrollContainer { Name = key + "Page", FollowFocus = true }; _root.AddChild(scroll); scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 28);
        var center = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; scroll.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new(840, 0) }; center.AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", Sandbox.TerminalBox(new("#101712ee"), new("#697657"), 20));
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 14); panel.AddChild(box);
        var top = new HBoxContainer(); box.AddChild(top);
        top.AddChild(Button("‹ Назад", () => Back(key)));
        var label = new Label { Text = heading, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; label.AddThemeFontSizeOverride("font_size", 25); top.AddChild(label);
        top.AddChild(new Label { Text = "OPENRTS", Modulate = new("#b5bc93") });
        box.AddChild(new HSeparator()); _pages[key] = scroll; return box;
    }
    private static Button Button(string text, Action pressed, string? name = null)
    {
        var button = new Button { Text = text, CustomMinimumSize = new(0, 36) }; if (name is not null) button.Name = name;
        button.Pressed += pressed; return button;
    }
    private static void Field(VBoxContainer parent, string label, Control control)
    { parent.AddChild(new Label { Text = label, Modulate = new("#b6c2a2") }); parent.AddChild(control); }
    private void ShowPage(string key)
    {
        _page = key;
        foreach (var pair in _pages) pair.Value.Visible = pair.Key == key;
        if (key == "replays") RefreshReplays();
        if (key == "browser" && _browser is null)
        {
            try { _browser = new LanGames(); } catch (System.Net.Sockets.SocketException error) { _gameStatus.Text = "Поиск недоступен: " + error.Message; }
        }
    }
    private void Back(string key)
    {
        if (key == "maps" || key == "advanced") { ShowPage("lobby"); return; }
        if (key == "lobby") { if (_networkSession is not null) { CancelNetwork?.Invoke(); _networkSession = null; _busy = false; } ShowPage(NetworkMode == 0 ? "main" : "browser"); return; }
        if (key == "replays" && _busy) CancelNetwork?.Invoke();
        _directoryCancellation?.Cancel();
        ShowPage("main");
    }
    private void BuildMain()
    {
        var layer = new MarginContainer { Name = "MainMenu" }; _root.AddChild(layer); layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 58);
        var body = new HBoxContainer(); layer.AddChild(body);
        var column = new VBoxContainer { CustomMinimumSize = new(300, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; body.AddChild(column);
        column.AddChild(new Label { Text = "СТРАТЕГИЧЕСКОЕ КОМАНДОВАНИЕ", Modulate = new("#c9bb8b") });
        var title = new Label { Text = "OPENRTS" }; title.AddThemeFontSizeOverride("font_size", 58); column.AddChild(title);
        column.AddChild(new Label { Text = "Выберите поле боя", Modulate = new("#b8c2aa") }); column.AddChild(new HSeparator());
        column.AddChild(Button("Skirmish", OpenSkirmish, "MainSkirmish"));
        column.AddChild(Button("Multiplayer", () => ShowPage("browser"), "MainMultiplayer"));
        column.AddChild(Button("Повторы", () => ShowPage("replays"), "MainReplays"));
        column.AddChild(Button("Настройки", () => ShowPage("settings"), "MainSettings"));
        column.AddChild(Button("Выход", () => GetTree().Quit(), "MainExit"));
        var credit = new Label { Text = "ПЕРЕКРЁСТОК\nТактическая карта", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkEnd, HorizontalAlignment = HorizontalAlignment.Right, Modulate = new("#8d9c81") }; body.AddChild(credit);
        _pages["main"] = layer;
    }
    private void BuildLobby()
    {
        var box = Page("lobby", "Лобби");
        var heading = new HBoxContainer(); box.AddChild(heading); _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; heading.AddChild(_title); heading.AddChild(_viewers);
        var body = new HBoxContainer(); body.AddThemeConstantOverride("separation", 20); box.AddChild(body);
        var invitePanel = new VBoxContainer(); box.AddChild(invitePanel); _directoryInvitePanel = invitePanel; invitePanel.AddChild(_publicationStatus);
        var invitation = new HBoxContainer(); invitePanel.AddChild(invitation); _ownInvite.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        invitation.AddChild(_ownInvite);
        invitation.AddChild(Button("Показать приглашение", () => _ownInvite.Secret = !_ownInvite.Secret));
        invitation.AddChild(Button("Скопировать", () => { if (_ownInvite.Text.Length != 0) DisplayServer.ClipboardSet(_ownInvite.Text); }));
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; body.AddChild(left);
        left.AddChild(_players);
        var fill = new Control { CustomMinimumSize = new(0, 150), SizeFlagsVertical = Control.SizeFlags.ExpandFill }; left.AddChild(fill);
        left.AddChild(new Label { Text = "Одинаковый альянс — союзники. Фракция пока одна: OpenRTS.", Modulate = new("#9ba98e") });
        left.AddChild(_status);
        var connection = new HBoxContainer(); _connectionPanel = connection; left.AddChild(connection);
        connection.AddChild(new Label { Text = "Отпечаток хоста", Modulate = new("#9ba98e") });
        var fingerprint = new LineEdit { Editable = false, Name = "HostFingerprint", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "Появится при создании сетевой игры" }; connection.AddChild(fingerprint); _fingerprintView = fingerprint;
        var right = new VBoxContainer { CustomMinimumSize = new(260, 0) }; body.AddChild(right);
        right.AddChild(_preview); _preview.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && Editable) ShowPage("maps"); };
        _mapButton = Button("Карта", () => ShowPage("maps"), "ChooseMap"); right.AddChild(_mapButton); right.AddChild(_details);
        Field(right, "Стартовый набор", _modes); Field(right, "Видимость карты", _fog); Field(right, "Темп игры", _speed);
        right.AddChild(Button("Расширенные настройки…", () => ShowPage("advanced"), "AdvancedOptions"));
        box.AddChild(new HSeparator());
        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; box.AddChild(footer);
        footer.AddChild(_readyNetwork); footer.AddChild(_startNetwork); footer.AddChild(_launch);
        _launch.AddThemeStyleboxOverride("normal", Sandbox.TerminalBox(new("#423b24"), new("#bdac73"), 9));
        _advanced = Page("advanced", "Параметры матча");
        Field(_advanced, "Буфер сетевого ввода · тики", _delay);
        _advanced.AddChild(new Label { Text = "Больше буфер — устойчивее соединение, дольше отклик команд.", Modulate = new("#9ba98e") });
        _advanced.AddChild(Button("Вернуться в лобби", () => ShowPage("lobby")));
    }
    private LineEdit? _fingerprintView;
    private Control? _connectionPanel;
    private void BuildBrowser()
    {
        var box = Page("browser", "Multiplayer"); BuildDirectory(box);
        var top = new HBoxContainer(); box.AddChild(top); _gameFilter.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; top.AddChild(_gameFilter);
        top.AddChild(Button("Обновить", RefreshGames));
        var headers = new Label { Text = "ИГРА                                      КАРТА                         ИГРОКИ       СВОБОДНО       ЗРИТЕЛИ", Modulate = new("#b5bc93") }; box.AddChild(headers);
        var scroll = new ScrollContainer { CustomMinimumSize = new(0, 220) }; box.AddChild(scroll); _gameRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_gameRows);
        _gameStatus = new Label { Text = "Поиск игр в локальной сети…", Modulate = new("#b5bc93") }; box.AddChild(_gameStatus);
        var create = new HBoxContainer(); box.AddChild(create); create.AddChild(_gameName); create.AddChild(Button("Создать игру", () => OpenNetworkLobby(true), "CreateGame"));
        box.AddChild(new HSeparator()); box.AddChild(new Label { Text = "Прямое подключение" });
        var direct = new HBoxContainer(); box.AddChild(direct); _address.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; direct.AddChild(_address); direct.AddChild(_port); _joinRole.AddItem("Игрок"); _joinRole.AddItem("Зритель"); direct.AddChild(_joinRole); direct.AddChild(_networkPlayer);
        _joinRole.ItemSelected += index => _networkPlayer.Visible = index == 0;
        box.AddChild(_pin);
        box.AddChild(Button("Подключиться", () => OpenNetworkLobby(false), "JoinGame"));
        box.AddChild(new Label { Text = "Для игры через интернет используйте адрес хоста. Зритель подключается до старта матча.", Modulate = new("#9ba98e") });
    }
    private void OpenNetworkLobby(bool host)
    {
        _directoryGrant = null;
        if (host && _publishDirectory.ButtonPressed) { PrepareDirectoryHost(); return; }
        BeginNetworkLobby(host);
    }
    private void BeginNetworkLobby(bool host)
    {
        _network.Select(host ? 1 : 2); _busy = false; _networkSession = null; _shownState = null; _lastSettings = "";
        if (host) _address.Text = "0.0.0.0";
        ShowPage("lobby"); RefreshControls(); LaunchMatch();
    }
    private void RefreshGames()
    {
        foreach (var child in _gameRows.GetChildren()) { _gameRows.RemoveChild(child); child.QueueFree(); }
        var games = _browser?.Games ?? [];
        foreach (var game in games.Where(g => (g.Title + " " + g.Map).Contains(_gameFilter.Text, StringComparison.OrdinalIgnoreCase)))
        {
            string title = _mapChoices.FirstOrDefault(m => m.Map.Id == game.Map).Title ?? game.Map;
            var row = new HBoxContainer(); _gameRows.AddChild(row);
            foreach (var cell in new[] { game.Title, title, game.Players.ToString(), game.OpenSlots.ToString(), game.Spectators.ToString() }) row.AddChild(new Label { Text = cell, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(90, 34), VerticalAlignment = VerticalAlignment.Center });
            row.AddChild(Button("Войти", () => { _address.Text = game.Address; _port.Value = game.Port; _pin.Text = game.Fingerprint; _joinRole.Select(0); _networkPlayer.Show(); if (game.AvailablePlayers is { Length: > 0 }) _networkPlayer.Value = game.AvailablePlayers[0]; OpenNetworkLobby(false); }));
            row.AddChild(Button("Смотреть", () => { _address.Text = game.Address; _port.Value = game.Port; _pin.Text = game.Fingerprint; _joinRole.Select(1); _networkPlayer.Hide(); OpenNetworkLobby(false); }));
        }
        AddDirectoryGames();
        _gameStatus.Text = games.Count == 0 ? "Игр в локальной сети пока нет. Создайте свою или подключитесь по адресу." : $"Найдено игр: {games.Count}";
    }
    private void BuildMaps()
    {
        var box = Page("maps", "Выбор карты"); box.AddChild(_mapFilter); _mapTiles = new GridContainer { Columns = 3 }; _mapTiles.AddThemeConstantOverride("h_separation", 16); _mapTiles.AddThemeConstantOverride("v_separation", 14); box.AddChild(_mapTiles);
        _mapFilter.TextChanged += _ => PopulateMaps(); PopulateMaps();
    }
    private void PopulateMaps()
    {
        foreach (var child in _mapTiles.GetChildren()) { _mapTiles.RemoveChild(child); child.QueueFree(); }
        for (int i = 0; i < _mapChoices.Length; i++)
        {
            int index = i; var map = _mapChoices[i]; if (!(map.Title + " " + map.Description).Contains(_mapFilter.Text, StringComparison.OrdinalIgnoreCase)) continue;
            var panel = new PanelContainer { CustomMinimumSize = new(310, 0) }; panel.AddThemeStyleboxOverride("panel", Sandbox.TerminalBox(new("#172018"), new("#647453"), 9)); _mapTiles.AddChild(panel);
            var tile = new VBoxContainer(); panel.AddChild(tile);
            var preview = new MatchSetupPreview { CustomMinimumSize = new(280, 180), MouseFilter = Control.MouseFilterEnum.Ignore }; tile.AddChild(preview); preview.SetMap(map.Map, _terrain);
            tile.AddChild(new Label { Text = $"{map.Title}\n{map.Map.Slots.Count} игрока · {map.Map.Grid.Width} × {map.Map.Grid.Height}" });
            tile.AddChild(new Label { Text = map.Description, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new(260, 48) });
            var select = Button("Выбрать", () => { if (!Editable) return; _maps.Select(index); _maps.EmitSignal(OptionButton.SignalName.ItemSelected, index); ShowPage("lobby"); }, "SelectMap" + index); select.Disabled = !Editable || _networkSession?.State.Slots.Any(s => s.Connected && s.Player > map.Map.Slots.Count) == true; tile.AddChild(select);
        }
    }
    private void BuildSettings()
    {
        var box = Page("settings", "Настройки");
        var fullscreen = new CheckButton { Text = "Полноэкранный режим", ButtonPressed = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen };
        fullscreen.Toggled += enabled => DisplayServer.WindowSetMode(enabled ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed); box.AddChild(fullscreen);
        Field(box, "Отображение тумана", _fogStyle); Field(box, "Отображение радарных контактов", _radarStyle);
        box.AddChild(Button("Сохранить", () => { var config = new ConfigFile(); config.SetValue("view", "fog", _fogStyle.Selected); config.SetValue("view", "radar", _radarStyle.Selected); config.Save("user://menu.cfg"); ShowPage("main"); }));
        var saved = new ConfigFile(); if (saved.Load("user://menu.cfg") == Error.Ok) { _fogStyle.Select(Math.Clamp((int)saved.GetValue("view", "fog", 1), 0, 2)); _radarStyle.Select(Math.Clamp((int)saved.GetValue("view", "radar", 1), 0, 2)); }
    }
    public override void _Process(double delta)
    {
        UpdateDirectory(delta);
        if (_fingerprintView is not null && _fingerprintView.Text != _pin.Text) _fingerprintView.Text = _pin.Text;
        if (_mapTiles is not null) _mapTiles.Columns = GetViewport().GetVisibleRect().Size.X < 1100 ? 2 : 3;
        if (_page == "browser" && (_refreshAt -= delta) <= 0) { _refreshAt = 1; RefreshGames(); }
    }
    public override void _ExitTree() { _directoryCancellation?.Cancel(); _directoryClient?.Dispose(); _browser?.Dispose(); }
}
