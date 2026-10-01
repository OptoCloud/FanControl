import { describe, expect, it } from 'vitest';
import type { UpsLimits, UpsState } from './types';
import { describeTransferReason, formatDuration, guardVerdict, marginVerdict, shutdownMargin, summarizeUps } from './ups';

interface Setup {
	lowBatteryRuntime?: number;
	hostShutdown?: number;
	monitors?: number | null;
	status?: string[];
}

function ups({ lowBatteryRuntime, hostShutdown, monitors = 1, status = ['OL'] }: Setup, runtime: number | null = 2640): UpsState {
	const limits: UpsLimits = { hostShutdownSeconds: hostShutdown ?? null, batteryTemperatureWarn: 40 };
	return {
		enabled: true,
		name: 'apc',
		error: null,
		limits,
		reading: {
			timestampUtc: 't',
			name: 'apc',
			model: null,
			status,
			batteryCharge: 100,
			batteryRuntimeSeconds: runtime,
			load: 26,
			realPower: null,
			inputVoltage: null,
			outputVoltage: null,
			batteryVoltage: null,
			batteryTemperature: null,
			batteryDate: null,
			lowBatteryRuntimeSeconds: lowBatteryRuntime ?? null,
			lowBatteryCharge: 30,
			transferReason: null,
			outputFrequency: null,
			outputCurrent: null,
			monitors,
			variables: {}
		}
	};
}

describe('shutdownMargin', () => {
	it('says how much of the low-battery runtime the shutdown leaves, and how long until it starts', () => {
		expect(shutdownMargin(ups({ lowBatteryRuntime: 150, hostShutdown: 84 }))).toEqual({
			startsAt: 150,
			spare: 66,
			untilShutdown: 2490
		});
	});

	it('goes negative when the battery would run out before the shutdown finishes', () => {
		const margin = shutdownMargin(ups({ lowBatteryRuntime: 84, hostShutdown: 150 }));

		expect(margin?.spare).toBe(-66);
		expect(margin && marginVerdict(margin)).toEqual({ level: 'critical', label: '66 s short' });
	});

	it('has nothing to measure without the low-battery runtime, and no verdict without the shutdown time', () => {
		expect(shutdownMargin(ups({ hostShutdown: 84 }))).toBeNull();

		const margin = shutdownMargin(ups({ lowBatteryRuntime: 150 }, null));
		expect(margin).toEqual({ startsAt: 150, spare: null, untilShutdown: null });
		expect(margin && marginVerdict(margin)).toBeNull();
	});

	it('never counts down past zero once the runtime is under the threshold', () => {
		expect(shutdownMargin(ups({ lowBatteryRuntime: 150 }, 100))?.untilShutdown).toBe(0);
	});
});

describe('guardVerdict', () => {
	it('is critical only for a count of zero, never for an unknown one', () => {
		expect(guardVerdict(0).level).toBe('critical');
		expect(guardVerdict(null).level).toBe('unknown');
		expect(guardVerdict(1)).toEqual({ level: 'good', label: 'upsmon' });
		expect(guardVerdict(2)).toEqual({ level: 'good', label: '2 monitors' });
	});
});

describe('describeTransferReason', () => {
	it('splits NUT CamelCase into a sentence', () => {
		expect(describeTransferReason('AcceptableInput')).toBe('Acceptable input');
		expect(describeTransferReason('LowInputVoltage')).toBe('Low input voltage');
		expect(describeTransferReason('none')).toBe('None');
	});
});

describe('formatDuration', () => {
	it('keeps the seconds up to ten minutes, and drops them beyond', () => {
		expect(formatDuration(66.4)).toBe('66 s');
		expect(formatDuration(150)).toBe('2 min 30 s');
		expect(formatDuration(180)).toBe('3 min');
		expect(formatDuration(600)).toBe('10 min');
	});
});

describe('summarizeUps', () => {
	it('is critical when nothing would shut orion down', () => {
		expect(summarizeUps(ups({ monitors: 0 }))).toMatchObject({ level: 'critical', label: 'No shutdown guard' });
	});

	it('still leads with a power event while there is one', () => {
		expect(summarizeUps(ups({ monitors: 0, status: ['OB'] })).label).toBe('On battery');
	});

	it('does not read an unknown count as none', () => {
		expect(summarizeUps(ups({ monitors: null })).label).toBe('Online');
	});
});
