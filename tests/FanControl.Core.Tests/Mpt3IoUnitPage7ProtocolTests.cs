using FanControl.Core.Sensors;
using Xunit;

namespace FanControl.Core.Tests;

public class Mpt3IoUnitPage7ProtocolTests
{
    [Fact]
    public void BuildIoctlBufferHasCorrectLength()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(iocNumber: 0, replyBufferAddress: 0, dataBufferAddress: 0);

        Assert.Equal(Mpt3IoUnitPage7Protocol.IoctlBufferSize, buffer.Length);
        Assert.Equal(96, buffer.Length);
    }

    [Fact]
    public void BuildIoctlBufferWritesHeaderFieldsAtCorrectOffsets()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(
            iocNumber: 3,
            replyBufferAddress: 0x1122_3344_5566_7788,
            dataBufferAddress: 0xAABB_CCDD_EEFF_0011,
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
        Assert.Equal((uint)Mpt3IoUnitPage7Protocol.PageSize, BitConverter.ToUInt32(buffer, 52));  // data_in_size
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 56));  // data_out_size
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 60));  // max_sense_bytes
        Assert.Equal(7u, BitConverter.ToUInt32(buffer, 64));  // data_sge_offset (28 bytes / 4)
    }

    [Fact]
    public void BuildIoctlBufferWritesConfigRequestMessageAtMfOffset()
    {
        var buffer = Mpt3IoUnitPage7Protocol.BuildIoctlBuffer(iocNumber: 0, replyBufferAddress: 0, dataBufferAddress: 0);
        var mf = buffer.AsSpan(Mpt3IoUnitPage7Protocol.IoctlHeaderSize);

        Assert.Equal(0x01, mf[0]);  // Action = PAGE_READ_CURRENT
        Assert.Equal(0x00, mf[2]);  // ChainOffset
        Assert.Equal(0x04, mf[3]);  // Function = CONFIG
        Assert.Equal(7, mf[22]);    // Header.PageNumber = 7
        Assert.Equal(0x00, mf[23]); // Header.PageType = IO_UNIT
        Assert.Equal(0u, BitConverter.ToUInt32(mf[24..].ToArray(), 0)); // PageAddress
    }

    [Fact]
    public void IsReplySuccessTrueWhenIocStatusIsZero()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        // IOCStatus at offset 14 left as 0.

        Assert.True(Mpt3IoUnitPage7Protocol.IsReplySuccess(reply));
    }

    [Fact]
    public void IsReplySuccessFalseWhenIocStatusIsNonZero()
    {
        var reply = new byte[Mpt3IoUnitPage7Protocol.ReplySize];
        BitConverter.GetBytes((ushort)0x0002).CopyTo(reply, 14); // MPI2_IOCSTATUS_INVALID_FUNCTION-ish, any nonzero

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
    public void ParseTemperaturePrefersIocTemperatureWhenPresent()
    {
        var page = new byte[Mpt3IoUnitPage7Protocol.PageSize];
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
        var page = new byte[Mpt3IoUnitPage7Protocol.PageSize];
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
        var page = new byte[Mpt3IoUnitPage7Protocol.PageSize]; // both *Units fields default to 0 = NotPresent

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.False(reading.IsAvailable);
    }

    [Fact]
    public void ParseTemperatureConvertsFahrenheitToCelsius()
    {
        var page = new byte[Mpt3IoUnitPage7Protocol.PageSize];
        BitConverter.GetBytes((ushort)113).CopyTo(page, 16); // IOCTemperature = 113F
        page[18] = 0x01; // Fahrenheit

        var reading = Mpt3IoUnitPage7Protocol.ParseTemperature(page);

        Assert.True(reading.IsAvailable);
        Assert.Equal(45, reading.CelsiusOrNull!.Value, precision: 5);
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
