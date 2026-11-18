using Godot;
using OpenRTS.Content;
using OpenRTS.Godot.Authoring;

public partial class Sandbox
{
    private string _activeManifestPath = "";
    private readonly Dictionary<string, SceneMapBundle> _sceneMaps = new(StringComparer.Ordinal);

    private GameMap LoadSceneMap(string path, UnitCatalog catalog, LocalMapPlaytest? playtest = null)
    {
        var bundle = SceneMapBundle.Read(ReadMapSource(path));
        if (_activeManifestPath != bundle.Mod &&
            Path.GetFullPath(ProjectSettings.GlobalizePath(_activeManifestPath)) != Path.GetFullPath(ProjectSettings.GlobalizePath(bundle.Mod)))
            throw new InvalidDataException($"Scene-map requires mod {bundle.Mod}: {path}.");
        CheckSceneMapDependencies(bundle);
        var compiled = bundle.LoadMap(path, ReadMapSource, catalog);
        playtest?.CheckMap(compiled);
        var map = compiled.Map;
        _sceneMaps.Add(map.Id, bundle);
        return map;
    }

    private void CheckInstalledSceneMaps(UnitCatalog catalog)
    {
        var settings = _manifest.Settings.Match;
        foreach (string path in settings.Maps.Select(choice => choice.Path).Append(settings.DefaultMap).Distinct(StringComparer.Ordinal))
        {
            bool bundle = SceneMapBundle.IsPath(path);
            string suffix = bundle ? SceneMapBundle.Suffix : ".ortmap.json";
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string resource = path.StartsWith("res://", StringComparison.Ordinal) ? path : "res://" + path;
            if (!resource.StartsWith("res://content/", StringComparison.Ordinal) || !SceneMapBundle.ResourcePath(resource, suffix))
                throw new InvalidDataException($"Install the map under res://content/ before export: {path}.");
            var map = bundle ? LoadSceneMap(path, catalog) : MapCompiler.Read(ReadMapSource(path), catalog).Map;
            if (!map.IsSkirmish) throw new InvalidDataException($"Match.Maps accepts skirmish maps. Launch the fixed fixture directly: {path}.");
            if (bundle)
            {
                InstantiateMapScene(map)!.Free();
                GD.Print("OPENRTS_SCENEMAP_CHECK:" + path);
            }
            else GD.Print("OPENRTS_COMPILED_MAP_CHECK:" + path);
        }
    }

    internal static void CheckSceneMapDependencies(SceneMapBundle bundle)
    {
        // Godot remaps source resources in exported packages. The export command checks the loose inputs first.
        if (OS.HasFeature("template") || OS.HasFeature("openrts_scene_map_pack")) return;
        foreach (var (path, sha) in bundle.Dependencies)
            if (!Godot.FileAccess.FileExists(path) || !string.Equals(Godot.FileAccess.GetSha256(path), sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Scene-map is stale or incomplete. Rebuild {bundle.Scene}. Changed: {path}.");
    }

    private OpenRtsMap? InstantiateMapScene(GameMap? map)
    {
        if (map is null || !_sceneMaps.TryGetValue(map.Id, out var bundle)) return null;
        CheckSceneMapDependencies(bundle);
        var packed = ResourceLoader.Load<PackedScene>(bundle.Scene, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
            ?? throw new IOException($"Scene-map entry is missing: {bundle.Scene}.");
        var entry = packed.Instantiate();
        try
        {
            if (entry is not OpenRtsMap root || root.MapId != map.Id || root.ContentManifest != bundle.Mod)
                throw new IOException($"Scene-map entry does not match {map.Id}: {bundle.Scene}.");
            if (root.DrawCompiledTerrain)
                foreach (string id in map.VisualIds)
                    if ((!root.VisualTextures.TryGetValue(id, out var texture) || texture is null) &&
                        !ResourceLoader.Exists("res://resources/terrain/" + id + ".tres", "Texture2D"))
                        throw new IOException($"Scene-map {map.Id} is missing terrain texture {id}.");
            return root;
        }
        catch { entry.Free(); throw; }
    }
}
