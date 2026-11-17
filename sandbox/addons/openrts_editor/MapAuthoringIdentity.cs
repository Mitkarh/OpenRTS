using Godot;

namespace OpenRTS.Godot.Authoring;

/// <summary>Explicit editor edits, never a repair performed during extraction/compilation.</summary>
public static class MapAuthoringIdentity
{
    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string IdField(Node node) => node switch
    {
        OpenRtsMapObject => nameof(OpenRtsMapObject.ObjectId),
        OpenRtsMapStart => nameof(OpenRtsMapStart.StartId),
        OpenRtsMapGroup => nameof(OpenRtsMapGroup.InstanceId),
        _ => throw new ArgumentException("Select an object, start or group marker.")
    };

    public static IEnumerable<Node> ContextChildren(Node context)
    {
        foreach (Node child in context.GetChildren())
        {
            if (child is OpenRtsMapGroup or OpenRtsMapObject or OpenRtsMapStart) yield return child;
            if (child is not OpenRtsMapGroup and not OpenRtsMap) foreach (var descendant in ContextChildren(child)) yield return descendant;
        }
    }

    public static int NextOrder(Node context)
    {
        var orders = ContextChildren(context).Where(n => n is OpenRtsMapGroup or OpenRtsMapObject)
            .Select(n => n.Get("Order").AsInt32()).ToHashSet();
        int maximum = orders.Count == 0 ? -1 : orders.Max();
        if (maximum < int.MaxValue) return Math.Max(0, maximum + 1);
        int value = 0;
        while (orders.Contains(value)) value++;
        return value;
    }

    public static Node Context(Node node)
    {
        for (Node? parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is OpenRtsMap or OpenRtsMapGroup) return parent;
        throw new ArgumentException("Marker must belong to a map/group.");
    }

    public static void Repair(Node context, Action<Node, string, Variant> set)
    {
        var children = ContextChildren(context).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        var reserved = children.Where(n => n is OpenRtsMapGroup or OpenRtsMapObject).Select(n => n.Get("Order").AsInt32()).ToHashSet();
        int next = reserved.Count == 0 || reserved.Max() == int.MaxValue ? 0 : Math.Max(0, reserved.Max() + 1);
        foreach (var node in children)
        {
            string field = IdField(node), id = node.Get(field).AsString();
            if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id is "." or ".." || !ids.Add(id))
            {
                id = NewId(); ids.Add(id); set(node, field, id);
            }
            if (node is OpenRtsMapGroup or OpenRtsMapObject)
            {
                int order = node.Get("Order").AsInt32();
                if (order < 0 || !orders.Add(order))
                {
                    while (reserved.Contains(next) || orders.Contains(next)) next = next == int.MaxValue ? 0 : next + 1;
                    set(node, "Order", next); orders.Add(next);
                }
            }
            if (node is OpenRtsMapGroup) Repair(node, set);
        }
    }
}
