select time_bucket('1 minute', ts) as bucket, fan_id,
    avg(duty_percent) as avg_duty, avg(rpm) as avg_rpm, min(rpm) as min_rpm, max(rpm) as max_rpm, count(*) as samples
from fan_samples group by 1, 2
