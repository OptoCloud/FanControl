-- The newest events, for a page's first paint: the live stream only carries events raised after
-- it connects. ts comes back as epoch milliseconds, formatted by the caller, as select_drives does.
--   $1 how many
select id, (extract(epoch from ts) * 1000)::int8, severity, kind, message
from events
order by ts desc, id desc
limit $1
