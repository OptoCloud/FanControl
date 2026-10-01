-- Returns the row's id and timestamp so the new event can go straight out on the live stream
-- without a second query.
--   $1 severity  $2 kind  $3 message
insert into events (severity, kind, message) values ($1, $2, $3)
returning id, (extract(epoch from ts) * 1000)::int8
