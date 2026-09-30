// Chart history queries. Every range is bucketed in SQL down to a few hundred points per
// series, so a one-year chart costs the browser no more than a one-hour one. Each range reads
// the coarsest level that still has its resolution: raw samples (kept 30 days), or
// vigil-core's TimescaleDB continuous aggregates, *_1m (kept a year) and *_1h (kept forever).

import type { HistoryResponse, RangeKey, SeriesPoints, UpsMetric } from '$lib/types';
import type { Sql } from './db';

export interface RangeSpec {
	seconds: number;
	bucketSeconds: number;
	source: 'raw' | '1m' | '1h';
}

const RANGES: Record<RangeKey, RangeSpec> = {
	'1h': { seconds: 3_600, bucketSeconds: 10, source: 'raw' },
	'6h': { seconds: 21_600, bucketSeconds: 60, source: 'raw' },
	'24h': { seconds: 86_400, bucketSeconds: 300, source: '1m' },
	'7d': { seconds: 604_800, bucketSeconds: 1_800, source: '1m' },
	'30d': { seconds: 2_592_000, bucketSeconds: 7_200, source: '1h' },
	'1y': { seconds: 31_536_000, bucketSeconds: 86_400, source: '1h' }
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

/** `upsName`: the UPS whose history to include, or undefined without NUT. */
export async function queryHistory(sql: Sql, range: RangeKey, upsName: string | undefined, now = new Date()): Promise<HistoryResponse> {
	const spec = rangeSpec(range);
	const from = new Date(now.getTime() - spec.seconds * 1000);
	const bucket = `${spec.bucketSeconds} seconds`;

	// The aggregates share their raw table's prefix: sensor_1m, fan_1h, ups_1m, ...
	const level = (prefix: string) => sql(`${prefix}_${spec.source}`);

	const sensorRows =
		spec.source === 'raw'
			? await sql`
					select sensor_id as id, date_bin(${bucket}::interval, ts, 'epoch'::timestamptz) as bucket, avg(celsius)::float8 as value
					from sensor_samples where ts >= ${from} group by 1, 2 order by 2`
			: await sql`
					select sensor_id as id, date_bin(${bucket}::interval, bucket, 'epoch'::timestamptz) as bucket,
						(sum(avg_celsius * samples) / sum(samples))::float8 as value
					from ${level('sensor')} where bucket >= ${from} group by 1, 2 order by 2`;

	const fanRows =
		spec.source === 'raw'
			? await sql`
					select fan_id as id, date_bin(${bucket}::interval, ts, 'epoch'::timestamptz) as bucket,
						avg(duty_percent)::float8 as duty, avg(rpm)::float8 as rpm
					from fan_samples where ts >= ${from} group by 1, 2 order by 2`
			: await sql`
					select fan_id as id, date_bin(${bucket}::interval, bucket, 'epoch'::timestamptz) as bucket,
						(sum(avg_duty * samples) / sum(samples))::float8 as duty,
						(sum(avg_rpm * samples) / nullif(sum(case when avg_rpm is not null then samples else 0 end), 0))::float8 as rpm
					from ${level('fan')} where bucket >= ${from} group by 1, 2 order by 2`;

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

	const ups: Partial<Record<UpsMetric, SeriesPoints>> = {};
	if (upsName) {
		const upsRows =
			spec.source === 'raw'
				? await sql`
						select date_bin(${bucket}::interval, ts, 'epoch'::timestamptz) as bucket,
							avg(charge)::float8 as charge, avg(load)::float8 as load,
							avg(runtime_seconds)::float8 as runtime, avg(input_voltage)::float8 as input_voltage
						from ups_samples where ups = ${upsName} and ts >= ${from} group by 1 order by 1`
				: await sql`
						select date_bin(${bucket}::interval, bucket, 'epoch'::timestamptz) as bucket,
							(sum(avg_charge * samples) / sum(samples))::float8 as charge, (sum(avg_load * samples) / sum(samples))::float8 as load,
							(sum(avg_runtime_seconds * samples) / sum(samples))::float8 as runtime,
							(sum(avg_input_voltage * samples) / sum(samples))::float8 as input_voltage
						from ${level('ups')} where ups = ${upsName} and bucket >= ${from} group by 1 order by 1`;

		const add = (metric: UpsMetric, time: number, value: number | null, digits: number) => {
			if (value !== null) (ups[metric] ??= []).push([time, round(value, digits)]);
		};
		for (const row of upsRows) {
			const time = (row.bucket as Date).getTime();
			add('charge', time, row.charge, 1);
			add('load', time, row.load, 1);
			add('runtime', time, row.runtime === null ? null : row.runtime / 60, 1);
			add('inputVoltage', time, row.input_voltage, 1);
		}
	}

	const group = (prefix: string) => Object.entries(temperatures).filter(([id]) => id.startsWith(prefix)).map(([, points]) => points);
	temperatures['drives:max'] = hottestOf(group('drive:'));
	temperatures['memory:max'] = hottestOf(group('dimm:'));

	return { range, from: from.getTime(), to: now.getTime(), bucketSeconds: spec.bucketSeconds, temperatures, duties, rpms, ups };
}

function round(value: number, digits: number): number {
	const factor = 10 ** digits;
	return Math.round(value * factor) / factor;
}
