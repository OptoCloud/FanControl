//! History's shape over time, handed to TimescaleDB: the raw sample tables are hypertables,
//! continuous aggregates keep 1-minute and 1-hour rollups, and background jobs compress and
//! drop old data. vigil-core only inserts raw samples; everything after that happens in the
//! database on its own schedule.
//!
//! | Level  | Built by                                    | Kept                        |
//! |--------|---------------------------------------------|-----------------------------|
//! | raw    | vigil-core, every PERSIST_INTERVAL_SECONDS  | RAW_RETENTION_DAYS (30), compressed after 7 days |
//! | 1 min  | *_1m, a continuous aggregate of raw         | 365 days                    |
//! | 1 hour | *_1h, a continuous aggregate of *_1m        | forever                     |
//!
//! Every step is idempotent, so this runs on every start. The extension itself is created
//! by CT 300's recipe: it needs a superuser, and vigil-core's role is not one.
//!
//! The one-time work (converting tables, copying old history, filling the aggregates over all
//! time) can take far longer than vigil-core's 15-second statement timeout, so it runs without
//! one. A finished fill is recorded in vigil_state; until that row exists every start fills
//! again, so an interrupted fill is resumed, never silently skipped.

use postgres::Client;
use std::time::Duration;

/// Minute rollups are kept this long; hourly ones forever.
const MINUTE_RETENTION: &str = "365 days";
/// Raw chunks older than this are compressed (they are only read by the 1h and 6h charts).
const COMPRESS_AFTER: &str = "7 days";

struct Series {
    raw: &'static str,
    key: &'static str,
    /// The 1-minute aggregate over raw, grouped by key.
    minute: &'static str,
    /// The 1-hour aggregate over the 1-minute one.
    hour: &'static str,
    /// Copies the pre-TimescaleDB minute table (if any) into raw as one sample per minute, for
    /// the time before the earliest raw sample, so the aggregates carry the old history on.
    backfill: &'static str,
    legacy: &'static str,
}

const SERIES: [Series; 3] = [
    Series {
        raw: "sensor_samples",
        key: "sensor_id",
        minute: "select time_bucket('1 minute', ts) as bucket, sensor_id,
                    avg(celsius) as avg_celsius, min(celsius) as min_celsius, max(celsius) as max_celsius, count(*) as samples
                 from sensor_samples group by 1, 2",
        hour: "select time_bucket('1 hour', bucket) as bucket, sensor_id,
                   sum(avg_celsius * samples) / sum(samples) as avg_celsius, min(min_celsius) as min_celsius,
                   max(max_celsius) as max_celsius, sum(samples) as samples
               from sensor_1m group by 1, 2",
        backfill: "insert into sensor_samples (ts, sensor_id, celsius)
                   select bucket, sensor_id, avg_celsius from sensor_minutes
                   where bucket < coalesce((select min(ts) from sensor_samples), 'infinity')
                   on conflict do nothing",
        legacy: "sensor_minutes",
    },
    Series {
        raw: "fan_samples",
        key: "fan_id",
        minute: "select time_bucket('1 minute', ts) as bucket, fan_id,
                    avg(duty_percent) as avg_duty, avg(rpm) as avg_rpm, min(rpm) as min_rpm, max(rpm) as max_rpm, count(*) as samples
                 from fan_samples group by 1, 2",
        hour: "select time_bucket('1 hour', bucket) as bucket, fan_id,
                   sum(avg_duty * samples) / sum(samples) as avg_duty,
                   sum(avg_rpm * samples) / nullif(sum(case when avg_rpm is not null then samples else 0 end), 0) as avg_rpm,
                   min(min_rpm) as min_rpm, max(max_rpm) as max_rpm, sum(samples) as samples
               from fan_1m group by 1, 2",
        backfill: "insert into fan_samples (ts, fan_id, duty_percent, rpm)
                   select bucket, fan_id, round(avg_duty)::int2, round(avg_rpm)::int4 from fan_minutes
                   where bucket < coalesce((select min(ts) from fan_samples), 'infinity')
                   on conflict do nothing",
        legacy: "fan_minutes",
    },
    Series {
        raw: "ups_samples",
        key: "ups",
        minute: "select time_bucket('1 minute', ts) as bucket, ups,
                    avg(charge) as avg_charge, min(charge) as min_charge,
                    avg(runtime_seconds) as avg_runtime_seconds, min(runtime_seconds) as min_runtime_seconds,
                    avg(load) as avg_load, max(load) as max_load, avg(real_power) as avg_real_power,
                    avg(input_voltage) as avg_input_voltage, min(input_voltage) as min_input_voltage,
                    max(input_voltage) as max_input_voltage, avg(output_voltage) as avg_output_voltage,
                    sum(case when ' ' || status || ' ' like '% OB %' then 1 else 0 end) as on_battery_samples,
                    count(*) as samples
                 from ups_samples group by 1, 2",
        hour: "select time_bucket('1 hour', bucket) as bucket, ups,
                   sum(avg_charge * samples) / sum(samples) as avg_charge, min(min_charge) as min_charge,
                   sum(avg_runtime_seconds * samples) / sum(samples) as avg_runtime_seconds,
                   min(min_runtime_seconds) as min_runtime_seconds,
                   sum(avg_load * samples) / sum(samples) as avg_load, max(max_load) as max_load,
                   sum(avg_real_power * samples) / sum(samples) as avg_real_power,
                   sum(avg_input_voltage * samples) / sum(samples) as avg_input_voltage,
                   min(min_input_voltage) as min_input_voltage, max(max_input_voltage) as max_input_voltage,
                   sum(avg_output_voltage * samples) / sum(samples) as avg_output_voltage,
                   sum(on_battery_samples) as on_battery_samples, sum(samples) as samples
               from ups_1m group by 1, 2",
        backfill: "insert into ups_samples (ts, ups, status, charge, runtime_seconds, load, real_power, input_voltage, output_voltage)
                   select bucket, ups, case when on_battery_samples * 2 > samples then 'OB' else 'OL' end,
                       avg_charge, round(avg_runtime_seconds)::int4, avg_load, avg_real_power, avg_input_voltage, avg_output_voltage
                   from ups_minutes
                   where bucket < coalesce((select min(ts) from ups_samples), 'infinity')
                   on conflict do nothing",
        legacy: "ups_minutes",
    },
];

/// A map_err adapter that says what was being done.
fn failed(doing: impl Into<String>) -> impl FnOnce(postgres::Error) -> String {
    let doing = doing.into();
    move |error| format!("{doing}: {}", crate::db::describe(&error))
}

fn name_of(series: &Series, level: &str) -> String {
    format!("{}_{level}", series.raw.trim_end_matches("_samples"))
}

pub fn setup(client: &mut Client, raw_retention: Duration) -> Result<(), String> {
    let installed =
        client.query_opt("select 1 from pg_extension where extname = 'timescaledb'", &[]).map_err(failed("checking for timescaledb"))?;
    if installed.is_none() {
        return Err("the timescaledb extension is not installed in this database. CT 300's recipe creates it \
                    (create extension timescaledb, as the superuser)"
            .to_owned());
    }

    // The one-time work outlasts the connection's 15-second limit; normal writes keep it.
    client.batch_execute("set statement_timeout = 0").map_err(failed("lifting the statement timeout"))?;
    client
        .batch_execute("create table if not exists vigil_state (key text primary key, value text not null, updated timestamptz not null default now())")
        .map_err(failed("creating vigil_state"))?;

    let mut fresh_aggregates = false;
    for series in &SERIES {
        client
            .execute(
                &format!(
                    "select create_hypertable('{}', by_range('ts', interval '1 day'), if_not_exists => true, migrate_data => true)",
                    series.raw
                ),
                &[],
            )
            .map_err(failed(&format!("making {} a hypertable", series.raw)))?;

        for (level, query) in [("1m", series.minute), ("1h", series.hour)] {
            let name = name_of(series, level);
            let exists = client
                .query_opt("select 1 from timescaledb_information.continuous_aggregates where view_name = $1", &[&name])
                .map_err(failed("listing continuous aggregates"))?
                .is_some();
            if !exists {
                client
                    .batch_execute(&format!("create materialized view {name} with (timescaledb.continuous) as {query} with no data"))
                    .map_err(failed(&format!("creating {name}")))?;
                fresh_aggregates = true;
            }
        }

        // Once: the old minute table becomes raw history, then is renamed out of the way (kept, not dropped).
        let legacy =
            client.query_one("select to_regclass($1) is not null", &[&series.legacy]).map_err(failed("looking for legacy tables"))?;
        if legacy.get::<_, bool>(0) {
            let copied = client.execute(series.backfill, &[]).map_err(failed(&format!("copying {} into {}", series.legacy, series.raw)))?;
            client
                .batch_execute(&format!("alter table {0} rename to legacy_{0}", series.legacy))
                .map_err(failed(&format!("renaming {}", series.legacy)))?;
            crate::log!(
                Info,
                "carried {copied} rows of {} over into {}; the old table is now legacy_{}",
                series.legacy,
                series.raw,
                series.legacy
            );
            fresh_aggregates = true;
        }
    }

    // New aggregates, history just copied in, or a fill that never finished: fill them over
    // everything their source still holds, minutes before hours. Refreshing is idempotent, so a
    // repeat only costs time. The window starts at the source's oldest row, never earlier: a
    // refresh over a period whose source rows retention has already dropped DELETES the rollups
    // for that period, which are then the only copy left.
    // CALL can't run inside a transaction or take a subquery, so each is its own statement.
    let filled = client
        .query_opt("select 1 from vigil_state where key = 'aggregates_filled'", &[])
        .map_err(failed("reading vigil_state"))?
        .is_some();
    if fresh_aggregates || !filled {
        crate::log!(Info, "filling the rollups over all history (once; this can take a while)");
        let started = std::time::Instant::now();
        for level in ["1m", "1h"] {
            for series in &SERIES {
                let name = name_of(series, level);
                // Aligned down to a whole bucket, so the first partial bucket is filled too.
                let (source, oldest_bucket) = if level == "1m" {
                    (series.raw.to_owned(), "time_bucket('1 minute', min(ts))")
                } else {
                    (name_of(series, "1m"), "time_bucket('1 hour', min(bucket))")
                };
                let oldest: Option<String> = client
                    .query_one(&format!("select {oldest_bucket}::text from {source}"), &[])
                    .map_err(failed(&format!("finding the oldest row of {source}")))?
                    .get(0);
                let Some(oldest) = oldest else { continue };
                client
                    .batch_execute(&format!("call refresh_continuous_aggregate('{name}', '{oldest}'::timestamptz, null)"))
                    .map_err(failed(&format!("filling {name}")))?;
            }
        }
        client
            .execute(
                "insert into vigil_state (key, value) values ('aggregates_filled', now()::text)
                 on conflict (key) do update set value = excluded.value, updated = now()",
                &[],
            )
            .map_err(failed("recording the fill"))?;
        crate::log!(Info, "rollups filled in {:.0}s", started.elapsed().as_secs_f64());
    }

    let raw_days = (raw_retention.as_secs() / 86_400).max(1);
    for series in &SERIES {
        let (minute, hour) = (name_of(series, "1m"), name_of(series, "1h"));
        let policies = format!(
            "
            select add_continuous_aggregate_policy('{minute}', start_offset => interval '2 hours',
                end_offset => interval '1 minute', schedule_interval => interval '1 minute', if_not_exists => true);
            select add_continuous_aggregate_policy('{hour}', start_offset => interval '2 days',
                end_offset => interval '1 hour', schedule_interval => interval '30 minutes', if_not_exists => true);
            select add_retention_policy('{minute}', drop_after => interval '{MINUTE_RETENTION}', if_not_exists => true);
            select remove_retention_policy('{raw}', if_exists => true);
            select add_retention_policy('{raw}', drop_after => interval '{raw_days} days');
            ",
            raw = series.raw
        );
        client.batch_execute(&policies).map_err(failed(&format!("scheduling {}'s rollups and retention", series.raw)))?;

        let compressed = client
            .query_one(
                "select coalesce(bool_or(compression_enabled), false) from timescaledb_information.hypertables where hypertable_name = $1",
                &[&series.raw],
            )
            .map_err(failed("reading compression settings"))?
            .get::<_, bool>(0);
        if !compressed {
            client
                .batch_execute(&format!(
                    "alter table {raw} set (timescaledb.compress, timescaledb.compress_segmentby = '{key}', timescaledb.compress_orderby = 'ts')",
                    raw = series.raw,
                    key = series.key
                ))
                .map_err(failed(&format!("enabling compression on {}", series.raw)))?;
        }
        client
            .execute(
                &format!(
                    "select add_compression_policy('{}', compress_after => interval '{COMPRESS_AFTER}', if_not_exists => true)",
                    series.raw
                ),
                &[],
            )
            .map_err(failed(&format!("scheduling compression of {}", series.raw)))?;
    }

    client.batch_execute("reset statement_timeout").map_err(failed("restoring the statement timeout"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aggregate_names_follow_the_raw_table() {
        assert_eq!(name_of(&SERIES[0], "1m"), "sensor_1m");
        assert_eq!(name_of(&SERIES[1], "1h"), "fan_1h");
        assert_eq!(name_of(&SERIES[2], "1m"), "ups_1m");
    }

    #[test]
    fn each_hourly_aggregate_reads_its_own_minute_one() {
        for series in &SERIES {
            assert!(series.hour.contains(&format!("from {}", name_of(series, "1m"))));
            assert!(series.minute.contains(&format!("from {}", series.raw)));
            assert!(series.backfill.contains(&format!("from {}", series.legacy)));
        }
    }
}
