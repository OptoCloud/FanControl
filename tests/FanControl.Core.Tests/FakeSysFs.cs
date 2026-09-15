using FanControl.Core.IO;

namespace FanControl.Core.Tests;

/// <summary>In-memory sysfs double, since real /sys/class/hwmon only exists on Linux.</summary>
public sealed class FakeSysFs : ISysFs
{
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _directories = [];
    private readonly Dictionary<string, string> _symlinks = new();

    public FakeSysFs AddFile(string path, string contents)
    {
        _files[Normalize(path)] = contents;
        AddDirectory(Path.GetDirectoryName(path) ?? "/");
        return this;
    }

    public FakeSysFs AddDirectory(string path)
    {
        var normalized = Normalize(path);
        while (!string.IsNullOrEmpty(normalized) && normalized != "/")
        {
            _directories.Add(normalized);
            normalized = Normalize(Path.GetDirectoryName(normalized) ?? "");
        }

        return this;
    }

    public FakeSysFs AddSymlink(string path, string target)
    {
        _symlinks[Normalize(path)] = target;
        _directories.Add(Normalize(path));
        return this;
    }

    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*")
    {
        var normalizedParent = Normalize(path);
        var prefix = normalizedParent.TrimEnd('/') + "/";
        var pattern = searchPattern.Replace("*", "");

        return _directories
            .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && d[prefix.Length..].IndexOf('/') < 0)
            .Where(d => searchPattern == "*" || Path.GetFileName(d).Contains(pattern, StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal);
    }

    public string ReadAllText(string path) => _files[Normalize(path)];

    public string? TryReadAllText(string path) => _files.GetValueOrDefault(Normalize(path));

    public void WriteAllText(string path, string contents) => _files[Normalize(path)] = contents;

    public string ResolveSymbolicLink(string path) => _symlinks.GetValueOrDefault(Normalize(path), path);

    private static string Normalize(string path) => path.Replace('\\', '/');
}
