using System.Runtime.InteropServices;

namespace FanControl.Core.Sensors;

/// <summary>
/// Reads IOC/board temperature from an LSI/Broadcom Fusion-MPT SAS controller (mpt3sas
/// driver — e.g. the SAS9300-8i) via a raw passthrough ioctl to /dev/mpt3ctl. mpt3sas has
/// no hwmon exposure for this in mainline Linux; the only path is a Config Page
/// IO_UNIT_PAGE_7 read, the same mechanism vendor tools like lsiutil use internally.
///
/// This class is only the native-call plumbing (open/ioctl/close, unmanaged buffers);
/// the actual wire-format logic lives in <see cref="Mpt3IoUnitPage7Protocol"/>, which is
/// pure and unit-tested. This class itself has no test coverage — there is no
/// /dev/mpt3ctl to exercise it against outside a machine with this HBA and driver loaded.
/// Assumes a little-endian 64-bit host (true for every realistic deployment target here).
/// </summary>
public sealed class Mpt3ctlHbaTemperatureProvider(string devicePath = "/dev/mpt3ctl", uint iocNumber = 0)
    : IHbaTemperatureProvider
{
    private const int ORdwr = 2; // fcntl.h O_RDWR

    // mpt3sas_ctl.h: #define MPT3_MAGIC_NUMBER 'L'
    //                #define MPT3COMMAND _IOWR(MPT3_MAGIC_NUMBER, 20, struct mpt3_ioctl_command)
    // struct mpt3_ioctl_command on x86-64 is 72 bytes (verified: 12-byte header + u32 timeout
    // + 4x8-byte pointers + 4x u32 + u32 data_sge_offset + 1-byte mf[], padded to the 8-byte
    // alignment the pointer members require).
    // _IOC encoding (asm-generic/ioctl.h): (dir<<30)|(size<<16)|(type<<8)|nr; dir for
    // _IOWR is _IOC_READ|_IOC_WRITE == 3.
    private const nuint Mpt3Command = (3u << 30) | (72u << 16) | ((uint)'L' << 8) | 20u;

    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Mpt3IoUnitPage7Protocol.Unavailable();
        }

        return await Task.Run(Read, cancellationToken);
    }

    private SensorReading Read()
    {
        var fd = NativeMethods.Open(devicePath, ORdwr);
        if (fd < 0)
        {
            return Mpt3IoUnitPage7Protocol.Unavailable();
        }

        try
        {
            return ReadIoUnitPage7(fd);
        }
        finally
        {
            NativeMethods.Close(fd);
        }
    }

    private SensorReading ReadIoUnitPage7(int fd)
    {
        var replyBuffer = Marshal.AllocHGlobal(Mpt3IoUnitPage7Protocol.ReplySize);
        var dataBuffer = Marshal.AllocHGlobal(Mpt3IoUnitPage7Protocol.PageSize);
        var ioctlBuffer = IntPtr.Zero;

        try
        {
            ZeroFill(replyBuffer, Mpt3IoUnitPage7Protocol.ReplySize);
            ZeroFill(dataBuffer, Mpt3IoUnitPage7Protocol.PageSize);

            var request = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
                iocNumber,
                (ulong)replyBuffer.ToInt64(),
                (ulong)dataBuffer.ToInt64());

            ioctlBuffer = Marshal.AllocHGlobal(request.Length);
            Marshal.Copy(request, 0, ioctlBuffer, request.Length);

            if (NativeMethods.Ioctl(fd, Mpt3Command, ioctlBuffer) < 0)
            {
                return Mpt3IoUnitPage7Protocol.Unavailable();
            }

            var replyBytes = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
            Marshal.Copy(replyBuffer, replyBytes, 0, replyBytes.Length);
            if (!Mpt3IoUnitPage7Protocol.IsReplySuccess(replyBytes))
            {
                return Mpt3IoUnitPage7Protocol.Unavailable();
            }

            var pageBytes = new byte[Mpt3IoUnitPage7Protocol.PageSize];
            Marshal.Copy(dataBuffer, pageBytes, 0, pageBytes.Length);
            return Mpt3IoUnitPage7Protocol.ParseTemperature(pageBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(replyBuffer);
            Marshal.FreeHGlobal(dataBuffer);
            if (ioctlBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ioctlBuffer);
            }
        }
    }

    private static void ZeroFill(IntPtr buffer, int size) =>
        Marshal.Copy(new byte[size], 0, buffer, size);
}
