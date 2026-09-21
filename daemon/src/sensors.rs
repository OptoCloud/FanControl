//! Sensor types, plus discovery and reading of hwmon temperature sensors.
//!
//! hwmonN numbering shifts across reboots and depends on module load order, so resolution
//! always goes by chip "name", never by a remembered hwmonN path. For drives even the
//! backing block device's sdX letter is unstable (it shifts across reboots and always
//! changes if the drive moves to another port), so drive identity is further resolved to
//! the drive's WWN (/sys/class/block/{dev}/device/wwid), confirmed present on this
//! hardware for both native-SATA (libata) and HBA-attached (mpt3sas) drives. The WWN is
//! burned into the drive, so it survives a port change: exactly the property a sensor id
//! that history gets keyed on needs.

use crate::sysfs::{self, SysFs};
use serde::Serialize;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum SensorCategory {
    Cpu,
    BoardAmbient,
    Drive,
    Gpu,
    Memory,
    Hba,
}

/// A single point-in-time reading. `id` is a stable logical name ("cpu", "drive:{wwn}"),
/// never a raw hwmonN path.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorReading {
    pub id: String,
    pub category: SensorCategory,
    pub label: String,
    pub celsius_or_null: Option<f64>,
    pub source_path: String,
    pub is_available: bool,
}

impl SensorReading {
    pub fn new(id: &str, category: SensorCategory, label: &str, celsius: Option<f64>, source_path: &str) -> Self {
        Self {
            id: id.to_owned(),
            category,
            label: label.to_owned(),
            celsius_or_null: celsius,
            source_path: source_path.to_owned(),
            is_available: celsius.is_some(),
        }
    }
}

/// Declarative description of one sensor (or sensor family) to pull out of hwmon. This is
/// the whitelist: only sensors described here are ever read, so unconnected or meaningless
/// inputs (AUXTIN, a floating CPUTIN, ...) never make it into a reading.
#[derive(Debug, Clone, Copy)]
pub struct SensorSpec {
    /// Stable logical id. For `all_instances` specs this is a prefix: the resolved id
    /// becomes "{id}:{stable name}".
    pub id: &'static str,
    pub category: SensorCategory,
    /// Exact match against the hwmon chip's "name" file.
    pub chip_name: &'static str,
    /// Matches one tempN_label within the chip. Ignored when `all_instances` is set.
    pub label: Option<&'static str>,
    /// Every hwmon instance of the chip contributes one sensor from its temp1_input.
    pub all_instances: bool,
}

pub const DRIVE_SPEC: SensorSpec =
    SensorSpec { id: "drive", category: SensorCategory::Drive, chip_name: "drivetemp", label: None, all_instances: true };

pub const DEFAULT_WHITELIST: [SensorSpec; 4] = [
    SensorSpec { id: "cpu", category: SensorCategory::Cpu, chip_name: "k10temp", label: Some("Tctl"), all_instances: false },
    SensorSpec { id: "board", category: SensorCategory::BoardAmbient, chip_name: "nct6798", label: Some("SYSTIN"), all_instances: false },
    DRIVE_SPEC,
    SensorSpec { id: "dimm", category: SensorCategory::Memory, chip_name: "jc42", label: None, all_instances: true },
];

/// A sensor spec bound to a concrete sysfs path at a point in time.
#[derive(Debug, Clone, PartialEq)]
pub struct ResolvedSensor {
    pub id: String,
    pub category: SensorCategory,
    pub label: String,
    pub temp_input_path: String,
    /// The live kernel block device name ("sdc"), drives only. NOT stable: it exists purely
    /// so something that has to operate on the device right now (smartctl) can, without
    /// that name leaking into anything that tracks a drive's identity over time.
    pub device_name: Option<String>,
}

pub struct HwmonResolver<'a> {
    sysfs: &'a dyn SysFs,
    hwmon_root: String,
    block_root: String,
}

impl<'a> HwmonResolver<'a> {
    /// `sysfs_root` is "/sys" everywhere except when running against a fake tree.
    pub fn new(sysfs: &'a dyn SysFs, sysfs_root: &str) -> Self {
        Self { sysfs, hwmon_root: format!("{sysfs_root}/class/hwmon"), block_root: format!("{sysfs_root}/class/block") }
    }

    pub fn resolve(&self, specs: &[SensorSpec]) -> Vec<ResolvedSensor> {
        let chips = self.chips();
        let mut resolved = Vec::new();

        for spec in specs {
            for (path, _) in chips.iter().filter(|(_, name)| name == spec.chip_name) {
                if spec.all_instances {
                    resolved.extend(self.resolve_instance(spec, path));
                } else if let Some(label) = spec.label {
                    resolved.extend(self.resolve_by_label(spec, path, label));
                }
            }
        }

        resolved
    }

    /// The hwmon directory of a named chip, for callers that need direct attribute access
    /// (fan control) rather than a temperature. None if the chip isn't present, which for
    /// a configured fan channel is fatal: better to refuse to start than to ignore it.
    pub fn chip_directory(&self, chip_name: &str) -> Option<String> {
        self.chips().into_iter().find(|(_, name)| name == chip_name).map(|(path, _)| path)
    }

    fn chips(&self) -> Vec<(String, String)> {
        self.sysfs
            .list_dirs(&self.hwmon_root)
            .into_iter()
            .filter(|dir| sysfs::file_name(dir).starts_with("hwmon"))
            .filter_map(|dir| {
                let name = self.sysfs.read(&sysfs::join(&[&dir, "name"]))?;
                Some((dir, name))
            })
            .collect()
    }

    fn resolve_instance(&self, spec: &SensorSpec, chip_path: &str) -> Option<ResolvedSensor> {
        // drivetemp/jc42 instances expose a single temp1_input per hwmon directory.
        let input_path = sysfs::join(&[chip_path, "temp1_input"]);
        if !self.sysfs.file_exists(&input_path) {
            return None;
        }

        let device_name = self.backing_device_name(chip_path);
        let stable_name = match &device_name {
            // A real block device (a drive): key it by WWN, not the live sdX name.
            Some(device) => self.sysfs.read(&sysfs::join(&[&self.block_root, device, "device", "wwid"])).unwrap_or_else(|| device.clone()),
            // No backing block device (jc42 DIMM sensors): nothing more stable exists
            // than the hwmon directory name.
            None => sysfs::file_name(chip_path).to_owned(),
        };

        Some(ResolvedSensor {
            id: format!("{}:{}", spec.id, stable_name),
            category: spec.category,
            label: stable_name,
            temp_input_path: input_path,
            device_name,
        })
    }

    fn resolve_by_label(&self, spec: &SensorSpec, chip_path: &str, wanted: &str) -> Option<ResolvedSensor> {
        // SysFs can only list directories, not files, and hwmon attribute counts are
        // small, so probe temp1_label..temp32_label directly.
        (1..=32).find_map(|index| {
            let label = self.sysfs.read(&sysfs::join(&[chip_path, &format!("temp{index}_label")]))?;
            if !label.eq_ignore_ascii_case(wanted) {
                return None;
            }

            let input_path = sysfs::join(&[chip_path, &format!("temp{index}_input")]);
            self.sysfs.file_exists(&input_path).then(|| ResolvedSensor {
                id: spec.id.to_owned(),
                category: spec.category,
                label,
                temp_input_path: input_path,
                device_name: None,
            })
        })
    }

    fn backing_device_name(&self, chip_path: &str) -> Option<String> {
        let block_dir = sysfs::join(&[chip_path, "device", "block"]);
        self.sysfs.list_dirs(&block_dir).first().map(|dir| sysfs::file_name(dir).to_owned())
    }
}

/// Reads the current value of an already-resolved hwmon sensor (millidegrees in sysfs).
pub fn read_sensor(sysfs: &dyn SysFs, sensor: &ResolvedSensor) -> SensorReading {
    let celsius =
        sysfs.read(&sensor.temp_input_path).and_then(|raw| raw.parse::<i64>().ok()).map(|millidegrees| millidegrees as f64 / 1000.0);

    SensorReading::new(&sensor.id, sensor.category, &sensor.label, celsius, &sensor.temp_input_path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sysfs::fake::FakeSysFs;

    const CPU: SensorSpec = DEFAULT_WHITELIST[0];
    const BOARD: SensorSpec = DEFAULT_WHITELIST[1];

    #[test]
    fn resolves_cpu_sensor_by_chip_and_label() {
        let sysfs = FakeSysFs::new()
            .with_file("/sys/class/hwmon/hwmon0/name", "k10temp")
            .with_file("/sys/class/hwmon/hwmon0/temp1_label", "Tctl")
            .with_file("/sys/class/hwmon/hwmon0/temp1_input", "44125");

        let resolved = HwmonResolver::new(&sysfs, "/sys").resolve(&[CPU]);

        assert_eq!(resolved.len(), 1);
        assert_eq!(resolved[0].id, "cpu");
        assert_eq!(resolved[0].temp_input_path, "/sys/class/hwmon/hwmon0/temp1_input");
    }

    #[test]
    fn resolves_drive_sensors_keyed_by_wwid_not_the_live_sdx_letter() {
        let sysfs = FakeSysFs::new()
            .with_file("/sys/class/hwmon/hwmon5/name", "drivetemp")
            .with_file("/sys/class/hwmon/hwmon5/temp1_input", "31000")
            .with_dir("/sys/class/hwmon/hwmon5/device/block/sda")
            .with_file("/sys/class/block/sda/device/wwid", "naa.5000c500aaaa0001")
            .with_file("/sys/class/hwmon/hwmon6/name", "drivetemp")
            .with_file("/sys/class/hwmon/hwmon6/temp1_input", "33000")
            .with_dir("/sys/class/hwmon/hwmon6/device/block/sdb")
            .with_file("/sys/class/block/sdb/device/wwid", "naa.5000c500bbbb0002");

        let resolved = HwmonResolver::new(&sysfs, "/sys").resolve(&[DRIVE_SPEC]);

        assert_eq!(resolved.len(), 2);
        assert!(resolved.iter().any(|r| r.id == "drive:naa.5000c500aaaa0001" && r.device_name.as_deref() == Some("sda")));
        assert!(resolved.iter().any(|r| r.id == "drive:naa.5000c500bbbb0002" && r.device_name.as_deref() == Some("sdb")));
    }

    #[test]
    fn falls_back_to_sdx_letter_when_wwid_file_is_missing() {
        let sysfs = FakeSysFs::new()
            .with_file("/sys/class/hwmon/hwmon5/name", "drivetemp")
            .with_file("/sys/class/hwmon/hwmon5/temp1_input", "31000")
            .with_dir("/sys/class/hwmon/hwmon5/device/block/sda");

        let resolved = HwmonResolver::new(&sysfs, "/sys").resolve(&[DRIVE_SPEC]);

        assert_eq!(resolved.len(), 1);
        assert_eq!(resolved[0].id, "drive:sda");
        assert_eq!(resolved[0].device_name.as_deref(), Some("sda"));
    }

    #[test]
    fn instance_without_a_block_device_is_keyed_by_hwmon_directory() {
        let sysfs =
            FakeSysFs::new().with_file("/sys/class/hwmon/hwmon3/name", "jc42").with_file("/sys/class/hwmon/hwmon3/temp1_input", "35000");

        let resolved = HwmonResolver::new(&sysfs, "/sys").resolve(&[DEFAULT_WHITELIST[3]]);

        assert_eq!(resolved[0].id, "dimm:hwmon3");
        assert_eq!(resolved[0].device_name, None);
    }

    #[test]
    fn ignores_labels_not_in_whitelist() {
        let sysfs = FakeSysFs::new()
            .with_file("/sys/class/hwmon/hwmon2/name", "nct6798")
            .with_file("/sys/class/hwmon/hwmon2/temp1_label", "AUXTIN0")
            .with_file("/sys/class/hwmon/hwmon2/temp1_input", "16000")
            .with_file("/sys/class/hwmon/hwmon2/temp2_label", "SYSTIN")
            .with_file("/sys/class/hwmon/hwmon2/temp2_input", "32000");

        let resolved = HwmonResolver::new(&sysfs, "/sys").resolve(&[BOARD]);

        assert_eq!(resolved.len(), 1);
        assert_eq!(resolved[0].id, "board");
        assert_eq!(resolved[0].temp_input_path, "/sys/class/hwmon/hwmon2/temp2_input");
    }

    #[test]
    fn returns_nothing_when_chip_is_absent() {
        let sysfs = FakeSysFs::new().with_file("/sys/class/hwmon/hwmon0/name", "k10temp");

        assert!(HwmonResolver::new(&sysfs, "/sys").resolve(&[BOARD]).is_empty());
        assert_eq!(HwmonResolver::new(&sysfs, "/sys").chip_directory("nct6798"), None);
        assert_eq!(HwmonResolver::new(&sysfs, "/sys").chip_directory("k10temp").as_deref(), Some("/sys/class/hwmon/hwmon0"));
    }

    #[test]
    fn reads_millidegrees_and_reports_missing_input_as_unavailable() {
        let sysfs = FakeSysFs::new()
            .with_file("/sys/class/hwmon/hwmon0/name", "k10temp")
            .with_file("/sys/class/hwmon/hwmon0/temp1_label", "Tctl")
            .with_file("/sys/class/hwmon/hwmon0/temp1_input", "44125");
        let sensor = HwmonResolver::new(&sysfs, "/sys").resolve(&[CPU]).remove(0);

        assert_eq!(read_sensor(&sysfs, &sensor).celsius_or_null, Some(44.125));

        sysfs.remove("/sys/class/hwmon/hwmon0/temp1_input");
        let reading = read_sensor(&sysfs, &sensor);
        assert_eq!(reading.celsius_or_null, None);
        assert!(!reading.is_available);
    }
}
