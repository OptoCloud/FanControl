// How a UPS's state reads to a person. Shared by the page's banner and the UPS panel.

import type { UpsState } from '$lib/types';

export interface UpsSummary {
	level: 'good' | 'warning' | 'critical' | 'unknown';
	label: string;
	/** Set when the state is worth a page-wide banner. */
	banner?: string;
}

export function summarizeUps(ups: UpsState): UpsSummary {
	const reading = ups.reading;
	if (!reading) return { level: 'unknown', label: ups.error ? 'Unreadable' : 'Waiting for data' };

	const flags = new Set(reading.status);
	const runtime = reading.batteryRuntimeSeconds === null ? '' : `, about ${formatRuntime(reading.batteryRuntimeSeconds)} left`;
	const charge = reading.batteryCharge === null ? '' : ` at ${Math.round(reading.batteryCharge)}%`;

	if (flags.has('FSD'))
		return { level: 'critical', label: 'Forced shutdown', banner: 'The UPS is in forced shutdown: the load is about to lose power.' };
	if (flags.has('LB'))
		return { level: 'critical', label: 'Battery low', banner: `UPS battery low${charge}. orion shuts itself down on this.` };
	if (flags.has('OB'))
		return { level: 'warning', label: 'On battery', banner: `Mains power lost: running on the UPS battery${charge}${runtime}.` };
	if (flags.has('OFF')) return { level: 'warning', label: 'Off' };
	if (flags.has('BYPASS')) return { level: 'warning', label: 'On bypass' };
	if (flags.has('OVER')) return { level: 'warning', label: 'Overloaded' };
	if (flags.has('RB')) return { level: 'warning', label: 'Replace battery' };
	if (flags.has('OL')) return { level: 'good', label: flags.has('CHRG') ? 'Online, charging' : 'Online' };
	return { level: 'unknown', label: reading.status.join(' ') || 'Status unknown' };
}

/** A status worth a badge, for the rows that judge rather than just report. */
export interface UpsVerdict {
	level: 'good' | 'warning' | 'critical' | 'unknown';
	label: string;
}

/** Whether something will shut the host down: upsd's count of logged-in monitors. */
export function guardVerdict(monitors: number | null): UpsVerdict {
	if (monitors === null) return { level: 'unknown', label: 'upsd would not say' };
	if (monitors === 0) return { level: 'critical', label: 'Nothing: no upsmon is logged in' };
	return { level: 'good', label: monitors === 1 ? 'upsmon' : `${monitors} monitors` };
}

/**
 * Where the shutdown falls against the battery. `startsAt` is the runtime left when the UPS raises LB and
 * upsmon starts the shutdown; `spare` is what is left of that once the host has shut down (negative: the
 * battery runs out first); `untilShutdown` is how long the battery lasts at this load before it starts.
 */
export interface ShutdownMargin {
	startsAt: number;
	spare: number | null;
	untilShutdown: number | null;
}

/** Null when upsd doesn't publish battery.runtime.low: without it there is nothing to measure against. */
export function shutdownMargin(ups: UpsState): ShutdownMargin | null {
	const startsAt = ups.reading?.lowBatteryRuntimeSeconds ?? null;
	if (startsAt === null) return null;

	const takes = ups.limits.hostShutdownSeconds;
	const runtime = ups.reading?.batteryRuntimeSeconds ?? null;
	return {
		startsAt,
		spare: takes === null ? null : startsAt - takes,
		untilShutdown: runtime === null ? null : Math.max(0, runtime - startsAt)
	};
}

export function marginVerdict(margin: ShutdownMargin): UpsVerdict | null {
	if (margin.spare === null) return null;
	return margin.spare >= 0
		? { level: 'good', label: `${formatDuration(margin.spare)} to spare` }
		: { level: 'critical', label: `${formatDuration(-margin.spare)} short` };
}

/** NUT's transfer reasons are CamelCase words: "LowInputVoltage" reads as "Low input voltage". */
export function describeTransferReason(reason: string): string {
	const words = reason.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
	return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Seconds where they matter (a 150 s shutdown is not "3 min"), minutes beyond ten. */
export function formatDuration(seconds: number): string {
	const whole = Math.round(seconds);
	if (whole < 120) return `${whole} s`;
	if (whole >= 600) return formatRuntime(whole);
	const rest = whole % 60;
	return rest === 0 ? `${whole / 60} min` : `${Math.floor(whole / 60)} min ${rest} s`;
}

export function formatRuntime(seconds: number): string {
	const minutes = Math.round(seconds / 60);
	return minutes < 90 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}
