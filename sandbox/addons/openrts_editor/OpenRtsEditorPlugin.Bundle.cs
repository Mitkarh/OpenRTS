#if TOOLS
using System.Diagnostics;
using System.Text.Json;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

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

    private static void PublishBundle(OpenRtsMap root, string output, CompiledMap map,
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
        SaveTextAtomically(bundle.Write(), output, stop);
    }

    private static string[] PlayMapArguments(string map, string mod, LocalMapPlaytest receipt) =>
        ["--path", ProjectSettings.GlobalizePath("res://"), "res://Main.tscn", "--", "--map=" + map, "--mod=" + mod,
            "--local-playtest=" + receipt.Write()];

    private static void PlayMap(string map, string mod, LocalMapPlaytest receipt, bool record)
    {
        string[] args = PlayMapArguments(map, mod, receipt);
        int process = OS.CreateProcess(OS.GetExecutablePath(), record ? [.. args, "--record-playtest"] : args);
        if (process <= 0) throw new IOException("Godot could not start the map player.");
    }

    private static async Task<SimulationReport> VerifyMap(string path, string mod, LocalMapPlaytest receipt,
        GameManifest manifest, UnitCatalog catalog, CancellationToken stop)
    {
        const int ticks = 8;
        var expected = await Task.Run(() =>
        {
            var match = receipt.Load(path, file => new(file, File.ReadAllText(file)), catalog, manifest);
            using var world = match.CreateWorld(new(), speedPerTick: receipt.SpeedPerTick, tickRate: receipt.TickRate);
            for (int tick = 0; tick < ticks; tick++) { stop.ThrowIfCancellationRequested(); world.Step(); }
            return world.Report();
        }, stop);

        stop.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(OS.GetExecutablePath()) { RedirectStandardOutput = true, RedirectStandardError = true };
        string[] arguments = ["--headless", .. PlayMapArguments(ProjectSettings.LocalizePath(path), mod, receipt),
            "--verify-simulation", $"--ticks={ticks}"];
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Godot could not start map verification.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            stop.ThrowIfCancellationRequested();
            throw new IOException("Godot map verification timed out.");
        }
        const string prefix = "OPENRTS_SIMULATION:";
        string[] reports = (await output).Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        string stderr = await errors;
        if (process.ExitCode != 0 || reports.Length != 1 || stderr.Contains("SCRIPT ERROR:", StringComparison.Ordinal))
            throw new IOException($"Godot map verification failed (exit {process.ExitCode}).\n{stderr.Trim()}");
        var actual = JsonSerializer.Deserialize<SimulationReport>(reports[0][prefix.Length..]);
        if (actual != expected)
            throw new InvalidDataException($"Godot and headless map verification differ. Godot: {actual}; headless: {expected}.");
        return expected;
    }

    private async Task BuildFromCommandLine()
    {
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            bool legacy = args.Contains("--build-scene-map");
            string? scene = null, format = legacy ? "bundle" : null, set = null;
            bool verify = false;
            var starts = new List<string>();
            foreach (string arg in args)
            {
                if (arg == "--build-scene-map") continue;
                if (arg.StartsWith("--build-map=", StringComparison.Ordinal) && scene is null) scene = arg[12..];
                else if (arg.StartsWith("--format=", StringComparison.Ordinal) && format is null) format = arg[9..];
                else if (arg == "--verify" && !verify) verify = true;
                else if (arg.StartsWith("--start=", StringComparison.Ordinal) && arg.Length > 8) starts.Add(arg[8..]);
                else if (arg.StartsWith("--start-set=", StringComparison.Ordinal) && arg.Length > 12 && set is null) set = arg[12..];
                else throw new ArgumentException($"Unknown or repeated map build argument '{arg}'.");
            }
            if (format is not (null or "compiled" or "bundle"))
                throw new ArgumentException("Map build format must be compiled or bundle.");
            if (!verify && (starts.Count != 0 || set is not null))
                throw new ArgumentException("--start and --start-set require --verify.");
            if (!legacy && (!SceneMapBundle.ResourcePath(scene, ".tscn") || !ResourceLoader.Exists(scene!, "PackedScene")))
                throw new ArgumentException($"Map build requires a saved project .tscn scene: {scene}.");
            for (int i = 0; i < 300 && !RequestedSceneIsOpen(); i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!RequestedSceneIsOpen()) throw new IOException($"Could not open the requested scene: {scene}.");
            bool bundle = format == "bundle" || format is null && EditedMap() is OpenRtsMap;
            var result = await BuildAsync(bundle: bundle, saveScene: false, verify: verify,
                selectedStarts: starts.Count == 0 ? null : starts.ToArray(), selectedStartSet: set);
            if (result is not null)
            {
                if (bundle) GD.Print("OPENRTS_SCENEMAP:" + result.Path);
                GD.Print("OPENRTS_MAP_BUILD:" + JsonSerializer.Serialize(new
                {
                    result.Path, result.Mod, Format = bundle ? "bundle" : "compiled",
                    result.Verification, Playtest = result.Playtest?.Write()
                }));
                GetTree().Quit();
            }
            else GetTree().Quit(1);

            bool RequestedSceneIsOpen() => EditorInterface.Singleton.GetEditedSceneRoot() is { } root &&
                (scene is null || root.SceneFilePath == scene);
        }
        catch (Exception error) { Fail(error); GetTree().Quit(1); }
    }
}
#endif
