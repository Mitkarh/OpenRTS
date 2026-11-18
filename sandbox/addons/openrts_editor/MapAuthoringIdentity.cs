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
        OpenRtsMapObject2D => nameof(OpenRtsMapObject2D.ObjectId),
        OpenRtsMapStart2D => nameof(OpenRtsMapStart2D.StartId),
        _ => throw new ArgumentException("Select an object, start or group marker.")
    };

    public static bool HasOrder(Node node) => node is OpenRtsMapObject or OpenRtsMapGroup or OpenRtsMapObject2D;

    public static IEnumerable<Node> ContextChildren(Node context)
    {
        if (context is OpenRtsMap2D)
        {
            foreach (Node child in context.GetChildren())
                if (child is OpenRtsMapStart2D or OpenRtsMapObject2D) yield return child;
            yield break;
        }
        foreach (Node child in context.GetChildren())
        {
            if (child is OpenRtsMapGroup or OpenRtsMapObject or OpenRtsMapStart) yield return child;
            if (child is not OpenRtsMapGroup and not OpenRtsMap) foreach (var descendant in ContextChildren(child)) yield return descendant;
        }
    }

    public static int NextOrder(Node context) => AvailableOrders(context).First();

    public static IEnumerable<int> AvailableOrders(Node context)
    {
        var used = ContextChildren(context).Where(HasOrder).Select(n => n.Get("Order").AsInt32()).ToHashSet();
        int maximum = used.Count == 0 ? -1 : used.Max();
        int next = maximum == int.MaxValue ? 0 : Math.Max(0, maximum + 1);
        while (true)
        {
            if (used.Add(next)) yield return next;
            next = next == int.MaxValue ? 0 : next + 1;
        }
    }

    public static Node Context(Node node)
    {
        for (Node? parent = node.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is OpenRtsMap or OpenRtsMapGroup or OpenRtsMap2D) return parent;
        throw new ArgumentException("Marker must belong to a map/group.");
    }

    public static void Repair(Node context, Action<Node, string, Variant> set)
    {
        var children = ContextChildren(context).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var starts2D = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        var reserved = children.Where(HasOrder).Select(n => n.Get("Order").AsInt32()).ToHashSet();
        int next = reserved.Count == 0 || reserved.Max() == int.MaxValue ? 0 : Math.Max(0, reserved.Max() + 1);
        foreach (var node in children)
        {
            string field = IdField(node), id = node.Get(field).AsString();
            var scope = node is OpenRtsMapStart2D ? starts2D : ids;
            bool invalidLocalId = context is not OpenRtsMap2D && (id.Contains('/') || id is "." or "..");
            if (string.IsNullOrWhiteSpace(id) || invalidLocalId || node is OpenRtsMapStart2D && id == "neutral" || !scope.Add(id))
            {
                id = NewId(); scope.Add(id); set(node, field, id);
            }
            if (HasOrder(node))
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
