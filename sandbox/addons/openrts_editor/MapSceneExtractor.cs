using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Authoring;

/// <summary>Call on the Godot owner thread. The returned snapshot contains no Godot references.</summary>
public static class MapSceneExtractor
{
    private sealed class Context(string prefix, int[] order)
    {
        internal readonly string Prefix = prefix;
        internal readonly int[] Order = order;
        internal readonly HashSet<string> Ids = new(StringComparer.Ordinal);
        internal readonly HashSet<int> Orders = [];
    }

    public static MapSource Extract(OpenRtsMap root, OpenRtsTerrainStroke? pendingStroke = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        Source Origin(Node node) => new($"{(root.SceneFilePath.Length == 0 ? "<unsaved-scene>" : root.SceneFilePath)}::{root.GetPathTo(node)}", 1, 1);
        ContentException Error(Node node, string field, string message) => new(Origin(node), field, message);
        if (!root.Transform.IsEqualApprox(Transform3D.Identity) || root.TopLevel)
            throw Error(root, "Transform", "Map root must have the identity transform and TopLevel disabled.");
        if (root.Size.X < 1 || root.Size.Y < 1 || (long)root.Size.X * root.Size.Y > NavigationGrid.MaximumCells ||
            root.Size.X > WorldPosition.MaximumExtent / WorldPosition.UnitsPerCell || root.Size.Y > WorldPosition.MaximumExtent / WorldPosition.UnitsPerCell)
            throw Error(root, "Size", "Map dimensions exceed the ground-plane cell/world budget.");
        string Id(Node node, string id, Context context)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id is "." or ".." || !context.Ids.Add(id))
                throw Error(node, "ID", "Expected a nonempty unique local ID without '/'. Use the dock's Duplicate or Repair identities action.");
            return context.Prefix + id;
        }
        int[] Order(Node node, int value, Context context)
        {
            if (value < 0 || !context.Orders.Add(value))
                throw Error(node, "Order", "Object/group Order must be nonnegative and unique within its map/group context.");
            return [.. context.Order, value];
        }
        WorldPosition Position(Node node, Transform3D transform, bool topLevel)
        {
            if (topLevel || !transform.IsFinite() || !transform.Basis.IsEqualApprox(Basis.Identity) || Math.Abs(transform.Origin.Y) > 0.000001f)
                throw Error(node, "Transform", "Ground-plane v1 requires zero elevation, identity basis and no TopLevel ancestor. Rotation, scale and mirror are unsupported.");
            int Coordinate(float value)
            {
                double fixedValue = Math.Round((double)value * WorldPosition.UnitsPerCell, MidpointRounding.AwayFromZero);
                if (fixedValue < 0 || fixedValue > WorldPosition.MaximumExtent)
                    throw Error(node, "Position", "Position is outside the supported world range.");
                return (int)fixedValue;
            }
            return new(Coordinate(transform.Origin.X), Coordinate(transform.Origin.Z));
        }
        var objects = new List<(int[] Order, MapObjectSource Object)>();
        var slots = new List<MapSlotSource>();
        int visited = 0;
        void Visit(Node node, Transform3D transform, Context context, bool topLevel, int depth)
        {
            // Bound authored trees too; decoration is traversed so misplaced markers cannot disappear silently.
            if (++visited > MapCompiler.MaximumObjects || depth > 128)
                throw Error(node, "Scene", "Authoring scene exceeds the node/depth budget (1 million / 128).");
            if (node is Node3D spatial) { transform *= spatial.Transform; topLevel |= spatial.TopLevel; }
            switch (node)
            {
                case OpenRtsMap:
                    throw Error(node, "Scene", "Nested map roots are not supported; instance an OpenRtsMapGroup instead.");
                case OpenRtsMapGroup group:
                    Position(node, transform, topLevel);
                    context = new(Id(node, group.InstanceId, context) + "/", Order(node, group.Order, context));
                    break;
                case OpenRtsMapObject obj:
                    if (objects.Count >= MapCompiler.MaximumObjects) throw Error(node, "Objects", "Object budget exceeded.");
                    objects.Add((Order(node, obj.Order, context), new(Id(node, obj.ObjectId, context), obj.TypeId,
                        Position(node, transform, topLevel), obj.OwnerSlot, Origin(node))));
                    break;
                case OpenRtsMapStart start:
                    if (!root.IsSkirmish) throw Error(node, "Start", "Fixture maps use FixtureSlots, not start markers.");
                    if (slots.Count >= MapCompiler.MaximumSlots) throw Error(node, "Starts", "Start budget exceeded.");
                    slots.Add(new(Id(node, start.StartId, context), Position(node, transform, topLevel), start.Reverse));
                    break;
            }
            foreach (Node child in node.GetChildren()) Visit(child, transform, context, topLevel, depth + 1);
        }
        var initial = new Context("", []);
        foreach (Node child in root.GetChildren()) Visit(child, Transform3D.Identity, initial, false, 1);
        if (!root.IsSkirmish) slots.AddRange(root.FixtureSlots.Select(id => new MapSlotSource(id)));
        objects.Sort((a, b) => CompareOrder(a.Order, b.Order));
        var layers = MapTerrain.Compose(root, pendingStroke);
        return new(root.MapId, root.IsSkirmish, root.Size.X, root.Size.Y,
            layers.Terrain, layers.Visual, slots,
            objects.Select(o => o.Object).ToArray(), origin: Origin(root));
    }

    private static int CompareOrder(int[] first, int[] second)
    {
        for (int i = 0; i < Math.Min(first.Length, second.Length); i++)
            if (first[i] != second[i]) return first[i].CompareTo(second[i]);
        return first.Length.CompareTo(second.Length);
    }
}
