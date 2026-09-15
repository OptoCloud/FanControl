namespace FanControl.Core.IO;

/// <summary>Real sysfs implementation. Only usable on Linux; every other member is a thin System.IO wrapper.</summary>
public sealed class LinuxSysFs : ISysFs
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*") =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path, searchPattern) : [];

    public string ReadAllText(string path) => File.ReadAllText(path).Trim();

    public string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            // Common for unpopulated/racing sysfs attributes (e.g. a drive that just spun down).
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public string ResolveSymbolicLink(string path) =>
        new FileInfo(path).LinkTarget is { } target
            ? Path.GetFullPath(target, Path.GetDirectoryName(path) ?? "/")
            : path;
}
