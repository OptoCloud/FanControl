using System.Buffers.Binary;

namespace FanControl.Core.Sensors;

/// <summary>
/// Pure wire-format logic for reading IO Unit Page 7 (IOCTemperature/BoardTemperature)
/// from an mpt3sas-managed LSI/Broadcom HBA via the MPT3COMMAND passthrough ioctl.
/// Deliberately kept free of Marshal/P/Invoke/unsafe so the byte layout — the part most
/// likely to have a subtle, silent offset bug — is unit-testable without real hardware.
/// <see cref="Mpt3ctlHbaTemperatureProvider"/> is the thin native-call wrapper around this.
///
/// Every offset here is taken directly from the Linux kernel source
/// (drivers/scsi/mpt3sas/mpt3sas_ctl.h and .../mpi/mpi2_cnfg.h), cross-checked against
/// mpt3sas_ctl.c's actual ioctl handler, not reconstructed from memory.
/// </summary>
public static class Mpt3IoUnitPage7Protocol
{
    public const int ReplySize = 24; // sizeof(MPI2_CONFIG_REPLY)
    public const int PageSize = 36;  // sizeof(MPI2_CONFIG_PAGE_IO_UNIT_7)

    // MPI2_CONFIG_REQUEST up to (not including) the trailing PageBufferSGE union — the
    // driver builds the actual scatter-gather element itself from data_in_size, so only
    // this fixed 28-byte header needs to be supplied.
    public const int MfSize = 28;

    // struct mpt3_ioctl_command fixed fields; this is the byte offset where mf[] begins,
    // and therefore also the offset the driver uses to locate the message header.
    public const int IoctlHeaderSize = 68;
    public const int IoctlBufferSize = IoctlHeaderSize + MfSize;

    private const byte ConfigActionPageReadCurrent = 0x01; // MPI2_CONFIG_ACTION_PAGE_READ_CURRENT
    private const byte ConfigPageTypeIoUnit = 0x00;         // MPI2_CONFIG_PAGETYPE_IO_UNIT
    private const byte IoUnitPageNumber7 = 7;
    private const byte FunctionConfig = 0x04;               // MPI2_FUNCTION_CONFIG
    private const ushort IocStatusMask = 0x7FFF;             // MPI2_IOCSTATUS_MASK

    private const byte TempUnitsNotPresent = 0x00;
    private const byte TempUnitsFahrenheit = 0x01;
    private const byte TempUnitsCelsius = 0x02;

    /// <summary>
    /// Builds the full struct mpt3_ioctl_command buffer (fixed header + embedded mf[]
    /// config-request message) ready to hand to ioctl(fd, MPT3COMMAND, ...).
    /// </summary>
    public static byte[] BuildIoctlBuffer(uint iocNumber, ulong replyBufferAddress, ulong dataBufferAddress, uint timeoutSeconds = 5)
    {
        var buffer = new byte[IoctlBufferSize];
        var span = buffer.AsSpan();

        // struct mpt3_ioctl_header (offsets 0-11)
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], iocNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0); // port_number
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0); // max_data_size (unused by MPT3COMMAND)

        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], timeoutSeconds);

        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], replyBufferAddress); // reply_frame_buf_ptr
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], dataBufferAddress);  // data_in_buf_ptr
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 0);                 // data_out_buf_ptr
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..], 0);                 // sense_data_ptr

        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], ReplySize);      // max_reply_bytes
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], PageSize);      // data_in_size
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0);             // data_out_size
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..], 0);             // max_sense_bytes
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], MfSize / 4);    // data_sge_offset (32-bit words)

        // mf[] begins at IoctlHeaderSize: MPI2_CONFIG_REQUEST header (up to PageAddress).
        var mf = span[IoctlHeaderSize..];
        mf[0] = ConfigActionPageReadCurrent; // Action
        mf[1] = 0;                           // SGLFlags (driver builds the real SGE itself)
        mf[2] = 0;                           // ChainOffset
        mf[3] = FunctionConfig;              // Function
        BinaryPrimitives.WriteUInt16LittleEndian(mf[4..], 0); // ExtPageLength
        mf[6] = 0;                           // ExtPageType
        mf[7] = 0;                           // MsgFlags
        mf[8] = 0;                           // VP_ID
        mf[9] = 0;                           // VF_ID
        BinaryPrimitives.WriteUInt16LittleEndian(mf[10..], 0); // Reserved1
        mf[12] = 0;                          // Reserved2
        mf[13] = 0;                          // ProxyVF_ID
        BinaryPrimitives.WriteUInt16LittleEndian(mf[14..], 0); // Reserved4
        BinaryPrimitives.WriteUInt32LittleEndian(mf[16..], 0); // Reserved3
        // MPI2_CONFIG_PAGE_HEADER at mf+20
        mf[20] = 0;                          // PageVersion
        mf[21] = 0;                          // PageLength
        mf[22] = IoUnitPageNumber7;          // PageNumber
        mf[23] = ConfigPageTypeIoUnit;       // PageType
        BinaryPrimitives.WriteUInt32LittleEndian(mf[24..], 0); // PageAddress

        return buffer;
    }

    /// <summary>True if the config reply's IOCStatus indicates success (masked per MPI2_IOCSTATUS_MASK).</summary>
    public static bool IsReplySuccess(ReadOnlySpan<byte> replyBytes)
    {
        var iocStatus = BinaryPrimitives.ReadUInt16LittleEndian(replyBytes[14..]);
        return (iocStatus & IocStatusMask) == 0;
    }

    /// <summary>
    /// Extracts a temperature reading from an IO Unit Page 7 buffer, preferring
    /// IOCTemperature (the chip's own sensor) and falling back to BoardTemperature —
    /// not every card populates both. Returns an unavailable reading if neither is
    /// present (units == NotPresent for both).
    /// </summary>
    public static SensorReading ParseTemperature(ReadOnlySpan<byte> pageBytes)
    {
        var iocTemperature = BinaryPrimitives.ReadUInt16LittleEndian(pageBytes[16..]);
        var iocTemperatureUnits = pageBytes[18];
        var boardTemperature = BinaryPrimitives.ReadUInt16LittleEndian(pageBytes[20..]);
        var boardTemperatureUnits = pageBytes[22];

        if (TryToCelsius(iocTemperature, iocTemperatureUnits, out var iocCelsius))
        {
            return new SensorReading("hba", SensorCategory.Hba, "IOC Temperature", iocCelsius, "mpt3ctl:IOUnitPage7.IOCTemperature");
        }

        if (TryToCelsius(boardTemperature, boardTemperatureUnits, out var boardCelsius))
        {
            return new SensorReading("hba", SensorCategory.Hba, "Board Temperature", boardCelsius, "mpt3ctl:IOUnitPage7.BoardTemperature");
        }

        return Unavailable();
    }

    public static SensorReading Unavailable() =>
        new("hba", SensorCategory.Hba, "LSI HBA", null, "mpt3ctl:unavailable");

    private static bool TryToCelsius(ushort rawValue, byte units, out double celsius)
    {
        switch (units)
        {
            case TempUnitsCelsius:
                celsius = rawValue;
                return true;
            case TempUnitsFahrenheit:
                celsius = (rawValue - 32) / 1.8;
                return true;
            case TempUnitsNotPresent:
            default:
                celsius = 0;
                return false;
        }
    }
}
