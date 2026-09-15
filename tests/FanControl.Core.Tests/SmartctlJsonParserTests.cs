using FanControl.Core.Drives;
using Xunit;

namespace FanControl.Core.Tests;

public class SmartctlJsonParserTests
{
    [Fact]
    public void ParsesHealthyAtaDriveWithAttributes()
    {
        const string json = """
            {
              "smart_status": { "passed": true },
              "ata_smart_attributes": {
                "table": [
                  { "id": 5, "name": "Reallocated_Sector_Ct", "raw": { "value": 0 } },
                  { "id": 197, "name": "Current_Pending_Sector", "raw": { "value": 0 } },
                  { "id": 9, "name": "Power_On_Hours", "raw": { "value": 12345 } }
                ]
              },
              "power_on_time": { "hours": 8760 }
            }
            """;

        var status = SmartctlJsonParser.Parse("sda", json, "/dev/sda");

        Assert.True(status.IsAvailable);
        Assert.True(status.Passed);
        Assert.Equal(0UL, status.ReallocatedSectorCount);
        Assert.Equal(0UL, status.PendingSectorCount);
        Assert.Equal(8760UL, status.PowerOnHours);
    }

    [Fact]
    public void ParsesFailingDrive()
    {
        const string json = """{ "smart_status": { "passed": false } }""";

        var status = SmartctlJsonParser.Parse("sdb", json, "/dev/sdb");

        Assert.True(status.IsAvailable);
        Assert.False(status.Passed);
    }

    [Fact]
    public void ExtractsNonZeroReallocatedAndPendingSectors()
    {
        const string json = """
            {
              "smart_status": { "passed": true },
              "ata_smart_attributes": {
                "table": [
                  { "id": 5, "raw": { "value": 12 } },
                  { "id": 197, "raw": { "value": 3 } }
                ]
              }
            }
            """;

        var status = SmartctlJsonParser.Parse("sdc", json, "/dev/sdc");

        Assert.Equal(12UL, status.ReallocatedSectorCount);
        Assert.Equal(3UL, status.PendingSectorCount);
    }

    [Fact]
    public void ParsesScsiDriveWithoutAtaAttributes()
    {
        // SAS/SCSI drives (behind the LSI HBA) report health via smart_status alone —
        // no ata_smart_attributes table at all.
        const string json = """
            {
              "smart_status": { "passed": true },
              "power_on_time": { "hours": 4200 }
            }
            """;

        var status = SmartctlJsonParser.Parse("sdd", json, "/dev/sdd");

        Assert.True(status.IsAvailable);
        Assert.True(status.Passed);
        Assert.Null(status.ReallocatedSectorCount);
        Assert.Null(status.PendingSectorCount);
        Assert.Equal(4200UL, status.PowerOnHours);
    }

    [Fact]
    public void UnavailableWhenSmartStatusMissingEntirely()
    {
        // e.g. smartctl -n standby against a sleeping drive: it deliberately skips the
        // real query rather than waking the drive, so there's no smart_status at all.
        const string json = """{ "smartctl": { "messages": [{ "string": "Device is in STANDBY mode" }] } }""";

        var status = SmartctlJsonParser.Parse("sde", json, "/dev/sde");

        Assert.False(status.IsAvailable);
        Assert.Null(status.Passed);
    }

    [Fact]
    public void UnavailableOnMalformedJson()
    {
        var status = SmartctlJsonParser.Parse("sdf", "not json at all", "/dev/sdf");

        Assert.False(status.IsAvailable);
    }

    [Fact]
    public void UnavailableOnEmptyOutput()
    {
        // e.g. the smartctl process failed to start or produced no output at all.
        var status = SmartctlJsonParser.Parse("sdg", "", "/dev/sdg");

        Assert.False(status.IsAvailable);
    }

    [Fact]
    public void IgnoresUnrelatedAttributeIds()
    {
        const string json = """
            {
              "smart_status": { "passed": true },
              "ata_smart_attributes": {
                "table": [
                  { "id": 1, "raw": { "value": 999 } },
                  { "id": 194, "raw": { "value": 35 } }
                ]
              }
            }
            """;

        var status = SmartctlJsonParser.Parse("sdh", json, "/dev/sdh");

        Assert.Null(status.ReallocatedSectorCount);
        Assert.Null(status.PendingSectorCount);
    }

    [Fact]
    public void UnavailableFactoryHasNullFieldsAndPreservesDeviceName()
    {
        var status = SmartctlJsonParser.Unavailable("sdi", "/dev/sdi");

        Assert.False(status.IsAvailable);
        Assert.Equal("sdi", status.DeviceName);
        Assert.Equal("/dev/sdi", status.SourcePath);
    }
}
