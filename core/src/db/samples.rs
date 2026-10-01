//! The sample writes: one row per sensor per poll, the fans, the UPS, and the inventory of
//! what exists.
//!
//! Raw samples are narrow (one row per sensor per sample) so a sensor appearing or vanishing
//! needs no schema change. A whole snapshot goes in as one statement per table, with the
//! columns passed as parallel arrays and expanded by `unnest` — fifteen sensors cost one
//! round trip, not fifteen.

use super::Result;
use postgres::Client;
use vigil_protocol::{SensorReading, Snapshot, UpsReading};

const INSERT_SENSOR_SAMPLES: &str = include_str!("../../sql/insert_sensor_samples.sql");
const INSERT_FAN_SAMPLES: &str = include_str!("../../sql/insert_fan_samples.sql");
const UPSERT_SENSORS: &str = include_str!("../../sql/upsert_sensors.sql");
const UPSERT_BAY_OCCUPANTS: &str = include_str!("../../sql/upsert_bay_occupants.sql");
const UPSERT_FANS: &str = include_str!("../../sql/upsert_fans.sql");
const INSERT_UPS_SAMPLE: &str = include_str!("../../sql/insert_ups_sample.sql");

/// The history keys one reading is stored under: its own id, plus its bay for a drive with a
/// known port.
///
/// A drive's temperature is stored twice. `drive:<wwn>` follows the disk — its own trend,
/// wherever it is plugged in. `port:<by-path>` follows the bay — the trend of that spot in the
/// case, whichever disk is in it. Two questions, two keys (ADR-009).
pub fn series_ids_of(sensor: &SensorReading) -> Vec<String> {
    match &sensor.port {
        Some(port) => vec![sensor.id.clone(), format!("port:{port}")],
        None => vec![sensor.id.clone()],
    }
}

pub fn insert_samples(client: &mut Client, snapshot: &Snapshot) -> Result<()> {
    let mut ids = Vec::new();
    let mut celsius = Vec::new();
    for sensor in &snapshot.sensors {
        let Some(value) = sensor.celsius_or_null else { continue };
        for id in series_ids_of(sensor) {
            ids.push(id);
            celsius.push(value as f32);
        }
    }
    if !ids.is_empty() {
        client.execute(INSERT_SENSOR_SAMPLES, &[&snapshot.timestamp_utc, &ids, &celsius])?;
    }

    if !snapshot.fans.is_empty() {
        let ids: Vec<&str> = snapshot.fans.iter().map(|fan| fan.id.as_str()).collect();
        let duties: Vec<i16> = snapshot.fans.iter().map(|fan| i16::from(fan.duty_percent)).collect();
        let rpms: Vec<Option<i32>> = snapshot.fans.iter().map(|fan| fan.rpm.map(|rpm| rpm as i32)).collect();
        client.execute(INSERT_FAN_SAMPLES, &[&snapshot.timestamp_utc, &ids, &duties, &rpms])?;
    }
    Ok(())
}

/// Records which sensors, bays and fans exist, when they were last seen, and which disk is in
/// which bay.
pub fn touch_inventory(client: &mut Client, snapshot: &Snapshot) -> Result<()> {
    let (mut ids, mut categories, mut labels) = (Vec::new(), Vec::new(), Vec::new());
    for sensor in &snapshot.sensors {
        ids.push(sensor.id.clone());
        categories.push(category_name(sensor).to_owned());
        labels.push(sensor.label.clone());
        if let Some(port) = &sensor.port {
            ids.push(format!("port:{port}"));
            categories.push("bay".to_owned());
            labels.push(port.clone());
        }
    }
    if !ids.is_empty() {
        client.execute(UPSERT_SENSORS, &[&ids, &categories, &labels])?;
    }

    let (mut ports, mut wwns) = (Vec::new(), Vec::new());
    for sensor in &snapshot.sensors {
        if let (Some(port), Some(wwn)) = (&sensor.port, sensor.id.strip_prefix("drive:")) {
            ports.push(port.clone());
            wwns.push(wwn.to_owned());
        }
    }
    if !ports.is_empty() {
        client.execute(UPSERT_BAY_OCCUPANTS, &[&ports, &wwns])?;
    }

    let fans: Vec<&str> = snapshot.fans.iter().map(|fan| fan.id.as_str()).collect();
    if !fans.is_empty() {
        client.execute(UPSERT_FANS, &[&fans])?;
    }
    Ok(())
}

/// The wire name of a sensor's category. Spelled out rather than derived from the enum, because
/// this value is stored and queried: a rename in Rust must not silently repartition history.
fn category_name(sensor: &SensorReading) -> &'static str {
    use vigil_protocol::SensorCategory::{BoardAmbient, Cpu, Drive, Gpu, Hba, Memory};
    match sensor.category {
        Cpu => "cpu",
        BoardAmbient => "boardAmbient",
        Drive => "drive",
        Gpu => "gpu",
        Memory => "memory",
        Hba => "hba",
    }
}

pub fn insert_ups_sample(client: &mut Client, reading: &UpsReading) -> Result<()> {
    // The columns are `real`, so the f64s the UPS reports are narrowed on the way in: a tenth
    // of a volt of precision is well inside what the hardware actually measures.
    let real = |value: Option<f64>| value.map(|v| v as f32);
    client.execute(
        INSERT_UPS_SAMPLE,
        &[
            &reading.timestamp_utc,
            &reading.name,
            &reading.status.join(" "),
            &real(reading.battery_charge),
            &reading.battery_runtime_seconds.map(|s| s.round() as i32),
            &real(reading.load),
            &real(reading.real_power),
            &real(reading.input_voltage),
            &real(reading.output_voltage),
            &real(reading.battery_voltage),
        ],
    )?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use vigil_protocol::SensorCategory;

    #[test]
    fn a_drive_is_stored_under_its_own_id_and_under_its_bay() {
        let drive = SensorReading::new("drive:naa.5000c500bae40598", SensorCategory::Drive, "l", Some(35.0), "p")
            .with_port(Some("pci-0000:01:00.1-ata-3".to_owned()));
        assert_eq!(series_ids_of(&drive), ["drive:naa.5000c500bae40598", "port:pci-0000:01:00.1-ata-3"]);
    }

    #[test]
    fn everything_else_and_a_drive_without_a_port_under_its_id_alone() {
        assert_eq!(series_ids_of(&SensorReading::new("cpu", SensorCategory::Cpu, "l", Some(40.0), "p")), ["cpu"]);
        assert_eq!(series_ids_of(&SensorReading::new("drive:naa.1", SensorCategory::Drive, "l", None, "p")), ["drive:naa.1"]);
    }
}
