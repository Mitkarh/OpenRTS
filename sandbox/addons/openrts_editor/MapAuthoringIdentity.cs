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
        OpenRtsMapGroup2D => nameof(OpenRtsMapGroup2D.InstanceId),
        OpenRtsMapObject2D => nameof(OpenRtsMapObject2D.ObjectId),
        OpenRtsMapStart2D => nameof(OpenRtsMapStart2D.StartId),
        _ => throw new ArgumentException("Select an object, start or group marker.")
    };

    public static bool HasOrder(Node node) => node is OpenRtsMapObject or OpenRtsMapGroup or OpenRtsMapObject2D or OpenRtsMapGroup2D;

    public static string? GroupId(Node node) => node switch
    {
        OpenRtsMapGroup group => group.InstanceId,
        OpenRtsMapGroup2D group => group.InstanceId,
        _ => null
    };

    public static IEnumerable<Node> ContextChildren(Node context)
    {
        bool canvas = context is OpenRtsMap2D or OpenRtsMapGroup2D;
        return Visit(context);
        IEnumerable<Node> Visit(Node parent)
        {
            foreach (Node child in parent.GetChildren())
            {
                if (canvas ? child is OpenRtsMapGroup2D or OpenRtsMapObject2D or OpenRtsMapStart2D :
                    child is OpenRtsMapGroup or OpenRtsMapObject or OpenRtsMapStart) yield return child;
                if (child is not (OpenRtsMapGroup or OpenRtsMap or OpenRtsMapGroup2D or OpenRtsMap2D))
                    foreach (var descendant in Visit(child)) yield return descendant;
            }
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
            if (parent is OpenRtsMap or OpenRtsMapGroup or OpenRtsMap2D or OpenRtsMapGroup2D) return parent;
        throw new ArgumentException("Marker must belong to a map/group.");
    }

    public static void Repair(Node context, Action<Node, string, Variant> set) =>
        Repair(context, set, "", new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    private static void Repair(Node context, Action<Node, string, Variant> set, string prefix,
        HashSet<string> objects, HashSet<string> starts)
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
            bool local = context is not OpenRtsMap2D || node is OpenRtsMapGroup2D;
            bool invalidLocalId = local && (id.Contains('/') || id is "." or "..");
            var composed = node is OpenRtsMapObject2D ? objects : node is OpenRtsMapStart2D ? starts : null;
            if (string.IsNullOrWhiteSpace(id) || invalidLocalId || node is OpenRtsMapStart2D && id == "neutral" ||
                !scope.Add(id) || composed?.Add(prefix + id) == false)
            {
                id = NewId(); scope.Add(id); set(node, field, id);
                composed?.Add(prefix + id);
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
            if (node is OpenRtsMapGroup or OpenRtsMapGroup2D) Repair(node, set, prefix + id + "/", objects, starts);
        }
    }
}
