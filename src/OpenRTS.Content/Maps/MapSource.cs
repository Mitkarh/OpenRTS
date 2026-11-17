using System.Collections.ObjectModel;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

/// <summary>Independent, immutable palette and row-major cell indices.</summary>
public sealed class MapLayer
{
    public ReadOnlyCollection<string> Palette { get; }
    public ReadOnlyCollection<int> Cells { get; }

    public MapLayer(IReadOnlyList<string> palette, IReadOnlyList<int> cells)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(cells);
        if (palette.Count is < 1 or > MapCompiler.MaximumPaletteEntries || cells.Count > NavigationGrid.MaximumCells)
            throw new ArgumentException("Map layer exceeds its palette or cell budget.");
        Palette = Array.AsReadOnly(palette.ToArray());
        Cells = Array.AsReadOnly(cells.ToArray());
    }
}

public sealed record MapSlotSource(string Id, WorldPosition? Position = null, bool Reverse = false);
public sealed record MapObjectSource(string Id, string Type, WorldPosition Position, string Owner, Source? Origin = null);

/// <summary>Numeric map input shared by M1 and future scene extraction; not a running world or a Godot scene.</summary>
public sealed partial class MapSource
{
    public string Id { get; }
    public bool IsSkirmish { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public MapLayer Terrain { get; }
    public MapLayer Visual { get; }
    public ReadOnlyCollection<MapSlotSource> Slots { get; }
    public ReadOnlyCollection<MapObjectSource> Objects { get; }
    public Source Origin { get; }

    public MapSource(string id, bool isSkirmish, int width, int height, MapLayer terrain, MapLayer visual,
        IReadOnlyList<MapSlotSource> slots, IReadOnlyList<MapObjectSource> objects,
        int cellSize = WorldPosition.UnitsPerCell, Source? origin = null)
    {
        ArgumentNullException.ThrowIfNull(terrain); ArgumentNullException.ThrowIfNull(visual);
        ArgumentNullException.ThrowIfNull(slots); ArgumentNullException.ThrowIfNull(objects);
        if (slots.Count > MapCompiler.MaximumSlots || objects.Count > MapCompiler.MaximumObjects)
            throw new ArgumentException("Map exceeds its slot or object budget.");
        Id = id; IsSkirmish = isSkirmish; Width = width; Height = height; CellSize = cellSize;
        Terrain = terrain; Visual = visual; Slots = Array.AsReadOnly(slots.ToArray()); Objects = Array.AsReadOnly(objects.ToArray());
        Origin = origin ?? new("<map>", 1, 1);
    }

    public string Write() => MapJson.Write(this);
    public static MapSource Read(ContentSource source) => MapJson.ReadSource(source);
}
