using System.Text.Json;
using System.Text.Json.Serialization;
using DeclarativeContent;

namespace OpenRTS.Content;

/// <summary>A publication pointer shared by content readers. Only the Godot host opens the scene resources.</summary>
public sealed record SceneMapBundle(int Version, string Scene, string Mod, string Data, string Fingerprint, string Catalog,
    SortedDictionary<string, string> Dependencies)
{
    public const string Suffix = ".scenemap.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4
    };

    public static bool IsPath(string path) => path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    public static SceneMapBundle Read(ContentSource source)
    {
        var result = source.Text.Length <= 1024 * 1024
            ? JsonSerializer.Deserialize<SceneMapBundle>(source.Text, Json) : null;
        if (result is null || result.Version != 1 || !ResourcePath(result.Scene, ".tscn") ||
            !ResourcePath(result.Mod, ".yaml") || !Digest(result.Fingerprint) ||
            !Digest(result.Catalog) || result.Data != DataName(result.Fingerprint, result.Catalog) ||
            result.Dependencies is null || result.Dependencies.Count is < 1 or > 4096 ||
            !result.Dependencies.ContainsKey(result.Scene) ||
            result.Dependencies.Any(pair => !ResourcePath(pair.Key, "") || !Digest(pair.Value)))
            throw new InvalidDataException($"Invalid scene-map manifest: {source.Name}.");
        return result;
    }

    public static string DataName(string fingerprint, string catalog) => catalog + "-" + fingerprint + ".ortmap.json";

    public string Write() => JsonSerializer.Serialize(this, Json) + "\n";

    public static bool ResourcePath(string? path, string suffix) =>
        path is not null && path.StartsWith("res://", StringComparison.Ordinal) &&
        path.EndsWith(suffix, StringComparison.Ordinal) && !path.AsSpan(6).ContainsAny(':', '\\', '\0') &&
        path[6..].Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public CompiledMap LoadMap(string bundlePath, Func<string, ContentSource> read, UnitCatalog catalog)
    {
        int slash = bundlePath.LastIndexOfAny(['/', '\\']);
        var compiled = MapCompiler.Read(read(bundlePath[..(slash + 1)] + Data), catalog);
        if (compiled.DataFingerprint != Fingerprint || compiled.ContentFingerprint != Catalog)
            throw new InvalidDataException($"Scene-map projection does not match {bundlePath}.");
        return compiled;
    }
}
