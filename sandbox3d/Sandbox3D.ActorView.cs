using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Presentation;

public partial class Sandbox3D
{
    private sealed record ActorModel(PackedScene Scene, bool Infantry, float Radius);

    private static ActorModel LoadModel(UnitDefinition type)
    {
        bool infantry = type.Id == "scout";
        string file = infantry ? "Infantry.tscn" : type.Id + ".glb";
        var scene = GD.Load<PackedScene>("res://assets/quaternius/" + file)
            ?? throw new InvalidOperationException($"Cannot load model {file}.");
        return new ActorModel(scene, infantry, Math.Max(type.Body.HalfWidth, type.Body.HalfHeight) / (float)CellSize);
    }

    private static TorusMesh RingMesh(Color color) => new()
    {
        InnerRadius = 0.88f,
        OuterRadius = 1,
        Rings = 32,
        RingSegments = 8,
        Material = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
    };

    private static Vector3 Display(WorldPosition point) => new Vector3(point.X, point.Elevation, point.Y) / CellSize;

    private sealed class ActorView
    {
        private readonly MeshInstance3D _ring;
        private readonly AnimationPlayer _animation;
        private readonly ActorModel _model;
        private bool? _moving;

        public Node3D Node { get; } = new();
        public int Type { get; }
        public Vector3 PickCenter => Node.Position + Vector3.Up * (_model.Infantry ? 0.65f : 0.28f);

        public ActorView(ActorModel model, Mesh marker, int type)
        {
            _model = model;
            Type = type;
            var visual = model.Scene.Instantiate<Node3D>();
            visual.Scale = Vector3.One * (model.Infantry ? 0.72f : 0.12f);
            if (!model.Infantry) visual.RotationDegrees = new(0, 90, 0);
            Node.AddChild(visual);
            _animation = (AnimationPlayer)visual.FindChild("AnimationPlayer", recursive: true, owned: false);

            _ring = new MeshInstance3D
            {
                Mesh = marker,
                Position = new(0, 0.04f, 0),
                Scale = new(model.Radius + 0.14f, 0.45f, model.Radius + 0.14f),
                Visible = false
            };
            Node.AddChild(_ring);
        }

        public void SetSelected(bool selected) => _ring.Visible = selected;

        public float PickRadius(Camera3D camera, Vector2 screenCenter)
        {
            float width = screenCenter.DistanceTo(camera.UnprojectPosition(PickCenter + Vector3.Right * (_model.Radius + 0.12f)));
            float halfHeight = screenCenter.DistanceTo(camera.UnprojectPosition(Node.Position));
            return Math.Max(12, Math.Max(width, halfHeight));
        }

        public void Apply(EntityPose pose, float alpha)
        {
            Node.Position = Display(pose.Previous).Lerp(Display(pose.Current), alpha);
            var before = pose.Presentation.PreviousBodyFacing;
            var after = pose.Presentation.BodyFacing;
            Node.Rotation = new(0, Mathf.LerpAngle(Mathf.Atan2(before.X, before.Y), Mathf.Atan2(after.X, after.Y), alpha), 0);

            bool moving = pose.Previous != pose.Current;
            if (_moving == moving) return;
            _moving = moving;
            if (_model.Infantry) _animation.Play(moving ? "Jog_Fwd" : "Pistol_Idle", 0.12);
            else if (moving) _animation.Play("TankArmature|Tank_Forward");
            else _animation.Stop(keepState: true);
        }
    }
}
