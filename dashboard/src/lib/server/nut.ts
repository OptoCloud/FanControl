// The one connection this app keeps to NUT's upsd: a plain TCP line protocol (RFC 9271).
// Variable reads are anonymous on upsd, so no login is needed. Like daemon.ts, this is a
// single consumer however many browsers are watching; it polls `LIST VAR <ups>` on a fixed
// interval, the same way upsmon does.

import net from 'node:net';
import type { UpsReading } from '$lib/types';

export interface NutTarget {
	host: string;
	port: number;
	/** The UPS's name on upsd ([apc] in ups.conf). */
	ups: string;
	pollIntervalMs: number;
}

export interface NutListener {
	onReading(reading: UpsReading): void;
	/** No reading this poll: upsd unreachable, or upsd answered with an error for the UPS. */
	onError(error: string): void;
}

const REQUEST_TIMEOUT_MS = 10_000;
const MIN_RETRY_MS = 1_000;
const MAX_RETRY_MS = 30_000;

export class NutClient {
	private socket: net.Socket | null = null;
	private pollTimer: NodeJS.Timeout | null = null;
	private retryTimer: NodeJS.Timeout | null = null;
	private requestTimer: NodeJS.Timeout | null = null;
	private retryDelay = MIN_RETRY_MS;
	private stopped = false;

	/** Lines of the LIST VAR answer in flight; null when no request is outstanding. */
	private pending: string[] | null = null;
	private buffer = '';

	constructor(
		private readonly target: NutTarget,
		private readonly listener: NutListener
	) {}

	start(): void {
		this.stopped = false;
		this.connect();
	}

	stop(): void {
		this.stopped = true;
		for (const timer of [this.pollTimer, this.retryTimer, this.requestTimer]) if (timer) clearTimeout(timer);
		if (this.socket && !this.socket.destroyed) this.socket.end('LOGOUT\n');
	}

	private connect(): void {
		const socket = net.createConnection({ host: this.target.host, port: this.target.port });
		this.socket = socket;
		this.buffer = '';
		this.pending = null;

		let finished = false;
		const finish = (reason: string) => {
			if (finished) return;
			finished = true;
			socket.destroy();
			this.handleDisconnect(reason);
		};

		socket.setEncoding('utf8');
		socket.setKeepAlive(true, 30_000);
		socket.setTimeout(REQUEST_TIMEOUT_MS, () => finish(`no answer from upsd within ${REQUEST_TIMEOUT_MS / 1000}s`));
		socket.on('connect', () => {
			this.retryDelay = MIN_RETRY_MS;
			// Idle between polls is normal; the timeout only guards connecting and answering.
			socket.setTimeout(0);
			this.poll();
		});
		socket.on('data', (chunk: string) => {
			this.buffer += chunk;
			let newline: number;
			while ((newline = this.buffer.indexOf('\n')) >= 0) {
				const line = this.buffer.slice(0, newline).replace(/\r$/, '');
				this.buffer = this.buffer.slice(newline + 1);
				this.handleLine(line);
			}
		});
		socket.on('error', (error) => finish(error.message));
		socket.on('close', () => finish('upsd closed the connection'));
	}

	private poll(): void {
		if (!this.socket || this.socket.destroyed) return;
		this.pending = [];
		this.socket.write(`LIST VAR ${this.target.ups}\n`);
		this.requestTimer = setTimeout(() => this.socket?.destroy(new Error(`no answer from upsd within ${REQUEST_TIMEOUT_MS / 1000}s`)), REQUEST_TIMEOUT_MS);
	}

	private handleLine(line: string): void {
		if (this.pending === null) return;

		if (line.startsWith('ERR ')) {
			this.complete();
			// DATA-STALE, DRIVER-NOT-CONNECTED: upsd is fine, but it has lost the UPS itself.
			this.listener.onError(`upsd: ${line.slice(4)}`);
			return;
		}

		this.pending.push(line);
		if (line.startsWith('END LIST VAR')) {
			const lines = this.pending;
			this.complete();
			try {
				this.listener.onReading(toReading(this.target.ups, parseVariables(lines), new Date()));
			} catch (error) {
				console.error('[nut] discarding a reading that could not be processed:', error);
			}
		}
	}

	private complete(): void {
		this.pending = null;
		if (this.requestTimer) clearTimeout(this.requestTimer);
		if (!this.stopped) this.pollTimer = setTimeout(() => this.poll(), this.target.pollIntervalMs);
	}

	private handleDisconnect(reason: string): void {
		for (const timer of [this.pollTimer, this.requestTimer]) if (timer) clearTimeout(timer);
		this.pending = null;
		if (this.stopped) return;

		this.listener.onError(reason);
		this.retryTimer = setTimeout(() => this.connect(), this.retryDelay);
		this.retryDelay = Math.min(this.retryDelay * 2, MAX_RETRY_MS);
	}
}

/**
 * The variables in a LIST VAR answer: lines of `VAR <ups> <name> "<value>"`, where the value
 * escapes `"` and `\` with a backslash. BEGIN/END and anything unexpected are skipped.
 */
export function parseVariables(lines: readonly string[]): Record<string, string> {
	const variables: Record<string, string> = {};
	for (const line of lines) {
		const match = /^VAR \S+ (\S+) "((?:[^"\\]|\\.)*)"$/.exec(line);
		if (match) variables[match[1]] = match[2].replace(/\\(.)/g, '$1');
	}
	return variables;
}

export function toReading(name: string, variables: Record<string, string>, at: Date): UpsReading {
	const number = (key: string) => {
		const value = variables[key];
		if (value === undefined || value.trim() === '') return null;
		const parsed = Number(value);
		return Number.isFinite(parsed) ? parsed : null;
	};

	const load = number('ups.load');
	const nominalPower = number('ups.realpower.nominal');
	const realPower = number('ups.realpower') ?? (load !== null && nominalPower !== null ? Math.round((load / 100) * nominalPower) : null);

	const manufacturer = variables['device.mfr'] ?? variables['ups.mfr'];
	const model = variables['device.model'] ?? variables['ups.model'];

	return {
		timestampUtc: at.toISOString(),
		name,
		model: model ? (manufacturer && !model.startsWith(manufacturer) ? `${manufacturer} ${model}` : model).trim() : null,
		status: (variables['ups.status'] ?? '').split(/\s+/).filter(Boolean),
		batteryCharge: number('battery.charge'),
		batteryRuntimeSeconds: number('battery.runtime'),
		load,
		realPower,
		inputVoltage: number('input.voltage'),
		outputVoltage: number('output.voltage'),
		batteryVoltage: number('battery.voltage'),
		variables
	};
}
