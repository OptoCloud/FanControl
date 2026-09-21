//! Pure wire-format logic for reading IO Unit Page 7 (IOCTemperature/BoardTemperature)
//! from an mpt3sas-managed LSI/Broadcom HBA via the MPT3COMMAND passthrough ioctl. Kept
//! free of any native call so the byte layout (the part most likely to hide a subtle,
//! silent offset bug) is unit-testable without real hardware. hba.rs is the thin
//! native-call wrapper around this.
//!
//! This is a two-step MPI2 CONFIG read, not a single request: firmware validates the
//! request's declared Header.PageVersion/PageLength against the actual page, so a
//! PAGE_READ_CURRENT sent with a zeroed/guessed header is rejected with IOCStatus
//! INVALID_SGL (0x0003) rather than silently succeeding. Step 1 (PAGE_HEADER, no data
//! transfer) asks firmware for the real header; step 2 (PAGE_READ_CURRENT) echoes that
//! header back with a correctly-sized data buffer. Confirmed against a known-working
//! reference implementation (farzadb/sas2308_temp) after a single-step version failed on
//! real hardware with exactly that IOCStatus.
//!
//! Every offset here is taken directly from the Linux kernel source
//! (drivers/scsi/mpt3sas/mpt3sas_ctl.h and .../mpi/mpi2_cnfg.h), cross-checked against
//! mpt3sas_ctl.c's actual ioctl handler, and has been verified against a real SAS9300-8i.

use crate::sensors::{SensorCategory, SensorReading};

/// sizeof(MPI2_CONFIG_REPLY)
pub const REPLY_SIZE: usize = 24;

/// MPI2_CONFIG_REQUEST up to (not including) the trailing PageBufferSGE union. The driver
/// builds the actual scatter-gather element itself from data_in_size, so only this fixed
/// 28-byte header needs to be supplied.
pub const MF_SIZE: usize = 28;

/// struct mpt3_ioctl_command's fixed fields; the byte offset where mf[] begins.
pub const IOCTL_HEADER_SIZE: usize = 68;

pub const IOCTL_BUFFER_SIZE: usize = IOCTL_HEADER_SIZE + MF_SIZE;

pub const CONFIG_ACTION_PAGE_HEADER: u8 = 0x00; // MPI2_CONFIG_ACTION_PAGE_HEADER
pub const CONFIG_ACTION_PAGE_READ_CURRENT: u8 = 0x01; // MPI2_CONFIG_ACTION_PAGE_READ_CURRENT
pub const CONFIG_PAGE_TYPE_IO_UNIT: u8 = 0x00; // MPI2_CONFIG_PAGETYPE_IO_UNIT
pub const IO_UNIT_PAGE_NUMBER_7: u8 = 7;
const FUNCTION_CONFIG: u8 = 0x04; // MPI2_FUNCTION_CONFIG
const IOC_STATUS_MASK: u16 = 0x7FFF; // MPI2_IOCSTATUS_MASK

const TEMP_UNITS_FAHRENHEIT: u8 = 0x01;
const TEMP_UNITS_CELSIUS: u8 = 0x02;

/// The 4-byte MPI2_CONFIG_PAGE_HEADER, used both to build a request and to read one back from a reply.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PageHeader {
    pub page_version: u8,
    /// In 4-byte words.
    pub page_length: u8,
    pub page_number: u8,
    pub page_type: u8,
}

impl PageHeader {
    /// What the PAGE_HEADER step sends: version and length unknown, to be filled in by firmware.
    pub const BLANK_IO_UNIT_PAGE_7: Self =
        Self { page_version: 0, page_length: 0, page_number: IO_UNIT_PAGE_NUMBER_7, page_type: CONFIG_PAGE_TYPE_IO_UNIT };
}

/// Builds the full struct mpt3_ioctl_command buffer (fixed header + embedded mf[]
/// config-request message) ready to hand to ioctl(fd, MPT3COMMAND, ...).
pub fn build_ioctl_buffer(
    ioc_number: u32,
    action: u8,
    header: PageHeader,
    reply_buffer_address: u64,
    data_buffer_address: u64,
    data_in_size: u32,
    timeout_seconds: u32,
) -> [u8; IOCTL_BUFFER_SIZE] {
    let mut buffer = [0u8; IOCTL_BUFFER_SIZE];

    // struct mpt3_ioctl_header (offsets 0-11); port_number and max_data_size stay 0.
    buffer[0..4].copy_from_slice(&ioc_number.to_le_bytes());

    buffer[12..16].copy_from_slice(&timeout_seconds.to_le_bytes());
    buffer[16..24].copy_from_slice(&reply_buffer_address.to_le_bytes()); // reply_frame_buf_ptr
    buffer[24..32].copy_from_slice(&data_buffer_address.to_le_bytes()); // data_in_buf_ptr
    // data_out_buf_ptr (32..40) and sense_data_ptr (40..48) stay 0.
    buffer[48..52].copy_from_slice(&(REPLY_SIZE as u32).to_le_bytes()); // max_reply_bytes
    buffer[52..56].copy_from_slice(&data_in_size.to_le_bytes()); // data_in_size
    // data_out_size (56..60) and max_sense_bytes (60..64) stay 0.
    buffer[64..68].copy_from_slice(&((MF_SIZE / 4) as u32).to_le_bytes()); // data_sge_offset, in 32-bit words

    // mf[]: MPI2_CONFIG_REQUEST. Every field not set here (SGLFlags, ChainOffset,
    // ExtPageLength, ExtPageType, MsgFlags, VP_ID, VF_ID, reserved, PageAddress) is 0.
    let mf = &mut buffer[IOCTL_HEADER_SIZE..];
    mf[0] = action;
    mf[3] = FUNCTION_CONFIG;
    // MPI2_CONFIG_PAGE_HEADER at mf+20: echoed from a prior PAGE_HEADER reply for a
    // READ_CURRENT request, blank for the PAGE_HEADER step itself.
    mf[20] = header.page_version;
    mf[21] = header.page_length;
    mf[22] = header.page_number;
    mf[23] = header.page_type;

    buffer
}

/// The config reply's IOCStatus, masked per MPI2_IOCSTATUS_MASK (bit 0x8000 only means
/// "log info available" and is not part of the status). 0 is success.
pub fn ioc_status(reply: &[u8; REPLY_SIZE]) -> u16 {
    u16::from_le_bytes([reply[14], reply[15]]) & IOC_STATUS_MASK
}

/// The MPI2_CONFIG_PAGE_HEADER firmware echoed back in a PAGE_HEADER reply (offset 0x14).
pub fn reply_header(reply: &[u8; REPLY_SIZE]) -> PageHeader {
    PageHeader { page_version: reply[20], page_length: reply[21], page_number: reply[22], page_type: reply[23] }
}

/// Extracts a temperature from an IO Unit Page 7 buffer, preferring IOCTemperature (the
/// chip's own sensor) and falling back to BoardTemperature: not every card populates
/// both. Unavailable if the buffer is too short to contain the fields or neither sensor
/// is present.
pub fn parse_temperature(page: &[u8]) -> SensorReading {
    const BOARD_TEMPERATURE_UNITS_OFFSET: usize = 22;
    if page.len() <= BOARD_TEMPERATURE_UNITS_OFFSET {
        return unavailable();
    }

    let ioc = to_celsius(u16::from_le_bytes([page[16], page[17]]), page[18]);
    let board = to_celsius(u16::from_le_bytes([page[20], page[21]]), page[22]);

    if let Some(celsius) = ioc {
        SensorReading::new("hba", SensorCategory::Hba, "IOC Temperature", Some(celsius), "mpt3ctl:IOUnitPage7.IOCTemperature")
    } else if let Some(celsius) = board {
        SensorReading::new("hba", SensorCategory::Hba, "Board Temperature", Some(celsius), "mpt3ctl:IOUnitPage7.BoardTemperature")
    } else {
        unavailable()
    }
}

pub fn unavailable() -> SensorReading {
    SensorReading::new("hba", SensorCategory::Hba, "LSI HBA", None, "mpt3ctl:unavailable")
}

fn to_celsius(raw: u16, units: u8) -> Option<f64> {
    match units {
        TEMP_UNITS_CELSIUS => Some(f64::from(raw)),
        TEMP_UNITS_FAHRENHEIT => Some((f64::from(raw) - 32.0) / 1.8),
        _ => None, // 0x00 = not present
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn u32_at(buffer: &[u8], offset: usize) -> u32 {
        u32::from_le_bytes(buffer[offset..offset + 4].try_into().unwrap())
    }

    fn u64_at(buffer: &[u8], offset: usize) -> u64 {
        u64::from_le_bytes(buffer[offset..offset + 8].try_into().unwrap())
    }

    #[test]
    fn ioctl_buffer_has_correct_length() {
        assert_eq!(IOCTL_BUFFER_SIZE, 96);
    }

    #[test]
    fn writes_header_fields_at_correct_offsets() {
        let buffer = build_ioctl_buffer(
            3,
            CONFIG_ACTION_PAGE_READ_CURRENT,
            PageHeader::BLANK_IO_UNIT_PAGE_7,
            0x1122_3344_5566_7788,
            0xAABB_CCDD_EEFF_0011,
            36,
            7,
        );

        assert_eq!(u32_at(&buffer, 0), 3); // ioc_number
        assert_eq!(u32_at(&buffer, 4), 0); // port_number
        assert_eq!(u32_at(&buffer, 8), 0); // max_data_size
        assert_eq!(u32_at(&buffer, 12), 7); // timeout

        assert_eq!(u64_at(&buffer, 16), 0x1122_3344_5566_7788); // reply_frame_buf_ptr
        assert_eq!(u64_at(&buffer, 24), 0xAABB_CCDD_EEFF_0011); // data_in_buf_ptr
        assert_eq!(u64_at(&buffer, 32), 0); // data_out_buf_ptr
        assert_eq!(u64_at(&buffer, 40), 0); // sense_data_ptr

        assert_eq!(u32_at(&buffer, 48), REPLY_SIZE as u32); // max_reply_bytes
        assert_eq!(u32_at(&buffer, 52), 36); // data_in_size
        assert_eq!(u32_at(&buffer, 56), 0); // data_out_size
        assert_eq!(u32_at(&buffer, 60), 0); // max_sense_bytes
        assert_eq!(u32_at(&buffer, 64), 7); // data_sge_offset (28 bytes / 4)
    }

    #[test]
    fn writes_config_request_message_at_mf_offset() {
        let buffer = build_ioctl_buffer(0, CONFIG_ACTION_PAGE_HEADER, PageHeader::BLANK_IO_UNIT_PAGE_7, 0, 0, 0, 5);
        let mf = &buffer[IOCTL_HEADER_SIZE..];

        assert_eq!(mf[0], 0x00); // Action = PAGE_HEADER
        assert_eq!(mf[2], 0x00); // ChainOffset
        assert_eq!(mf[3], 0x04); // Function = CONFIG
        assert_eq!(mf[22], 7); // Header.PageNumber
        assert_eq!(mf[23], 0x00); // Header.PageType = IO_UNIT
        assert_eq!(u32_at(mf, 24), 0); // PageAddress
    }

    #[test]
    fn echoes_the_supplied_page_header_for_read_current() {
        let firmware_header = PageHeader { page_version: 5, page_length: 9, page_number: 7, page_type: 0 };

        let buffer = build_ioctl_buffer(0, CONFIG_ACTION_PAGE_READ_CURRENT, firmware_header, 0, 0, 36, 5);
        let mf = &buffer[IOCTL_HEADER_SIZE..];

        assert_eq!(mf[0], 0x01);
        assert_eq!(&mf[20..24], &[5, 9, 7, 0]);
    }

    #[test]
    fn ioc_status_is_zero_on_success_and_masks_the_log_info_bit() {
        let mut reply = [0u8; REPLY_SIZE];
        assert_eq!(ioc_status(&reply), 0);

        reply[14..16].copy_from_slice(&0x0003u16.to_le_bytes()); // INVALID_SGL
        assert_eq!(ioc_status(&reply), 0x0003);

        reply[14..16].copy_from_slice(&0x8000u16.to_le_bytes()); // "log info available" alone is still success
        assert_eq!(ioc_status(&reply), 0);

        reply[14..16].copy_from_slice(&0x8003u16.to_le_bytes());
        assert_eq!(ioc_status(&reply), 0x0003);
    }

    #[test]
    fn reads_firmware_echoed_header_fields_from_reply() {
        let mut reply = [0u8; REPLY_SIZE];
        reply[20..24].copy_from_slice(&[5, 9, 7, 0]);

        assert_eq!(reply_header(&reply), PageHeader { page_version: 5, page_length: 9, page_number: 7, page_type: 0 });
    }

    #[test]
    fn prefers_ioc_temperature_when_present() {
        let mut page = [0u8; 36];
        page[16..18].copy_from_slice(&45u16.to_le_bytes());
        page[18] = 0x02;
        page[20..22].copy_from_slice(&38u16.to_le_bytes());
        page[22] = 0x02;

        let reading = parse_temperature(&page);

        assert_eq!(reading.celsius_or_null, Some(45.0));
        assert_eq!(reading.label, "IOC Temperature");
    }

    #[test]
    fn falls_back_to_board_temperature_when_ioc_not_present() {
        let mut page = [0u8; 36];
        page[20..22].copy_from_slice(&38u16.to_le_bytes());
        page[22] = 0x02;

        let reading = parse_temperature(&page);

        assert_eq!(reading.celsius_or_null, Some(38.0));
        assert_eq!(reading.label, "Board Temperature");
    }

    #[test]
    fn converts_fahrenheit_to_celsius() {
        let mut page = [0u8; 36];
        page[16..18].copy_from_slice(&113u16.to_le_bytes());
        page[18] = 0x01;

        let celsius = parse_temperature(&page).celsius_or_null.unwrap();

        assert!((celsius - 45.0).abs() < 1e-9);
    }

    #[test]
    fn unavailable_when_neither_sensor_present_or_buffer_too_short() {
        assert!(!parse_temperature(&[0u8; 36]).is_available);
        assert!(!parse_temperature(&[0u8; 10]).is_available); // firmware could report a smaller/older page

        let reading = unavailable();
        assert_eq!((reading.id.as_str(), reading.category, reading.is_available), ("hba", SensorCategory::Hba, false));
    }
}
