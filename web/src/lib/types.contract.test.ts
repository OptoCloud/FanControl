// The other half of the wire contract. protocol/contract.json is generated from the Rust types
// by `cargo test -p vigil-protocol`; this checks web/src/lib/types.ts against it in BOTH
// directions, so neither side can drift silently. See docs/STYLE.md §1.3.
//
// How it works: one hand-written key list per wire type, checked twice over.
//   - `satisfies Record<keyof T, 1>` makes the COMPILER reject a list that misses a field of
//     the TypeScript type or invents one that is not in it.
//   - the runtime assertions compare that same list against the generated contract.
// A field added in Rust fails the runtime check; a field added only in TypeScript fails the
// compile-time check. Either way the commit cannot land half-done.

import { describe, expect, it } from 'vitest';
import contract from '../../../protocol/contract.json';
import * as limits from './limits';
import type {
	DriveHealth,
	DriveState,
	EventRecord,
	FanStatus,
	LiveMessage,
	PwmMode,
	SensorCategory,
	SensorReading,
	Severity,
	Snapshot,
	UpsReading,
	UpsState
} from './types';

const keysOf = (shape: Record<string, 1>) => Object.keys(shape).sort();
const fieldsOf = (sample: object) => Object.keys(sample).sort();

const TYPE_KEYS = {
	SensorReading: {
		id: 1,
		category: 1,
		label: 1,
		celsiusOrNull: 1,
		sourcePath: 1,
		isAvailable: 1,
		port: 1
	} satisfies Record<keyof SensorReading, 1>,
	FanStatus: { id: 1, dutyPercent: 1, rpm: 1, mode: 1, stalled: 1 } satisfies Record<keyof FanStatus, 1>,
	DriveHealth: {
		deviceName: 1,
		port: 1,
		passed: 1,
		reallocatedSectorCount: 1,
		pendingSectorCount: 1,
		powerOnHours: 1,
		sourcePath: 1,
		isAvailable: 1,
		asOf: 1
	} satisfies Record<keyof DriveHealth, 1>,
	Snapshot: {
		timestampUtc: 1,
		sensors: 1,
		fans: 1,
		driveHealth: 1,
		controlLoopHealthy: 1
	} satisfies Record<keyof Snapshot, 1>,
	UpsReading: {
		timestampUtc: 1,
		name: 1,
		model: 1,
		status: 1,
		batteryCharge: 1,
		batteryRuntimeSeconds: 1,
		load: 1,
		realPower: 1,
		inputVoltage: 1,
		outputVoltage: 1,
		batteryVoltage: 1,
		variables: 1
	} satisfies Record<keyof UpsReading, 1>,
	UpsState: { enabled: 1, name: 1, reading: 1, error: 1 } satisfies Record<keyof UpsState, 1>,
	DriveState: {
		wwn: 1,
		port: 1,
		passed: 1,
		reallocatedSectorCount: 1,
		pendingSectorCount: 1,
		powerOnHours: 1,
		sourcePath: 1,
		asOf: 1
	} satisfies Record<keyof DriveState, 1>,
	EventRecord: { id: 1, ts: 1, severity: 1, kind: 1, message: 1 } satisfies Record<keyof EventRecord, 1>
};

const ENUM_MEMBERS = {
	SensorCategory: { cpu: 1, boardAmbient: 1, drive: 1, gpu: 1, memory: 1, hba: 1 } satisfies Record<SensorCategory, 1>,
	PwmMode: {
		disabled: 1,
		manual: 1,
		thermalCruise: 1,
		speedCruise: 1,
		smartFanIII: 1,
		smartFanIV: 1
	} satisfies Record<PwmMode, 1>,
	Severity: { info: 1, warning: 1, critical: 1 } satisfies Record<Severity, 1>
};

/** The payload keys of each `LiveMessage` variant, `type` included. */
const LIVE_MESSAGE_KEYS = {
	snapshot: { type: 1, snapshot: 1 },
	daemon: { type: 1, connected: 1 },
	event: { type: 1, event: 1 },
	drives: { type: 1, drives: 1 },
	ups: { type: 1, ups: 1 }
} satisfies Record<Exclude<LiveMessage['type'], 'core'>, Record<string, 1>>;

/** vigil-web's own addition: whether it can reach vigil-core. Not part of vigil-core's stream. */
const WEB_ONLY_LIVE_MESSAGES = ['core'] as const satisfies readonly LiveMessage['type'][];

describe('the generated wire contract', () => {
	it('is the file the Rust tests generate', () => {
		expect(contract.$comment).toContain('cargo test -p vigil-protocol');
	});

	it('covers every type this file claims to mirror', () => {
		expect(Object.keys(contract.types).sort()).toEqual(Object.keys(TYPE_KEYS).sort());
		expect(Object.keys(contract.enums).sort()).toEqual(Object.keys(ENUM_MEMBERS).sort());
	});
});

describe.each(Object.entries(TYPE_KEYS))('%s', (name, shape) => {
	it('has exactly the fields the Rust type serializes', () => {
		const sample = contract.types[name as keyof typeof contract.types];
		expect(fieldsOf(sample)).toEqual(keysOf(shape));
	});
});

describe.each(Object.entries(ENUM_MEMBERS))('%s', (name, members) => {
	it('has exactly the variants the Rust enum serializes', () => {
		const variants = contract.enums[name as keyof typeof contract.enums];
		expect([...variants].sort()).toEqual(keysOf(members));
	});
});

describe('LiveMessage', () => {
	it('handles every variant vigil-core streams, and no phantom ones', () => {
		expect(Object.keys(contract.liveMessages).sort()).toEqual(Object.keys(LIVE_MESSAGE_KEYS).sort());
	});

	it.each(Object.entries(LIVE_MESSAGE_KEYS))('%s carries exactly its payload', (tag, shape) => {
		const sample = contract.liveMessages[tag as keyof typeof contract.liveMessages];
		expect(fieldsOf(sample)).toEqual(keysOf(shape));
		expect((sample as { type: string }).type).toBe(tag);
	});

	it("keeps vigil-web-only variants out of vigil-core's contract", () => {
		for (const tag of WEB_ONLY_LIVE_MESSAGES) {
			expect(Object.keys(contract.liveMessages)).not.toContain(tag);
		}
	});
});

// protocol/src/limits.rs is the single declaration; $lib/limits.ts is its TypeScript mirror.
// If one side changes, this fails rather than letting the two drift apart silently.
describe('the shared limits', () => {
	it.each([
		['streamKeepaliveSeconds', limits.STREAM_KEEPALIVE_MS / 1000],
		['streamSilenceTimeoutSeconds', limits.STREAM_SILENCE_TIMEOUT_MS / 1000],
		['coreDefaultPort', limits.CORE_DEFAULT_PORT],
		['databaseUrlDefault', limits.DATABASE_URL_DEFAULT],
		['databaseStatementTimeoutMillis', limits.DATABASE_STATEMENT_TIMEOUT_MS]
	])('%s matches protocol/src/limits.rs', (key, mirrored) => {
		expect(contract.limits[key as keyof typeof contract.limits]).toBe(mirrored);
	});

	it('gives a reader more than two keepalives of slack', () => {
		expect(limits.STREAM_SILENCE_TIMEOUT_MS).toBeGreaterThan(limits.STREAM_KEEPALIVE_MS * 2);
	});
});
