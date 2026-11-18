using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed class MapMatch
{
    internal sealed record Placement(string Label, int Type, WorldPosition Position, uint Owner, Source Source, bool IsStart = false,
        int QuarterTurns = 0, int? InitialHealth = null);
    private readonly UnitCatalog _catalog;
    private readonly PlayerDefinition[] _players;
    private readonly Placement[] _placements;
    private readonly ulong _seed;
    public GameMap Map { get; }
    public ulong Seed => _seed;
    public int UnitCount => _placements.Length;

    internal MapMatch(GameMap map, UnitCatalog catalog, PlayerDefinition[] players, Placement[] placements, ulong seed)
    {
        Map = map;
        _catalog = catalog;
        _players = players;
        _placements = placements;
        _seed = seed;
    }

    public SimulationWorld CreateWorld(int workers = 1, bool restrictPlayerCommands = false) =>
        CreateWorld(new(), workers: workers, restrictPlayerCommands: restrictPlayerCommands);

    public SimulationWorld CreateWorld(GameplayMechanics mechanics, int speedPerTick = 128, int workers = 1,
        bool restrictPlayerCommands = false, int tickRate = 25)
    {
        var grid = Map.Grid;
        var world = SimulationWorld.CreateWithMechanics(mechanics, _catalog.Units, grid.Width * grid.CellSize, grid.Height * grid.CellSize,
            seed: _seed, initialCapacity: UnitCount, tickRate: tickRate, speedPerTick: speedPerTick, navigationGrid: grid, navigationWorkers: workers, players: _players);
        try
        {
            if (restrictPlayerCommands) world.RestrictCommandsToPlayers();
            foreach (var placement in _placements)
            {
                try
                {
                    world.AddInitialMapUnit(placement.Type, placement.Position, placement.Owner, placement.IsStart, placement.QuarterTurns, placement.InitialHealth);
                }
                catch (ArgumentException error)
                {
                    throw new ContentException(placement.Source, placement.Label,
                        $"Unit '{_catalog.Units[placement.Type].Id}' at ({placement.Position.X}, {placement.Position.Y}): {error.Message}");
                }
            }
            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }
}
