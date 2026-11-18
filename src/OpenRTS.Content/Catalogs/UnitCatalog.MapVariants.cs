using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

public sealed partial class UnitCatalog
{
    public const int MaximumMapHealthVariants = 256;

    public static string MapTypeId(string mapId, MapObjectSource placement) =>
        placement.HealthVariant is { } variant ? $"@map/{mapId.Length}:{mapId}/{variant.Id}" : placement.Type;

    /// <summary>Validates the authored initializers and derives the catalog used by both match and preview.</summary>
    public UnitCatalog PrepareMapUnits(MapSource map, CancellationToken stop = default)
    {
        var types = Units.Select((unit, index) => (unit.Id, index)).ToDictionary(t => t.Id, t => t.index, StringComparer.Ordinal);
        var variants = new SortedDictionary<string, (int Base, int MaxHealth)>(StringComparer.Ordinal);
        foreach (var obj in map.Objects)
        {
            stop.ThrowIfCancellationRequested();
            ContentException Error(string field, string message) => new(obj.Origin ?? map.Origin, $"objects.{obj.Id}.{field}", message);
            if (!types.TryGetValue(obj.Type, out int index))
                throw Error("type", $"Unknown base unit '{obj.Type}'.");
            var unit = Units[index];
            if ((uint)obj.QuarterTurns > 3 || obj.QuarterTurns != 0 && unit.Footprint is null)
                throw Error("quarterTurns", "Use 0–3 clockwise turns. A nonzero turn requires a footprint.");
            int maximum = unit.MaxHealth;
            if (obj.HealthVariant is { } variant)
            {
                if (variant.Id is not { Length: > 0 and <= 64 } || !variant.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    throw Error("healthVariant.id", "Use 1–64 ASCII letters, digits, hyphens or underscores.");
                if (variant.MaxHealth < 1) throw Error("healthVariant.maxHealth", "Maximum health must be positive.");
                string id = MapTypeId(map.Id, obj);
                if (types.ContainsKey(id)) throw Error("healthVariant.id", $"Variant ID collides with catalog type '{id}'.");
                var definition = (index, variant.MaxHealth);
                if (!variants.TryAdd(id, definition) && variants[id] != definition)
                    throw Error("healthVariant.id", $"Variant '{variant.Id}' has conflicting base types or maximum health.");
                if (variants.Count > MaximumMapHealthVariants) throw Error("healthVariant.id", "Too many map health variants.");
                maximum = variant.MaxHealth;
            }
            if (obj.InitialHealth is { } hp && (hp < 1 || hp > maximum))
                throw Error("initialHealth", $"Initial health must be between 1 and {maximum}.");
        }
        if (variants.Count == 0) return this;

        var units = Units.ToList();
        var presentations = Presentations.ToList();
        foreach (var (id, (index, maxHealth)) in variants)
        {
            units.Add(new UnitDefinition(Units[index].Definition.WithMaxHealth(id, maxHealth)));
            presentations.Add(Presentations[index] with { UnitId = id });
        }
        return new UnitCatalog(units.ToArray(), presentations.ToArray(), Locomotion);
    }
}
