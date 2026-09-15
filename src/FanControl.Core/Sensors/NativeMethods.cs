using System.Runtime.InteropServices;

namespace FanControl.Core.Sensors;

internal static partial class NativeMethods
{
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    // request is native `unsigned long` (8 bytes on Linux x86-64/arm64) — declared as
    // nuint rather than uint/int so it's passed at the correct width; a narrower type
    // would leave the upper 32 bits of the register undefined for this argument.
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, IntPtr argp);
}
