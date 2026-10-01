--   $1 timestamp (RFC 3339 text)   $2 fan ids   $3 duty percent   $4 rpm (null if unreadable)
insert into fan_samples (ts, fan_id, duty_percent, rpm)
select $1::text::timestamptz, id, duty, rpm from unnest($2::text[], $3::int2[], $4::int4[]) as t(id, duty, rpm)
on conflict do nothing
