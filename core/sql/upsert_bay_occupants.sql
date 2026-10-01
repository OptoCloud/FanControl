-- Which disk is in which bay right now, so a swap can be reconstructed later.
--   $1 ports (by-path)   $2 wwns
insert into bay_occupants (port, wwn) select * from unnest($1::text[], $2::text[])
on conflict (port, wwn) do update set last_seen = now()
