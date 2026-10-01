-- Hourly, built on the minute rollup rather than on raw: raw chunks are dropped after
-- RAW_RETENTION_DAYS, and this level is kept forever.
select time_bucket('1 hour', bucket) as bucket, sensor_id,
    sum(avg_celsius * samples) / sum(samples) as avg_celsius, min(min_celsius) as min_celsius,
    max(max_celsius) as max_celsius, sum(samples) as samples
from sensor_1m group by 1, 2
