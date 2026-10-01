select time_bucket('1 hour', bucket) as bucket, ups,
    sum(avg_charge * samples) / sum(samples) as avg_charge, min(min_charge) as min_charge,
    sum(avg_runtime_seconds * samples) / sum(samples) as avg_runtime_seconds,
    min(min_runtime_seconds) as min_runtime_seconds,
    sum(avg_load * samples) / sum(samples) as avg_load, max(max_load) as max_load,
    sum(avg_real_power * samples) / sum(samples) as avg_real_power,
    sum(avg_input_voltage * samples) / sum(samples) as avg_input_voltage,
    min(min_input_voltage) as min_input_voltage, max(max_input_voltage) as max_input_voltage,
    sum(avg_output_voltage * samples) / sum(samples) as avg_output_voltage,
    sum(on_battery_samples) as on_battery_samples, sum(samples) as samples
from ups_1m group by 1, 2
