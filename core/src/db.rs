//! Postgres for vigil-core: connection, schema, and every write. vigild keeps no history at
//! all, so this database is the only place any of it lives. vigil-core owns the schema;
//! vigil-web only reads it.
//!
//! Raw samples are narrow (one row per sensor per sample) so a sensor appearing or vanishing
//! needs no schema change. They are only kept for a few days; the *_minutes tables are the
//! long-term record and what every chart range beyond an hour reads from.
//!
//! A drive's temperature is stored twice, under two keys: "drive:<wwn>" follows the disk (its
//! own trend, wherever it is plugged in) and "port:<by-path>" follows the bay (the trend of
//! that spot in the case, whichever disk is in it). bay_occupants records which disk was in
//! which bay and when, so the two can be told apart after a swap.
//!
//! Timestamps cross the boundary as RFC 3339 text (`$n::text::timestamptz` in, epoch
//! milliseconds out), which keeps a date/time library out of the dependency tree.

use postgres::{Client, NoTls};
use std::str::FromStr;
use std::time::Duration;
use vigil_protocol::{DriveState, EventRecord, SensorReading, Severity, Snapshot, UpsReading, rfc3339_from_millis};

pub type Result<T> = std::result::Result<T, postgres::Error>;

pub fn connect(url: &str) -> std::result::Result<Client, String> {
    let mut config = postgres::Config::from_str(url).map_err(|error| format!("DATABASE_URL: {error}"))?;
    // A hung database must not freeze vigil-core's one state thread for long.
    config.connect_timeout(Duration::from_secs(10)).options("-c statement_timeout=15000");
    config.connect(NoTls).map_err(|error| error.to_string())
}

pub fn migrate(client: &mut Client) -> Result<()> {
    client.batch_execute(
        "
        set client_min_messages = warning;
        create table if not exists sensors (
            id text primary key,
            category text not null,
            label text not null,
            first_seen timestamptz not null default now(),
            last_seen timestamptz not null default now()
        );
        create table if not exists fans (
            id text primary key,
            first_seen timestamptz not null default now(),
            last_seen timestamptz not null default now()
        );
        create table if not exists sensor_samples (
            ts timestamptz not null,
            sensor_id text not null,
            celsius real not null,
            primary key (sensor_id, ts)
        );
        create index if not exists sensor_samples_ts on sensor_samples (ts);
        create table if not exists fan_samples (
            ts timestamptz not null,
            fan_id text not null,
            duty_percent smallint not null,
            rpm integer,
            primary key (fan_id, ts)
        );
        create index if not exists fan_samples_ts on fan_samples (ts);
        create table if not exists sensor_minutes (
            bucket timestamptz not null,
            sensor_id text not null,
            avg_celsius real not null,
            min_celsius real not null,
            max_celsius real not null,
            samples integer not null,
            primary key (sensor_id, bucket)
        );
        create index if not exists sensor_minutes_bucket on sensor_minutes (bucket);
        create table if not exists fan_minutes (
            bucket timestamptz not null,
            fan_id text not null,
            avg_duty real not null,
            avg_rpm real,
            min_rpm integer,
            max_rpm integer,
            samples integer not null,
            primary key (fan_id, bucket)
        );
        create index if not exists fan_minutes_bucket on fan_minutes (bucket);
        create table if not exists drives (
            wwn text primary key,
            passed boolean,
            reallocated bigint,
            pending bigint,
            power_on_hours bigint,
            source_path text not null,
            as_of timestamptz not null,
            first_seen timestamptz not null default now()
        );
        create table if not exists drive_health_history (
            id bigserial primary key,
            ts timestamptz not null,
            wwn text not null,
            passed boolean,
            reallocated bigint,
            pending bigint,
            power_on_hours bigint
        );
        create index if not exists drive_health_history_wwn_ts on drive_health_history (wwn, ts desc);
        alter table drives add column if not exists port text;
        create table if not exists bay_occupants (
            port text not null,
            wwn text not null,
            first_seen timestamptz not null default now(),
            last_seen timestamptz not null default now(),
            primary key (port, wwn)
        );
        create table if not exists events (
            id bigserial primary key,
            ts timestamptz not null default now(),
            severity text not null,
            kind text not null,
            message text not null
        );
        create index if not exists events_ts on events (ts desc);

        -- The UPS, from NUT. Wide rather than narrow: its handful of metrics always arrive together.
        create table if not exists ups_samples (
            ts timestamptz not null,
            ups text not null,
            status text not null,
            charge real,
            runtime_seconds integer,
            load real,
            real_power real,
            input_voltage real,
            output_voltage real,
            battery_voltage real,
            primary key (ups, ts)
        );
        create index if not exists ups_samples_ts on ups_samples (ts);
        create table if not exists ups_minutes (
            bucket timestamptz not null,
            ups text not null,
            avg_charge real,
            min_charge real,
            avg_runtime_seconds real,
            min_runtime_seconds integer,
            avg_load real,
            max_load real,
            avg_real_power real,
            avg_input_voltage real,
            min_input_voltage real,
            max_input_voltage real,
            avg_output_voltage real,
            on_battery_samples integer not null,
            samples integer not null,
            primary key (ups, bucket)
        );
        create index if not exists ups_minutes_bucket on ups_minutes (bucket);
        ",
    )
}

/// The history keys one reading is stored under: its own id, plus its bay for a drive with a known port.
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
        client.execute(
            "insert into sensor_samples (ts, sensor_id, celsius)
             select $1::text::timestamptz, id, value from unnest($2::text[], $3::real[]) as t(id, value)
             on conflict do nothing",
            &[&snapshot.timestamp_utc, &ids, &celsius],
        )?;
    }

    if !snapshot.fans.is_empty() {
        let ids: Vec<&str> = snapshot.fans.iter().map(|fan| fan.id.as_str()).collect();
        let duties: Vec<i16> = snapshot.fans.iter().map(|fan| i16::from(fan.duty_percent)).collect();
        let rpms: Vec<Option<i32>> = snapshot.fans.iter().map(|fan| fan.rpm.map(|rpm| rpm as i32)).collect();
        client.execute(
            "insert into fan_samples (ts, fan_id, duty_percent, rpm)
             select $1::text::timestamptz, id, duty, rpm from unnest($2::text[], $3::int2[], $4::int4[]) as t(id, duty, rpm)
             on conflict do nothing",
            &[&snapshot.timestamp_utc, &ids, &duties, &rpms],
        )?;
    }
    Ok(())
}

/// Records which sensors, bays and fans exist, when they were last seen, and which disk is in which bay.
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
        client.execute(
            "insert into sensors (id, category, label) select * from unnest($1::text[], $2::text[], $3::text[])
             on conflict (id) do update set category = excluded.category, label = excluded.label, last_seen = now()",
            &[&ids, &categories, &labels],
        )?;
    }

    let (mut ports, mut wwns) = (Vec::new(), Vec::new());
    for sensor in &snapshot.sensors {
        if let (Some(port), Some(wwn)) = (&sensor.port, sensor.id.strip_prefix("drive:")) {
            ports.push(port.clone());
            wwns.push(wwn.to_owned());
        }
    }
    if !ports.is_empty() {
        client.execute(
            "insert into bay_occupants (port, wwn) select * from unnest($1::text[], $2::text[])
             on conflict (port, wwn) do update set last_seen = now()",
            &[&ports, &wwns],
        )?;
    }

    let fans: Vec<&str> = snapshot.fans.iter().map(|fan| fan.id.as_str()).collect();
    if !fans.is_empty() {
        client
            .execute("insert into fans (id) select * from unnest($1::text[]) on conflict (id) do update set last_seen = now()", &[&fans])?;
    }
    Ok(())
}

fn category_name(sensor: &SensorReading) -> &'static str {
    use vigil_protocol::SensorCategory::*;
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
    let real = |value: Option<f64>| value.map(|v| v as f32);
    client.execute(
        "insert into ups_samples (ts, ups, status, charge, runtime_seconds, load, real_power, input_voltage, output_voltage, battery_voltage)
         values ($1::text::timestamptz, $2, $3, $4, $5, $6, $7, $8, $9, $10) on conflict do nothing",
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

/// Folds complete minutes of raw samples since `since` (RFC 3339) into the *_minutes tables.
/// Safe to re-run over the same window: it overwrites those buckets with the same result.
pub fn roll_up(client: &mut Client, since: &str) -> Result<()> {
    client.execute(
        "insert into sensor_minutes (bucket, sensor_id, avg_celsius, min_celsius, max_celsius, samples)
         select date_trunc('minute', ts), sensor_id, avg(celsius), min(celsius), max(celsius), count(*)
         from sensor_samples
         where ts >= date_trunc('minute', $1::text::timestamptz) and ts < date_trunc('minute', now())
         group by 1, 2
         on conflict (sensor_id, bucket) do update set
             avg_celsius = excluded.avg_celsius, min_celsius = excluded.min_celsius,
             max_celsius = excluded.max_celsius, samples = excluded.samples",
        &[&since],
    )?;
    client.execute(
        "insert into fan_minutes (bucket, fan_id, avg_duty, avg_rpm, min_rpm, max_rpm, samples)
         select date_trunc('minute', ts), fan_id, avg(duty_percent), avg(rpm), min(rpm), max(rpm), count(*)
         from fan_samples
         where ts >= date_trunc('minute', $1::text::timestamptz) and ts < date_trunc('minute', now())
         group by 1, 2
         on conflict (fan_id, bucket) do update set
             avg_duty = excluded.avg_duty, avg_rpm = excluded.avg_rpm,
             min_rpm = excluded.min_rpm, max_rpm = excluded.max_rpm, samples = excluded.samples",
        &[&since],
    )?;
    client.execute(
        "insert into ups_minutes (bucket, ups, avg_charge, min_charge, avg_runtime_seconds, min_runtime_seconds, avg_load, max_load,
             avg_real_power, avg_input_voltage, min_input_voltage, max_input_voltage, avg_output_voltage, on_battery_samples, samples)
         select date_trunc('minute', ts), ups, avg(charge), min(charge), avg(runtime_seconds), min(runtime_seconds), avg(load), max(load),
             avg(real_power), avg(input_voltage), min(input_voltage), max(input_voltage), avg(output_voltage),
             count(*) filter (where ' ' || status || ' ' like '% OB %'), count(*)
         from ups_samples
         where ts >= date_trunc('minute', $1::text::timestamptz) and ts < date_trunc('minute', now())
         group by 1, 2
         on conflict (ups, bucket) do update set
             avg_charge = excluded.avg_charge, min_charge = excluded.min_charge,
             avg_runtime_seconds = excluded.avg_runtime_seconds, min_runtime_seconds = excluded.min_runtime_seconds,
             avg_load = excluded.avg_load, max_load = excluded.max_load, avg_real_power = excluded.avg_real_power,
             avg_input_voltage = excluded.avg_input_voltage, min_input_voltage = excluded.min_input_voltage,
             max_input_voltage = excluded.max_input_voltage, avg_output_voltage = excluded.avg_output_voltage,
             on_battery_samples = excluded.on_battery_samples, samples = excluded.samples",
        &[&since],
    )?;
    Ok(())
}

pub fn prune_raw_samples(client: &mut Client, older_than: &str) -> Result<()> {
    for table in ["sensor_samples", "fan_samples", "ups_samples"] {
        client.execute(&format!("delete from {table} where ts < $1::text::timestamptz"), &[&older_than])?;
    }
    Ok(())
}

pub fn load_drives(client: &mut Client) -> Result<Vec<DriveState>> {
    let rows = client.query(
        "select wwn, port, passed, reallocated, pending, power_on_hours, source_path, (extract(epoch from as_of) * 1000)::int8
         from drives order by wwn",
        &[],
    )?;
    Ok(rows
        .iter()
        .map(|row| DriveState {
            wwn: row.get(0),
            port: row.get(1),
            passed: row.get(2),
            reallocated_sector_count: row.get::<_, Option<i64>>(3).map(|n| n as u64),
            pending_sector_count: row.get::<_, Option<i64>>(4).map(|n| n as u64),
            power_on_hours: row.get::<_, Option<i64>>(5).map(|n| n as u64),
            source_path: row.get(6),
            as_of: rfc3339_from_millis(row.get(7)),
        })
        .collect())
}

/// Stores a drive's new last-good state, and a history row if `changed` (anything but power-on hours moved).
pub fn save_drive(client: &mut Client, drive: &DriveState, changed: bool) -> Result<()> {
    let big = |value: Option<u64>| value.map(|n| n as i64);
    client.execute(
        "insert into drives (wwn, port, passed, reallocated, pending, power_on_hours, source_path, as_of)
         values ($1, $2, $3, $4, $5, $6, $7, $8::text::timestamptz)
         on conflict (wwn) do update set
             port = excluded.port, passed = excluded.passed, reallocated = excluded.reallocated, pending = excluded.pending,
             power_on_hours = excluded.power_on_hours, source_path = excluded.source_path, as_of = excluded.as_of",
        &[
            &drive.wwn,
            &drive.port,
            &drive.passed,
            &big(drive.reallocated_sector_count),
            &big(drive.pending_sector_count),
            &big(drive.power_on_hours),
            &drive.source_path,
            &drive.as_of,
        ],
    )?;

    if changed {
        client.execute(
            "insert into drive_health_history (ts, wwn, passed, reallocated, pending, power_on_hours)
             values ($1::text::timestamptz, $2, $3, $4, $5, $6)",
            &[
                &drive.as_of,
                &drive.wwn,
                &drive.passed,
                &big(drive.reallocated_sector_count),
                &big(drive.pending_sector_count),
                &big(drive.power_on_hours),
            ],
        )?;
    }
    Ok(())
}

pub fn insert_event(client: &mut Client, severity: Severity, kind: &str, message: &str) -> Result<EventRecord> {
    let row = client.query_one(
        "insert into events (severity, kind, message) values ($1, $2, $3)
         returning id, (extract(epoch from ts) * 1000)::int8",
        &[&severity_name(severity), &kind, &message],
    )?;
    Ok(EventRecord { id: row.get(0), ts: rfc3339_from_millis(row.get(1)), severity, kind: kind.to_owned(), message: message.to_owned() })
}

fn severity_name(severity: Severity) -> &'static str {
    match severity {
        Severity::Info => "info",
        Severity::Warning => "warning",
        Severity::Critical => "critical",
    }
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
