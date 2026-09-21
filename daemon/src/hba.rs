//! Reads IOC/board temperature from an LSI/Broadcom Fusion-MPT SAS controller (mpt3sas
//! driver, e.g. the SAS9300-8i) via a raw passthrough ioctl to /dev/mpt3ctl. mpt3sas has
//! no hwmon exposure for this in mainline Linux; the only path is a Config Page
//! IO_UNIT_PAGE_7 read, the same mechanism vendor tools like lsiutil use internally.
//!
//! This file is only the native-call plumbing plus the two-step request orchestration; the
//! wire format lives in mpt3.rs, which is pure and unit-tested. Nothing here has test
//! coverage: there is no /dev/mpt3ctl to exercise it against outside a machine with this
//! HBA. Assumes a little-endian 64-bit host.

use crate::config::HbaConfig;
use crate::log;
use crate::mpt3;
#[cfg(target_os = "linux")]
use crate::mpt3::PageHeader;
use crate::sensors::SensorReading;

pub struct HbaTemperatureProvider {
    config: HbaConfig,
    /// Logged only when the outcome changes (keyed on the specific failure reason, or on
    /// which sensor succeeded), not on every 2-second poll.
    last_logged_outcome: Option<String>,
    #[cfg(target_os = "linux")]
    device: Option<linux::Device>,
    /// The device stays open and the firmware's page header stays cached between polls, so
    /// the steady state is one ioctl per poll instead of open + two ioctls + close. Both
    /// are dropped on any failure, so the next poll starts again from scratch (covers a
    /// driver reload invalidating the fd, or new firmware changing the page version).
    #[cfg(target_os = "linux")]
    cached_header: Option<PageHeader>,
}

impl HbaTemperatureProvider {
    pub fn new(config: HbaConfig) -> Self {
        Self {
            config,
            last_logged_outcome: None,
            #[cfg(target_os = "linux")]
            device: None,
            #[cfg(target_os = "linux")]
            cached_header: None,
        }
    }

    pub fn read(&mut self) -> SensorReading {
        if !self.config.enabled {
            return mpt3::unavailable();
        }

        match self.read_page_7() {
            Ok(reading) => {
                let outcome = format!("available:{}", reading.label);
                if self.last_logged_outcome.as_deref() != Some(&outcome) {
                    log!(Info, "HBA temperature available via {}: {}C", reading.label, reading.celsius_or_null.unwrap_or_default());
                    self.last_logged_outcome = Some(outcome);
                }
                reading
            }
            Err(reason) => {
                if self.last_logged_outcome.as_deref() != Some(&reason) {
                    log!(Warning, "HBA temperature unavailable: {reason}");
                    self.last_logged_outcome = Some(reason);
                }
                self.reset();
                mpt3::unavailable()
            }
        }
    }

    #[cfg(not(target_os = "linux"))]
    fn read_page_7(&mut self) -> Result<SensorReading, String> {
        Err("mpt3ctl is only available on Linux".to_owned())
    }

    #[cfg(not(target_os = "linux"))]
    fn reset(&mut self) {}

    #[cfg(target_os = "linux")]
    fn reset(&mut self) {
        self.device = None;
        self.cached_header = None;
    }

    #[cfg(target_os = "linux")]
    fn read_page_7(&mut self) -> Result<SensorReading, String> {
        if self.device.is_none() {
            self.device = Some(linux::Device::open(&self.config.device_path)?);
        }
        let device = self.device.as_ref().expect("opened just above");
        let ioc_number = self.config.ioc_number;

        let header = match self.cached_header {
            Some(header) => header,
            None => {
                // Step 1: PAGE_HEADER, no data transfer. Firmware validates a READ_CURRENT's
                // declared PageVersion/PageLength against the real page, so those must come
                // from firmware itself (a zeroed header fails READ_CURRENT with IOCStatus
                // INVALID_SGL, confirmed on real hardware).
                let (reply, _) = device.config_request(ioc_number, mpt3::CONFIG_ACTION_PAGE_HEADER, PageHeader::BLANK_IO_UNIT_PAGE_7, 0)?;
                let header = mpt3::reply_header(&reply);
                if header.page_length == 0 {
                    return Err(
                        "Firmware returned PageLength=0 for IO Unit Page 7 on the PAGE_HEADER step: page not supported by this firmware."
                            .to_owned(),
                    );
                }

                self.cached_header = Some(header);
                header
            }
        };

        // Step 2: READ_CURRENT, echoing the header firmware gave us.
        let page_bytes = usize::from(header.page_length) * 4;
        let (_, page) = device.config_request(ioc_number, mpt3::CONFIG_ACTION_PAGE_READ_CURRENT, header, page_bytes)?;

        let reading = mpt3::parse_temperature(&page);
        if reading.is_available {
            Ok(reading)
        } else {
            Err("IO Unit Page 7 read succeeded, but both IOCTemperature and BoardTemperature report \"not present\": this card/firmware doesn't expose these sensors.".to_owned())
        }
    }
}

#[cfg(target_os = "linux")]
mod linux {
    use crate::mpt3::{self, PageHeader, REPLY_SIZE};
    use std::fs::{File, OpenOptions};
    use std::os::fd::AsRawFd;

    // mpt3sas_ctl.h: #define MPT3_MAGIC_NUMBER 'L'
    //                #define MPT3COMMAND _IOWR(MPT3_MAGIC_NUMBER, 20, struct mpt3_ioctl_command)
    // struct mpt3_ioctl_command on x86-64 is 72 bytes (12-byte header + u32 timeout + 4x
    // 8-byte pointers + 4x u32 + u32 data_sge_offset + 1-byte mf[], padded to the 8-byte
    // alignment the pointer members require).
    // _IOC encoding (asm-generic/ioctl.h): (dir<<30)|(size<<16)|(type<<8)|nr; dir for
    // _IOWR is _IOC_READ|_IOC_WRITE == 3.
    const MPT3COMMAND: u32 = (3 << 30) | (72 << 16) | ((b'L' as u32) << 8) | 20;

    const IOCTL_TIMEOUT_SECONDS: u32 = 5;

    /// Closed on drop.
    pub struct Device(File);

    impl Device {
        pub fn open(path: &str) -> Result<Self, String> {
            OpenOptions::new()
                .read(true)
                .write(true)
                .open(path)
                .map(Self)
                .map_err(|e| format!("open(\"{path}\") failed ({e}): device missing, module not loaded, or not running as root."))
        }

        /// Sends one MPT3COMMAND config request; returns the reply and `data_in_size` bytes of page data.
        pub fn config_request(
            &self,
            ioc_number: u32,
            action: u8,
            header: PageHeader,
            data_in_size: usize,
        ) -> Result<([u8; REPLY_SIZE], Vec<u8>), String> {
            let mut reply = [0u8; REPLY_SIZE];
            let mut data = vec![0u8; data_in_size];
            let data_address = if data.is_empty() { 0 } else { data.as_mut_ptr() as u64 };

            let mut request = mpt3::build_ioctl_buffer(
                ioc_number,
                action,
                header,
                reply.as_mut_ptr() as u64,
                data_address,
                data_in_size as u32,
                IOCTL_TIMEOUT_SECONDS,
            );

            // SAFETY: `request` is a correctly laid out struct mpt3_ioctl_command whose
            // embedded pointers refer to `reply` and `data`, both of which are live,
            // exclusively borrowed and at least as large as the sizes declared in the
            // request for the whole duration of the call. The request-number cast is to
            // libc's own ioctl request type, which is c_int on musl and c_ulong on glibc;
            // the kernel only looks at the low 32 bits either way.
            let result = unsafe { libc::ioctl(self.0.as_raw_fd(), MPT3COMMAND as libc::Ioctl, request.as_mut_ptr()) };
            if result < 0 {
                return Err(format!(
                    "MPT3COMMAND ioctl failed ({}, action 0x{action:02X}): wrong ioc_number ({ioc_number}), or the driver rejected the request.",
                    std::io::Error::last_os_error()
                ));
            }

            match mpt3::ioc_status(&reply) {
                0 => Ok((reply, data)),
                status => Err(format!("Config Page request (action 0x{action:02X}) returned non-success IOCStatus 0x{status:04X}.")),
            }
        }
    }
}
