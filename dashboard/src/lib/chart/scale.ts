// Pure chart maths: scales, tick selection and label formatting. No DOM, so all of it is unit-tested.

export interface Scale {
	(value: number): number;
	invert(pixel: number): number;
}

export function linearScale(domain: [number, number], range: [number, number]): Scale {
	const [d0, d1] = domain;
	const [r0, r1] = range;
	const span = d1 - d0 || 1;

	const scale = ((value: number) => r0 + ((value - d0) / span) * (r1 - r0)) as Scale;
	scale.invert = (pixel: number) => d0 + ((pixel - r0) / (r1 - r0 || 1)) * span;
	return scale;
}

/** Round tick values (multiples of 1, 2 or 5 times a power of ten) covering [min, max]. */
export function niceTicks(min: number, max: number, target = 5): number[] {
	if (!Number.isFinite(min) || !Number.isFinite(max)) return [];
	if (min === max) {
		min -= 1;
		max += 1;
	}

	const rough = (max - min) / Math.max(1, target);
	const magnitude = 10 ** Math.floor(Math.log10(rough));
	const step = [1, 2, 5, 10].map((m) => m * magnitude).find((candidate) => candidate >= rough) ?? 10 * magnitude;

	const ticks: number[] = [];
	for (let tick = Math.floor(min / step) * step; tick <= max + step * 1e-9; tick += step) {
		// Snapping removes floating-point residue (0.30000000000000004).
		ticks.push(Number((Math.round(tick / step) * step).toPrecision(12)));
	}
	if (ticks[0] > min) ticks.unshift(ticks[0] - step);
	if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step);
	return ticks;
}

const MINUTE = 60_000;
const HOUR = 3_600_000;
const DAY = 86_400_000;
const TIME_STEPS = [MINUTE, 5 * MINUTE, 10 * MINUTE, 15 * MINUTE, 30 * MINUTE, HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 5 * DAY, 7 * DAY];

/** Tick instants on round local-clock boundaries (whole hours, midnights), at most `maxTicks` of them. */
export function timeTicks(from: number, to: number, maxTicks: number): number[] {
	const step = TIME_STEPS.find((candidate) => (to - from) / candidate <= maxTicks) ?? TIME_STEPS[TIME_STEPS.length - 1];

	// Align to local time, so day ticks land on local midnight rather than UTC midnight.
	const offset = new Date(from).getTimezoneOffset() * MINUTE;
	const first = Math.ceil((from - offset) / step) * step + offset;

	const ticks: number[] = [];
	for (let tick = first; tick <= to; tick += step) ticks.push(tick);
	return ticks;
}

export function formatTick(time: number, spanMs: number): string {
	const date = new Date(time);
	if (spanMs > 2 * DAY) return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
	return date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hour12: false });
}

export function formatInstant(time: number, spanMs: number): string {
	const date = new Date(time);
	const clock = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: spanMs <= HOUR ? '2-digit' : undefined, hour12: false });
	return spanMs > DAY / 2 ? `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}, ${clock}` : clock;
}

/** Index of the point whose time is closest to `time`, in a list sorted by time. -1 if empty. */
export function nearestIndex(points: readonly (readonly [number, number])[], time: number): number {
	if (points.length === 0) return -1;

	let low = 0;
	let high = points.length - 1;
	while (high - low > 1) {
		const middle = (low + high) >> 1;
		if (points[middle][0] <= time) low = middle;
		else high = middle;
	}
	return Math.abs(points[low][0] - time) <= Math.abs(points[high][0] - time) ? low : high;
}

/** "drive-cage" to "Drive cage". */
export function humanize(id: string): string {
	const words = id.replace(/[-_]+/g, ' ').trim();
	return words.charAt(0).toUpperCase() + words.slice(1);
}

export function relativeTime(iso: string, now: number): string {
	const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
	if (seconds < 60) return `${seconds}s ago`;
	if (seconds < 3_600) return `${Math.round(seconds / 60)} min ago`;
	if (seconds < 86_400) return `${Math.round(seconds / 3_600)} h ago`;
	return `${Math.round(seconds / 86_400)} d ago`;
}
