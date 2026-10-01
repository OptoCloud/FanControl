-- {table} is fan_1m or fan_1h.
--   $1 bucket width (an interval)   $2 oldest bucket to include
--
-- avg_rpm is weighted only by the buckets that actually had a reading: a header whose tach is
-- unreadable contributes its duty but must not drag the rpm average towards zero.
select fan_id as id,
       date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_duty * samples) / sum(samples))::float8 as duty,
       (sum(avg_rpm * samples) / nullif(sum(case when avg_rpm is not null then samples else 0 end), 0))::float8 as rpm
from {table}
where bucket >= $2
group by 1, 2
order by 2
