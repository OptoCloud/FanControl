insert into fan_samples (ts, fan_id, duty_percent, rpm)
select bucket, fan_id, round(avg_duty)::int2, round(avg_rpm)::int4 from fan_minutes
where bucket < coalesce((select min(ts) from fan_samples), 'infinity')
on conflict do nothing
