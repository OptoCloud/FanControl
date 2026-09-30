import { error, json } from '@sveltejs/kit';
import { isRangeKey, queryHistory } from '$lib/server/history';
import { getRuntime } from '$lib/server/runtime';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async ({ url }) => {
	const range = url.searchParams.get('range');
	if (!isRangeKey(range)) error(400, 'range must be one of 1h, 6h, 24h, 7d, 30d');

	const runtime = getRuntime();
	try {
		return json(await queryHistory(runtime.sql, range, runtime.ups.enabled ? runtime.upsName : undefined));
	} catch (cause) {
		console.error('[vigil] history query failed:', cause instanceof Error ? cause.message : cause);
		error(503, 'History is unavailable: the database could not be reached.');
	}
};
