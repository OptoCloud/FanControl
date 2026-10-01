-- A drive's new last-good state.
--   $1 wwn  $2 port  $3 passed  $4 reallocated  $5 pending  $6 power-on hours
--   $7 source path  $8 as of (RFC 3339 text)
insert into drives (wwn, port, passed, reallocated, pending, power_on_hours, source_path, as_of)
values ($1, $2, $3, $4, $5, $6, $7, $8::text::timestamptz)
on conflict (wwn) do update set
    port = excluded.port, passed = excluded.passed, reallocated = excluded.reallocated, pending = excluded.pending,
    power_on_hours = excluded.power_on_hours, source_path = excluded.source_path, as_of = excluded.as_of
