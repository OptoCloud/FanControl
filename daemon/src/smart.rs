//! Drive SMART health via smartctl's JSON output.
//!
//! Always passes `-n standby`: smartctl then skips the query and returns immediately if
//! the drive is asleep, rather than spinning it up just to answer a health check. Health
//! polling must never be the thing that defeats a drive's spindown policy.
//!
//! smartctl's exit code is a bitmask with several unrelated meanings (parse errors,
//! prefailure attributes, self-test failures, ...) and is deliberately not used as a
//! success signal: whether usable data came back is decided entirely by whether the JSON
//! contains "smart_status".
//!
//! This reports exactly what each poll saw and nothing more. Remembering a sleeping
//! drive's last good result, and noticing that a sector count changed since last time,
//! both need history, and history is the consumer's job.

use crate::config::DriveHealthConfig;
use crate::process;
use crate::sensors::ResolvedSensor;
use serde::Serialize;
use serde_json::Value;
use std::time::Duration;

const ATA_ATTRIBUTE_REALLOCATED_SECTOR_COUNT: u64 = 5;
const ATA_ATTRIBUTE_CURRENT_PENDING_SECTOR_COUNT: u64 = 197;

/// `passed` mirrors smartctl's normalized overall-health flag, which works the same way
/// for ATA and SCSI/SAS drives. Everything else is ATA-attribute-specific and is simply
/// absent for a drive that doesn't report it (SAS drives, attribute 197 on most SSDs).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveHealth {
    /// Stable drive identity (WWN), NOT the live sdX letter.
    pub device_name: String,
    pub passed: Option<bool>,
    pub reallocated_sector_count: Option<u64>,
    pub pending_sector_count: Option<u64>,
    pub power_on_hours: Option<u64>,
    /// The live /dev/sdX path smartctl was run against for this read. Not stable, informational only.
    pub source_path: String,
    /// False when the drive was asleep (so deliberately not queried), or smartctl was
    /// missing, timed out, or printed something unusable.
    pub is_available: bool,
    /// When this poll ran (RFC 3339, UTC). Health is polled on a much slower cycle than
    /// the snapshot it is embedded in.
    pub as_of: String,
}

pub fn parse(stable_id: &str, json: &str, source_path: &str, as_of: &str) -> DriveHealth {
    let root: Value = serde_json::from_str(json).unwrap_or(Value::Null);

    let passed = root["smart_status"]["passed"].as_bool();

    // as_u64() is None for a negative, fractional or out-of-range number, so an odd value
    // just leaves that one field empty.
    let attribute = |wanted_id: u64| {
        root["ata_smart_attributes"]["table"]
            .as_array()?
            .iter()
            .find(|attribute| attribute["id"].as_u64() == Some(wanted_id))
            .and_then(|attribute| attribute["raw"]["value"].as_u64())
    };

    DriveHealth {
        device_name: stable_id.to_owned(),
        passed,
        reallocated_sector_count: attribute(ATA_ATTRIBUTE_REALLOCATED_SECTOR_COUNT),
        pending_sector_count: attribute(ATA_ATTRIBUTE_CURRENT_PENDING_SECTOR_COUNT),
        power_on_hours: root["power_on_time"]["hours"].as_u64(),
        source_path: source_path.to_owned(),
        is_available: passed.is_some(),
        as_of: as_of.to_owned(),
    }
}

/// Polls every given drive, in parallel: one slow or hung drive then costs its own timeout
/// rather than delaying the other twelve.
pub fn poll_all(config: &DriveHealthConfig, drives: &[ResolvedSensor], as_of: &str) -> Vec<DriveHealth> {
    let timeout = Duration::from_secs_f64(config.timeout_secs);

    std::thread::scope(|scope| {
        let polls: Vec<_> = drives
            .iter()
            .filter_map(|drive| Some((drive, drive.device_name.as_deref()?)))
            .map(|(drive, device_name)| {
                scope.spawn(move || {
                    let device_path = format!("/dev/{device_name}");
                    let output = process::run(&config.smartctl_path, &["-H", "-A", "-j", "-n", "standby", &device_path], timeout);
                    parse(&drive.label, output.as_ref().map_or("", |o| o.stdout.as_str()), &device_path, as_of)
                })
            })
            .collect();

        polls.into_iter().filter_map(|poll| poll.join().ok()).collect()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: &str = "2026-01-01T00:00:00Z";

    #[test]
    fn parses_healthy_ata_drive_with_attributes() {
        let json = r#"{
            "smart_status": { "passed": true },
            "ata_smart_attributes": { "table": [
                { "id": 5, "name": "Reallocated_Sector_Ct", "raw": { "value": 0 } },
                { "id": 197, "name": "Current_Pending_Sector", "raw": { "value": 0 } },
                { "id": 9, "name": "Power_On_Hours", "raw": { "value": 12345 } }
            ] },
            "power_on_time": { "hours": 8760 }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert!(health.is_available);
        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, Some(0));
        assert_eq!(health.pending_sector_count, Some(0));
        assert_eq!(health.power_on_hours, Some(8760));
        assert_eq!((health.device_name.as_str(), health.source_path.as_str(), health.as_of.as_str()), ("naa.1", "/dev/sda", NOW));
    }

    #[test]
    fn parses_failing_drive_and_non_zero_sector_counts() {
        let json = r#"{
            "smart_status": { "passed": false },
            "ata_smart_attributes": { "table": [
                { "id": 5, "raw": { "value": 1048 } },
                { "id": 197, "raw": { "value": 8 } }
            ] }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!(health.passed, Some(false));
        assert!(health.is_available);
        assert_eq!(health.reallocated_sector_count, Some(1048));
        assert_eq!(health.pending_sector_count, Some(8));
    }

    #[test]
    fn scsi_drive_without_ata_attributes_still_reports_passed() {
        let json = r#"{ "smart_status": { "passed": true }, "power_on_time": { "hours": 100 } }"#;

        let health = parse("naa.1", json, "/dev/sdf", NOW);

        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, None);
        assert_eq!(health.power_on_hours, Some(100));
    }

    #[test]
    fn unavailable_when_smart_status_is_missing_malformed_or_empty() {
        // First case: what `-n standby` prints for a sleeping drive.
        for json in [r#"{ "smartctl": { "exit_status": 2 } }"#, "{ not json", "", r#"{ "smart_status": { "passed": "yes" } }"#] {
            let health = parse("naa.1", json, "/dev/sda", NOW);

            assert!(!health.is_available, "{json}");
            assert_eq!(health.passed, None);
        }
    }

    #[test]
    fn skips_numbers_that_do_not_fit_instead_of_failing() {
        let json = r#"{
            "smart_status": { "passed": true },
            "ata_smart_attributes": { "table": [
                { "id": 5.5, "raw": { "value": 1 } },
                { "id": 5, "raw": { "value": -1 } },
                { "id": 197, "raw": { "value": 1e40 } },
                "not an object"
            ] },
            "power_on_time": { "hours": 12.5 }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, None);
        assert_eq!(health.pending_sector_count, None);
        assert_eq!(health.power_on_hours, None);
    }

    #[test]
    fn poll_all_reports_every_drive_unavailable_when_smartctl_is_missing() {
        let config = DriveHealthConfig { smartctl_path: "definitely-not-smartctl-4f1c".to_owned(), ..DriveHealthConfig::default() };
        let drive = |wwn: &str, device: Option<&str>| ResolvedSensor {
            id: format!("drive:{wwn}"),
            category: crate::sensors::SensorCategory::Drive,
            label: wwn.to_owned(),
            temp_input_path: String::new(),
            device_name: device.map(str::to_owned),
        };

        let results = poll_all(&config, &[drive("naa.1", Some("sda")), drive("naa.2", Some("sdb")), drive("dimm", None)], NOW);

        assert_eq!(results.len(), 2);
        assert!(results.iter().all(|r| !r.is_available));
        assert_eq!(results[1].device_name, "naa.2");
        assert_eq!(results[1].source_path, "/dev/sdb");
    }
}
