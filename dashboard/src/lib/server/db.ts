// Postgres access: connection, schema, and every statement the app runs. The daemon keeps
// no history at all, so this database is the only place any of it lives.

import postgres from 'postgres';
import type { DriveState, EventRecord, Severity, Snapshot } from '$lib/types';

export type Sql = postgres.Sql;

export function connect(databaseUrl: string): Sql {
	return postgres(databaseUrl, {
		max: 5,
		// The schema below is idempotent, which makes Postgres chatty ("relation already exists, skipping").
		onnotice: () => {}
	});
}

/**
 * Raw samples are narrow (one row per sensor per sample) so a sensor appearing or vanishing
 * needs no schema change. They are only kept for a few days; the *_minutes tables are the
 * long-term record and what every chart range beyond an hour reads from.
 */
export async function migrate(sql: Sql): Promise<void> {
	await sql`
		create table if not exists sensors (
			id text primary key,
			category text not null,
			label text not null,
			first_seen timestamptz not null default now(),
			last_seen timestamptz not null default now()
		)`;
	await sql`
		create table if not exists fans (
			id text primary key,
			first_seen timestamptz not null default now(),
			last_seen timestamptz not null default now()
		)`;
	await sql`
		create table if not exists sensor_samples (
			ts timestamptz not null,
			sensor_id text not null,
			celsius real not null,
			primary key (sensor_id, ts)
		)`;
	await sql`create index if not exists sensor_samples_ts on sensor_samples (ts)`;
	await sql`
		create table if not exists fan_samples (
			ts timestamptz not null,
			fan_id text not null,
			duty_percent smallint not null,
			rpm integer,
			primary key (fan_id, ts)
		)`;
	await sql`create index if not exists fan_samples_ts on fan_samples (ts)`;
	await sql`
		create table if not exists sensor_minutes (
			bucket timestamptz not null,
			sensor_id text not null,
			avg_celsius real not null,
			min_celsius real not null,
			max_celsius real not null,
			samples integer not null,
			primary key (sensor_id, bucket)
		)`;
	await sql`create index if not exists sensor_minutes_bucket on sensor_minutes (bucket)`;
	await sql`
		create table if not exists fan_minutes (
			bucket timestamptz not null,
			fan_id text not null,
			avg_duty real not null,
			avg_rpm real,
			min_rpm integer,
			max_rpm integer,
			samples integer not null,
			primary key (fan_id, bucket)
		)`;
	await sql`create index if not exists fan_minutes_bucket on fan_minutes (bucket)`;
	await sql`
		create table if not exists drives (
			wwn text primary key,
			passed boolean,
			reallocated bigint,
			pending bigint,
			power_on_hours bigint,
			source_path text not null,
			as_of timestamptz not null,
			first_seen timestamptz not null default now()
		)`;
	await sql`
		create table if not exists drive_health_history (
			id bigserial primary key,
			ts timestamptz not null,
			wwn text not null,
			passed boolean,
			reallocated bigint,
			pending bigint,
			power_on_hours bigint
		)`;
	await sql`create index if not exists drive_health_history_wwn_ts on drive_health_history (wwn, ts desc)`;
	await sql`
		create table if not exists events (
			id bigserial primary key,
			ts timestamptz not null default now(),
			severity text not null,
			kind text not null,
			message text not null
		)`;
	await sql`create index if not exists events_ts on events (ts desc)`;
}

export async function insertSamples(sql: Sql, snapshot: Snapshot): Promise<void> {
	const ts = snapshot.timestampUtc;

	const sensorRows = snapshot.sensors
		.filter((sensor) => sensor.celsiusOrNull !== null)
		.map((sensor) => ({ ts, sensor_id: sensor.id, celsius: sensor.celsiusOrNull as number }));
	if (sensorRows.length > 0) {
		await sql`insert into sensor_samples ${sql(sensorRows, 'ts', 'sensor_id', 'celsius')} on conflict do nothing`;
	}

	const fanRows = snapshot.fans.map((fan) => ({ ts, fan_id: fan.id, duty_percent: fan.dutyPercent, rpm: fan.rpm }));
	if (fanRows.length > 0) {
		await sql`insert into fan_samples ${sql(fanRows, 'ts', 'fan_id', 'duty_percent', 'rpm')} on conflict do nothing`;
	}
}

/** Records which sensors and fans exist and when they were last seen. */
export async function touchInventory(sql: Sql, snapshot: Snapshot): Promise<void> {
	const sensorRows = snapshot.sensors.map((sensor) => ({ id: sensor.id, category: sensor.category, label: sensor.label }));
	if (sensorRows.length > 0) {
		await sql`
			insert into sensors ${sql(sensorRows, 'id', 'category', 'label')}
			on conflict (id) do update set category = excluded.category, label = excluded.label, last_seen = now()`;
	}

	const fanRows = snapshot.fans.map((fan) => ({ id: fan.id }));
	if (fanRows.length > 0) {
		await sql`insert into fans ${sql(fanRows, 'id')} on conflict (id) do update set last_seen = now()`;
	}
}

/**
 * Folds complete minutes of raw samples since `since` into the *_minutes tables. Safe to
 * re-run over the same window: it overwrites those buckets with the same result.
 */
export async function rollUp(sql: Sql, since: Date): Promise<void> {
	await sql`
		insert into sensor_minutes (bucket, sensor_id, avg_celsius, min_celsius, max_celsius, samples)
		select date_trunc('minute', ts), sensor_id, avg(celsius), min(celsius), max(celsius), count(*)
		from sensor_samples
		where ts >= date_trunc('minute', ${since}::timestamptz) and ts < date_trunc('minute', now())
		group by 1, 2
		on conflict (sensor_id, bucket) do update set
			avg_celsius = excluded.avg_celsius, min_celsius = excluded.min_celsius,
			max_celsius = excluded.max_celsius, samples = excluded.samples`;

	await sql`
		insert into fan_minutes (bucket, fan_id, avg_duty, avg_rpm, min_rpm, max_rpm, samples)
		select date_trunc('minute', ts), fan_id, avg(duty_percent), avg(rpm), min(rpm), max(rpm), count(*)
		from fan_samples
		where ts >= date_trunc('minute', ${since}::timestamptz) and ts < date_trunc('minute', now())
		group by 1, 2
		on conflict (fan_id, bucket) do update set
			avg_duty = excluded.avg_duty, avg_rpm = excluded.avg_rpm,
			min_rpm = excluded.min_rpm, max_rpm = excluded.max_rpm, samples = excluded.samples`;
}

export async function pruneRawSamples(sql: Sql, olderThan: Date): Promise<void> {
	await sql`delete from sensor_samples where ts < ${olderThan}`;
	await sql`delete from fan_samples where ts < ${olderThan}`;
}

export async function loadDrives(sql: Sql): Promise<DriveState[]> {
	const rows = await sql`select wwn, passed, reallocated, pending, power_on_hours, source_path, as_of from drives order by wwn`;
	return rows.map((row) => ({
		wwn: row.wwn,
		passed: row.passed,
		reallocatedSectorCount: toNumber(row.reallocated),
		pendingSectorCount: toNumber(row.pending),
		powerOnHours: toNumber(row.power_on_hours),
		sourcePath: row.source_path,
		asOf: (row.as_of as Date).toISOString()
	}));
}

/** Stores a drive's new last-good state, and a history row if `changed` (anything but power-on hours moved). */
export async function saveDrive(sql: Sql, drive: DriveState, changed: boolean): Promise<void> {
	await sql`
		insert into drives (wwn, passed, reallocated, pending, power_on_hours, source_path, as_of)
		values (${drive.wwn}, ${drive.passed}, ${drive.reallocatedSectorCount}, ${drive.pendingSectorCount},
			${drive.powerOnHours}, ${drive.sourcePath}, ${drive.asOf})
		on conflict (wwn) do update set
			passed = excluded.passed, reallocated = excluded.reallocated, pending = excluded.pending,
			power_on_hours = excluded.power_on_hours, source_path = excluded.source_path, as_of = excluded.as_of`;

	if (changed) {
		await sql`
			insert into drive_health_history (ts, wwn, passed, reallocated, pending, power_on_hours)
			values (${drive.asOf}, ${drive.wwn}, ${drive.passed}, ${drive.reallocatedSectorCount},
				${drive.pendingSectorCount}, ${drive.powerOnHours})`;
	}
}

export async function insertEvent(sql: Sql, severity: Severity, kind: string, message: string): Promise<EventRecord> {
	const [row] = await sql`
		insert into events (severity, kind, message) values (${severity}, ${kind}, ${message})
		returning id, ts, severity, kind, message`;
	return toEvent(row);
}

export async function recentEvents(sql: Sql, limit: number): Promise<EventRecord[]> {
	const rows = await sql`select id, ts, severity, kind, message from events order by ts desc, id desc limit ${limit}`;
	return rows.map(toEvent);
}

function toEvent(row: postgres.Row): EventRecord {
	return { id: Number(row.id), ts: (row.ts as Date).toISOString(), severity: row.severity, kind: row.kind, message: row.message };
}

// bigint columns arrive as strings; SMART counters are far below 2^53.
function toNumber(value: string | number | null): number | null {
	return value === null ? null : Number(value);
}
