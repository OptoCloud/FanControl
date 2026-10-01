// Postgres, read-only from here. vigil-core owns the schema and every write; vigil-web reads
// history (history.ts) and the event log.
//
// "Read-only" is enforced by the ROLE, not by this comment: vigil-web connects as `vigil_web`,
// which holds SELECT and nothing else. docs/SECURITY.md §3.4 has the grant. Connecting as
// vigil-core's schema-owning role would mean a bug here could write or drop history.

import postgres from 'postgres';
import { DATABASE_STATEMENT_TIMEOUT_MS } from '$lib/limits';
import type { EventRecord } from '$lib/types';

/** Small: every query here serves one page load or one chart, and the database is shared. */
const MAX_CONNECTIONS = 5;

export type Sql = postgres.Sql;

export function connect(databaseUrl: string): Sql {
	return postgres(databaseUrl, {
		max: MAX_CONNECTIONS,
		// vigil-core sets the same bound on its writes; declared once in $lib/limits.
		connection: { statement_timeout: DATABASE_STATEMENT_TIMEOUT_MS }
	});
}

export async function recentEvents(sql: Sql, limit: number): Promise<EventRecord[]> {
	const rows = await sql`select id, ts, severity, kind, message from events order by ts desc, id desc limit ${limit}`;
	return rows.map((row) => ({
		id: Number(row.id),
		ts: (row.ts as Date).toISOString(),
		severity: row.severity,
		kind: row.kind,
		message: row.message
	}));
}
