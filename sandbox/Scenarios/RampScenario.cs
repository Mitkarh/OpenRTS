using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Samples;

internal sealed class RampScenario(UnitCatalog catalog)
{
    public const int Width = 32, Depth = 25;
    public const int RampLeft = 12, RampRight = 19, PlateauSouth = 12, RampFoot = 20;
    public const int CellSize = WorldPosition.UnitsPerCell;
    public const int RisePerCell = CellSize / 4;
    public const int PlateauElevation = (RampFoot - PlateauSouth) * RisePerCell;

    internal static readonly WorldPosition PlateauGoal = new(22 * CellSize + 256, 8 * CellSize + 384, PlateauElevation);
    internal static readonly WorldPosition LowGoal = new(13 * CellSize + 256, 22 * CellSize + 384);
    private static readonly (string Type, float X, float Y)[] Starts =
    [
        ("ramp-rifleman", 8.5f, 21.5f), ("ramp-tank", 6.5f, 20.5f),
        ("ramp-rifleman", 16.5f, 16.5f), ("ramp-tank", 6.75f, 22.75f),
        ("ramp-rifleman", 17.5f, 7.5f), ("ramp-tank", 20.5f, 5.5f)
    ];

    public static int UnitCount => Starts.Length;
    public NavigationGrid Grid { get; } = CreateGrid(catalog.Locomotion!);

    private static NavigationGrid CreateGrid(LocomotionCatalog terrain)
    {
        var samples = new SurfaceSample[Width * Depth];
        var rises = new SurfaceRise[samples.Length];
        var kinds = new byte[samples.Length];
        Array.Fill(kinds, terrain.TerrainIndex("grass"));
        byte road = terrain.TerrainIndex("road");

        for (int y = 3; y < PlateauSouth; y++)
        {
            int left = y switch { 3 => 9, 4 => 7, 5 or 11 => 6, _ => 5 };
            int right = y switch { 3 => 23, 4 => 24, 5 or 11 => 26, _ => 27 };
            for (int x = left; x < right; x++)
                samples[y * Width + x] = new(PlateauElevation);
        }
        for (int y = PlateauSouth; y < RampFoot; y++)
            for (int x = RampLeft; x < RampRight; x++)
            {
                int cell = y * Width + x;
                samples[cell] = new((RampFoot - y) * RisePerCell - RisePerCell / 2, Slope: RisePerCell);
                rises[cell] = new(0, -RisePerCell);
                kinds[cell] = road;
            }
        return new NavigationGrid(Width, Depth, terrain: kinds, surface: samples, rises: rises);
    }

    public SimulationWorld CreateWorld(int workers)
    {
        var world = new SimulationWorld(catalog.Units, Width * CellSize, Depth * CellSize, initialCapacity: UnitCount,
            navigationGrid: Grid, navigationWorkers: workers, players: [new PlayerDefinition(1, "Синий")]);
        try
        {
            var types = catalog.Units.Select((unit, index) => (unit.Id, index)).ToDictionary(entry => entry.Id, entry => entry.index);
            foreach (var (type, x, y) in Starts)
                world.AddInitialUnit(types[type], new((int)(x * CellSize), (int)(y * CellSize)), owner: 1);
            return world;
        }
        catch { world.Dispose(); throw; }
    }
}
