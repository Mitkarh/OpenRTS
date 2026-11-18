using System.Collections.ObjectModel;
using DeclarativeContent;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Content;

/// <summary>Shared named weapon definitions; adapter visuals are loaded by EffectCatalog separately.</summary>
public sealed class WeaponCatalog
{
    public ReadOnlyDictionary<string, WeaponDefinition> Weapons { get; }
    private WeaponCatalog(Dictionary<string, WeaponDefinition> weapons) => Weapons = new(weapons);
    public static WeaponCatalog Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        var registry = new TraitRegistry<string>().Register<WeaponOptions>("Weapon", (_, _, options) => options, required: true);
        registry.ValidateTemplates(definitions);
        var weapons = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);
        foreach (var (id, node) in definitions.Concrete.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var o = registry.Compile(id, node).Create(id).Single<WeaponOptions>();
            if (o.Trajectory is { } trajectory && o.SpatialTrajectory is bool spatial &&
                trajectory != (spatial ? ProjectileTrajectory.Direct : ProjectileTrajectory.Planar))
                throw node.Error(id + ".Weapon.Trajectory", "Trajectory conflicts with SpatialTrajectory.");
            try { weapons.Add(id, new(o.Range, o.AcquireRange ?? Math.Max(8192, o.Range), o.Damage, o.ReloadTicks, o.WindupTicks, id, o.Projectile, o.Effect,
                o.Speed, o.TravelLimit, o.SplashRadius, o.Burst, o.BurstIntervalTicks, o.AnimationTicks,
                o.Versus.Living, o.Versus.Mechanical, o.Versus.Building, o.FriendlyFire, o.PassengerDamage, o.PassengerDamageSpill, new TargetCategories(o.Targets), o.SpatialTrajectory ?? false,
                o.ExcludedTargets.Length == 0 ? null : new TargetCategories(o.ExcludedTargets), o.MinimumContact, o.Trajectory,
                o.TrajectoryHeight, o.Homing, o.ScatterRadius, o.LaunchFan)); }
            catch (ArgumentException error) { throw node.Error(id + ".Weapon", error.Message); }
        }
        return new(weapons);
    }
    private sealed class WeaponOptions
    {
        public string[] Targets { get; set; } = ["ground"];
        public string[] ExcludedTargets { get; set; } = [];
        public bool? SpatialTrajectory { get; set; }
        public ProjectileTrajectory? Trajectory { get; set; }
        public int? TrajectoryHeight { get; set; }
        public bool? Homing { get; set; }
        public int ScatterRadius { get; set; }
        public int LaunchFan { get; set; }
        public SensorDisplay MinimumContact { get; set; } = SensorDisplay.Model;
        public int Range { get; set; } = 4096;
        public int? AcquireRange { get; set; }
        public int Damage { get; set; } = 10;
        public int ReloadTicks { get; set; } = 20;
        public int WindupTicks { get; set; } = 2;
        public int Burst { get; set; } = 1;
        public int BurstIntervalTicks { get; set; } = 2;
        public int AnimationTicks { get; set; } = 4;
        public ProjectileKind Projectile { get; set; }
        public WeaponEffect Effect { get; set; }
        public int Speed { get; set; } = 256;
        public int? TravelLimit { get; set; }
        public int SplashRadius { get; set; }
        public bool FriendlyFire { get; set; }
        public int PassengerDamage { get; set; }
        public bool PassengerDamageSpill { get; set; }
        public ArmorOptions Versus { get; set; } = new();
    }
    private sealed class ArmorOptions
    {
        public int Living { get; set; } = 100;
        public int Mechanical { get; set; } = 100;
        public int Building { get; set; } = 100;
    }
}
