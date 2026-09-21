// Turns the daemon's stream of snapshots into discrete events ("drive-cage stalled",
// "reallocated sectors grew"). The daemon deliberately reports only the present; noticing
// that something CHANGED needs memory, and that lives here. Everything in this file is
// pure, so it is fully unit-tested; runtime.ts wires it to the database and notifications.

import type { DriveHealth, DriveState, Severity, Snapshot } from '$lib/types';

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
				message: `Sensor '${sensor.id}' (${sensor.label}) is unreadable. Curves that name it run at their fail-safe floor.`,
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
