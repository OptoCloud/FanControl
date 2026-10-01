--   $1 fan ids
insert into fans (id) select * from unnest($1::text[])
on conflict (id) do update set last_seen = now()
