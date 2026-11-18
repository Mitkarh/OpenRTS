using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

public sealed partial class CombatEffectsView
{
    private const int MaxSmokePuffs = 4096;
    private readonly record struct SmokePuff(Vector3 Position, float Size, double Start, int Duration);

    private readonly bool _hasMountedRockets;
    private readonly List<SmokePuff> _puffs = [];
    private readonly ColoredMeshBatch _smoke = SmokeBatch();
    private long _lastSmokeTick = -1;

    private void ApplyMountedRockets(ReadOnlySpan<EntityPose> actors, ReadOnlySpan<WeaponPose> weapons, long tick, float alpha, Vector3 eye)
    {
        foreach (var actor in actors)
        {
            var mounts = _units.Units[actor.UnitType].Weapons;
            foreach (var channel in weapons.Slice(actor.WeaponOffset, actor.WeaponCount))
            {
                if (channel.Reloading) continue;
                var weapon = mounts[channel.Mount].Weapon;
                var style = _effects.Weapons[weapon.Id];
                if (style.MountedRocket is not { } mount || style.ProjectileTexture is not { } path) continue;
                float phase = channel.WindupEndTick > 0 && weapon.WindupTicks > 0
                    ? Mathf.SmoothStep(0, 1, 1 - (channel.WindupEndTick - tick - alpha) / weapon.WindupTicks) : 0;
                var body = actor.Presentation;
                var (direction, center) = MountedRocket(mount, Position(actor.Previous).Lerp(Position(actor.Current), alpha),
                    SpriteWorldView.Angle(body.PreviousBodyFacing, body.BodyFacing, alpha),
                    SpriteWorldView.Angle(channel.PreviousFacing, channel.Facing, alpha), phase, (float)style.Size);
                ProjectileSprite(path, center, direction, (float)style.Size, eye);
            }
        }
    }

    private static (Vector3 Direction, Vector3 Center) MountedRocket(MountedRocketStyle style, Vector3 position,
        float body, float mount, float phase, float halfLength)
    {
        float yaw = style.FollowsBody ? body : mount;
        var horizontal = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var tail = position + new Vector3(MathF.Sin(body), 0, -MathF.Cos(body)) * (float)style.ChassisOffset +
            horizontal * (float)style.TailOffset + Vector3.Up * (float)style.Height;
        float pitch = Mathf.DegToRad((float)style.LaunchPitch * phase);
        var direction = horizontal * MathF.Cos(pitch) + Vector3.Up * MathF.Sin(pitch);
        return (direction, tail + direction * halfLength);
    }

    private void ProjectileSprite(string path, Vector3 point, Vector3 direction, float halfLength, Vector3 eye)
    {
        var (batch, aspect) = _projectileSprites[path];
        float length = 2 * halfLength;
        batch.Add(point, new(length * aspect, 1, length), PitchBasis(direction, eye), Colors.White);
    }

    private static Basis PitchBasis(Vector3 flight, Vector3 eye)
    {
        if (flight == Vector3.Zero) flight = Vector3.Forward;
        var right = flight.Cross(Vector3.Up);
        if (MathF.Abs(flight.Y) > 0.001f)
        {
            var screenRight = flight.Cross(eye);
            if (screenRight.LengthSquared() > 0.0001f) right = screenRight;
        }
        right = right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Right;
        return new(right, right.Cross(flight), -flight);
    }

    private static ColoredMeshBatch SmokeBatch()
    {
        var texture = new GradientTexture2D
        {
            Width = 32, Height = 32, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new(0.5f, 0.5f), FillTo = new(1, 0.5f),
            Gradient = new Gradient
            {
                Offsets = [0, 0.45f, 1],
                Colors = [Colors.White, new(1, 1, 1, 0.6f), new(1, 1, 1, 0)]
            }
        };
        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        return new(new QuadMesh { Size = Vector2.One }, "RocketSmoke", material);
    }

    private void EmitSmoke(Vector3 tail, EffectStyle style, double time)
    {
        if (_puffs.Count < MaxSmokePuffs) _puffs.Add(new(tail, (float)style.SmokeSize, time, style.SmokeTicks));
    }

    private void PublishSmoke(double time)
    {
        _smoke.Clear();
        int write = 0;
        for (int read = 0; read < _puffs.Count; read++)
        {
            var puff = _puffs[read];
            float age = (float)((time - puff.Start) / puff.Duration);
            if (age >= 1) continue;
            _puffs[write++] = puff;
            age = Math.Max(0, age);
            float size = puff.Size * (1 + age * 1.9f);
            var color = new Color(0.66f + 0.16f * (1 - age), 0.64f + 0.11f * (1 - age), 0.61f, 0.78f * (1 - age) * (1 - age));
            _smoke.Add(puff.Position + Vector3.Up * (age * puff.Size), Vector3.One * size, 0, color);
        }
        if (write < _puffs.Count) _puffs.RemoveRange(write, _puffs.Count - write);
        _smoke.Publish();
    }
}
