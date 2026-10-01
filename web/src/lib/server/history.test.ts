import { describe, expect, it } from 'vitest';
import { hottestOf, isRangeKey, rangeSpec } from './history';

describe('rangeSpec', () => {
	it('keeps every range to a few hundred points per series', () => {
		for (const range of ['1h', '6h', '24h', '7d', '30d', '1y'] as const) {
			const { seconds, bucketSeconds } = rangeSpec(range);
			const points = seconds / bucketSeconds;

			expect(points).toBeGreaterThanOrEqual(250);
			expect(points).toBeLessThanOrEqual(400);
		}
	});

	it('reads each level only within its retention: raw 30 days, minutes a year, hours forever', () => {
		const kept = { raw: 30 * 86_400, '1m': 365 * 86_400, '1h': Infinity };
		for (const range of ['1h', '6h', '24h', '7d', '30d', '1y'] as const) {
			const { seconds, bucketSeconds, source } = rangeSpec(range);
			expect(seconds).toBeLessThanOrEqual(kept[source]);
			// A bucket never finer than the level it reads.
			expect(bucketSeconds).toBeGreaterThanOrEqual({ raw: 10, '1m': 60, '1h': 3_600 }[source]);
		}
	});

	it('rejects unknown range keys', () => {
		expect(isRangeKey('24h')).toBe(true);
		expect(isRangeKey('2h')).toBe(false);
		expect(isRangeKey(null)).toBe(false);
	});
});

describe('hottestOf', () => {
	it('takes the maximum per bucket across series with different coverage', () => {
		const a: [number, number][] = [
			[1000, 30],
			[2000, 35]
		];
		const b: [number, number][] = [
			[2000, 33],
			[3000, 40],
			[1000, 31]
		];

		expect(hottestOf([a, b])).toEqual([
			[1000, 31],
			[2000, 35],
			[3000, 40]
		]);
	});

	it('is empty for an empty group', () => {
		expect(hottestOf([])).toEqual([]);
	});
});
