-- One poll of the UPS. Every metric may be null: which ones a UPS reports depends on its
-- driver.
--   $1 timestamp (RFC 3339 text)  $2 ups  $3 status flags  $4 charge  $5 runtime seconds
--   $6 load  $7 real power  $8 input volts  $9 output volts  $10 battery volts
insert into ups_samples (ts, ups, status, charge, runtime_seconds, load, real_power, input_voltage, output_voltage, battery_voltage)
values ($1::text::timestamptz, $2, $3, $4, $5, $6, $7, $8, $9, $10)
on conflict do nothing
