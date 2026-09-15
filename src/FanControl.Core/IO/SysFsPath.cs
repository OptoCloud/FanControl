namespace FanControl.Core.IO;

/// <summary>
/// Joins sysfs path segments with '/' regardless of host OS. Path.Combine is wrong here:
/// it uses the host separator, but sysfs is always POSIX-style, and this daemon is built
/// and unit-tested on Windows while only ever running on Linux.
/// </summary>
public static class SysFsPath
{
    public static string Combine(params ReadOnlySpan<string> segments) => string.Join('/', segments);

    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string? DirectoryName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? null : path[..index];
    }
}
