// The long-lived heart of the server: one daemon connection in, Postgres and any number of
// browsers out. Started once from hooks.server.ts and kept on globalThis so a dev-server
// module reload doesn't leave a second copy running.

import { env } from '$env/dynamic/private';
import type { DriveState, EventRecord, LiveMessage, Snapshot } from '$lib/types';
import { ConditionTracker, conditionsIn, diffDriveHealth, type NewEvent } from './alerts';
import { DaemonClient } from './daemon';
import * as db from './db';

const config = {
	socketPath: env.FANCONTROL_SOCKET || undefined,
	daemonUrl: env.FANCONTROL_URL || undefined,
	databaseUrl: env.DATABASE_URL || 'postgres://fancontrol@localhost/fancontrol',
	// The daemon publishes every 2s. That resolution matters live, not in history.
	persistIntervalMs: Number(env.PERSIST_INTERVAL_SECONDS || 10) * 1000,
	rawRetentionDays: Number(env.RAW_RETENTION_DAYS || 7),
	ntfyUrl: env.NTFY_URL || undefined,
	// How long the daemon has to be unreachable before that counts as an incident.
	daemonLostAfterMs: 20_000
};

type Subscriber = (message: LiveMessage) => void;

class Runtime {
	readonly sql = db.connect(config.databaseUrl);

	latest: Snapshot | null = null;
	daemonConnected = false;
	drives = new Map<string, DriveState>();

	private readonly subscribers = new Set<Subscriber>();
	private readonly tracker = new ConditionTracker();
	private readonly knownSensorIds = new Set<string>();
	private readonly client: DaemonClient;

	private databaseReady = false;
	private lastPersistedAt = 0;
	private lastInventoryAt = 0;
	private lastDriveHealthAsOf = '';
	private daemonLostTimer: NodeJS.Timeout | null = null;
	private daemonLostRaised = false;
	private lastDatabaseError = '';

	constructor() {
		this.client = new DaemonClient(
			config.socketPath ? { socketPath: config.socketPath } : { url: config.daemonUrl },
			{
				onSnapshot: (snapshot) => void this.handleSnapshot(snapshot),
				onConnectionChange: (connected, reason) => this.handleConnectionChange(connected, reason)
			}
		);
	}

	async start(): Promise<void> {
		console.log(`[fancontrol] daemon: ${config.socketPath ?? config.daemonUrl ?? 'http://127.0.0.1:5178'}`);
		this.client.start();

		await this.prepareDatabase();
		setInterval(() => void this.maintain(), 60_000).unref();
	}

	subscribe(subscriber: Subscriber): () => void {
		this.subscribers.add(subscriber);
		return () => this.subscribers.delete(subscriber);
	}

	get driveList(): DriveState[] {
		return [...this.drives.values()].sort((a, b) => a.wwn.localeCompare(b.wwn));
	}

	async recentEvents(limit: number): Promise<EventRecord[]> {
		return this.databaseReady ? db.recentEvents(this.sql, limit) : [];
	}

	/** The live view must work without a database, so a failure here is retried, never fatal. */
	private async prepareDatabase(): Promise<void> {
		try {
			await db.migrate(this.sql);
			for (const drive of await db.loadDrives(this.sql)) this.drives.set(drive.wwn, drive);

			// Catch up on whatever raw samples accumulated but weren't rolled up before a restart.
			await db.rollUp(this.sql, new Date(Date.now() - config.rawRetentionDays * 86_400_000));
			this.databaseReady = true;
			console.log('[fancontrol] database ready');
		} catch (error) {
			this.reportDatabaseError('preparing the database', error);
			setTimeout(() => void this.prepareDatabase(), 15_000).unref();
		}
	}

	private async maintain(): Promise<void> {
		if (!this.databaseReady) return;
		try {
			await db.rollUp(this.sql, new Date(Date.now() - 10 * 60_000));
			await db.pruneRawSamples(this.sql, new Date(Date.now() - config.rawRetentionDays * 86_400_000));
		} catch (error) {
			this.reportDatabaseError('rolling up history', error);
		}
	}

	private async handleSnapshot(snapshot: Snapshot): Promise<void> {
		this.latest = snapshot;
		this.broadcast({ type: 'snapshot', snapshot });

		const events = this.tracker.update(conditionsIn(snapshot, this.knownSensorIds));
		for (const sensor of snapshot.sensors) this.knownSensorIds.add(sensor.id);

		// Drive health only changes when the daemon's slow SMART poll runs, which shows up as a new asOf.
		const asOf = snapshot.driveHealth[0]?.asOf ?? '';
		const drivesToSave: { state: DriveState; changed: boolean }[] = [];
		if (asOf && asOf !== this.lastDriveHealthAsOf) {
			this.lastDriveHealthAsOf = asOf;
			for (const health of snapshot.driveHealth) {
				const update = diffDriveHealth(this.drives.get(health.deviceName), health);
				if (!update.state) continue;
				this.drives.set(update.state.wwn, update.state);
				drivesToSave.push({ state: update.state, changed: update.changed });
				events.push(...update.events);
			}
			this.broadcast({ type: 'drives', drives: this.driveList });
		}

		for (const event of events) await this.raise(event);

		if (!this.databaseReady) return;
		try {
			for (const { state, changed } of drivesToSave) await db.saveDrive(this.sql, state, changed);

			const now = Date.now();
			if (now - this.lastPersistedAt >= config.persistIntervalMs) {
				this.lastPersistedAt = now;
				await db.insertSamples(this.sql, snapshot);
			}
			if (now - this.lastInventoryAt >= 60_000) {
				this.lastInventoryAt = now;
				await db.touchInventory(this.sql, snapshot);
			}
		} catch (error) {
			this.reportDatabaseError('writing history', error);
		}
	}

	private handleConnectionChange(connected: boolean, reason?: string): void {
		this.daemonConnected = connected;
		this.broadcast({ type: 'daemon', connected });
		console.log(connected ? '[fancontrol] connected to the daemon' : `[fancontrol] daemon connection lost: ${reason}`);

		if (this.daemonLostTimer) clearTimeout(this.daemonLostTimer);
		if (connected) {
			if (this.daemonLostRaised) {
				this.daemonLostRaised = false;
				void this.raise({ severity: 'info', kind: 'daemon-lost', message: 'The fancontrol daemon is reachable again.' });
			}
			return;
		}

		// A daemon restart drops the stream for a second or two; only a lasting outage is news.
		this.daemonLostTimer = setTimeout(() => {
			this.daemonLostRaised = true;
			void this.raise({
				severity: 'critical',
				kind: 'daemon-lost',
				message: `The fancontrol daemon is unreachable (${reason ?? 'unknown reason'}). If it is not running, the fans are on BIOS control.`
			});
		}, config.daemonLostAfterMs);
		this.daemonLostTimer.unref();
	}

	private async raise(event: NewEvent): Promise<void> {
		console.log(`[fancontrol] ${event.severity}: ${event.message}`);

		let record: EventRecord = { id: -Date.now(), ts: new Date().toISOString(), ...event };
		if (this.databaseReady) {
			try {
				record = await db.insertEvent(this.sql, event.severity, event.kind, event.message);
			} catch (error) {
				this.reportDatabaseError('recording an event', error);
			}
		}

		this.broadcast({ type: 'event', event: record });
		if (event.severity !== 'info') void this.notify(event);
	}

	/** Push notification via ntfy (https://ntfy.sh or self-hosted). Optional, and never allowed to break anything. */
	private async notify(event: NewEvent): Promise<void> {
		if (!config.ntfyUrl) return;
		try {
			await fetch(config.ntfyUrl, {
				method: 'POST',
				body: event.message,
				headers: {
					Title: event.severity === 'critical' ? 'fancontrol: CRITICAL' : 'fancontrol: warning',
					Priority: event.severity === 'critical' ? 'urgent' : 'default',
					Tags: event.severity === 'critical' ? 'rotating_light' : 'warning'
				},
				signal: AbortSignal.timeout(10_000)
			});
		} catch (error) {
			console.error('[fancontrol] could not send a notification:', error instanceof Error ? error.message : error);
		}
	}

	private broadcast(message: LiveMessage): void {
		for (const subscriber of this.subscribers) {
			try {
				subscriber(message);
			} catch {
				this.subscribers.delete(subscriber);
			}
		}
	}

	/** Logged once per distinct error, not once per failed 10-second write. */
	private reportDatabaseError(doing: string, error: unknown): void {
		const message = error instanceof Error ? error.message : String(error);
		if (message === this.lastDatabaseError) return;
		this.lastDatabaseError = message;
		console.error(`[fancontrol] database error while ${doing}: ${message}`);
	}
}

const globalKey = Symbol.for('fancontrol.runtime');
type GlobalWithRuntime = typeof globalThis & { [globalKey]?: Runtime };

export function getRuntime(): Runtime {
	const holder = globalThis as GlobalWithRuntime;
	if (!holder[globalKey]) {
		holder[globalKey] = new Runtime();
		void holder[globalKey].start();
	}
	return holder[globalKey];
}
