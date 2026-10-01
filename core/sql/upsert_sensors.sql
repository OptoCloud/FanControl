-- Which sensors and bays exist, and when each was last seen.
--   $1 ids   $2 categories   $3 labels
insert into sensors (id, category, label) select * from unnest($1::text[], $2::text[], $3::text[])
on conflict (id) do update set category = excluded.category, label = excluded.label, last_seen = now()
