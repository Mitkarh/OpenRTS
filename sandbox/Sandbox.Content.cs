using System;
using System.Linq;
using DeclarativeContent;
using Godot;
using OpenRTS.Content;

public partial class Sandbox
{
    private GameManifest _manifest = null!;
    private UnitCatalog _installedCatalog = null!;

    private static GameManifest LoadManifest(string path)
    {
        if (!path.StartsWith("res://", StringComparison.Ordinal)) return GameManifest.Load(path);
        string root = path[..(path.LastIndexOf('/') + 1)];
        var files = new ContentFiles(file =>
        {
            using var resource = Godot.FileAccess.Open(root + file, Godot.FileAccess.ModeFlags.Read)
                ?? throw new System.IO.IOException($"Не удалось открыть {root + file}: {Godot.FileAccess.GetOpenError()}.");
            return resource.GetAsText();
        }, directory =>
        {
            using var dir = DirAccess.Open(root + directory);
            return dir is null ? null : dir.GetDirectories().Concat(dir.GetFiles())
                .Where(entry => !dir.IsLink(entry))
                .Select(entry => directory.Length == 0 ? entry : directory + "/" + entry).ToArray();
        });
        return GameManifest.Load(files, path[(path.LastIndexOf('/') + 1)..]);
    }

    private ContentSource ReadMapSource(string path)
    {
        if (!System.IO.Path.IsPathRooted(path) && !path.StartsWith("res://", StringComparison.Ordinal))
            return _manifest.Files.Read(path);
        return ReadFile(path);
    }

    private static ContentSource ReadFile(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new System.IO.IOException($"Не удалось открыть {path}: {Godot.FileAccess.GetOpenError()}.");
        return new(path, file.GetAsText());
    }
}
