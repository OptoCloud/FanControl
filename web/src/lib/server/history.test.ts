import { describe, expect, it } from 'vitest';
import { hottestOf, isRangeKey, rangeSpec } from './history';

describe('rangeSpec', () => {
	it('keeps every range to a few hundred points per series', () => {
		for (const range of ['1h', '6h', '24h', '7d', '30d'] as const) {
			const { seconds, bucketSeconds } = rangeSpec(range);
			const points = seconds / bucketSeconds;

			expect(points).toBeGreaterThanOrEqual(250);
			expect(points).toBeLessThanOrEqual(400);
		}
	});

	it('only reads raw samples for the shortest range, since they are pruned after a few days', () => {
		expect(rangeSpec('1h').source).toBe('raw');
		expect(rangeSpec('30d').source).toBe('minutes');
	});

	it('rejects unknown range keys', () => {
		expect(isRangeKey('24h')).toBe(true);
		expect(isRangeKey('2h')).toBe(false);
		expect(isRangeKey(null)).toBe(false);
	});
});

describe('hottestOf', () => {
	it('takes the maximum per bucket across series with different coverage', () => {
		const a: [number, number][] = [[1000, 30], [2000, 35]];
		const b: [number, number][] = [[2000, 33], [3000, 40], [1000, 31]];

		expect(hottestOf([a, b])).toEqual([[1000, 31], [2000, 35], [3000, 40]]);
	});

	it('is empty for an empty group', () => {
		expect(hottestOf([])).toEqual([]);
	});
});
