// vigil-web's one connection to vigil-core: its /live stream (the current state on connect,
// then every change), mirrored here and fanned out to any number of browsers. vigil-web
// holds no state of its own beyond this mirror; history comes from Postgres (history.ts).
// Kept on globalThis so a dev-server module reload doesn't leave a second copy running.

import http from 'node:http';
import { env } from '$env/dynamic/private';
import type { DriveState, LiveMessage, Snapshot, UpsState } from '$lib/types';
import * as db from './db';
import { SseParser } from './sse';

// vigil-core sends a keepalive comment after 20s of silence, so going this long without a
// single byte means the connection is dead even if the socket hasn't noticed.
const SILENCE_TIMEOUT_MS = 40_000;
const MIN_RETRY_MS = 1_000;
const MAX_RETRY_MS = 30_000;

type Subscriber = (message: LiveMessage) => void;

class CoreLink {
	readonly coreUrl = new URL('/live', env.CORE_URL || 'http://127.0.0.1:3001');
	readonly sql = db.connect(env.DATABASE_URL || 'postgres://vigil@localhost/vigil');

	coreConnected = false;
	daemonConnected = false;
	snapshot: Snapshot | null = null;
	drives: DriveState[] = [];
	ups: UpsState = { enabled: false, name: 'apc', reading: null, error: null };

	private readonly subscribers = new Set<Subscriber>();
	private retryDelay = MIN_RETRY_MS;
	private silenceTimer: NodeJS.Timeout | null = null;

	start(): void {
		console.log(`[vigil-web] vigil-core: ${this.coreUrl}`);
		this.connect();
	}

	subscribe(subscriber: Subscriber): () => void {
		this.subscribers.add(subscriber);
		return () => this.subscribers.delete(subscriber);
	}

	/** Everything a new browser needs before the next change arrives. */
	get currentState(): LiveMessage[] {
		return [
			{ type: 'core', connected: this.coreConnected },
			{ type: 'daemon', connected: this.daemonConnected },
			...(this.snapshot ? [{ type: 'snapshot', snapshot: this.snapshot } as const] : []),
			{ type: 'drives', drives: this.drives },
			{ type: 'ups', ups: this.ups }
		];
	}

	private connect(): void {
		let finished = false;
		const finish = (reason: string) => {
			if (finished) return;
			finished = true;
			request.destroy();
			this.handleDisconnect(reason);
		};
		const silent = () => finish(`no data from vigil-core for ${SILENCE_TIMEOUT_MS / 1000}s`);

		const request = http.get(this.coreUrl, { headers: { Accept: 'text/event-stream' } }, (response) => {
			if (response.statusCode !== 200) {
				response.resume();
				finish(`vigil-core answered HTTP ${response.statusCode}`);
				return;
			}

			this.retryDelay = MIN_RETRY_MS;
			this.setCoreConnected(true);
			this.armSilenceTimer(silent);

			const parser = new SseParser();
			// Decoding here (rather than per chunk) keeps a multi-byte character that
			// straddles two chunks intact.
			response.setEncoding('utf8');
			response.on('data', (chunk: string) => {
				this.armSilenceTimer(silent);
				for (const event of parser.push(chunk)) {
					try {
						this.apply(JSON.parse(event.data) as LiveMessage);
					} catch (error) {
						console.error('[vigil-web] discarding a message from vigil-core that could not be processed:', error);
					}
				}
			});
			response.on('end', () => finish('vigil-core closed the stream'));
			response.on('error', (error) => finish(error.message));
		});
		request.on('error', (error) => finish(error.message));
	}

	private apply(message: LiveMessage): void {
		if (message.type === 'snapshot') this.snapshot = message.snapshot;
		else if (message.type === 'daemon') this.daemonConnected = message.connected;
		else if (message.type === 'drives') this.drives = message.drives;
		else if (message.type === 'ups') this.ups = message.ups;
		this.broadcast(message);
	}

	private handleDisconnect(reason: string): void {
		if (this.silenceTimer) clearTimeout(this.silenceTimer);
		if (this.coreConnected) console.log(`[vigil-web] lost vigil-core: ${reason}`);
		this.setCoreConnected(false);

		setTimeout(() => this.connect(), this.retryDelay).unref();
		this.retryDelay = Math.min(this.retryDelay * 2, MAX_RETRY_MS);
	}

	private setCoreConnected(connected: boolean): void {
		if (this.coreConnected === connected) return;
		this.coreConnected = connected;
		if (connected) console.log('[vigil-web] connected to vigil-core');
		this.broadcast({ type: 'core', connected });
	}

	private armSilenceTimer(onSilence: () => void): void {
		if (this.silenceTimer) clearTimeout(this.silenceTimer);
		this.silenceTimer = setTimeout(onSilence, SILENCE_TIMEOUT_MS);
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
}

const globalKey = Symbol.for('vigil.core-link');
type GlobalWithLink = typeof globalThis & { [globalKey]?: CoreLink };

export function getCore(): CoreLink {
	const holder = globalThis as GlobalWithLink;
	if (!holder[globalKey]) {
		holder[globalKey] = new CoreLink();
		holder[globalKey].start();
	}
	return holder[globalKey];
}
