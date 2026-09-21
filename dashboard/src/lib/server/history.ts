// Chart history queries. Every range is bucketed in SQL down to a few hundred points per
// series, so a 30-day chart costs the browser no more than a 1-hour one.

import type { HistoryResponse, RangeKey, SeriesPoints } from '$lib/types';
import type { Sql } from './db';

export interface RangeSpec {
	seconds: number;
	bucketSeconds: number;
	/** Raw samples only exist for a few days, and only the shortest range needs their resolution. */
	source: 'raw' | 'minutes';
}

const RANGES: Record<RangeKey, RangeSpec> = {
	'1h': { seconds: 3_600, bucketSeconds: 10, source: 'raw' },
	'6h': { seconds: 21_600, bucketSeconds: 60, source: 'minutes' },
	'24h': { seconds: 86_400, bucketSeconds: 300, source: 'minutes' },
	'7d': { seconds: 604_800, bucketSeconds: 1_800, source: 'minutes' },
	'30d': { seconds: 2_592_000, bucketSeconds: 7_200, source: 'minutes' }
};

export function isRangeKey(value: string | null): value is RangeKey {
	return value !== null && value in RANGES;
}

export function rangeSpec(range: RangeKey): RangeSpec {
	return RANGES[range];
}

/** The hottest member of a group per bucket, e.g. one "drives" line instead of thirteen. */
export function hottestOf(series: SeriesPoints[]): SeriesPoints {
	const hottest = new Map<number, number>();
	for (const points of series) {
		for (const [time, value] of points) {
			const current = hottest.get(time);
			if (current === undefined || value > current) hottest.set(time, value);
		}
	}
	return [...hottest.entries()].sort((a, b) => a[0] - b[0]);
}

export async function queryHistory(sql: Sql, range: RangeKey, now = new Date()): Promise<HistoryResponse> {
	const spec = rangeSpec(range);
	const from = new Date(now.getTime() - spec.seconds * 1000);
	const bucket = `${spec.bucketSeconds} seconds`;

	const sensorRows =
		spec.source === 'raw'
			? await sql`
					select sensor_id as id, date_bin(${bucket}::interval, ts, 'epoch'::timestamptz) as bucket, avg(celsius)::float8 as value
					from sensor_samples where ts >= ${from} group by 1, 2 order by 2`
			: await sql`
					select sensor_id as id, date_bin(${bucket}::interval, bucket, 'epoch'::timestamptz) as bucket,
						(sum(avg_celsius * samples) / sum(samples))::float8 as value
					from sensor_minutes where bucket >= ${from} group by 1, 2 order by 2`;

	const fanRows =
		spec.source === 'raw'
			? await sql`
					select fan_id as id, date_bin(${bucket}::interval, ts, 'epoch'::timestamptz) as bucket,
						avg(duty_percent)::float8 as duty, avg(rpm)::float8 as rpm
					from fan_samples where ts >= ${from} group by 1, 2 order by 2`
			: await sql`
					select fan_id as id, date_bin(${bucket}::interval, bucket, 'epoch'::timestamptz) as bucket,
						(sum(avg_duty * samples) / sum(samples))::float8 as duty, avg(avg_rpm)::float8 as rpm
					from fan_minutes where bucket >= ${from} group by 1, 2 order by 2`;

	const temperatures: Record<string, SeriesPoints> = {};
	for (const row of sensorRows) {
		(temperatures[row.id] ??= []).push([(row.bucket as Date).getTime(), round(row.value, 2)]);
	}

	const duties: Record<string, SeriesPoints> = {};
	const rpms: Record<string, SeriesPoints> = {};
	for (const row of fanRows) {
		const time = (row.bucket as Date).getTime();
		(duties[row.id] ??= []).push([time, round(row.duty, 1)]);
		if (row.rpm !== null) (rpms[row.id] ??= []).push([time, Math.round(row.rpm)]);
	}

	const group = (prefix: string) => Object.entries(temperatures).filter(([id]) => id.startsWith(prefix)).map(([, points]) => points);
	temperatures['drives:max'] = hottestOf(group('drive:'));
	temperatures['memory:max'] = hottestOf(group('dimm:'));

	return { range, from: from.getTime(), to: now.getTime(), bucketSeconds: spec.bucketSeconds, temperatures, duties, rpms };
}

function round(value: number, digits: number): number {
	const factor = 10 ** digits;
	return Math.round(value * factor) / factor;
}
