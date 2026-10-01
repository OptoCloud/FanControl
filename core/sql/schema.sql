-- vigil's base tables. vigil-core owns this schema and applies it on every start; every
-- statement is `if not exists` so that is idempotent. TimescaleDB turns the three *_samples
-- tables into hypertables and builds the rollups over them afterwards: see sql/aggregates/
-- and core/src/timescale.rs.
--
-- Raw samples are narrow (one row per sensor per sample) so a sensor appearing or vanishing
-- needs no schema change.

set client_min_messages = warning;

create table if not exists sensors (
    id text primary key,
    category text not null,
    label text not null,
    first_seen timestamptz not null default now(),
    last_seen timestamptz not null default now()
);

create table if not exists fans (
    id text primary key,
    first_seen timestamptz not null default now(),
    last_seen timestamptz not null default now()
);

create table if not exists sensor_samples (
    ts timestamptz not null,
    sensor_id text not null,
    celsius real not null,
    primary key (sensor_id, ts)
);
create index if not exists sensor_samples_ts on sensor_samples (ts);

create table if not exists fan_samples (
    ts timestamptz not null,
    fan_id text not null,
    duty_percent smallint not null,
    rpm integer,
    primary key (fan_id, ts)
);
create index if not exists fan_samples_ts on fan_samples (ts);

-- A drive's last GOOD health result. vigild reports only what its latest poll saw and never
-- wakes a sleeping drive, so this is what survives those gaps.
create table if not exists drives (
    wwn text primary key,
    passed boolean,
    reallocated bigint,
    pending bigint,
    power_on_hours bigint,
    source_path text not null,
    as_of timestamptz not null,
    first_seen timestamptz not null default now()
);

create table if not exists drive_health_history (
    id bigserial primary key,
    ts timestamptz not null,
    wwn text not null,
    passed boolean,
    reallocated bigint,
    pending bigint,
    power_on_hours bigint
);
create index if not exists drive_health_history_wwn_ts on drive_health_history (wwn, ts desc);

alter table drives add column if not exists port text;

-- Which disk sat in which bay, and when. A drive's temperature is stored twice, under
-- "drive:<wwn>" (the disk, wherever it is plugged in) and "port:<by-path>" (the bay, whichever
-- disk is in it); this is what lets the two be told apart after a swap.
create table if not exists bay_occupants (
    port text not null,
    wwn text not null,
    first_seen timestamptz not null default now(),
    last_seen timestamptz not null default now(),
    primary key (port, wwn)
);

create table if not exists events (
    id bigserial primary key,
    ts timestamptz not null default now(),
    severity text not null,
    kind text not null,
    message text not null
);
create index if not exists events_ts on events (ts desc);

-- The UPS, from NUT. Wide rather than narrow: its handful of metrics always arrive together.
create table if not exists ups_samples (
    ts timestamptz not null,
    ups text not null,
    status text not null,
    charge real,
    runtime_seconds integer,
    load real,
    real_power real,
    input_voltage real,
    output_voltage real,
    battery_voltage real,
    primary key (ups, ts)
);
create index if not exists ups_samples_ts on ups_samples (ts);
