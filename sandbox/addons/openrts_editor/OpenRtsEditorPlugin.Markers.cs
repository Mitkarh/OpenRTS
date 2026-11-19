#if TOOLS
using Godot;

namespace OpenRTS.Godot.Authoring;

public partial class OpenRtsEditorPlugin
{
    internal static OpenRtsMap2D NewMap2D(bool isometric)
    {
        string name = isometric ? "GrassMudIsometric" : "GrassMudTopDown";
        var tiles = GD.Load<TileSet>($"res://resources/terrain/{name}.tres")
            ?? throw new IOException($"Could not load the {name} TileSet.");
        var layer = new TileMapLayer { Name = "Terrain", TileSet = tiles };
        string id = "map-" + MapAuthoringIdentity.NewId();
        var root = new OpenRtsMap2D { Name = "Map", MapId = id, CompiledPath = $"res://artifacts/maps/{id}.ortmap.json" };
        root.AddChild(layer);
        layer.Owner = root;
        root.Terrain = layer;
        for (int y = 0; y < root.Size.Y; y++)
            for (int x = 0; x < root.Size.X; x++) layer.SetCell(new(x, y), 0, Vector2I.Zero);
        return root;
    }

    private void CreateMap2D(bool isometric)
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not null)
            throw new ArgumentException("Open an empty scene first (Scene → New Scene).");
        EditorInterface.Singleton.AddRootNode(NewMap2D(isometric));
    }

    private void RefreshOwners()
    {
        string[] slots = EditorInterface.Singleton.GetEditedSceneRoot() switch
        {
            OpenRtsMap { IsSkirmish: false } map => map.FixtureSlots,
            OpenRtsMap2D { IsSkirmish: false } map => map.FixtureSlots,
            _ => []
        };
        var owners = slots.Where(id => !string.IsNullOrWhiteSpace(id)).Prepend("neutral").Distinct(StringComparer.Ordinal).ToArray();
        if (_owners.ItemCount == owners.Length && owners.Select((owner, index) => owner == _owners.GetItemText(index)).All(same => same)) return;
        string selected = _owners.Selected < 0 ? "neutral" : _owners.GetItemText(_owners.Selected);
        _owners.Clear();
        foreach (string owner in owners) _owners.AddItem(owner);
        _owners.Select(Math.Max(0, Array.IndexOf(owners, selected)));
    }

    internal void AddObject()
    {
        var root = EditedMap();
        if (_types.ItemCount == 0) LoadTypes();
        if (_types.Selected < 0) throw new ArgumentException("The map catalog has no selected object type.");
        RefreshOwners();
        string type = _types.GetItemText(_types.Selected), owner = _owners.GetItemText(_owners.Selected);
        if (root is OpenRtsMap2D map) AddMarker2D(map, new OpenRtsMapObject2D { TypeId = type, OwnerSlot = owner });
        else AddMarker(new OpenRtsMapObject { TypeId = type, OwnerSlot = owner });
    }

    internal void AddStart()
    {
        var root = EditedMap();
        if (root is OpenRtsMap2D map)
        {
            if (!map.IsSkirmish) throw new ArgumentException("Fixture maps use FixtureSlots instead of start markers.");
            AddMarker2D(map, new OpenRtsMapStart2D());
        }
        else AddMarker(new OpenRtsMapStart());
    }

    internal void AddMarker2D(OpenRtsMap2D root, Node2D marker)
    {
        if (root.Terrain is not { } layer)
        {
            marker.Free();
            throw new ArgumentException("Assign the map's Terrain TileMapLayer first.");
        }
        marker.Set(MapAuthoringIdentity.IdField(marker), MapAuthoringIdentity.NewId());
        var selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes();
        var parent = selected.Count == 1 && selected[0] is OpenRtsMapGroup2D group && root.IsAncestorOf(group) ? group : (Node2D)root;
        if (MapAuthoringIdentity.HasOrder(marker)) marker.Set("Order", MapAuthoringIdentity.NextOrder(parent));
        marker.Name = marker is OpenRtsMapObject2D unit ? unit.TypeId : marker is OpenRtsMapStart2D ? "Start" : "Group";
        if (parent == root) marker.Position = layer.MapToLocal(root.Size / 2);
        AttachUndo(root, parent, [marker], "Add OpenRTS marker");
    }
}
#endif
