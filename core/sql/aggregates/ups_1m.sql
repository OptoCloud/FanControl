-- on_battery_samples counts the polls that saw the OB flag, so the hourly rollup can say how
-- much of an hour was spent on battery without keeping the status text.
select time_bucket('1 minute', ts) as bucket, ups,
    avg(charge) as avg_charge, min(charge) as min_charge,
    avg(runtime_seconds) as avg_runtime_seconds, min(runtime_seconds) as min_runtime_seconds,
    avg(load) as avg_load, max(load) as max_load, avg(real_power) as avg_real_power,
    avg(input_voltage) as avg_input_voltage, min(input_voltage) as min_input_voltage,
    max(input_voltage) as max_input_voltage, avg(output_voltage) as avg_output_voltage,
    sum(case when ' ' || status || ' ' like '% OB %' then 1 else 0 end) as on_battery_samples,
    count(*) as samples
from ups_samples group by 1, 2
