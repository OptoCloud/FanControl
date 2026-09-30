import { getCore } from '$lib/server/core';
import { recentEvents } from '$lib/server/db';
import type { PageServerLoad } from './$types';

// Everything the page needs to render complete on first paint, before its live stream connects.
export const load: PageServerLoad = async () => {
	const core = getCore();
	return {
		coreConnected: core.coreConnected,
		snapshot: core.snapshot,
		daemonConnected: core.daemonConnected,
		drives: core.drives,
		ups: core.ups,
		events: await recentEvents(core.sql, 50).catch(() => [])
	};
};
