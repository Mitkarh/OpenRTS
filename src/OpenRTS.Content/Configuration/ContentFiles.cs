using DeclarativeContent;

namespace OpenRTS.Content;

/// <summary>Named text files relative to a content root; also usable with a packaged resource filesystem.</summary>
public sealed class ContentFiles(Func<string, string> readText, Func<string, IReadOnlyList<string>?> listDirectory)
{
    public static ContentFiles FromDirectory(string directory)
    {
        string root = Path.GetFullPath(directory);
        return new(path => File.ReadAllText(Path.Combine(root, path)), path =>
        {
            string full = Path.Combine(root, path);
            if (!Directory.Exists(full)) return null;
            return Directory.EnumerateFileSystemEntries(full)
                .Where(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                .Select(entry => Path.GetRelativePath(root, entry).Replace('\\', '/')).ToArray();
        });
    }

    public ContentSource Read(string path)
    {
        path = Normalize(path);
        return new(path, readText(path));
    }

    public IReadOnlyList<ContentSource> Expand(IEnumerable<string> paths)
    {
        var sources = new List<ContentSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            var files = new List<string>();
            Collect(Normalize(path), files, 0);
            foreach (string file in files.Order(StringComparer.Ordinal))
                if (seen.Add(file)) sources.Add(Read(file));
        }
        return sources.AsReadOnly();
    }

    private void Collect(string path, List<string> files, int depth)
    {
        if (depth > 64) throw new IOException($"Content directory nesting exceeds 64: {path}.");
        var entries = listDirectory(path);
        if (entries is null)
        {
            if (!IsYaml(path)) throw new IOException($"Expected a YAML file or directory: {path}.");
            files.Add(path);
            return;
        }
        int before = files.Count;
        foreach (string entry in entries)
        {
            string normalized = Normalize(entry);
            if (listDirectory(normalized) is not null) Collect(normalized, files, depth + 1);
            else if (IsYaml(normalized)) files.Add(normalized);
        }
        if (depth == 0 && before == files.Count) throw new IOException($"Content directory contains no YAML files: {path}.");
    }

    private static bool IsYaml(string path) => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
            throw new ArgumentException($"Expected a path relative to the content root: '{path}'.");
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new ArgumentException($"Path leaves the content root: '{path}'.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
