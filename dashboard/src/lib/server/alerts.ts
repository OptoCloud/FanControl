// Turns the daemon's stream of snapshots into discrete events ("drive-cage stalled",
// "reallocated sectors grew", "on battery"). The daemon deliberately reports only the present; noticing
// that something CHANGED needs memory, and that lives here. Everything in this file is
// pure, so it is fully unit-tested; runtime.ts wires it to the database and notifications.

import type { DriveHealth, DriveState, Severity, Snapshot, UpsState } from '$lib/types';

export interface NewEvent {
	severity: Severity;
	kind: string;
	message: string;
}

interface Condition {
	severity: Severity;
	message: string;
	/** What to log when it goes away. */
	cleared: string;
}

/** Every problem visible in one snapshot, keyed by a stable id for the condition. */
export function conditionsIn(snapshot: Snapshot, knownSensorIds: ReadonlySet<string>): Map<string, Condition> {
	const conditions = new Map<string, Condition>();

	if (!snapshot.controlLoopHealthy) {
		conditions.set('loop-unhealthy', {
			severity: 'critical',
			message: 'The control loop reports unhealthy: a fan channel could not be driven, or the loop has stopped polling.',
			cleared: 'The control loop is healthy again.'
		});
	}

	for (const fan of snapshot.fans) {
		if (fan.stalled) {
			conditions.set(`fan-stalled:${fan.id}`, {
				severity: 'critical',
				message: `Fan '${fan.id}' reads 0 RPM at ${fan.dutyPercent}% duty: dead, jammed or unplugged?`,
				cleared: `Fan '${fan.id}' is spinning again.`
			});
		}

		if (fan.mode !== 'manual') {
			conditions.set(`fan-mode:${fan.id}`, {
				severity: 'warning',
				message: `Fan '${fan.id}' is not under the daemon's control (pwm mode: ${fan.mode ?? 'unreadable'}).`,
				cleared: `Fan '${fan.id}' is back under the daemon's control.`
			});
		}
	}

	const present = new Set(snapshot.sensors.map((sensor) => sensor.id));
	for (const sensor of snapshot.sensors) {
		// A drive with no temperature is usually just asleep, which is not a problem.
		if (!sensor.isAvailable && sensor.category !== 'drive') {
			conditions.set(`sensor-unavailable:${sensor.id}`, {
				severity: 'warning',
				message: `Sensor '${sensor.id}' (${sensor.label}) is unreadable. Curves that name it run at no less than their fail-safe duty.`,
				cleared: `Sensor '${sensor.id}' is readable again.`
			});
		}
	}

	for (const id of knownSensorIds) {
		if (!present.has(id)) {
			conditions.set(`sensor-missing:${id}`, {
				severity: 'warning',
				message: `Sensor '${id}' has disappeared from the host.`,
				cleared: `Sensor '${id}' is back.`
			});
		}
	}

	return conditions;
}

/**
 * Every problem visible in the UPS's state. `unreachableForMs` is how long there has been no
 * reading: a single dropped poll or an upsd restart is not worth an event, a lasting gap is.
 */
export function upsConditions(ups: UpsState, unreachableForMs: number): Map<string, Condition> {
	const conditions = new Map<string, Condition>();
	if (!ups.enabled) return conditions;

	const reading = ups.reading;
	if (!reading) {
		if (unreachableForMs >= 30_000) {
			conditions.set('ups-unreadable', {
				severity: 'warning',
				message: `The UPS can't be read (${ups.error ?? 'no reason given'}). A power cut would not show up here; orion's own upsmon is unaffected.`,
				cleared: 'The UPS is readable again.'
			});
		}
		return conditions;
	}

	const flags = new Set(reading.status);
	const charge = reading.batteryCharge === null ? '' : ` Battery at ${Math.round(reading.batteryCharge)}%`;
	const runtime = reading.batteryRuntimeSeconds === null ? '' : `, about ${Math.round(reading.batteryRuntimeSeconds / 60)} min of runtime left`;

	if (flags.has('OB')) {
		conditions.set('ups-on-battery', {
			severity: 'warning',
			message: `Mains power lost: the UPS is on battery.${charge}${runtime}.`,
			cleared: 'Mains power is back: the UPS is online again.'
		});
	}
	if (flags.has('LB')) {
		conditions.set('ups-low-battery', {
			severity: 'critical',
			message: `The UPS battery is LOW.${charge}. orion's upsmon shuts the host down on this.`,
			cleared: 'The UPS battery is no longer low.'
		});
	}
	if (flags.has('FSD')) {
		conditions.set('ups-forced-shutdown', {
			severity: 'critical',
			message: 'The UPS is in forced shutdown (FSD): the load is about to lose power.',
			cleared: 'Forced shutdown is over.'
		});
	}
	if (flags.has('RB')) {
		conditions.set('ups-replace-battery', {
			severity: 'warning',
			message: 'The UPS reports its battery needs replacing.',
			cleared: 'The UPS no longer reports a battery to replace.'
		});
	}
	if (flags.has('OVER')) {
		conditions.set('ups-overload', {
			severity: 'warning',
			message: `The UPS is overloaded${reading.load === null ? '' : ` (${Math.round(reading.load)}% load)`}.`,
			cleared: 'The UPS is no longer overloaded.'
		});
	}
	if (flags.has('BYPASS') || flags.has('OFF')) {
		conditions.set('ups-not-protecting', {
			severity: 'warning',
			message: `The UPS is ${flags.has('OFF') ? 'off' : 'on bypass'}: the load is not protected.`,
			cleared: 'The UPS is protecting the load again.'
		});
	}

	return conditions;
}

/**
 * Debounces conditions into raise/clear events. A condition has to hold for `threshold`
 * consecutive snapshots before it is raised, and be absent for as many before it clears,
 * so a single odd poll (nvidia-smi timing out once) never produces a notification.
 */
export class ConditionTracker {
	private readonly pending = new Map<string, number>();
	private readonly raised = new Map<string, { condition: Condition; absentFor: number }>();

	constructor(private readonly threshold = 3) {}

	update(conditions: ReadonlyMap<string, Condition>): NewEvent[] {
		const events: NewEvent[] = [];

		for (const [kind, condition] of conditions) {
			const active = this.raised.get(kind);
			if (active) {
				active.absentFor = 0;
				continue;
			}

			const seen = (this.pending.get(kind) ?? 0) + 1;
			if (seen >= this.threshold) {
				this.pending.delete(kind);
				this.raised.set(kind, { condition, absentFor: 0 });
				events.push({ severity: condition.severity, kind, message: condition.message });
			} else {
				this.pending.set(kind, seen);
			}
		}

		for (const kind of [...this.pending.keys()]) {
			if (!conditions.has(kind)) this.pending.delete(kind);
		}

		for (const [kind, active] of [...this.raised]) {
			if (conditions.has(kind)) continue;
			active.absentFor += 1;
			if (active.absentFor >= this.threshold) {
				this.raised.delete(kind);
				events.push({ severity: 'info', kind, message: active.condition.cleared });
			}
		}

		return events;
	}

	get activeKinds(): string[] {
		return [...this.raised.keys()];
	}
}

export interface DriveUpdate {
	/** The drive's new last-good state, or null if this poll had nothing usable (asleep, smartctl failed). */
	state: DriveState | null;
	/** True if anything other than power-on hours moved, i.e. worth a history row. */
	changed: boolean;
	events: NewEvent[];
}

/**
 * Compares one drive's fresh health poll with its last good state. smart_status.passed is a
 * lagging indicator (drives routinely die with it still true), so sector counts are watched
 * too, but on CHANGE rather than level: a drive with a long-standing, stable reallocated
 * count would otherwise alert forever.
 */
export function diffDriveHealth(previous: DriveState | undefined, current: DriveHealth): DriveUpdate {
	if (!current.isAvailable) {
		return { state: null, changed: false, events: [] };
	}

	const state: DriveState = {
		wwn: current.deviceName,
		port: current.port ?? null,
		passed: current.passed,
		reallocatedSectorCount: current.reallocatedSectorCount,
		pendingSectorCount: current.pendingSectorCount,
		powerOnHours: current.powerOnHours,
		sourcePath: current.sourcePath,
		asOf: current.asOf
	};

	const events: NewEvent[] = [];
	const name = `Drive ${current.deviceName} (${current.sourcePath})`;

	if (current.passed === false && previous?.passed !== false) {
		events.push({ severity: 'critical', kind: `drive-failed:${state.wwn}`, message: `${name} FAILED its SMART overall-health self-assessment.` });
	} else if (current.passed === true && previous?.passed === false) {
		events.push({ severity: 'info', kind: `drive-failed:${state.wwn}`, message: `${name} passes its SMART self-assessment again.` });
	}

	const reallocated = current.reallocatedSectorCount;
	const previousReallocated = previous?.reallocatedSectorCount;
	if (reallocated != null && previousReallocated != null && reallocated > previousReallocated) {
		events.push({
			severity: 'warning',
			kind: `drive-reallocated:${state.wwn}`,
			message: `${name}: reallocated sector count grew from ${previousReallocated} to ${reallocated}.`
		});
	}

	const pending = current.pendingSectorCount;
	const previousPending = previous?.pendingSectorCount ?? 0;
	if (pending != null && pending !== previousPending) {
		events.push(
			pending > 0
				? { severity: 'warning', kind: `drive-pending:${state.wwn}`, message: `${name}: ${pending} pending (unreadable, not yet reallocated) sector(s), was ${previousPending}.` }
				: { severity: 'info', kind: `drive-pending:${state.wwn}`, message: `${name}: no pending sectors any more (was ${previousPending}).` }
		);
	}

	const changed =
		previous === undefined ||
		previous.passed !== state.passed ||
		previous.reallocatedSectorCount !== state.reallocatedSectorCount ||
		previous.pendingSectorCount !== state.pendingSectorCount;

	return { state, changed, events };
}
