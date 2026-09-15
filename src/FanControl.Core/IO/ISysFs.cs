namespace FanControl.Core.IO;

/// <summary>
/// Thin abstraction over sysfs file access so hwmon/PWM logic can be
/// unit-tested against an in-memory fake instead of a real Linux /sys tree.
/// </summary>
public interface ISysFs
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*");

    string ReadAllText(string path);

    /// <summary>Reads a file and returns null instead of throwing if it doesn't exist or can't be read.</summary>
    string? TryReadAllText(string path);

    void WriteAllText(string path, string contents);

    string ResolveSymbolicLink(string path);
}
