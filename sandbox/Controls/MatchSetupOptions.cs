using System;
using System.Text.Json;
using OpenRTS.Network;

public sealed record MatchSeat(SlotController Controller, int Start, uint Team = 0);
public sealed record MatchSetupOptions(string Map, string Mode, int Fog, int Delay, int TickRate, MatchSeat[] Seats, int SpeedPerTick = 128, int FogHoldTicks = 0)
{
    public System.Collections.Generic.IReadOnlySet<uint> Opponents(uint player) => Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller != SlotController.None && p.i + 1 != player &&
        (Seats[player - 1].Team == 0 || Seats[player - 1].Team != p.s.Team)).Select(p => (uint)p.i + 1).ToHashSet();
    public string Serialize() => JsonSerializer.Serialize(this);
    public static MatchSetupOptions Parse(string data)
    {
        var options = JsonSerializer.Deserialize<MatchSetupOptions>(data) ?? throw new ArgumentException("Нет настроек матча.");
        if (string.IsNullOrWhiteSpace(options.Map) || string.IsNullOrWhiteSpace(options.Mode) || options.Fog is < 0 or > 2 ||
            options.Delay is < 1 or > 16 || options.TickRate is not (15 or 25 or 40) || options.Seats is null || options.Seats.Length != 4 ||
            options.Seats.Any(s => s is null || !Enum.IsDefined(s.Controller) || s.Start is < 0 or > 3 || s.Team > 4) ||
            options.Seats[0].Controller != SlotController.Human || options.SpeedPerTick is < 1 or > 1024 || options.FogHoldTicks is < 0 or > 250)
            throw new ArgumentException("Неверные настройки матча.");
        return options;
    }
}
