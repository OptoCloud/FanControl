using FanControl.Core.Sensors;
using Xunit;

namespace FanControl.Core.Tests;

public class Mpt3IoUnitPage7ProtocolTests
{
    private static readonly Mpt3IoUnitPage7Protocol.PageHeader BlankHeader = new(
        PageVersion: 0, PageLength: 0,
        PageNumber: Mpt3IoUnitPage7Protocol.IoUnitPageNumber7,
        PageType: Mpt3IoUnitPage7Protocol.ConfigPageTypeIoUnit);

    [Fact]
    public void BuildIoctlBufferHasCorrectLength()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
            iocNumber: 0, action: Mpt3IoUnitPage7Protocol.ConfigActionPageHeader, header: BlankHeader,
            replyBufferAddress: 0, dataBufferAddress: 0, dataInSize: 0);

        Assert.Equal(Mpt3IoUnitPage7Protocol.IoctlBufferSize(), buffer.Length);
        Assert.Equal(96, buffer.Length);
    }

    [Fact]
    public void BuildIoctlBufferWritesHeaderFieldsAtCorrectOffsets()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
            iocNumber: 3,
            action: Mpt3IoUnitPage7Protocol.ConfigActionPageReadCurrent,
            header: BlankHeader,
            replyBufferAddress: 0x1122_3344_5566_7788,
            dataBufferAddress: 0xAABB_CCDD_EEFF_0011,
            dataInSize: 36,
            timeoutSeconds: 7);

        Assert.Equal(3u, BitConverter.ToUInt32(buffer, 0));   // ioc_number
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 4));   // port_number
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 8));   // max_data_size
        Assert.Equal(7u, BitConverter.ToUInt32(buffer, 12));  // timeout

        Assert.Equal(0x1122_3344_5566_7788UL, BitConverter.ToUInt64(buffer, 16)); // reply_frame_buf_ptr
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, BitConverter.ToUInt64(buffer, 24)); // data_in_buf_ptr
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, 32)); // data_out_buf_ptr
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, 40)); // sense_data_ptr

        Assert.Equal((uint)Mpt3IoUnitPage7Protocol.ReplySize, BitConverter.ToUInt32(buffer, 48)); // max_reply_bytes
        Assert.Equal(36u, BitConverter.ToUInt32(buffer, 52)); // data_in_size
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 56));  // data_out_size
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 60));  // max_sense_bytes
        Assert.Equal(7u, BitConverter.ToUInt32(buffer, 64));  // data_sge_offset (28 bytes / 4)
    }

    [Fact]
    public void BuildIoctlBufferWritesConfigRequestMessageAtMfOffset()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
            iocNumber: 0, action: Mpt3IoUnitPage7Protocol.ConfigActionPageHeader, header: BlankHeader,
            replyBufferAddress: 0, dataBufferAddress: 0, dataInSize: 0);
        var mf = buffer.AsSpan(Mpt3IoUnitPage7Protocol.IoctlHeaderSize);

        Assert.Equal(0x00, mf[0]);  // Action = PAGE_HEADER
        Assert.Equal(0x00, mf[2]);  // ChainOffset
        Assert.Equal(0x04, mf[3]);  // Function = CONFIG
        Assert.Equal(7, mf[22]);    // Header.PageNumber = 7
        Assert.Equal(0x00, mf[23]); // Header.PageType = IO_UNIT
        Assert.Equal(0u, BitConverter.ToUInt32(mf[24..].ToArray(), 0)); // PageAddress
    }

    [Fact]
    public void BuildIoctlBufferEchoesTheSuppliedPageHeaderForReadCurrent()
    {
        var firmwareHeader = new Mpt3IoUnitPage7Protocol.PageHeader(
            PageVersion: 5, PageLength: 9, PageNumber: 7, PageType: 0x00);

        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
            iocNumber: 0, action: Mpt3IoUnitPage7Protocol.ConfigActionPageReadCurrent, header: firmwareHeader,
            replyBufferAddress: 0, dataBufferAddress: 0, dataInSize: 36);
        var mf = buffer.AsSpan(Mpt3IoUnitPage7Protocol.IoctlHeaderSize);

        Assert.Equal(5, mf[20]); // Header.PageVersion echoed from firmware
        Assert.Equal(9, mf[21]); // Header.PageLength echoed from firmware
        Assert.Equal(7, mf[22]);
        Assert.Equal(0, mf[23]);
    }

    [Fact]
    public void IsReplySuccessTrueWhenIocStatusIsZero()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];

        Assert.True(Mpt3IoUnitPage7Protocol.IsReplySuccess(reply));
    }

    [Fact]
    public void IsReplySuccessFalseWhenIocStatusIsNonZero()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        BitConverter.GetBytes((ushort)0x0003).CopyTo(reply, 14); // INVALID_SGL

        Assert.False(Mpt3IoUnitPage7Protocol.IsReplySuccess(reply));
    }

    [Fact]
    public void IsReplySuccessIgnoresTheLogInfoAvailableBit()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        BitConverter.GetBytes((ushort)0x8000).CopyTo(reply, 14); // bit 0x8000 is "log info available", not a status

        Assert.True(Mpt3IoUnitPage7Protocol.IsReplySuccess(reply));
    }

    [Fact]
    public void ReadIocStatusMasksTheLogInfoAvailableBit()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        BitConverter.GetBytes((ushort)0x8003).CopyTo(reply, 14);

        Assert.Equal(0x0003, Mpt3IoUnitPage7Protocol.ReadIocStatus(reply));
    }

    [Fact]
    public void ReadReplyHeaderReadsFirmwareEchoedHeaderFields()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        reply[20] = 5; // PageVersion
        reply[21] = 9; // PageLength (dwords)
        reply[22] = 7; // PageNumber
        reply[23] = 0; // PageType

        var header = Mpt3IoUnitPage7Protocol.ReadReplyHeader(reply);

        Assert.Equal(5, header.PageVersion);
        Assert.Equal(9, header.PageLength);
        Assert.Equal(7, header.PageNumber);
        Assert.Equal(0, header.PageType);
    }

    [Fact]
    public void ParseTemperaturePrefersIocTemperatureWhenPresent()
    {
        var page = new byte[36];
        BitConverter.GetBytes((ushort)45).CopyTo(page, 16); // IOCTemperature
        page[18] = 0x02; // Celsius
        BitConverter.GetBytes((ushort)38).CopyTo(page, 20); // BoardTemperature
        page[22] = 0x02; // Celsius

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.True(reading.IsAvailable);
        Assert.Equal(45, reading.CelsiusOrNull);
        Assert.Equal("IOC Temperature", reading.Label);
    }

    [Fact]
    public void ParseTemperatureFallsBackToBoardTemperatureWhenIocNotPresent()
    {
        var page = new byte[36];
        page[18] = 0x00; // IOCTemperatureUnits = NotPresent
        BitConverter.GetBytes((ushort)38).CopyTo(page, 20); // BoardTemperature
        page[22] = 0x02; // Celsius

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.True(reading.IsAvailable);
        Assert.Equal(38, reading.CelsiusOrNull);
        Assert.Equal("Board Temperature", reading.Label);
    }

    [Fact]
    public void ParseTemperatureUnavailableWhenNeitherSensorPresent()
    {
        var page = new byte[36]; // both *Units fields default to 0 = NotPresent

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.False(reading.IsAvailable);
    }

    [Fact]
    public void ParseTemperatureConvertsFahrenheitToCelsius()
    {
        var page = new byte[36];
        BitConverter.GetBytes((ushort)113).CopyTo(page, 16); // IOCTemperature = 113F
        page[18] = 0x01; // Fahrenheit

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.True(reading.IsAvailable);
        Assert.Equal(45, reading.CelsiusOrNull!.Value, precision: 5);
    }

    [Fact]
    public void ParseTemperatureUnavailableWhenBufferTooShortForTemperatureFields()
    {
        var page = new byte[10]; // firmware could report a smaller/older page

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.False(reading.IsAvailable);
    }

    [Fact]
    public void UnavailableReadingHasNullCelsiusAndHbaCategory()
    {
        var reading = Mpt3IoUnitPage7Protocol.Unavailable();

        Assert.False(reading.IsAvailable);
        Assert.Equal(SensorCategory.Hba, reading.Category);
        Assert.Equal("hba", reading.Id);
    }
}
