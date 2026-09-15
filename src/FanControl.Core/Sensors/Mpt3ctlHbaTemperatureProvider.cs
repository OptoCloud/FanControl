using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FanControl.Core.Sensors;

/// <summary>
/// Reads IOC/board temperature from an LSI/Broadcom Fusion-MPT SAS controller (mpt3sas
/// driver — e.g. the SAS9300-8i) via a raw passthrough ioctl to /dev/mpt3ctl. mpt3sas has
/// no hwmon exposure for this in mainline Linux; the only path is a Config Page
/// IO_UNIT_PAGE_7 read, the same mechanism vendor tools like lsiutil use internally.
///
/// This class is only the native-call plumbing (open/ioctl/close, unmanaged buffers) plus
/// the two-step request orchestration; the wire-format byte layout lives in
/// <see cref="Mpt3IoUnitPage7Protocol"/>, which is pure and unit-tested. This class itself
/// has no test coverage — there is no /dev/mpt3ctl to exercise it against outside a
/// machine with this HBA and driver loaded. Assumes a little-endian 64-bit host (true for
/// every realistic deployment target here).
/// </summary>
public sealed class Mpt3ctlHbaTemperatureProvider(
    string devicePath = "/dev/mpt3ctl",
    uint iocNumber = 0,
    ILogger<Mpt3ctlHbaTemperatureProvider>? logger = null)
    : IHbaTemperatureProvider
{
    private readonly ILogger<Mpt3ctlHbaTemperatureProvider> _logger =
        logger ?? NullLogger<Mpt3ctlHbaTemperatureProvider>.Instance;

    // Logged only when the outcome changes, not every poll (every 2s) — diagnostic for
    // "why is hba unavailable", not something worth spamming the journal with forever,
    // but still surfaces a transition (e.g. it starts working after a driver reload).
    private string? _lastLoggedMessage;

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
            var errno = Marshal.GetLastPInvokeError();
            return Fail($"open(\"{devicePath}\") failed (errno {errno}) — device missing, module not loaded, or not running as root.");
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
        // Step 1: PAGE_HEADER, no data transfer — firmware validates a READ_CURRENT's
        // declared PageVersion/PageLength against the real page, so those must come from
        // firmware itself rather than being guessed/zeroed (a zeroed header here fails
        // READ_CURRENT with IOCStatus INVALID_SGL, confirmed on real hardware).
        var headerRequestBlank = new Mpt3IoUnitPage7Protocol.PageHeader(
            0, 0, Mpt3IoUnitPage7Protocol.IoUnitPageNumber7, Mpt3IoUnitPage7Protocol.ConfigPageTypeIoUnit);

        if (!TrySendConfigRequest(fd, Mpt3IoUnitPage7Protocol.ConfigActionPageHeader, headerRequestBlank, dataInSize: 0,
                out var headerReplyBytes, out _))
        {
            return Mpt3IoUnitPage7Protocol.Unavailable(); // TrySendConfigRequest already logged the reason
        }

        var realHeader = Mpt3IoUnitPage7Protocol.ReadReplyHeader(headerReplyBytes);
        if (realHeader.PageLength == 0)
        {
            return Fail("Firmware returned PageLength=0 for IO Unit Page 7 on the PAGE_HEADER step — page not supported by this firmware.");
        }

        var pageBytes = realHeader.PageLength * 4;

        // Step 2: READ_CURRENT, echoing the header firmware just gave us.
        if (!TrySendConfigRequest(fd, Mpt3IoUnitPage7Protocol.ConfigActionPageReadCurrent, realHeader, pageBytes,
                out _, out var pageData))
        {
            return Mpt3IoUnitPage7Protocol.Unavailable();
        }

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(pageData!);
        if (!reading.IsAvailable)
        {
            return Fail("IO Unit Page 7 read succeeded, but both IOCTemperature and BoardTemperature report \"not present\" — this card/firmware doesn't expose these sensors.");
        }

        LogOnce(LogLevel.Information, "HBA temperature available via {Label}: {Celsius}C", reading.Label, reading.CelsiusOrNull);
        return reading;
    }

    /// <summary>
    /// Sends one MPT3COMMAND config request and reads back the reply (+ data, when
    /// dataInSize > 0). Returns false (having already logged why) on any failure: open
    /// ioctl error, or a non-success IOCStatus.
    /// </summary>
    private bool TrySendConfigRequest(
        int fd,
        byte action,
        Mpt3IoUnitPage7Protocol.PageHeader header,
        int dataInSize,
        out byte[] replyBytes,
        out byte[]? dataBytes)
    {
        replyBytes = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        dataBytes = null;

        var replyBuffer = Marshal.AllocHGlobal(Mpt3IoUnitPage7Protocol.ReplySize);
        var dataBuffer = dataInSize > 0 ? Marshal.AllocHGlobal(dataInSize) : IntPtr.Zero;
        var ioctlBuffer = IntPtr.Zero;

        try
        {
            ZeroFill(replyBuffer, Mpt3IoUnitPage7Protocol.ReplySize);
            if (dataBuffer != IntPtr.Zero)
            {
                ZeroFill(dataBuffer, dataInSize);
            }

            var request = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
                iocNumber,
                action,
                header,
                (ulong)replyBuffer.ToInt64(),
                (ulong)dataBuffer.ToInt64(),
                dataInSize);

            ioctlBuffer = Marshal.AllocHGlobal(request.Length);
            Marshal.Copy(request, 0, ioctlBuffer, request.Length);

            if (NativeMethods.Ioctl(fd, Mpt3Command, ioctlBuffer) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                Fail($"MPT3COMMAND ioctl failed (errno {errno}, action 0x{action:X2}) — wrong ioc_number ({iocNumber}), or the driver rejected the request.");
                return false;
            }

            Marshal.Copy(replyBuffer, replyBytes, 0, replyBytes.Length);
            if (!Mpt3IoUnitPage7Protocol.IsReplySuccess(replyBytes))
            {
                var iocStatus = Mpt3IoUnitPage7Protocol.ReadIocStatus(replyBytes);
                Fail($"Config Page request (action 0x{action:X2}) returned non-success IOCStatus 0x{iocStatus:X4}.");
                return false;
            }

            if (dataInSize > 0)
            {
                dataBytes = new byte[dataInSize];
                Marshal.Copy(dataBuffer, dataBytes, 0, dataInSize);
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(replyBuffer);
            if (dataBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataBuffer);
            }

            if (ioctlBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ioctlBuffer);
            }
        }
    }

    private SensorReading Fail(string reason)
    {
        LogOnce(LogLevel.Warning, "HBA temperature unavailable: {Reason}", reason);
        return Mpt3IoUnitPage7Protocol.Unavailable();
    }

    private void LogOnce(LogLevel level, string message, params object?[] args)
    {
        if (_lastLoggedMessage == message)
        {
            return;
        }

        _lastLoggedMessage = message;
        _logger.Log(level, message, args);
    }

    private static void ZeroFill(IntPtr buffer, int size) =>
        Marshal.Copy(new byte[size], 0, buffer, size);
}
