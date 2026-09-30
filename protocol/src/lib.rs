//! The types that cross vigil's process boundaries, defined once:
//!
//! - vigild's snapshot, served on its socket's `/status` and `/events` and read by vigil-core;
//! - vigil-core's live stream (`/live`), read by vigil-web.
//!
//! Field names are the JSON contract (camelCase on the wire). vigil-web's TypeScript types
//! in web/src/lib/types.ts mirror these; treat a rename here as a change to both.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::time::{SystemTime, UNIX_EPOCH};

// ---------------------------------------------------------------- vigild's snapshot

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
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
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorReading {
    pub id: String,
    pub category: SensorCategory,
    pub label: String,
    pub celsius_or_null: Option<f64>,
    pub source_path: String,
    pub is_available: bool,
    /// Drives only: the by-path name of the port the drive is plugged into. Null for every
    /// other sensor, and for a drive when no by-path link points at it.
    #[serde(default)]
    pub port: Option<String>,
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
            port: None,
        }
    }

    pub fn with_port(mut self, port: Option<String>) -> Self {
        self.port = port;
        self
    }
}

/// Values accepted by nct6775/nct6798's pwmN_enable sysfs attribute.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PwmMode {
    /// Fans jump to full speed. Never written by vigild.
    Disabled = 0,
    Manual = 1,
    ThermalCruise = 2,
    SpeedCruise = 3,
    /// NCT6775F only; listed so a read-back of it isn't reported as unknown.
    SmartFanIII = 4,
    /// BIOS "Smart Fan IV": the multi-slope curve mode the board ships in.
    SmartFanIV = 5,
}

impl PwmMode {
    pub fn from_raw(raw: &str) -> Option<Self> {
        match raw.parse::<u8>().ok()? {
            0 => Some(Self::Disabled),
            1 => Some(Self::Manual),
            2 => Some(Self::ThermalCruise),
            3 => Some(Self::SpeedCruise),
            4 => Some(Self::SmartFanIII),
            5 => Some(Self::SmartFanIV),
            _ => None,
        }
    }

    /// True for the modes where the chip itself regulates the fan.
    pub fn is_automatic(self) -> bool {
        !matches!(self, Self::Disabled | Self::Manual)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FanStatus {
    pub id: String,
    pub duty_percent: u8,
    pub rpm: Option<u32>,
    /// None if pwmN_enable couldn't be read or held a value vigild doesn't know.
    pub mode: Option<PwmMode>,
    /// Reading 0 RPM for several consecutive polls while being driven.
    pub stalled: bool,
}

/// `passed` mirrors smartctl's normalized overall-health flag, which works the same way
/// for ATA and SCSI/SAS drives. Everything else is ATA-attribute-specific and is simply
/// absent for a drive that doesn't report it (SAS drives, attribute 197 on most SSDs).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveHealth {
    /// Stable drive identity (WWN), NOT the live sdX letter.
    pub device_name: String,
    /// The by-path name of the port the drive was plugged into for this poll, if known.
    #[serde(default)]
    pub port: Option<String>,
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

/// The full read-only view of vigild's state. This is the contract consumers build on:
/// treat a field rename here as a breaking change.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub timestamp_utc: String,
    pub sensors: Vec<SensorReading>,
    pub fans: Vec<FanStatus>,
    pub drive_health: Vec<DriveHealth>,
    /// False when any channel couldn't be driven this poll, or (judged at read time) when
    /// this snapshot is older than the deadman timeout, i.e. the loop has hung.
    pub control_loop_healthy: bool,
}

// ---------------------------------------------------------------- vigil-core's live stream

/// One poll of a UPS's variables (`LIST VAR <ups>` on upsd). The named fields are the
/// handful vigil charts and alerts on; `variables` is everything upsd reported, verbatim.
/// Any of them is None when this UPS or driver doesn't provide it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpsReading {
    pub timestamp_utc: String,
    /// The UPS's name on upsd, e.g. "apc".
    pub name: String,
    pub model: Option<String>,
    /// ups.status split into its flags: OL, OB, LB, HB, RB, CHRG, DISCHRG, BYPASS, CAL, OFF, OVER, TRIM, BOOST, FSD.
    pub status: Vec<String>,
    /// Percent.
    pub battery_charge: Option<f64>,
    pub battery_runtime_seconds: Option<f64>,
    /// Percent of the UPS's capacity.
    pub load: Option<f64>,
    /// Watts, derived from load and ups.realpower.nominal when the UPS doesn't report it directly.
    pub real_power: Option<f64>,
    pub input_voltage: Option<f64>,
    pub output_voltage: Option<f64>,
    pub battery_voltage: Option<f64>,
    pub variables: BTreeMap<String, String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpsState {
    /// False when NUT isn't configured: the UPS section is hidden, not shown as broken.
    pub enabled: bool,
    /// The UPS's name on upsd, which its history is stored under.
    pub name: String,
    /// The latest reading, or None while upsd can't be reached or has no fresh data for the UPS.
    pub reading: Option<UpsReading>,
    /// Why there is no reading: a connection error, or upsd's own (DATA-STALE, DRIVER-NOT-CONNECTED, UNKNOWN-UPS).
    pub error: Option<String>,
}

/// A drive's last GOOD health result. vigild only reports what its latest poll saw, and a
/// sleeping drive isn't woken, so this is what survives those gaps.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveState {
    pub wwn: String,
    /// The bay it was in when it last answered.
    pub port: Option<String>,
    pub passed: Option<bool>,
    pub reallocated_sector_count: Option<u64>,
    pub pending_sector_count: Option<u64>,
    pub power_on_hours: Option<u64>,
    pub source_path: String,
    /// When the drive last actually answered.
    pub as_of: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Severity {
    Info,
    Warning,
    Critical,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EventRecord {
    pub id: i64,
    pub ts: String,
    pub severity: Severity,
    /// Stable key for the condition, e.g. "fan-stalled:drive-cage".
    pub kind: String,
    pub message: String,
}

/// What vigil-core streams on `/live`: the current state on connect, then every change.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum LiveMessage {
    Snapshot { snapshot: Box<Snapshot> },
    Daemon { connected: bool },
    Event { event: EventRecord },
    Drives { drives: Vec<DriveState> },
    Ups { ups: Box<UpsState> },
}

// ---------------------------------------------------------------- time

/// Current time as RFC 3339 UTC with millisecond precision, e.g. "2026-09-21T20:39:27.482Z".
pub fn now_rfc3339() -> String {
    rfc3339(SystemTime::now())
}

pub fn rfc3339(time: SystemTime) -> String {
    let since_epoch = time.duration_since(UNIX_EPOCH).unwrap_or_default();
    rfc3339_from_millis(since_epoch.as_millis() as i64)
}

/// Milliseconds since the Unix epoch as RFC 3339 UTC. Before 1970 is clamped to the epoch.
pub fn rfc3339_from_millis(millis: i64) -> String {
    let millis = millis.max(0) as u64;
    let seconds = millis / 1000;
    let (days, second_of_day) = (seconds / 86_400, seconds % 86_400);

    // Days since 1970-01-01 to a proleptic Gregorian date (Howard Hinnant's civil_from_days).
    let shifted = days + 719_468;
    let era = shifted / 146_097;
    let day_of_era = shifted % 146_097;
    let year_of_era = (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 { month_index + 3 } else { month_index - 9 };
    let year = year_of_era + era * 400 + u64::from(month <= 2);

    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:03}Z",
        second_of_day / 3_600,
        second_of_day % 3_600 / 60,
        second_of_day % 60,
        millis % 1000
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn formats_known_instants() {
        let at = |seconds: u64, millis: u32| UNIX_EPOCH + Duration::new(seconds, millis * 1_000_000);

        assert_eq!(rfc3339(at(0, 0)), "1970-01-01T00:00:00.000Z");
        assert_eq!(rfc3339(at(951_782_400, 0)), "2000-02-29T00:00:00.000Z"); // leap day
        assert_eq!(rfc3339(at(1_790_023_167, 482)), "2026-09-21T20:39:27.482Z");
        assert_eq!(rfc3339(at(4_102_444_799, 999)), "2099-12-31T23:59:59.999Z");
        assert_eq!(rfc3339_from_millis(-5), "1970-01-01T00:00:00.000Z");
    }

    #[test]
    fn live_messages_carry_their_type_beside_the_payload() {
        let json = serde_json::to_string(&LiveMessage::Daemon { connected: true }).unwrap();
        assert_eq!(json, r#"{"type":"daemon","connected":true}"#);

        let ups = UpsState { enabled: false, name: "apc".to_owned(), reading: None, error: None };
        let json = serde_json::to_string(&LiveMessage::Ups { ups: Box::new(ups) }).unwrap();
        assert_eq!(json, r#"{"type":"ups","ups":{"enabled":false,"name":"apc","reading":null,"error":null}}"#);
    }

    #[test]
    fn severities_are_lowercase_on_the_wire() {
        assert_eq!(serde_json::to_string(&Severity::Critical).unwrap(), r#""critical""#);
    }

    #[test]
    fn a_snapshot_round_trips_and_tolerates_a_daemon_without_ports() {
        let json = r#"{"timestampUtc":"t","sensors":[{"id":"cpu","category":"cpu","label":"Tctl","celsiusOrNull":40.5,"sourcePath":"p","isAvailable":true}],
            "fans":[{"id":"f","dutyPercent":50,"rpm":null,"mode":"smartFanIV","stalled":false}],"driveHealth":[],"controlLoopHealthy":true}"#;
        let snapshot: Snapshot = serde_json::from_str(json).unwrap();

        assert_eq!(snapshot.sensors[0].port, None);
        assert_eq!(snapshot.fans[0].mode, Some(PwmMode::SmartFanIV));
        assert_eq!(serde_json::from_str::<Snapshot>(&serde_json::to_string(&snapshot).unwrap()).unwrap(), snapshot);
    }
}
