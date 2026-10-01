--   $1 bucket width (an interval)   $2 oldest timestamp to include   $3 the UPS's name
select date_bin($1::interval, ts, 'epoch'::timestamptz) as bucket,
       avg(charge)::float8 as charge,
       avg(load)::float8 as load,
       avg(runtime_seconds)::float8 as runtime,
       avg(input_voltage)::float8 as input_voltage
from ups_samples
where ups = $3 and ts >= $2
group by 1
order by 1
