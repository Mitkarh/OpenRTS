namespace OpenRTS.Content;

public enum GameRenderer { Godot3D }
public enum GameCamera { Default, Isometric }

public sealed class PresentationSettings
{
    public GameRenderer Renderer { get; init; } = GameRenderer.Godot3D;
    public GameCamera Camera { get; init; } = GameCamera.Default;
    public Dictionary<string, string> ModelOverrides { get; init; } = new(StringComparer.Ordinal);
}
