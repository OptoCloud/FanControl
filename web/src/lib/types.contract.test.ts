// The browser's half of the wire contract. Two programs define the JSON this page reads, and each
// writes its half to a contract file from its own tests: vigild (Rust) writes daemon/contract.json
// with the snapshot types, vigil-core (C#) writes core/contract.json with the live stream's. This
// checks web/src/lib/types.ts against both in BOTH directions, so no side can drift silently.
// See docs/STYLE.md §1.3.
//
// How it works: one hand-written key list per wire type, checked twice over.
//   - `satisfies Record<keyof T, 1>` makes the COMPILER reject a list that misses a field of
//     the TypeScript type or invents one that is not in it.
//   - the runtime assertions compare that same list against the generated contract.
// A field added in Rust fails the runtime check; a field added only in TypeScript fails the
// compile-time check. Either way the commit cannot land half-done.

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
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
	UpsLimits,
	UpsReading,
	UpsState
} from './types';

/** The sections a contract file may have. Each program writes only the ones it owns. */
interface ContractHalf {
	$comment: string;
	enums?: Record<string, string[]>;
	types?: Record<string, object>;
	liveMessages?: Record<string, object>;
}

/** Read at test time rather than imported, so the page's own build never depends on these files. */
const half = (path: string) => JSON.parse(readFileSync(new URL(path, import.meta.url), 'utf8')) as ContractHalf;
const daemon = half('../../../daemon/contract.json');
const core = half('../../../core/contract.json');

const contract = {
	enums: { ...daemon.enums, ...core.enums },
	types: { ...daemon.types, ...core.types },
	liveMessages: core.liveMessages ?? {}
};

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
		batteryTemperature: 1,
		batteryDate: 1,
		lowBatteryRuntimeSeconds: 1,
		lowBatteryCharge: 1,
		transferReason: 1,
		outputFrequency: 1,
		outputCurrent: 1,
		monitors: 1,
		variables: 1
	} satisfies Record<keyof UpsReading, 1>,
	UpsLimits: { hostShutdownSeconds: 1, batteryTemperatureWarn: 1 } satisfies Record<keyof UpsLimits, 1>,
	UpsState: { enabled: 1, name: 1, reading: 1, error: 1, limits: 1 } satisfies Record<keyof UpsState, 1>,
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
} satisfies Record<LiveMessage['type'], Record<string, 1>>;

describe('the generated wire contract', () => {
	it('is the pair of files the two programs generate', () => {
		// Each says how it is regenerated; an empty comment means a hand-written file.
		expect(daemon.$comment).not.toBe('');
		expect(core.$comment).not.toBe('');
	});

	it('defines no type twice', () => {
		const both = (a: object | undefined, b: object | undefined) => Object.keys(a ?? {}).filter((key) => Object.keys(b ?? {}).includes(key));
		expect(both(daemon.types, core.types)).toEqual([]);
		expect(both(daemon.enums, core.enums)).toEqual([]);
	});

	it('covers every type this file claims to mirror', () => {
		expect(Object.keys(contract.types).sort()).toEqual(Object.keys(TYPE_KEYS).sort());
		expect(Object.keys(contract.enums).sort()).toEqual(Object.keys(ENUM_MEMBERS).sort());
	});
});

describe.each(Object.entries(TYPE_KEYS))('%s', (name, shape) => {
	it('has exactly the fields its owner serializes', () => {
		const sample = contract.types[name as keyof typeof contract.types];
		expect(fieldsOf(sample)).toEqual(keysOf(shape));
	});
});

describe.each(Object.entries(ENUM_MEMBERS))('%s', (name, members) => {
	it('has exactly the variants its owner serializes', () => {
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
});
