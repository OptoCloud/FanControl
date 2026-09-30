// Postgres, read-only from here. vigil-core owns the schema and every write; vigil-web reads
// history (history.ts) and the event log.

import postgres from 'postgres';
import type { EventRecord } from '$lib/types';

export type Sql = postgres.Sql;

export function connect(databaseUrl: string): Sql {
	return postgres(databaseUrl, { max: 5 });
}

export async function recentEvents(sql: Sql, limit: number): Promise<EventRecord[]> {
	const rows = await sql`select id, ts, severity, kind, message from events order by ts desc, id desc limit ${limit}`;
	return rows.map((row) => ({ id: Number(row.id), ts: (row.ts as Date).toISOString(), severity: row.severity, kind: row.kind, message: row.message }));
}
