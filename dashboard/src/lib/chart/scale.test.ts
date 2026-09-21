import { describe, expect, it } from 'vitest';
import { humanize, linearScale, nearestIndex, niceTicks, relativeTime, timeTicks } from './scale';

describe('linearScale', () => {
	it('maps the domain onto the range and back, including an inverted pixel range', () => {
		const y = linearScale([20, 60], [200, 0]);

		expect(y(20)).toBe(200);
		expect(y(60)).toBe(0);
		expect(y(40)).toBe(100);
		expect(y.invert(100)).toBe(40);
	});

	it('does not divide by zero on a flat domain', () => {
		expect(Number.isFinite(linearScale([5, 5], [0, 100])(5))).toBe(true);
	});
});

describe('niceTicks', () => {
	it('picks round steps that cover the data', () => {
		expect(niceTicks(28.4, 54.2)).toEqual([20, 30, 40, 50, 60]);
		expect(niceTicks(0, 100)).toEqual([0, 20, 40, 60, 80, 100]);
	});

	it('has no floating-point residue', () => {
		expect(niceTicks(0, 1)).toEqual([0, 0.2, 0.4, 0.6, 0.8, 1]);
	});

	it('copes with a single value and with bad input', () => {
		const ticks = niceTicks(40, 40);
		expect(ticks[0]).toBeLessThanOrEqual(39);
		expect(ticks[ticks.length - 1]).toBeGreaterThanOrEqual(41);
		expect(niceTicks(NaN, 5)).toEqual([]);
	});
});

describe('timeTicks', () => {
	it('stays within the window and under the tick budget', () => {
		const from = Date.UTC(2026, 8, 21, 20, 7);
		const to = from + 6 * 3_600_000;

		const ticks = timeTicks(from, to, 6);

		expect(ticks.length).toBeGreaterThan(2);
		expect(ticks.length).toBeLessThanOrEqual(7);
		expect(ticks.every((tick) => tick >= from && tick <= to)).toBe(true);
	});

	it('lands on round clock boundaries', () => {
		const from = Date.UTC(2026, 8, 21, 20, 7);

		for (const tick of timeTicks(from, from + 3_600_000, 6)) {
			expect(new Date(tick).getMinutes() % 10).toBe(0);
			expect(new Date(tick).getSeconds()).toBe(0);
		}
	});
});

describe('nearestIndex', () => {
	const points: [number, number][] = [[100, 1], [200, 2], [300, 3], [1000, 4]];

	it('finds the closest point, clamping outside the data', () => {
		expect(nearestIndex(points, 0)).toBe(0);
		expect(nearestIndex(points, 149)).toBe(0);
		expect(nearestIndex(points, 151)).toBe(1);
		expect(nearestIndex(points, 700)).toBe(3);
		expect(nearestIndex(points, 5000)).toBe(3);
	});

	it('handles empty and single-point series', () => {
		expect(nearestIndex([], 5)).toBe(-1);
		expect(nearestIndex([[100, 1]], 5)).toBe(0);
	});
});

describe('labels', () => {
	it('humanizes ids', () => {
		expect(humanize('intake-gpu-lsi')).toBe('Intake gpu lsi');
	});

	it('describes ages in the largest sensible unit', () => {
		const now = Date.UTC(2026, 0, 2);

		expect(relativeTime(new Date(now - 5_000).toISOString(), now)).toBe('5s ago');
		expect(relativeTime(new Date(now - 15 * 60_000).toISOString(), now)).toBe('15 min ago');
		expect(relativeTime(new Date(now - 3 * 3_600_000).toISOString(), now)).toBe('3 h ago');
		expect(relativeTime(new Date(now + 60_000).toISOString(), now)).toBe('0s ago');
	});
});
