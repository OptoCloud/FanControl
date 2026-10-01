-- avg_rpm is weighted only by the buckets that actually had an rpm reading: a header whose
-- tach is unreadable contributes duty but must not drag the rpm average towards zero.
select time_bucket('1 hour', bucket) as bucket, fan_id,
    sum(avg_duty * samples) / sum(samples) as avg_duty,
    sum(avg_rpm * samples) / nullif(sum(case when avg_rpm is not null then samples else 0 end), 0) as avg_rpm,
    min(min_rpm) as min_rpm, max(max_rpm) as max_rpm, sum(samples) as samples
from fan_1m group by 1, 2
