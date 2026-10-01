-- Temperatures from a continuous aggregate. {table} is sensor_1m or sensor_1h, chosen from a
-- closed set by the range (see HistoryQueries): raw chunks are dropped after
-- RAW_RETENTION_DAYS, so a long range has to read a rollup.
--   $1 bucket width (an interval)   $2 oldest bucket to include
--
-- Weighted by the sample count, not a plain average of averages: a minute with three samples
-- must not count as much as one with thirty.
select sensor_id as id,
       date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_celsius * samples) / sum(samples))::float8 as value
from {table}
where bucket >= $2
group by 1, 2
order by 2
