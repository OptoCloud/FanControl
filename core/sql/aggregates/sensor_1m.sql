-- One row per sensor per minute, over the raw samples. `samples` is the count, which is what
-- makes the hourly rollup above a correct weighted average rather than an average of averages.
select time_bucket('1 minute', ts) as bucket, sensor_id,
    avg(celsius) as avg_celsius, min(celsius) as min_celsius, max(celsius) as max_celsius, count(*) as samples
from sensor_samples group by 1, 2
