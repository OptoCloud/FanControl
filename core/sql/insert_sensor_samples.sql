-- One row per sensor per sample. The ids and values arrive as parallel arrays so a whole
-- snapshot is one statement rather than one per sensor.
--   $1 timestamp (RFC 3339 text)   $2 sensor ids   $3 degrees celsius
insert into sensor_samples (ts, sensor_id, celsius)
select $1::text::timestamptz, id, value from unnest($2::text[], $3::real[]) as t(id, value)
on conflict do nothing
