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

export function formatRuntime(seconds: number): string {
	const minutes = Math.round(seconds / 60);
	return minutes < 90 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}
