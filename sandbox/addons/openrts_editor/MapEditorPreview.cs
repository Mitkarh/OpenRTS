#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Presentation;
using OpenRTS.Godot.Rendering;
using DeclarativeContent;

namespace OpenRTS.Godot.Authoring;

/// <summary>Transient batches, never authored Nodes, ECS state or a second simulation.</summary>
public sealed class MapEditorPreview : IDisposable
{
    private readonly Node3D _node = new() { Name = "OpenRTSEditorPreview" };
    private readonly UnitCatalog _catalog;
    private readonly SpriteWorldView _sprites;
    private Node3D? _terrain;
    private string[] _visual = [];
    private Vector2I _size;
    private Dictionary<string, ulong> _textures = new(StringComparer.Ordinal);
    public int SpriteCount => _sprites.SpriteCount;
    public int BatchCount => _sprites.BatchCount;
    public Node3D Node => _node;

    public MapEditorPreview(OpenRtsMap root, UnitCatalog catalog, ModelCatalog models)
    {
        try
        {
            _catalog = catalog;
            // Isolate SpriteFrames from editor auto-save/migration; texture dependencies still share the normal cache.
            _sprites = new(catalog, models, owner => owner == 1 ? new(0.25f, 0.55f, 1) : owner == 2 ? new(1, 0.25f, 0.2f) : Colors.White,
                path => ResourceLoader.Load<SpriteFrames>(path, cacheMode: ResourceLoader.CacheMode.Ignore));
            _node.AddChild(_sprites.Node);
            root.AddChild(_node, false, global::Godot.Node.InternalMode.Back);
        }
        catch { _node.Free(); throw; }
    }

    public void Apply(OpenRtsMap root, MapSource source)
    {
        var visual = source.Visual.Cells.Select(i => source.Visual.Palette[i]).ToArray();
        Texture2D Texture(string id) => root.VisualTextures.TryGetValue(id, out var texture) && texture is not null ? texture :
            GD.Load<Texture2D>("res://resources/terrain/" + id + ".tres") ?? throw new ArgumentException("Missing visual texture: " + id);
        var textures = source.Visual.Palette.ToDictionary(id => id, id => Texture(id).GetInstanceId(), StringComparer.Ordinal);
        if (_size != root.Size || !_visual.SequenceEqual(visual) || textures.Count != _textures.Count || textures.Any(p => _textures.GetValueOrDefault(p.Key) != p.Value))
        {
            var terrain = new TerrainView(source.Width, source.Height, 1, source.Visual.Palette,
                (x, y) => visual[y * source.Width + x], Texture);
            _terrain?.Free(); _terrain = terrain.Node; _node.AddChild(_terrain);
            _size = root.Size; _visual = visual; _textures = textures;
        }
        var types = _catalog.Units.Select((unit, i) => (unit.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var owners = source.Slots.OrderBy(s => s.Id, StringComparer.Ordinal).Select((s, i) => (s.Id, Owner: (uint)i + 1)).ToDictionary(p => p.Id, p => p.Owner);
        var poses = new EntityPose[source.Objects.Count];
        for (int i = 0; i < poses.Length; i++)
        {
            var obj = source.Objects[i];
            if (!types.TryGetValue(obj.Type, out int type)) throw new ContentException(obj.Origin ?? source.Origin, $"objects.{obj.Id}.TypeId", $"Unknown unit '{obj.Type}'.");
            var unit = _catalog.Units[type]; var facing = FacingDirection.North;
            var presentation = new EntityPresentation(owners.GetValueOrDefault(obj.Owner), facing, facing, facing, facing,
                new(unit.IdleAnimation, 0, 0), AnimationPlayback.None, 0);
            poses[i] = new(new((uint)i + 1), type, obj.Position, obj.Position, presentation, Health: unit.MaxHealth);
        }
        _sprites.Apply(poses, 1, 0, 25);
    }
    public void Dispose() { if (GodotObject.IsInstanceValid(_node)) _node.Free(); }
}
#endif
