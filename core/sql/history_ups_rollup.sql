-- {table} is ups_1m or ups_1h.
--   $1 bucket width (an interval)   $2 oldest bucket to include   $3 the UPS's name
select date_bin($1::interval, bucket, 'epoch'::timestamptz) as bucket,
       (sum(avg_charge * samples) / sum(samples))::float8 as charge,
       (sum(avg_load * samples) / sum(samples))::float8 as load,
       (sum(avg_runtime_seconds * samples) / sum(samples))::float8 as runtime,
       (sum(avg_input_voltage * samples) / sum(samples))::float8 as input_voltage
from {table}
where ups = $3 and bucket >= $2
group by 1
order by 1
