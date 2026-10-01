-- Once, on the upgrade to TimescaleDB: carries the pre-existing minute table into raw as one
-- sample per minute, for the period before the earliest raw sample, so the aggregates pick up
-- the old history instead of starting from today.
insert into sensor_samples (ts, sensor_id, celsius)
select bucket, sensor_id, avg_celsius from sensor_minutes
where bucket < coalesce((select min(ts) from sensor_samples), 'infinity')
on conflict do nothing
