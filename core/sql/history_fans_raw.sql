--   $1 bucket width (an interval)   $2 oldest timestamp to include
select fan_id as id,
       date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(duty_percent)::float8 as duty,
       avg(rpm)::float8 as rpm
from fan_samples
where ts >= $2
group by 1, 2
order by 2
