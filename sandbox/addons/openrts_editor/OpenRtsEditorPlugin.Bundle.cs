#if TOOLS
using Godot;
using OpenRTS.Content;

namespace OpenRTS.Godot.Authoring;

public partial class OpenRtsEditorPlugin
{
    private static SortedDictionary<string, string> SceneDependencies(string scene)
    {
        var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Visit(string path)
        {
            if (dependencies.ContainsKey(path)) return;
            if (dependencies.Count >= 4096 || !SceneMapBundle.ResourcePath(path, "") || !File.Exists(ProjectSettings.GlobalizePath(path)))
                throw new IOException($"Could not resolve scene dependency {path}.");
            dependencies.Add(path, global::Godot.FileAccess.GetSha256(path).ToUpperInvariant());
            foreach (string dep in ResourceLoader.GetDependencies(path))
            {
                var fields = dep.Split("::", StringSplitOptions.None);
                var uid = fields[0].StartsWith("uid://", StringComparison.Ordinal) ? ResourceUid.TextToId(fields[0]) : -1;
                Visit(uid >= 0 && ResourceUid.HasId(uid) ? ResourceUid.GetIdPath(uid) : fields[^1]);
            }
        }
        Visit(scene);
        return dependencies;
    }

    private static string PublishBundle(OpenRtsMap root, string output, CompiledMap map,
        SortedDictionary<string, string> dependencies, CancellationToken stop)
    {
        var sourceScene = ResourceLoader.Load<PackedScene>(root.SceneFilePath, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
            ?? throw new IOException($"Cannot read saved scene {root.SceneFilePath}.");
        var instance = sourceScene.Instantiate();
        try
        {
            if (instance is not OpenRtsMap saved || saved.ContentManifest != root.ContentManifest ||
                MapSceneExtractor.Extract(saved).Write() != MapSceneExtractor.Extract(root).Write())
                throw new IOException("Saved scene differs from the edited map. Save its dependencies and build again.");
        }
        finally { instance.Free(); }

        var data = SceneMapBundle.DataName(map.DataFingerprint, map.ContentFingerprint);
        var bundle = new SceneMapBundle(1, root.SceneFilePath, root.ContentManifest, data, map.DataFingerprint, map.ContentFingerprint, dependencies);
        MapCompiler.Save(map, Path.Combine(Path.GetDirectoryName(output)!, data), stop);
        Sandbox.CheckSceneMapDependencies(bundle);
        string manifest = output[..^".ortmap.json".Length] + SceneMapBundle.Suffix;
        SaveTextAtomically(bundle.Write(), manifest, stop);
        return manifest;
    }

    private static void PlayBundle(string manifest, string mod)
    {
        int process = OS.CreateProcess(OS.GetExecutablePath(),
            ["--path", ProjectSettings.GlobalizePath("res://"), "res://Main.tscn", "--", "--map=" + manifest, "--mod=" + mod]);
        if (process <= 0) throw new IOException("Godot could not start the map player.");
    }

    private async Task BuildFromCommandLine()
    {
        try
        {
            for (int i = 0; i < 300 && EditorInterface.Singleton.GetEditedSceneRoot() is not OpenRtsMap; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await BuildAsync(bundle: true, saveScene: false);
            if (Status.StartsWith("Compiled ", StringComparison.Ordinal))
            {
                GD.Print("OPENRTS_SCENEMAP:" + Status.Split('\n')[2]);
                GetTree().Quit();
            }
            else GetTree().Quit(1);
        }
        catch (Exception error) { Fail(error); GetTree().Quit(1); }
    }
}
#endif
