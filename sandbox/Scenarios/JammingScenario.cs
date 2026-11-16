using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

public static class JammingScenario
{
    public static SimulationWorld Create(UnitCatalog catalog)
    {
        var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 }, catalog.Units,
            80 * 1024, 56 * 1024, navigationGrid: BuildingScenario.Grid(catalog.Locomotion!),
            players: [new(1, "Blue", 20000), new(2, "Red", 20000)]);
        try
        {
            void Add(string type, int x, int y, uint owner)
            {
                int index = catalog.Units.ToList().FindIndex(t => t.Id == type);
                var footprint = catalog.Units[index].Footprint;
                world.AddInitialUnit(index, new(x * 1024 + (footprint?.HalfWidth ?? 0) % 1024,
                    y * 1024 + (footprint?.HalfHeight ?? 0) % 1024), owner);
            }
            Add("headquarters", 16, 20, 1);
            Add("power-plant", 12, 24, 1);
            Add("tank-mixed", 25, 19, 1);
            Add("radar-buggy", 25, 24, 1);
            Add("construction-engineer", 19, 25, 1);
            Add("stealth-truck", 36, 20, 2);
            Add("tank", 37, 23, 2);
            Add("rifleman", 35, 23, 2);
            Add("stealth-generator", 43, 32, 2);
            Add("power-plant", 49, 32, 2);
            Add("civilian-house", 24, 30, 0);
            world.SynchronizeVisibility();
            return world;
        }
        catch { world.Dispose(); throw; }
    }
}
