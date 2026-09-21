// The one connection this app keeps to the fancontrol daemon: a Server-Sent Events stream
// of snapshots, over the daemon's unix socket. However many browsers are watching, the
// daemon only ever sees this single consumer.

import http from 'node:http';
import type { Snapshot } from '$lib/types';
import { SseParser } from './sse';

export interface DaemonTarget {
	/** Path of the daemon's unix socket. Takes precedence over `url`. */
	socketPath?: string;
	/** Base URL of a TCP endpoint instead. Only for development against the mock daemon. */
	url?: string;
}

export interface DaemonListener {
	onSnapshot(snapshot: Snapshot): void;
	onConnectionChange(connected: boolean, reason?: string): void;
}

// The daemon sends a keepalive comment after 15s of silence, so going this long without a
// single byte means the connection is dead even if the socket hasn't noticed.
const SILENCE_TIMEOUT_MS = 40_000;
const MIN_RETRY_MS = 1_000;
const MAX_RETRY_MS = 30_000;

export class DaemonClient {
	private request: http.ClientRequest | null = null;
	private retryTimer: NodeJS.Timeout | null = null;
	private silenceTimer: NodeJS.Timeout | null = null;
	private retryDelay = MIN_RETRY_MS;
	private connected = false;
	private stopped = false;

	constructor(
		private readonly target: DaemonTarget,
		private readonly listener: DaemonListener
	) {}

	get isConnected(): boolean {
		return this.connected;
	}

	start(): void {
		this.stopped = false;
		this.connect();
	}

	stop(): void {
		this.stopped = true;
		if (this.retryTimer) clearTimeout(this.retryTimer);
		if (this.silenceTimer) clearTimeout(this.silenceTimer);
		this.request?.destroy();
	}

	private connect(): void {
		const headers = { Accept: 'text/event-stream' };
		const options: http.RequestOptions = this.target.socketPath
			? { socketPath: this.target.socketPath, path: '/events', headers }
			: { ...urlOptions(this.target.url ?? 'http://127.0.0.1:5178'), path: '/events', headers };

		let finished = false;
		const finish = (reason: string) => {
			if (finished) return;
			finished = true;
			request.destroy();
			this.handleDisconnect(reason);
		};

		const request = http.request(options, (response) => {
			if (response.statusCode !== 200) {
				response.resume();
				// 503 is the daemon's answer when its max_clients cap is reached.
				finish(`daemon answered HTTP ${response.statusCode}`);
				return;
			}

			this.retryDelay = MIN_RETRY_MS;
			this.setConnected(true);
			this.armSilenceTimer(() => finish('no data from the daemon for 40s'));

			const parser = new SseParser();
			// Decoding here (rather than per chunk) keeps a multi-byte character that
			// straddles two chunks intact.
			response.setEncoding('utf8');
			response.on('data', (chunk: string) => {
				this.armSilenceTimer(() => finish('no data from the daemon for 40s'));
				for (const event of parser.push(chunk)) {
					if (event.event !== 'status') continue;
					try {
						this.listener.onSnapshot(JSON.parse(event.data) as Snapshot);
					} catch (error) {
						console.error('[daemon] discarding a snapshot that could not be processed:', error);
					}
				}
			});
			response.on('end', () => finish('the daemon closed the stream'));
			response.on('error', (error) => finish(error.message));
		});

		request.on('error', (error) => finish(error.message));
		request.end();
		this.request = request;
	}

	private handleDisconnect(reason: string): void {
		if (this.silenceTimer) clearTimeout(this.silenceTimer);
		this.setConnected(false, reason);
		if (this.stopped) return;

		this.retryTimer = setTimeout(() => this.connect(), this.retryDelay);
		this.retryDelay = Math.min(this.retryDelay * 2, MAX_RETRY_MS);
	}

	private setConnected(connected: boolean, reason?: string): void {
		if (this.connected === connected) return;
		this.connected = connected;
		this.listener.onConnectionChange(connected, reason);
	}

	private armSilenceTimer(onSilence: () => void): void {
		if (this.silenceTimer) clearTimeout(this.silenceTimer);
		this.silenceTimer = setTimeout(onSilence, SILENCE_TIMEOUT_MS);
	}
}

function urlOptions(url: string): http.RequestOptions {
	const parsed = new URL(url);
	return { protocol: parsed.protocol, hostname: parsed.hostname, port: parsed.port };
}
