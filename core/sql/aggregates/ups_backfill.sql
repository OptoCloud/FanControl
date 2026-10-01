-- The old minute table kept no status text, so it is reconstructed from the on-battery count:
-- a minute that was mostly on battery becomes OB, anything else OL.
insert into ups_samples (ts, ups, status, charge, runtime_seconds, load, real_power, input_voltage, output_voltage)
select bucket, ups, case when on_battery_samples * 2 > samples then 'OB' else 'OL' end,
    avg_charge, round(avg_runtime_seconds)::int4, avg_load, avg_real_power, avg_input_voltage, avg_output_voltage
from ups_minutes
where bucket < coalesce((select min(ts) from ups_samples), 'infinity')
on conflict do nothing
