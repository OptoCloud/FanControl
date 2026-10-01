//! The wire contract, as a file both languages read.
//!
//! `protocol/src/lib.rs` and `web/src/lib/types.ts` are the same JSON contract written twice,
//! once per language. A comment asking for both to be changed together is not an enforcer, so
//! this module generates `protocol/contract.json` from the Rust types and fails if the file on
//! disk is out of date. `web/src/lib/types.contract.test.ts` reads that same file and checks
//! the TypeScript side against it, in both directions.
//!
//! Adding or renaming a field is therefore a three-part change in one commit: the Rust type,
//! the regenerated contract file (`cargo test -p vigil-protocol` writes it), and the
//! TypeScript type. Miss any one and CI fails.

use crate::limits;
use crate::{
    DriveHealth, DriveState, EventRecord, FanStatus, LiveMessage, PwmMode, SensorCategory, SensorReading, Severity, Snapshot, UpsReading,
    UpsState,
};
use serde_json::{Map, Value, json};
use std::collections::BTreeMap;

const CONTRACT_PATH: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/contract.json");

// Every variant of every wire enum. The `_exhaustive` guards below stop compiling when a
// variant is added without being listed here, which is the whole point of them.
const SENSOR_CATEGORIES: [SensorCategory; 6] = [
    SensorCategory::Cpu,
    SensorCategory::BoardAmbient,
    SensorCategory::Drive,
    SensorCategory::Gpu,
    SensorCategory::Memory,
    SensorCategory::Hba,
];
const PWM_MODES: [PwmMode; 6] =
    [PwmMode::Disabled, PwmMode::Manual, PwmMode::ThermalCruise, PwmMode::SpeedCruise, PwmMode::SmartFanIII, PwmMode::SmartFanIV];
const SEVERITIES: [Severity; 3] = [Severity::Info, Severity::Warning, Severity::Critical];

fn _exhaustive_sensor_category(value: SensorCategory) {
    use SensorCategory::{BoardAmbient, Cpu, Drive, Gpu, Hba, Memory};
    match value {
        Cpu | BoardAmbient | Drive | Gpu | Memory | Hba => (),
    }
}

fn _exhaustive_pwm_mode(value: PwmMode) {
    use PwmMode::{Disabled, Manual, SmartFanIII, SmartFanIV, SpeedCruise, ThermalCruise};
    match value {
        Disabled | Manual | ThermalCruise | SpeedCruise | SmartFanIII | SmartFanIV => (),
    }
}

fn _exhaustive_severity(value: Severity) {
    use Severity::{Critical, Info, Warning};
    match value {
        Info | Warning | Critical => (),
    }
}

fn _exhaustive_live_message(value: &LiveMessage) {
    use LiveMessage::{Daemon, Drives, Event, Snapshot, Ups};
    match value {
        Snapshot { .. } | Daemon { .. } | Event { .. } | Drives { .. } | Ups { .. } => (),
    }
}

/// Every field populated, including the optional ones: a `None` would serialize to `null` and
/// tell the other side nothing about the field's type.
fn samples() -> Value {
    let sensor = SensorReading {
        id: "drive:naa.5000c500bae40598".to_owned(),
        category: SensorCategory::Drive,
        label: "naa.5000c500bae40598".to_owned(),
        celsius_or_null: Some(35.5),
        source_path: "/sys/class/hwmon/hwmon4/temp1_input".to_owned(),
        is_available: true,
        port: Some("pci-0000:01:00.1-ata-3".to_owned()),
    };
    let fan = FanStatus { id: "drive-cage".to_owned(), duty_percent: 65, rpm: Some(1211), mode: Some(PwmMode::Manual), stalled: false };
    let drive_health = DriveHealth {
        device_name: "naa.5000c500bae40598".to_owned(),
        port: Some("pci-0000:01:00.1-ata-3".to_owned()),
        passed: Some(true),
        reallocated_sector_count: Some(0),
        pending_sector_count: Some(0),
        power_on_hours: Some(8760),
        source_path: "/dev/sdc".to_owned(),
        is_available: true,
        as_of: "2026-09-21T23:00:38.412Z".to_owned(),
    };
    let snapshot = Snapshot {
        timestamp_utc: "2026-09-21T23:01:34.155Z".to_owned(),
        sensors: vec![sensor.clone()],
        fans: vec![fan.clone()],
        drive_health: vec![drive_health.clone()],
        control_loop_healthy: true,
    };
    let ups_reading = UpsReading {
        timestamp_utc: "2026-09-21T23:01:34.155Z".to_owned(),
        name: "apc".to_owned(),
        model: Some("American Power Conversion Smart-UPS 1000".to_owned()),
        status: vec!["OL".to_owned()],
        battery_charge: Some(100.0),
        battery_runtime_seconds: Some(2310.0),
        load: Some(25.0),
        real_power: Some(168.0),
        input_voltage: Some(231.4),
        output_voltage: Some(230.0),
        battery_voltage: Some(27.3),
        variables: BTreeMap::from([("ups.status".to_owned(), "OL".to_owned())]),
    };
    let ups_state =
        UpsState { enabled: true, name: "apc".to_owned(), reading: Some(ups_reading.clone()), error: Some("upsd: DATA-STALE".to_owned()) };
    let drive_state = DriveState {
        wwn: "naa.5000c500bae40598".to_owned(),
        port: Some("pci-0000:01:00.1-ata-3".to_owned()),
        passed: Some(true),
        reallocated_sector_count: Some(0),
        pending_sector_count: Some(0),
        power_on_hours: Some(8760),
        source_path: "/dev/sdc".to_owned(),
        as_of: "2026-09-21T23:00:38.412Z".to_owned(),
    };
    let event = EventRecord {
        id: 1,
        ts: "2026-09-21T23:01:34.155Z".to_owned(),
        severity: Severity::Warning,
        kind: "fan-stalled:drive-cage".to_owned(),
        message: "drive-cage is being driven at 65% but reads 0 RPM".to_owned(),
    };

    let live_messages: Map<String, Value> = [
        LiveMessage::Snapshot { snapshot: Box::new(snapshot.clone()) },
        LiveMessage::Daemon { connected: true },
        LiveMessage::Event { event: event.clone() },
        LiveMessage::Drives { drives: vec![drive_state.clone()] },
        LiveMessage::Ups { ups: Box::new(ups_state.clone()) },
    ]
    .iter()
    .map(|message| {
        let value = serde_json::to_value(message).expect("a wire type must serialize");
        let tag = value["type"].as_str().expect("every LiveMessage carries a type tag").to_owned();
        (tag, value)
    })
    .collect();

    json!({
        "$comment": "GENERATED by `cargo test -p vigil-protocol` from protocol/src/lib.rs. \
                     Do not edit by hand. web/src/lib/types.contract.test.ts checks the \
                     TypeScript mirror against this file. See docs/STYLE.md §1.3.",
        "enums": {
            "SensorCategory": SENSOR_CATEGORIES,
            "PwmMode": PWM_MODES,
            "Severity": SEVERITIES,
        },
        "types": {
            "SensorReading": sensor,
            "FanStatus": fan,
            "DriveHealth": drive_health,
            "Snapshot": snapshot,
            "UpsReading": ups_reading,
            "UpsState": ups_state,
            "DriveState": drive_state,
            "EventRecord": event,
        },
        "liveMessages": live_messages,
        // Mirrored for web/src/lib/limits.ts, so a value crossing the language boundary is
        // declared once (protocol/src/limits.rs) rather than once per language.
        "limits": {
            "streamKeepaliveSeconds": limits::STREAM_KEEPALIVE_SECONDS,
            "streamSilenceTimeoutSeconds": limits::STREAM_SILENCE_TIMEOUT_SECONDS,
            "coreDefaultPort": limits::CORE_DEFAULT_PORT,
            "databaseUrlDefault": limits::DATABASE_URL_DEFAULT,
            "databaseStatementTimeoutMillis": limits::DATABASE_STATEMENT_TIMEOUT_MILLIS,
        },
    })
}

#[test]
fn contract_json_is_current() {
    let expected = format!("{}\n", serde_json::to_string_pretty(&samples()).expect("the contract must serialize"));
    let actual = std::fs::read_to_string(CONTRACT_PATH).unwrap_or_default();

    if actual != expected {
        std::fs::write(CONTRACT_PATH, &expected).expect("could not write the contract file");
        panic!(
            "protocol/contract.json was out of date and has been regenerated.\n\
             Commit it, and make the matching change to web/src/lib/types.ts.\n\
             See docs/STYLE.md §1.3."
        );
    }
}

#[test]
fn every_sample_populates_every_optional_field() {
    // A null in the contract would tell the TypeScript side nothing about that field's type,
    // so the generator must never emit one.
    fn find_null(value: &Value, path: &str, found: &mut Vec<String>) {
        match value {
            Value::Null => found.push(path.to_owned()),
            Value::Object(map) => {
                for (key, child) in map {
                    find_null(child, &format!("{path}.{key}"), found);
                }
            }
            Value::Array(items) => {
                for (index, child) in items.iter().enumerate() {
                    find_null(child, &format!("{path}[{index}]"), found);
                }
            }
            _ => {}
        }
    }

    let mut nulls = Vec::new();
    find_null(&samples(), "contract", &mut nulls);
    assert!(nulls.is_empty(), "the contract must populate every field, but these are null: {nulls:?}");
}
