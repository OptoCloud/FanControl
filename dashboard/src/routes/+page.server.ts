import { getRuntime } from '$lib/server/runtime';
import type { PageServerLoad } from './$types';

// Everything the page needs to render complete on first paint, before its live stream connects.
export const load: PageServerLoad = async () => {
	const runtime = getRuntime();
	return {
		snapshot: runtime.latest,
		daemonConnected: runtime.daemonConnected,
		drives: runtime.driveList,
		events: await runtime.recentEvents(50).catch(() => [])
	};
};
