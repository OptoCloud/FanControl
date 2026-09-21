import { describe, expect, it } from 'vitest';
import type { DriveHealth, DriveState, FanStatus, SensorReading, Snapshot } from '$lib/types';
import { ConditionTracker, conditionsIn, diffDriveHealth } from './alerts';

const fan = (overrides: Partial<FanStatus> = {}): FanStatus => ({ id: 'drive-cage', dutyPercent: 65, rpm: 1200, mode: 'manual', stalled: false, ...overrides });

const sensor = (overrides: Partial<SensorReading> = {}): SensorReading => ({
	id: 'cpu',
	category: 'cpu',
	label: 'Tctl',
	celsiusOrNull: 40,
	sourcePath: 'path',
	isAvailable: true,
	...overrides
});

const snapshot = (overrides: Partial<Snapshot> = {}): Snapshot => ({
	timestampUtc: '2026-01-01T00:00:00.000Z',
	sensors: [sensor()],
	fans: [fan()],
	driveHealth: [],
	controlLoopHealthy: true,
	...overrides
});

describe('conditionsIn', () => {
	it('finds nothing wrong with a healthy snapshot', () => {
		expect(conditionsIn(snapshot(), new Set(['cpu'])).size).toBe(0);
	});

	it('flags an unhealthy loop, a stalled fan and a fan that is not on manual', () => {
		const conditions = conditionsIn(
			snapshot({ controlLoopHealthy: false, fans: [fan({ stalled: true, rpm: 0 }), fan({ id: 'intake', mode: 'smartFanIV' }), fan({ id: 'rear', mode: null })] }),
			new Set()
		);

		expect([...conditions.keys()].sort()).toEqual(['fan-mode:intake', 'fan-mode:rear', 'fan-stalled:drive-cage', 'loop-unhealthy']);
		expect(conditions.get('fan-stalled:drive-cage')?.severity).toBe('critical');
		expect(conditions.get('fan-mode:rear')?.message).toContain('unreadable');
	});

	it('treats an unreadable named sensor as a problem but a sleeping drive as normal', () => {
		const conditions = conditionsIn(
			snapshot({
				sensors: [
					sensor({ id: 'gpu', category: 'gpu', celsiusOrNull: null, isAvailable: false }),
					sensor({ id: 'drive:naa.1', category: 'drive', celsiusOrNull: null, isAvailable: false })
				]
			}),
			new Set()
		);

		expect([...conditions.keys()]).toEqual(['sensor-unavailable:gpu']);
	});

	it('flags a sensor that was there before and is now gone entirely', () => {
		const conditions = conditionsIn(snapshot(), new Set(['cpu', 'drive:naa.1']));

		expect([...conditions.keys()]).toEqual(['sensor-missing:drive:naa.1']);
	});
});

describe('ConditionTracker', () => {
	const stalled = snapshot({ fans: [fan({ stalled: true })] });
	const healthy = snapshot();
	const step = (tracker: ConditionTracker, s: Snapshot) => tracker.update(conditionsIn(s, new Set()));

	it('raises only after the condition has held for the threshold, and only once', () => {
		const tracker = new ConditionTracker(3);

		expect(step(tracker, stalled)).toEqual([]);
		expect(step(tracker, stalled)).toEqual([]);
		expect(step(tracker, stalled).map((e) => [e.severity, e.kind])).toEqual([['critical', 'fan-stalled:drive-cage']]);
		expect(step(tracker, stalled)).toEqual([]);
		expect(tracker.activeKinds).toEqual(['fan-stalled:drive-cage']);
	});

	it('ignores a blip shorter than the threshold', () => {
		const tracker = new ConditionTracker(3);

		step(tracker, stalled);
		step(tracker, stalled);
		step(tracker, healthy);

		expect(step(tracker, stalled)).toEqual([]);
		expect(step(tracker, stalled)).toEqual([]);
	});

	it('clears with an info event only after staying away for the threshold', () => {
		const tracker = new ConditionTracker(2);
		step(tracker, stalled);
		step(tracker, stalled);

		expect(step(tracker, healthy)).toEqual([]);
		// It comes back before clearing: still the same incident, no new event.
		expect(step(tracker, stalled)).toEqual([]);
		expect(step(tracker, healthy)).toEqual([]);
		expect(step(tracker, healthy)).toEqual([{ severity: 'info', kind: 'fan-stalled:drive-cage', message: "Fan 'drive-cage' is spinning again." }]);
		expect(tracker.activeKinds).toEqual([]);
	});
});

describe('diffDriveHealth', () => {
	const health = (overrides: Partial<DriveHealth> = {}): DriveHealth => ({
		deviceName: 'naa.1',
		passed: true,
		reallocatedSectorCount: 0,
		pendingSectorCount: 0,
		powerOnHours: 1000,
		sourcePath: '/dev/sda',
		isAvailable: true,
		asOf: '2026-01-01T00:15:00.000Z',
		...overrides
	});

	const stateOf = (h: DriveHealth): DriveState => diffDriveHealth(undefined, h).state as DriveState;

	it('keeps the previous state when the drive is asleep', () => {
		const update = diffDriveHealth(stateOf(health()), health({ isAvailable: false, passed: null, reallocatedSectorCount: null }));

		expect(update).toEqual({ state: null, changed: false, events: [] });
	});

	it('records a first sighting without alerting on a long-standing reallocated count', () => {
		const update = diffDriveHealth(undefined, health({ reallocatedSectorCount: 1048 }));

		expect(update.changed).toBe(true);
		expect(update.events).toEqual([]);
		expect(update.state?.reallocatedSectorCount).toBe(1048);
	});

	it('is quiet while nothing but power-on hours moves', () => {
		const update = diffDriveHealth(stateOf(health({ reallocatedSectorCount: 1048 })), health({ reallocatedSectorCount: 1048, powerOnHours: 1001 }));

		expect(update.changed).toBe(false);
		expect(update.events).toEqual([]);
		expect(update.state?.powerOnHours).toBe(1001);
	});

	it('warns when the reallocated count grows', () => {
		const update = diffDriveHealth(stateOf(health({ reallocatedSectorCount: 1048 })), health({ reallocatedSectorCount: 1056 }));

		expect(update.changed).toBe(true);
		expect(update.events).toHaveLength(1);
		expect(update.events[0]).toMatchObject({ severity: 'warning', kind: 'drive-reallocated:naa.1' });
		expect(update.events[0].message).toContain('1048 to 1056');
	});

	it('warns when pending sectors appear or change, and notes when they are gone', () => {
		const clean = stateOf(health());
		const withPending = stateOf(health({ pendingSectorCount: 8 }));

		expect(diffDriveHealth(clean, health({ pendingSectorCount: 8 })).events[0]).toMatchObject({ severity: 'warning', kind: 'drive-pending:naa.1' });
		expect(diffDriveHealth(undefined, health({ pendingSectorCount: 8 })).events).toHaveLength(1);
		expect(diffDriveHealth(withPending, health({ pendingSectorCount: 8 })).events).toEqual([]);
		expect(diffDriveHealth(withPending, health({ pendingSectorCount: 0 })).events[0].severity).toBe('info');
	});

	it('does not invent a pending-sector event for a drive that does not report the attribute', () => {
		expect(diffDriveHealth(undefined, health({ pendingSectorCount: null })).events).toEqual([]);
	});

	it('raises a failed self-assessment once, and notes recovery', () => {
		const failed = health({ passed: false });

		expect(diffDriveHealth(stateOf(health()), failed).events[0]).toMatchObject({ severity: 'critical', kind: 'drive-failed:naa.1' });
		expect(diffDriveHealth(undefined, failed).events).toHaveLength(1);
		expect(diffDriveHealth(stateOf(failed), failed).events).toEqual([]);
		expect(diffDriveHealth(stateOf(failed), health()).events[0].severity).toBe('info');
	});
});
