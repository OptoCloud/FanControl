//! The config file's shape (TOML) and its defaults.
//!
//! Validation lives in `validate.rs`, next door: it is the larger half and a different job.
//! What it guards is described there.

mod validate;

use serde::Deserialize;
use std::time::Duration;

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Config {
    pub poll_interval_secs: f64,
    /// If the control loop doesn't complete a poll within this window, every channel is
    /// released back to automatic control.
    pub deadman_timeout_secs: f64,
    /// hwmon nodes aren't permanent: a drive that resets or is hot-swapped comes back
    /// under a new hwmonN, and without a re-scan it would stay unavailable (and silently
    /// out of every drive:* curve) until the daemon restarted.
    pub sensor_rescan_interval_secs: f64,
    /// Where sysfs is mounted. Only ever changed to run the daemon against a fake tree
    /// (integration tests, trying a config on a machine without the hardware).
    pub sysfs_root: String,
    /// Where udev keeps its by-path links. A drive's port id is the shortest link here that
    /// points at it: a fixed physical location (controller PCI address plus port or phy),
    /// unlike the drive's WWN, which follows the disk wherever it is plugged in.
    pub disk_by_path_dir: String,
    pub api: ApiConfig,
    pub gpu: GpuConfig,
    pub hba: HbaConfig,
    pub drive_health: DriveHealthConfig,
    pub zones: Vec<ZoneConfig>,
    pub channels: Vec<ChannelConfig>,
    pub curves: Vec<CurveConfig>,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            poll_interval_secs: 2.0,
            deadman_timeout_secs: 30.0,
            sensor_rescan_interval_secs: 30.0,
            sysfs_root: "/sys".to_owned(),
            disk_by_path_dir: "/dev/disk/by-path".to_owned(),
            api: ApiConfig::default(),
            gpu: GpuConfig::default(),
            hba: HbaConfig::default(),
            drive_health: DriveHealthConfig::default(),
            zones: Vec::new(),
            channels: Vec::new(),
            curves: Vec::new(),
        }
    }
}

/// The status API listens on a unix socket only: this daemon runs as root with raw PWM and
/// ioctl access, so it never opens a network port. A containerised consumer gets the
/// socket's directory bind-mounted in.
#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct ApiConfig {
    pub enabled: bool,
    pub socket_path: String,
    /// Octal permission string for the socket file.
    pub socket_mode: String,
    /// Owner to chown the socket to. An unprivileged LXC maps its root to a high host uid
    /// (100000 by default), so that is what has to own the socket for the container to
    /// connect to it.
    pub socket_uid: Option<u32>,
    pub socket_gid: Option<u32>,
    /// Upper bound on simultaneous connections, so a misbehaving consumer can't exhaust threads.
    pub max_clients: usize,
}

impl Default for ApiConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            socket_path: "/run/vigil/vigild.sock".to_owned(),
            socket_mode: "0660".to_owned(),
            socket_uid: None,
            socket_gid: None,
            max_clients: 16,
        }
    }
}

impl ApiConfig {
    pub fn socket_mode_bits(&self) -> Option<u32> {
        u32::from_str_radix(self.socket_mode.trim_start_matches("0o"), 8).ok().filter(|mode| *mode <= 0o777)
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct GpuConfig {
    /// False on a host with no NVIDIA GPU: never spawns nvidia-smi, gpu is always unavailable.
    pub enabled: bool,
    pub nvidia_smi_path: String,
    /// nvidia-smi is a process spawn, far heavier than the sysfs reads the rest of a poll
    /// consists of, so its result is reused for this long. Zero queries on every poll.
    pub min_read_interval_secs: f64,
    /// nvidia-smi is killed and gpu reported unavailable past this. Counts against the deadman.
    pub timeout_secs: f64,
}

impl Default for GpuConfig {
    fn default() -> Self {
        Self { enabled: true, nvidia_smi_path: "nvidia-smi".to_owned(), min_read_interval_secs: 6.0, timeout_secs: 5.0 }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct HbaConfig {
    pub enabled: bool,
    pub device_path: String,
    /// Which IOC this is, per mpt3sas' enumeration order. 0 is correct for a single HBA.
    pub ioc_number: u32,
}

impl Default for HbaConfig {
    fn default() -> Self {
        Self { enabled: true, device_path: "/dev/mpt3ctl".to_owned(), ioc_number: 0 }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct DriveHealthConfig {
    pub enabled: bool,
    pub smartctl_path: String,
    /// Deliberately independent of and much slower than the control loop: SMART health
    /// barely changes, and running smartctl against every drive every 2s would be pure waste.
    pub poll_interval_secs: f64,
    /// Per drive: smartctl is killed and that drive reported unavailable past this.
    pub timeout_secs: f64,
}

impl Default for DriveHealthConfig {
    fn default() -> Self {
        Self { enabled: true, smartctl_path: "smartctl".to_owned(), poll_interval_secs: 900.0, timeout_secs: 60.0 }
    }
}

/// One fan header. chip_name + index are resolved against the live hwmon tree at startup
/// rather than storing a hwmonN path, because hwmon numbering isn't stable.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ChannelConfig {
    pub id: String,
    pub chip_name: String,
    /// The N in pwmN / fanN.
    pub index: i64,
    /// Duty below which this fan stalls. The curve result is never allowed under it.
    #[serde(default = "default_minimum_duty")]
    pub minimum_duty_percent: i64,
}

fn default_minimum_duty() -> i64 {
    20
}

/// A physical region of the case: the sensors that share the airflow of one fan or fan
/// group. Zones exist because a sensor's *kind* doesn't say where it sits: two SSDs may
/// be `drive:*` sensors but live next to the expansion cards, nowhere near the drive cage.
///
/// A member is a sensor id ("cpu", "drive:naa.5000c500bae40598") or a drive's port, written
/// "port:" plus its /dev/disk/by-path name ("port:pci-0000:01:00.1-ata-3"). Zones describe
/// airflow at a location, so ports are the right key for them: a port stays with the bay,
/// while a WWN follows the disk if it is moved or swapped.
///
/// Any member ending in "*" is a prefix wildcard ("drive:*", "port:pci-0000:03:00.0-sas-*").
/// One rule joins them: a member named explicitly in any zone is claimed by that zone and
/// left out of every other zone's wildcards, so "drive:*" stops covering a drive the moment
/// its port or WWN is listed in another zone.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ZoneConfig {
    pub id: String,
    pub sensor_ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CurveConfig {
    pub fan_channel_id: String,
    /// Zone ids whose members drive this fan. Combined with sensor_ids; at least one of
    /// the two must be non-empty.
    #[serde(default)]
    pub zones: Vec<String>,
    /// Sensor ids, aggregated by max. "prefix:*" matches every sensor id with that prefix.
    /// Wildcards here are raw: zone claims don't apply to them.
    #[serde(default)]
    pub sensor_ids: Vec<String>,
    /// [temperature_celsius, duty_percent] pairs, ascending by temperature.
    pub points: Vec<(f64, i64)>,
    #[serde(default = "default_hysteresis")]
    pub hysteresis_celsius: f64,
    #[serde(default = "default_fail_safe")]
    pub fail_safe_duty_percent: i64,
}

fn default_hysteresis() -> f64 {
    3.0
}

fn default_fail_safe() -> i64 {
    100
}

impl Config {
    pub fn parse(toml_text: &str) -> Result<Self, String> {
        toml::from_str(toml_text).map_err(|e| e.to_string())
    }

    pub fn poll_interval(&self) -> Duration {
        Duration::from_secs_f64(self.poll_interval_secs)
    }

    pub fn deadman_timeout(&self) -> Duration {
        Duration::from_secs_f64(self.deadman_timeout_secs)
    }
}

/// "drive:*", "port:pci-0000:03:00.0-sas-*": matches every sensor or port key with the
/// text before the "*" as its prefix.
pub fn is_wildcard(id: &str) -> bool {
    id.ends_with('*')
}
